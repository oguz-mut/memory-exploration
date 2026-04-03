namespace MemoryLib.Readers;

using MemoryLib;
using MemoryLib.Models;
using System.Text.Json;

public sealed class SkillReader
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private ulong _skillVtable;
    private readonly string _cacheDir;

    // Empirically discovered offsets from live game memory dump.
    // Object layout (base at i, 8-byte aligned):
    //   +0x00: vtable pointer
    //   +0x40: Mono string pointer  → skill name (e.g. "Cooking")
    //   +0x50: int32                → raw level (0..200)
    //   +0x68: int32                → max level (1..200)
    // Guessed fields between level and max (unverified — logged for inspection):
    //   +0x54: int32                → bonus level?
    //   +0x58: int32                → XP earned?
    //   +0x5C: int32                → XP to next level?
    private const int OffNamePtr    = 0x40;
    private const int OffLevel      = 0x50;
    private const int OffBonusGuess = 0x54;
    private const int OffXpGuess    = 0x58;
    private const int OffTnlGuess   = 0x5C;
    private const int OffMax        = 0x68;

    // Minimum bytes needed from object base to read all fields (0x68 + 4 bytes for int32)
    private const int MinSpan = OffMax + 4; // 0x6C

    public SkillReader(ProcessMemory memory, MemoryRegionScanner scanner)
    {
        _memory = memory;
        _scanner = scanner;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectGorgonTools");
    }

    public bool AutoDiscover()
    {
        // Strategy 1 — Cache
        try
        {
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
            if (File.Exists(cachePath))
            {
                string json = File.ReadAllText(cachePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("skillVtable", out var prop))
                {
                    string? hex = prop.GetString();
                    if (hex != null && ulong.TryParse(hex.Replace("0x", ""),
                            System.Globalization.NumberStyles.HexNumber, null, out ulong cached))
                    {
                        if (IsValidPointer(cached) && ValidateSkillVtable(cached))
                        {
                            _skillVtable = cached;
                            Console.WriteLine($"[SkillReader] Loaded vtable from cache: 0x{_skillVtable:X}");
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy 2 — Empirical structural scan
        // Single-pass over all private regions. For every 8-byte-aligned offset i:
        //   • vtable  at i+0x00 must be a valid pointer
        //   • strPtr  at i+0x40 must be a valid pointer
        //   • level   at i+0x50 must be 0..200
        //   • max     at i+0x68 must be 1..200, and level <= max
        // Group candidates by vtable; the most common vtable with >= 3 hits wins.
        Console.WriteLine("[SkillReader] Starting empirical vtable discovery scan...");
        var vtableCandidates = new Dictionary<ulong, List<ulong>>(); // vtable → object addresses

        foreach (var region in _scanner.GetGameRegions())
        {
            ulong chunkOffset = 0;
            while (chunkOffset < region.Size)
            {
                ulong remaining = region.Size - chunkOffset;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + chunkOffset;

                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    for (int i = 0; i + MinSpan <= chunk.Length; i += 8)
                    {
                        ulong vtable = BitConverter.ToUInt64(chunk, i);
                        if (!IsValidPointer(vtable)) continue;

                        ulong strPtr = BitConverter.ToUInt64(chunk, i + OffNamePtr);
                        if (!IsValidPointer(strPtr)) continue;

                        int level = BitConverter.ToInt32(chunk, i + OffLevel);
                        if (level < 0 || level > 200) continue;

                        int max = BitConverter.ToInt32(chunk, i + OffMax);
                        if (max < 1 || max > 200) continue;

                        if (level > max) continue;

                        ulong objAddr = readAddr + (ulong)i;
                        if (!vtableCandidates.TryGetValue(vtable, out var list))
                        {
                            list = new List<ulong>();
                            vtableCandidates[vtable] = list;
                        }
                        list.Add(objAddr);
                    }
                }

                chunkOffset += (ulong)readSize;
            }
        }

        // Pick the vtable with the most structural hits (minimum 3)
        ulong bestVtable = 0;
        List<ulong>? bestAddrs = null;
        foreach (var (vtable, addrs) in vtableCandidates)
        {
            if (addrs.Count >= 3 && (bestAddrs == null || addrs.Count > bestAddrs.Count))
            {
                bestVtable = vtable;
                bestAddrs = addrs;
            }
        }

        if (bestVtable == 0 || bestAddrs == null)
        {
            Console.WriteLine("[SkillReader] Empirical scan found no vtable with >= 3 structural hits.");
            return false;
        }

        Console.WriteLine($"[SkillReader] Best vtable candidate: 0x{bestVtable:X} ({bestAddrs.Count} hits)");

        // Validate by reading the skill name string from a few instances
        int validated = 0;
        foreach (ulong objAddr in bestAddrs.Take(10))
        {
            ulong strPtr = _memory.ReadPointer(objAddr + OffNamePtr);
            string? name = _memory.ReadMonoString(strPtr, maxLength: 64);
            if (!string.IsNullOrEmpty(name) && name.All(c => char.IsLetterOrDigit(c) || c == ' '))
            {
                Console.WriteLine($"[SkillReader] Validated obj 0x{objAddr:X}: name=\"{name}\"");
                validated++;
                if (validated >= 3) break;
            }
        }

        if (validated == 0)
        {
            Console.WriteLine("[SkillReader] Vtable candidate failed name validation.");
            return false;
        }

        _skillVtable = bestVtable;
        SaveVtableCache();
        return true;
    }

    private static bool IsValidPointer(ulong ptr) =>
        ptr > 0x1_0000_0000UL && ptr < 0x7FFF_FFFF_FFFFul;

    private bool ValidateSkillVtable(ulong vtable)
    {
        foreach (var region in _scanner.GetGameRegions())
        {
            ulong chunkOffset = 0;
            int found = 0;
            while (chunkOffset < region.Size)
            {
                ulong remaining = region.Size - chunkOffset;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + chunkOffset;

                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    for (int i = 0; i + MinSpan <= chunk.Length; i += 8)
                    {
                        if (BitConverter.ToUInt64(chunk, i) != vtable) continue;

                        ulong strPtr = BitConverter.ToUInt64(chunk, i + OffNamePtr);
                        if (!IsValidPointer(strPtr)) continue;

                        int level = BitConverter.ToInt32(chunk, i + OffLevel);
                        if (level < 0 || level > 200) continue;

                        int max = BitConverter.ToInt32(chunk, i + OffMax);
                        if (max < 1 || max > 200) continue;

                        if (level > max) continue;

                        found++;
                        if (found >= 3)
                        {
                            ulong objAddr = readAddr + (ulong)i;
                            string? name = _memory.ReadMonoString(
                                _memory.ReadPointer(objAddr + OffNamePtr), maxLength: 64);
                            if (!string.IsNullOrEmpty(name))
                                return true;
                        }
                    }
                }

                chunkOffset += (ulong)readSize;
            }
        }
        return false;
    }

    private void SaveVtableCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");

            Dictionary<string, string> data = new();
            if (File.Exists(cachePath))
            {
                try
                {
                    string existing = File.ReadAllText(cachePath);
                    using var doc = JsonDocument.Parse(existing);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        data[prop.Name] = prop.Value.GetString() ?? "";
                }
                catch { }
            }

            data["skillVtable"] = $"0x{_skillVtable:X}";
            File.WriteAllText(cachePath, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public List<SkillSnapshot>? ReadAllSkills()
    {
        if (_skillVtable == 0) return null;

        var results = new List<SkillSnapshot>();
        var seen = new HashSet<ulong>();
        int logged = 0;

        foreach (var region in _scanner.GetGameRegions())
        {
            ulong chunkOffset = 0;
            while (chunkOffset < region.Size)
            {
                ulong remaining = region.Size - chunkOffset;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + chunkOffset;

                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    for (int i = 0; i + MinSpan <= chunk.Length; i += 8)
                    {
                        if (BitConverter.ToUInt64(chunk, i) != _skillVtable) continue;

                        ulong strPtr = BitConverter.ToUInt64(chunk, i + OffNamePtr);
                        if (!IsValidPointer(strPtr)) continue;

                        int level = BitConverter.ToInt32(chunk, i + OffLevel);
                        if (level < 0 || level > 200) continue;

                        int max = BitConverter.ToInt32(chunk, i + OffMax);
                        if (max < 1 || max > 200) continue;

                        if (level > max) continue;

                        ulong objAddr = readAddr + (ulong)i;
                        if (!seen.Add(objAddr)) continue;

                        string? name = _memory.ReadMonoString(strPtr, maxLength: 64);
                        if (string.IsNullOrEmpty(name)) continue;

                        // Guessed fields — read and log for verification
                        int bonusGuess = _memory.ReadInt32(objAddr + OffBonusGuess);
                        int xpGuess    = _memory.ReadInt32(objAddr + OffXpGuess);
                        int tnlGuess   = _memory.ReadInt32(objAddr + OffTnlGuess);

                        if (logged < 5)
                        {
                            Console.WriteLine(
                                $"[SkillReader] obj=0x{objAddr:X} name=\"{name}\" " +
                                $"+0x50={level} +0x54={bonusGuess}(bonus?) " +
                                $"+0x58={xpGuess}(xp?) +0x5C={tnlGuess}(tnl?) +0x68={max}(max)");
                            logged++;
                        }

                        results.Add(new SkillSnapshot
                        {
                            Name          = name,
                            ObjectAddress = objAddr,
                            RawLevel      = level,
                            Bonus         = bonusGuess,
                            Level         = level + bonusGuess,
                            Xp            = xpGuess,
                            Tnl           = tnlGuess,
                            Max           = max,
                        });
                    }
                }

                chunkOffset += (ulong)readSize;
            }
        }

        Console.WriteLine($"[SkillReader] Found {results.Count} skill objects.");
        return results.OrderByDescending(s => s.Level).ToList();
    }
}

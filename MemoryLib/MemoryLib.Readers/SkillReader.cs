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
    private Dictionary<int, string> _skillNames = new();

    // Offset of rawLevel field within object. All other fields are derived from this.
    // Pattern A (standard): 0x14  — layout: vtable(8) obj_header(8) SkillType(4) rawLevel(4) ...
    // Pattern B (+4 shift):  0x18  — SkillType is 8 bytes, or extra field before it
    // Pattern C (-4 shift):  0x10  — no SkillType field before rawLevel
    private int _rawLevelOffset = 0x14;

    public SkillReader(ProcessMemory memory, MemoryRegionScanner scanner)
    {
        _memory = memory;
        _scanner = scanner;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectGorgonTools");
    }

    // Field offsets derived from _rawLevelOffset (all fields are sequential ints, 4 bytes each).
    private int SkillTypeOff  => _rawLevelOffset - 4;
    private int RawLevelOff   => _rawLevelOffset;
    private int BonusLevelOff => _rawLevelOffset + 4;
    private int XpOff         => _rawLevelOffset + 8;
    private int XpToNextOff   => _rawLevelOffset + 12;
    private int MaxRawOff     => _rawLevelOffset + 16;
    // paragonLevel is always at +0x30 (not part of the sequential int block)
    private const int ParagonLevelOff = 0x30;

    public bool AutoDiscover()
    {
        // Strategy 1 - Cache
        try
        {
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
            if (File.Exists(cachePath))
            {
                string json = File.ReadAllText(cachePath);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("skillVtable", out var vtProp)
                 && doc.RootElement.TryGetProperty("skillRawLevelOffset", out var offProp))
                {
                    string? hex = vtProp.GetString();
                    string? offHex = offProp.GetString();
                    if (hex != null && offHex != null
                     && ulong.TryParse(hex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong cached)
                     && int.TryParse(offHex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out int cachedOff))
                    {
                        if (cached > 0x1_0000_0000)
                        {
                            _rawLevelOffset = cachedOff;
                            if (ValidateSkillVtable(cached))
                            {
                                _skillVtable = cached;
                                return true;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy 2 — Brute-force vtable grouping, single pass, three field-offset patterns.
        // Pattern A: rawLevel at +0x14 (standard Cpp2IL offsets)
        // Pattern B: rawLevel at +0x18 (SkillType is 8 bytes or extra padding before it)
        // Pattern C: rawLevel at +0x10 (no SkillType before rawLevel)
        var countsA = new Dictionary<ulong, int>();
        var countsB = new Dictionary<ulong, int>();
        var countsC = new Dictionary<ulong, int>();

        foreach (var region in _scanner.GetGameRegions())
        {
            ulong chunkBase = 0;
            while (chunkBase < region.Size)
            {
                ulong remaining = region.Size - chunkBase;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + chunkBase;

                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    // i + 0x2C ensures all three patterns fit (Pattern B needs up to i+0x28+4=i+0x2C).
                    for (int i = 0; i + 0x2C <= chunk.Length; i += 8)
                    {
                        ulong vtable = BitConverter.ToUInt64(chunk, i);
                        if (vtable <= 0x1_0000_0000ul || vtable > 0x7FFF_FFFF_FFFFul) continue;

                        // Pattern A: rawLevel=+0x14, bonusLevel=+0x18, xpToNext=+0x20, maxRaw=+0x24
                        int rawA = BitConverter.ToInt32(chunk, i + 0x14);
                        int bonA = BitConverter.ToInt32(chunk, i + 0x18);
                        int xpnA = BitConverter.ToInt32(chunk, i + 0x20);
                        int maxA = BitConverter.ToInt32(chunk, i + 0x24);
                        if (rawA >= 0 && rawA <= 200
                         && bonA >= 0 && bonA <= 100
                         && xpnA >= 0 && xpnA <= 100_000_000
                         && maxA >= 1 && maxA <= 200)
                        {
                            countsA.TryGetValue(vtable, out int cA);
                            countsA[vtable] = cA + 1;
                        }

                        // Pattern B: rawLevel=+0x18, bonusLevel=+0x1C, xpToNext=+0x24, maxRaw=+0x28
                        int rawB = BitConverter.ToInt32(chunk, i + 0x18);
                        int bonB = BitConverter.ToInt32(chunk, i + 0x1C);
                        int xpnB = BitConverter.ToInt32(chunk, i + 0x24);
                        int maxB = BitConverter.ToInt32(chunk, i + 0x28);
                        if (rawB >= 0 && rawB <= 200
                         && bonB >= 0 && bonB <= 100
                         && xpnB >= 0 && xpnB <= 100_000_000
                         && maxB >= 1 && maxB <= 200)
                        {
                            countsB.TryGetValue(vtable, out int cB);
                            countsB[vtable] = cB + 1;
                        }

                        // Pattern C: rawLevel=+0x10, bonusLevel=+0x14, xpToNext=+0x1C, maxRaw=+0x20
                        int rawC = BitConverter.ToInt32(chunk, i + 0x10);
                        int bonC = BitConverter.ToInt32(chunk, i + 0x14);
                        int xpnC = BitConverter.ToInt32(chunk, i + 0x1C);
                        int maxC = BitConverter.ToInt32(chunk, i + 0x20);
                        if (rawC >= 0 && rawC <= 200
                         && bonC >= 0 && bonC <= 100
                         && xpnC >= 0 && xpnC <= 100_000_000
                         && maxC >= 1 && maxC <= 200)
                        {
                            countsC.TryGetValue(vtable, out int cC);
                            countsC[vtable] = cC + 1;
                        }
                    }
                }

                chunkBase += (ulong)readSize;
            }
        }

        // Log top 10 for each pattern to aid diagnostics.
        LogTopCandidates("A (+0x14)", countsA);
        LogTopCandidates("B (+0x18)", countsB);
        LogTopCandidates("C (+0x10)", countsC);

        // Pick the best (vtable, count, pattern) across all three.
        (ulong vtable, int count, int rawOff) best = (0, 0, 0x14);
        foreach (var (counts, rawOff) in new[] {
            (countsA, 0x14),
            (countsB, 0x18),
            (countsC, 0x10),
        })
        {
            foreach (var kv in counts)
            {
                if (kv.Value > best.count)
                    best = (kv.Key, kv.Value, rawOff);
            }
        }

        if (best.vtable == 0)
        {
            Console.WriteLine("[SkillReader] No candidates found across all patterns.");
            return false;
        }

        if (best.count < 3)
        {
            Console.WriteLine($"[SkillReader] Best candidate has only {best.count} hits (need >= 3). Giving up.");
            return false;
        }

        if (best.count < 5)
            Console.WriteLine($"[SkillReader] WARNING: low hit count ({best.count}) for winner vtable=0x{best.vtable:X}. May be a false positive.");

        _rawLevelOffset = best.rawOff;
        Console.WriteLine($"[SkillReader] Trying winner: vtable=0x{best.vtable:X}  count={best.count}  pattern rawLevelOffset=0x{best.rawOff:X}");

        if (!ValidateSkillVtable(best.vtable))
        {
            Console.WriteLine("[SkillReader] Winner failed ValidateSkillVtable.");
            return false;
        }

        _skillVtable = best.vtable;
        SaveVtableCache();
        return true;
    }

    private static void LogTopCandidates(string label, Dictionary<ulong, int> counts)
    {
        if (counts.Count == 0)
        {
            Console.WriteLine($"[SkillReader] Pattern {label}: no candidates.");
            return;
        }
        Console.WriteLine($"[SkillReader] Top vtable candidates (pattern {label}):");
        foreach (var kv in counts.OrderByDescending(x => x.Value).Take(10))
            Console.WriteLine($"  vtable=0x{kv.Key:X}  count={kv.Value}");
    }

    private bool ValidateSkillObject(ulong addr)
    {
        int rawLevel   = _memory.ReadInt32(addr + (ulong)RawLevelOff);
        int bonusLevel = _memory.ReadInt32(addr + (ulong)BonusLevelOff);
        int xpToNext   = _memory.ReadInt32(addr + (ulong)XpToNextOff);
        int maxRaw     = _memory.ReadInt32(addr + (ulong)MaxRawOff);
        return rawLevel   >= 0 && rawLevel   <= 200
            && bonusLevel >= 0 && bonusLevel <= 100
            && xpToNext   >= 0 && xpToNext   <= 100_000_000
            && maxRaw     >= 1 && maxRaw     <= 200;
    }

    private bool ValidateSkillVtable(ulong vtable)
    {
        byte[] pattern = BitConverter.GetBytes(vtable);
        var regions = _scanner.GetGameRegions();
        int checked_ = 0;
        foreach (var region in regions)
        {
            if (checked_ >= 5) break;
            checked_++;
            ulong regionReadOffset = 0;
            while (regionReadOffset < region.Size)
            {
                ulong remaining = region.Size - regionReadOffset;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + regionReadOffset;
                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    for (int i = 0; i <= chunk.Length - 8; i += 8)
                    {
                        if (BitConverter.ToUInt64(chunk, i) == vtable)
                        {
                            ulong objAddr = readAddr + (ulong)i;
                            if (ValidateSkillObject(objAddr))
                                return true;
                        }
                    }
                }
                regionReadOffset += (ulong)readSize;
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
            data["skillRawLevelOffset"] = $"0x{_rawLevelOffset:X}";
            File.WriteAllText(cachePath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void LoadSkillNames()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "skills.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "skills.json"),
            Path.Combine(Environment.CurrentDirectory, "skills.json"),
        };

        foreach (string path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                string json = File.ReadAllText(path);
                using var doc = JsonDocument.Parse(json);
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("Id", out var idProp) && idProp.TryGetInt32(out int id))
                        _skillNames[id] = prop.Name;
                }
                return;
            }
            catch { }
        }
    }

    public List<SkillSnapshot>? ReadAllSkills()
    {
        if (_skillVtable == 0) return null;

        if (_skillNames.Count == 0)
            LoadSkillNames();

        var results = new List<SkillSnapshot>();
        var seen = new HashSet<ulong>();

        foreach (var region in _scanner.GetGameRegions())
        {
            ulong chunkBase = 0;
            while (chunkBase < region.Size)
            {
                ulong remaining = region.Size - chunkBase;
                int readSize = (int)Math.Min(remaining, (ulong)(8 * 1024 * 1024));
                ulong readAddr = region.BaseAddress + chunkBase;

                byte[]? chunk = _memory.ReadBytes(readAddr, readSize);
                if (chunk != null)
                {
                    for (int i = 0; i <= chunk.Length - 8; i += 8)
                    {
                        if (BitConverter.ToUInt64(chunk, i) == _skillVtable)
                        {
                            ulong objAddr = region.BaseAddress + chunkBase + (ulong)i;
                            if (seen.Contains(objAddr)) continue;
                            seen.Add(objAddr);

                            if (!ValidateSkillObject(objAddr)) continue;

                            int skillTypeInt = _memory.ReadInt32(objAddr + (ulong)SkillTypeOff);
                            int rawLevel     = _memory.ReadInt32(objAddr + (ulong)RawLevelOff);
                            int bonusLevel   = _memory.ReadInt32(objAddr + (ulong)BonusLevelOff);
                            int xp           = _memory.ReadInt32(objAddr + (ulong)XpOff);
                            int xpToNext     = _memory.ReadInt32(objAddr + (ulong)XpToNextOff);
                            int maxRaw       = _memory.ReadInt32(objAddr + (ulong)MaxRawOff);
                            int paragonLevel = _memory.ReadInt32(objAddr + ParagonLevelOff);

                            string name = _skillNames.GetValueOrDefault(skillTypeInt, $"Skill_{skillTypeInt}");
                            results.Add(new SkillSnapshot
                            {
                                Name          = name,
                                ObjectAddress = objAddr,
                                RawLevel      = rawLevel,
                                Bonus         = bonusLevel,
                                Level         = rawLevel + bonusLevel,
                                Xp            = (float)xp,
                                Tnl           = (float)xpToNext,
                                Max           = maxRaw,
                                ParagonLevel  = paragonLevel,
                            });
                        }
                    }
                }

                chunkBase += (ulong)readSize;
            }
        }

        return results.OrderByDescending(s => s.Level).ToList();
    }
}

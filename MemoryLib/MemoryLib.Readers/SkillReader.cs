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

    // Mono string chars start at +0x14 (vtable 8 + sync 8 + length 4 = 0x14)
    private const int MonoStringCharsOffset = 0x14;

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
        // Strategy 1 — Cache (re-validate before using; vtables change across ASLR restarts)
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
                        Console.WriteLine($"[SkillReader] Cached vtable 0x{cached:X} failed validation (ASLR restart?), re-discovering.");
                    }
                }
            }
        }
        catch { }

        // Strategy 2 — Phase A: string-trace discovery (~30s).
        // Scan for "Cooking" UTF-16 chars, trace back to the Mono string object,
        // then find pointers to that object and derive the skill vtable.
        // This is ASLR-safe because we start from the string content itself.
        Console.WriteLine("[SkillReader] Phase A: string-trace discovery via 'Cooking'...");
        ulong phaseAVtable = PhaseAStringTrace();
        if (phaseAVtable != 0)
        {
            _skillVtable = phaseAVtable;
            Console.WriteLine($"[SkillReader] Phase A succeeded: vtable=0x{_skillVtable:X}");
            SaveVtableCache();
            return true;
        }

        // Strategy 3 — Phase B: value-pattern scan with per-candidate string validation (~12s).
        // Falls back when "Cooking" skill is not active (character hasn't trained it, etc.).
        // Validates top 10 vtable candidates with real Mono string reads to reject false positives
        // like 0x6400000064 (int pair 100,100 misread as a vtable).
        Console.WriteLine("[SkillReader] Phase B: value-pattern scan with inline string validation...");
        ulong phaseBVtable = PhaseBValueScan();
        if (phaseBVtable != 0)
        {
            _skillVtable = phaseBVtable;
            Console.WriteLine($"[SkillReader] Phase B succeeded: vtable=0x{_skillVtable:X}");
            SaveVtableCache();
            return true;
        }

        Console.WriteLine("[SkillReader] All discovery strategies failed.");
        return false;
    }

    // Phase A: trace "Cooking" UTF-16 bytes → Mono string object → pointer to object → skill object → vtable.
    private ulong PhaseAStringTrace()
    {
        var stringHits = _scanner.ScanForUtf16String("Cooking", maxResults: 10);
        Console.WriteLine($"[SkillReader] Phase A: {stringHits.Count} UTF-16 'Cooking' hit(s) in memory");

        foreach (var hit in stringHits)
        {
            // ScanForUtf16String returns the address of the raw chars.
            // Mono string layout: vtable(8) + sync(8) + length(4) + chars at +0x14
            // So Mono string object base = charAddr - 0x14
            ulong strObjAddr = hit.Address - MonoStringCharsOffset;
            if (!IsValidPointer(strObjAddr)) continue;

            string? name = _memory.ReadMonoString(strObjAddr, maxLength: 64);
            if (name != "Cooking")
            {
                Console.WriteLine($"[SkillReader] Phase A: 0x{strObjAddr:X} is not a Mono 'Cooking' string (got \"{name}\"), skipping");
                continue;
            }

            Console.WriteLine($"[SkillReader] Phase A: valid Mono 'Cooking' string at 0x{strObjAddr:X}");

            // Find all pointers to this Mono string object in memory
            var ptrs = _scanner.ScanForPointerTo(strObjAddr, maxResults: 10);
            Console.WriteLine($"[SkillReader] Phase A: {ptrs.Count} pointer(s) to string object 0x{strObjAddr:X}");

            foreach (var ptr in ptrs)
            {
                // The name field is at objBase + 0x40, so objBase = ptr.Address - 0x40
                ulong objBase = ptr.Address - OffNamePtr;

                ulong vtable = _memory.ReadPointer(objBase);
                if (!IsValidPointer(vtable)) continue;

                int level = _memory.ReadInt32(objBase + OffLevel);
                if (level < 0 || level > 200) continue;

                int max = _memory.ReadInt32(objBase + OffMax);
                if (max < 1 || max > 200) continue;

                Console.WriteLine(
                    $"[SkillReader] Phase A: skill obj=0x{objBase:X} vtable=0x{vtable:X} " +
                    $"level={level} max={max} name=\"Cooking\"");
                return vtable;
            }
        }

        Console.WriteLine("[SkillReader] Phase A: no valid skill object found via string trace.");
        return 0;
    }

    // Phase B: chunk scan collecting vtable candidates, then validate top 10 by reading actual
    // Mono strings. Requires >= 3 valid skill names per candidate to accept it.
    // This prevents false positives like 0x6400000064 (100,100 int pair) from winning.
    private ulong PhaseBValueScan()
    {
        var vtableCandidates = new Dictionary<ulong, List<ulong>>();

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

                        // Inline: strPtr at +0x40 must also look like a valid pointer
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

        // Test top 10 candidates by structural hit count.
        // Must read actual Mono strings to reject false positives — structural checks alone
        // are not enough (e.g., 0x6400000064 passes all numeric checks with 1.2M false hits).
        var topCandidates = vtableCandidates
            .Where(kv => kv.Value.Count >= 3)
            .OrderByDescending(kv => kv.Value.Count)
            .Take(10)
            .ToList();

        Console.WriteLine($"[SkillReader] Phase B: {topCandidates.Count} vtable candidate(s) with >= 3 structural hits");

        foreach (var (vtable, addrs) in topCandidates)
        {
            Console.WriteLine($"[SkillReader] Phase B: testing vtable 0x{vtable:X} ({addrs.Count} structural hits)");
            int validated = 0;
            foreach (ulong objAddr in addrs.Take(20))
            {
                ulong strPtr = _memory.ReadPointer(objAddr + OffNamePtr);
                string? name = _memory.ReadMonoString(strPtr, maxLength: 64);
                if (IsSkillName(name))
                {
                    Console.WriteLine($"[SkillReader] Phase B: valid obj=0x{objAddr:X} name=\"{name}\"");
                    validated++;
                    if (validated >= 3)
                    {
                        Console.WriteLine($"[SkillReader] Phase B: vtable 0x{vtable:X} accepted ({validated} valid names)");
                        return vtable;
                    }
                }
            }
            Console.WriteLine($"[SkillReader] Phase B: vtable 0x{vtable:X} rejected ({validated}/3 valid names)");
        }

        return 0;
    }

    // Skill names are short ASCII words/phrases: "Cooking", "Sword", "Fire Magic", etc.
    private static bool IsSkillName(string? name) =>
        name != null && name.Length >= 3 && name.Length <= 30 &&
        name.All(c => c < 128 && (char.IsLetterOrDigit(c) || c == ' '));

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
                            if (IsSkillName(name))
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

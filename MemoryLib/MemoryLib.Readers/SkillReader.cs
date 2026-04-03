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
        // Strategy 1 - Cache
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
                    if (hex != null && ulong.TryParse(hex.Replace("0x", ""), System.Globalization.NumberStyles.HexNumber, null, out ulong cached))
                    {
                        if (cached > 0x10000 && ValidateSkillVtable(cached))
                        {
                            _skillVtable = cached;
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy 2 - Structural discovery
        string[] probeSkills = { "Cooking", "Mycology", "Sword", "Carpentry", "Foraging" };
        foreach (string probe in probeSkills)
        {
            var hits = _scanner.ScanForUtf16String(probe, maxResults: 20);
            foreach (var hit in hits)
            {
                ulong strObjAddr = hit.Address - 0x14;
                // Only chase pointers for valid Mono string objects
                var str = _memory.ReadMonoString(strObjAddr);
                if (str != probe) continue;

                var ptrs = _scanner.ScanForPointerTo(strObjAddr, maxResults: 10);
                foreach (var ptrMatch in ptrs)
                {
                    for (int backStep = 0; backStep <= 10; backStep++)
                    {
                        ulong candidateBase = ptrMatch.Address - (ulong)(backStep * 8);
                        ulong vtable = _memory.ReadPointer(candidateBase);
                        if (vtable <= 0x10000 || vtable > 0x7FFF_FFFF_FFFFul) continue;
                        if (ValidateSkillObject(candidateBase))
                        {
                            _skillVtable = vtable;
                            SaveVtableCache();
                            return true;
                        }
                    }
                }
            }
        }

        return false;
    }

    private bool ValidateSkillObject(ulong addr)
    {
        int rawLevel   = _memory.ReadInt32(addr + 0x14);
        int bonusLevel = _memory.ReadInt32(addr + 0x18);
        int xpToNext   = _memory.ReadInt32(addr + 0x20);
        int maxRaw     = _memory.ReadInt32(addr + 0x24);
        return rawLevel   >= 0 && rawLevel   <= 100
            && bonusLevel >= 0 && bonusLevel <= 50
            && xpToNext   >= 0 && xpToNext   <= 10_000_000
            && maxRaw     >= 5 && maxRaw     <= 100;
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

                            int skillTypeInt = _memory.ReadInt32(objAddr + 0x10);
                            int rawLevel     = _memory.ReadInt32(objAddr + 0x14);
                            int bonusLevel   = _memory.ReadInt32(objAddr + 0x18);
                            int xp           = _memory.ReadInt32(objAddr + 0x1C);
                            int xpToNext     = _memory.ReadInt32(objAddr + 0x20);
                            int maxRaw       = _memory.ReadInt32(objAddr + 0x24);
                            int paragonLevel = _memory.ReadInt32(objAddr + 0x30);

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

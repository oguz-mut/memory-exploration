namespace MemoryLib.Readers;

using MemoryLib;
using MemoryLib.Models;
using System.Text.Json;

public sealed class CombatantReader
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private ulong _localPlayerClassPtr;
    private ulong _combatantVtable;
    private ulong _combatantAddr;
    private readonly string _cacheDir;

    public CombatantReader(ProcessMemory memory, MemoryRegionScanner scanner)
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

                if (doc.RootElement.TryGetProperty("combatantVtable", out var vtableProp))
                {
                    string? hex = vtableProp.GetString();
                    if (hex != null && ulong.TryParse(hex.Replace("0x", ""),
                            System.Globalization.NumberStyles.HexNumber, null, out ulong cachedVtable)
                        && cachedVtable > 0x10000)
                    {
                        ulong addr = FindLocalCombatantByVtable(cachedVtable);
                        if (addr != 0)
                        {
                            _combatantVtable = cachedVtable;
                            _combatantAddr   = addr;
                            return true;
                        }
                    }
                }

                if (doc.RootElement.TryGetProperty("localPlayerClassPtr", out var classProp))
                {
                    string? hex = classProp.GetString();
                    if (hex != null && ulong.TryParse(hex.Replace("0x", ""),
                            System.Globalization.NumberStyles.HexNumber, null, out ulong cachedClassPtr)
                        && cachedClassPtr > 0x10000)
                    {
                        _localPlayerClassPtr = cachedClassPtr;
                    }
                }
            }
        }
        catch { }

        // Strategy 2 - Find Combatant via "Combatant" string scan then vtable discovery
        {
            // First try: find string, locate pointers to it, walk back to object headers
            var stringHits = _scanner.ScanForUtf16String("Combatant", maxResults: 50);
            foreach (var hit in stringHits)
            {
                var ptrs = _scanner.ScanForPointerTo(hit.Address, maxResults: 20);
                foreach (var ptrMatch in ptrs)
                {
                    for (int back = 0; back <= 8; back++)
                    {
                        ulong candidate = ptrMatch.Address - (ulong)(back * 8);
                        ulong vtable = _memory.ReadPointer(candidate);
                        if (!IsValidPointer(vtable)) continue;
                        if (!ValidateCombatantObject(candidate)) continue;
                        if (_memory.ReadBool(candidate + 0x98))
                        {
                            _combatantVtable = vtable;
                            _combatantAddr   = candidate;
                            SaveVtableCache();
                            return true;
                        }
                    }
                }
            }

            // Second try: brute-scan regions for objects that structurally look like Combatant,
            // extract their vtable, then find the local-player instance
            ulong foundVtable = DiscoverCombatantVtable();
            if (foundVtable != 0)
            {
                ulong localAddr = FindLocalCombatantByVtable(foundVtable);
                if (localAddr != 0)
                {
                    _combatantVtable = foundVtable;
                    _combatantAddr   = localAddr;
                    SaveVtableCache();
                    return true;
                }
            }
        }

        // Strategy 3 - Brute-force dictionary scan: find the attribute dict, trace back to Combatant
        {
            ulong combatantAddr = BruteForceDiscoverCombatant();
            if (combatantAddr != 0)
            {
                _combatantVtable = _memory.ReadPointer(combatantAddr);
                _combatantAddr   = combatantAddr;
                SaveVtableCache();
                return true;
            }
        }

        return false;
    }

    public CombatantSnapshot? ReadLocalCombatant()
    {
        if (_combatantVtable == 0) return null;

        ulong combatantAddr = _combatantAddr;
        if (combatantAddr == 0 || !ValidateCombatantObject(combatantAddr) || !_memory.ReadBool(combatantAddr + 0x98))
        {
            combatantAddr = FindLocalCombatantByVtable(_combatantVtable);
            if (combatantAddr == 0) return null;
            _combatantAddr = combatantAddr;
        }

        bool isDead = _memory.ReadBool(combatantAddr + 0xA9);
        ulong dictPtr = _memory.ReadPointer(combatantAddr + 0x90);
        if (dictPtr == 0) return null;

        var attributes = ReadAttributeDictionary(dictPtr);

        return new CombatantSnapshot
        {
            ObjectAddress = combatantAddr,
            IsDead        = isDead,
            IsLocalPlayer = true,
            Attributes    = attributes,
            // Health/MaxHealth/Power/MaxPower/Armor/MaxArmor left at 0 — attribute IDs need live testing
        };
    }

    private Dictionary<int, double> ReadAttributeDictionary(ulong dictAddr)
    {
        int count = _memory.ReadInt32(dictAddr + 0x20);
        if (count <= 0 || count > 2000) return new Dictionary<int, double>();

        ulong entriesArrayPtr = _memory.ReadPointer(dictAddr + 0x18);
        if (entriesArrayPtr == 0) return new Dictionary<int, double>();

        int maxLength = _memory.ReadInt32(entriesArrayPtr + 0x18);
        if (maxLength <= 0 || maxLength > 4096) return new Dictionary<int, double>();

        ulong dataStart = entriesArrayPtr + 0x20;

        var result = new Dictionary<int, double>();
        int entrySize = 24; // hashCode(4) + next(4) + key(4) + pad(4) + value(8)

        for (int i = 0; i < Math.Min(count * 2, maxLength); i++)
        {
            ulong entryAddr = dataStart + (ulong)(i * entrySize);
            int hashCode = _memory.ReadInt32(entryAddr);
            if (hashCode < 0) continue; // -1 means free slot
            int key     = _memory.ReadInt32(entryAddr + 8);
            double value = _memory.ReadDouble(entryAddr + 16);
            if (key >= 0 && key < 10000 && !double.IsNaN(value) && Math.Abs(value) < 1e12)
                result.TryAdd(key, value);
            if (result.Count >= count) break;
        }

        // If too few results, retry without alignment padding (entry size 20)
        if (result.Count < count / 2 && count > 10)
        {
            result.Clear();
            entrySize = 20; // hashCode(4) + next(4) + key(4) + value(8) — no pad
            for (int i = 0; i < Math.Min(count * 2, maxLength); i++)
            {
                ulong entryAddr = dataStart + (ulong)(i * entrySize);
                int hashCode = _memory.ReadInt32(entryAddr);
                if (hashCode < 0) continue;
                int key     = _memory.ReadInt32(entryAddr + 8);
                double value = _memory.ReadDouble(entryAddr + 12);
                if (key >= 0 && key < 10000 && !double.IsNaN(value) && Math.Abs(value) < 1e12)
                    result.TryAdd(key, value);
                if (result.Count >= count) break;
            }
        }

        return result;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private bool IsValidPointer(ulong ptr) =>
        ptr > 0x10000 && ptr < 0x7FFF_FFFF_FFFFul;

    private bool ValidateCombatantObject(ulong addr)
    {
        ulong vtable = _memory.ReadPointer(addr);
        if (!IsValidPointer(vtable)) return false;

        ulong dictPtr = _memory.ReadPointer(addr + 0x90);
        if (!IsValidPointer(dictPtr)) return false;

        int count = _memory.ReadInt32(dictPtr + 0x20);
        if (count < 50 || count > 1000) return false;

        ulong entriesPtr = _memory.ReadPointer(dictPtr + 0x18);
        if (!IsValidPointer(entriesPtr)) return false;

        byte isDead = _memory.ReadByte(addr + 0xA9);
        return isDead <= 1;
    }

    private ulong FindLocalCombatantByVtable(ulong vtable)
    {
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
                        if (BitConverter.ToUInt64(chunk, i) != vtable) continue;
                        ulong objAddr = readAddr + (ulong)i;
                        if (!ValidateCombatantObject(objAddr)) continue;
                        if (_memory.ReadBool(objAddr + 0x98)) return objAddr;
                    }
                }

                chunkBase += (ulong)readSize;
            }
        }
        return 0;
    }

    /// <summary>
    /// Scans regions for any object that structurally looks like a Combatant and returns its vtable.
    /// </summary>
    private ulong DiscoverCombatantVtable()
    {
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
                    for (int i = 0; i + 0xB0 <= chunk.Length; i += 8)
                    {
                        ulong potentialVtable  = BitConverter.ToUInt64(chunk, i);
                        ulong potentialDictPtr = BitConverter.ToUInt64(chunk, i + 0x90);
                        if (!IsValidPointer(potentialVtable) || !IsValidPointer(potentialDictPtr))
                            continue;

                        ulong objAddr = readAddr + (ulong)i;
                        if (ValidateCombatantObject(objAddr))
                            return potentialVtable;
                    }
                }

                chunkBase += (ulong)readSize;
            }
        }
        return 0;
    }

    /// <summary>
    /// Finds the local player's Combatant by scanning for dictionaries with 50-1000 attribute entries,
    /// then tracing back through pointer references to find the owning Combatant where forLocalPlayer==true.
    /// </summary>
    private ulong BruteForceDiscoverCombatant()
    {
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
                    for (int i = 0; i + 0x30 <= chunk.Length; i += 8)
                    {
                        ulong entriesPtr = BitConverter.ToUInt64(chunk, i + 0x18);
                        if (!IsValidPointer(entriesPtr)) continue;

                        int count = BitConverter.ToInt32(chunk, i + 0x20);
                        if (count < 50 || count > 1000) continue;

                        ulong dictAddr = readAddr + (ulong)i;
                        if (QuickValidateDictionary(dictAddr, count) < count / 2) continue;

                        var dictPtrs = _scanner.ScanForPointerTo(dictAddr, maxResults: 20);
                        foreach (var ptrMatch in dictPtrs)
                        {
                            ulong candidateCombatant = ptrMatch.Address - 0x90;
                            if (!ValidateCombatantObject(candidateCombatant)) continue;
                            if (_memory.ReadBool(candidateCombatant + 0x98)) return candidateCombatant;
                        }
                    }
                }

                chunkBase += (ulong)readSize;
            }
        }
        return 0;
    }

    private int QuickValidateDictionary(ulong dictAddr, int count)
    {
        ulong entriesArrayPtr = _memory.ReadPointer(dictAddr + 0x18);
        if (!IsValidPointer(entriesArrayPtr)) return 0;

        int maxLength = _memory.ReadInt32(entriesArrayPtr + 0x18);
        if (maxLength <= 0 || maxLength > 4096) return 0;

        ulong dataStart = entriesArrayPtr + 0x20;
        int found = 0;
        int limit  = Math.Min(count * 2, maxLength);

        for (int i = 0; i < limit; i++)
        {
            ulong entryAddr = dataStart + (ulong)(i * 24);
            int hashCode = _memory.ReadInt32(entryAddr);
            if (hashCode < 0) continue;
            int key     = _memory.ReadInt32(entryAddr + 8);
            double value = _memory.ReadDouble(entryAddr + 16);
            if (key >= 0 && key < 10000 && !double.IsNaN(value) && Math.Abs(value) < 1e12)
                found++;
            if (found >= count) break;
        }

        return found;
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

            if (_combatantVtable != 0)
                data["combatantVtable"] = $"0x{_combatantVtable:X}";
            if (_localPlayerClassPtr != 0)
                data["localPlayerClassPtr"] = $"0x{_localPlayerClassPtr:X}";

            File.WriteAllText(cachePath, JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

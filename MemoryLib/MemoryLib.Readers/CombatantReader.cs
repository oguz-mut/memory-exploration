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

        // Try the primary dict pointer (+0x90) and adjacent candidates (+0x88, +0x98) if needed.
        ulong dictPtr = ResolveDictPointer(combatantAddr);
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

    /// <summary>
    /// Reads the attribute dictionary pointer from the combatant, trying +0x90 first then
    /// adjacent offsets +0x88 and +0x98 if the primary slot is zero or invalid.
    /// </summary>
    private ulong ResolveDictPointer(ulong combatantAddr)
    {
        ulong[] candidateOffsets = [0x90, 0x88, 0x98];
        foreach (ulong off in candidateOffsets)
        {
            ulong ptr = _memory.ReadPointer(combatantAddr + off);
            if (IsValidPointer(ptr))
                return ptr;
        }
        return 0;
    }

    // Layout variant descriptor used during probing.
    private readonly record struct DictVariant(
        uint CountOffset,    // offset from dictAddr to int _count
        uint EntriesOffset,  // offset from dictAddr to Entry[] pointer
        uint ArrayDataOffset,// offset from entriesArrayAddr to first Entry
        int  EntrySize,      // bytes per Entry
        uint ValueOffset,    // offset from entry base to double value
        uint KeyOffset);     // offset from entry base to int key

    private static readonly DictVariant[] Variants =
    [
        // A (original): count@+0x20, entries@+0x18, data@array+0x20, 24-byte entry, key@+8, val@+16
        new(0x20, 0x18, 0x20, 24, 16, 8),
        // B: count@+0x28, entries@+0x20, data@array+0x20, 24-byte entry
        new(0x28, 0x20, 0x20, 24, 16, 8),
        // C: count@+0x20, entries@+0x18, shorter array header (data@+0x10), 24-byte entry
        new(0x20, 0x18, 0x10, 24, 16, 8),
        // D: count@+0x28, entries@+0x20, data@array+0x10, 24-byte entry
        new(0x28, 0x20, 0x10, 24, 16, 8),
        // E: 20-byte entry (no key-alignment pad), data@+0x20
        new(0x20, 0x18, 0x20, 20, 12, 8),
        // F: 20-byte entry, data@+0x10
        new(0x20, 0x18, 0x10, 20, 12, 8),
        // G: 20-byte entry, count@+0x28
        new(0x28, 0x20, 0x20, 20, 12, 8),
        // H: 32-byte entry (extra padding), data@+0x20
        new(0x20, 0x18, 0x20, 32, 16, 8),
        // I: 16-byte entry (hashCode4+next4+key4+val_lo4 — value straddles; unlikely but worth a try at +8)
        new(0x20, 0x18, 0x20, 16, 8, 4),
    ];

    private Dictionary<int, double> ReadAttributeDictionary(ulong dictAddr)
    {
        foreach (var v in Variants)
        {
            int count = _memory.ReadInt32(dictAddr + v.CountOffset);
            if (count < 1 || count > 2000) continue;

            ulong entriesArrayPtr = _memory.ReadPointer(dictAddr + v.EntriesOffset);
            if (!IsValidPointer(entriesArrayPtr)) continue;

            // IL2CPP array max_length lives at either +0x18 or +0x8 depending on the Unity version.
            // Try both positions; pick the one that gives a plausible length >= count.
            int maxLength = _memory.ReadInt32(entriesArrayPtr + 0x18);
            if (maxLength <= 0 || maxLength > 8192)
                maxLength = _memory.ReadInt32(entriesArrayPtr + 0x8);
            if (maxLength <= 0 || maxLength > 8192) continue;

            ulong dataStart = entriesArrayPtr + v.ArrayDataOffset;
            var result = ProbeEntries(dataStart, count, maxLength, v);
            if (result.Count >= count / 2)
            {
                Console.WriteLine($"[CombatantReader] Dictionary variant worked: " +
                    $"count@+0x{v.CountOffset:X}, entries@+0x{v.EntriesOffset:X}, " +
                    $"data@array+0x{v.ArrayDataOffset:X}, entrySize={v.EntrySize}, " +
                    $"key@+{v.KeyOffset}, val@+{v.ValueOffset} => {result.Count}/{count} entries");
                return result;
            }
        }

        Console.WriteLine($"[CombatantReader] ReadAttributeDictionary: no variant yielded entries for dict 0x{dictAddr:X}");
        return new Dictionary<int, double>();
    }

    private Dictionary<int, double> ProbeEntries(ulong dataStart, int count, int maxLength, DictVariant v)
    {
        var result = new Dictionary<int, double>(count);
        int limit = Math.Min(count * 2, maxLength);
        for (int i = 0; i < limit; i++)
        {
            ulong entryAddr = dataStart + (ulong)(i * v.EntrySize);
            int hashCode = _memory.ReadInt32(entryAddr);
            if (hashCode < 0) continue; // free slot
            int key = _memory.ReadInt32(entryAddr + v.KeyOffset);
            double value = _memory.ReadDouble(entryAddr + v.ValueOffset);
            if (key >= 0 && key < 10000 && !double.IsNaN(value) && Math.Abs(value) < 1e12)
                result.TryAdd(key, value);
            if (result.Count >= count) break;
        }
        return result;
    }

    /// <summary>
    /// Diagnostic helper: dumps raw memory around the dictionary pointer and probes every
    /// plausible _count location so the correct offsets can be identified from test output.
    /// </summary>
    public void DumpCombatantMemory(ulong combatantAddr)
    {
        Console.WriteLine($"[Dump] Combatant @ 0x{combatantAddr:X}");

        // Hex dump of combatant+0x88..+0x98 (three 8-byte slots around the dict pointer)
        for (ulong off = 0x88; off <= 0x98; off += 8)
        {
            ulong val = _memory.ReadPointer(combatantAddr + off);
            Console.WriteLine($"  combatant+0x{off:X2} = 0x{val:X}");
        }

        ulong dictPtr = _memory.ReadPointer(combatantAddr + 0x90);
        Console.WriteLine($"[Dump] Primary dict pointer (combatant+0x90) = 0x{dictPtr:X}");

        if (!IsValidPointer(dictPtr))
        {
            Console.WriteLine("[Dump] Primary dict pointer invalid — trying adjacent offsets");
            dictPtr = ResolveDictPointer(combatantAddr);
            if (dictPtr == 0) { Console.WriteLine("[Dump] No valid dict pointer found."); return; }
            Console.WriteLine($"[Dump] Using fallback dict pointer = 0x{dictPtr:X}");
        }

        // Hex dump of first 0x40 bytes of the dictionary object
        Console.WriteLine($"[Dump] Dictionary object @ 0x{dictPtr:X} (first 0x40 bytes):");
        byte[]? dictBytes = _memory.ReadBytes(dictPtr, 0x40);
        if (dictBytes == null)
        {
            Console.WriteLine("  <read failed>");
        }
        else
        {
            for (int off = 0; off < 0x40; off += 8)
            {
                ulong qword = BitConverter.ToUInt64(dictBytes, off);
                int   dwordLo = BitConverter.ToInt32(dictBytes, off);
                Console.WriteLine($"  +0x{off:X2}  qword=0x{qword:X16}  int32={dwordLo}");
            }
        }

        // Probe every 4-byte aligned slot for a plausible _count (50-1000)
        Console.WriteLine("[Dump] Probing dict offsets for plausible _count (50-1000):");
        for (uint off = 0x10; off <= 0x3C; off += 4)
        {
            int candidate = _memory.ReadInt32(dictPtr + off);
            if (candidate >= 50 && candidate <= 1000)
                Console.WriteLine($"  dict+0x{off:X2} = {candidate}  <-- plausible _count");
        }

        // For any plausible count, also dump the entries pointer and array header
        for (uint countOff = 0x10; countOff <= 0x30; countOff += 4)
        {
            int count = _memory.ReadInt32(dictPtr + countOff);
            if (count < 50 || count > 1000) continue;

            foreach (uint entriesOff in new uint[] { 0x10, 0x18, 0x20, 0x28 })
            {
                ulong entriesPtr = _memory.ReadPointer(dictPtr + entriesOff);
                if (!IsValidPointer(entriesPtr)) continue;

                Console.WriteLine($"[Dump] count@+0x{countOff:X2}={count}, entries@+0x{entriesOff:X2}=0x{entriesPtr:X}");
                Console.WriteLine($"  Array header (first 0x30 bytes):");
                byte[]? arrBytes = _memory.ReadBytes(entriesPtr, 0x30);
                if (arrBytes != null)
                {
                    for (int i = 0; i < 0x30; i += 8)
                    {
                        ulong qw = BitConverter.ToUInt64(arrBytes, i);
                        int   dw = BitConverter.ToInt32(arrBytes, i);
                        Console.WriteLine($"    +0x{i:X2}  qword=0x{qw:X16}  int32={dw}");
                    }
                }
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private bool IsValidPointer(ulong ptr) =>
        ptr > 0x10000 && ptr < 0x7FFF_FFFF_FFFFul;

    private bool ValidateCombatantObject(ulong addr)
    {
        ulong vtable = _memory.ReadPointer(addr);
        if (!IsValidPointer(vtable)) return false;

        // Accept the object if any candidate dict pointer + any layout variant looks plausible.
        ulong[] dictOffsets = [0x90, 0x88, 0x98];
        bool dictOk = false;
        foreach (ulong dOff in dictOffsets)
        {
            ulong dictPtr = _memory.ReadPointer(addr + dOff);
            if (!IsValidPointer(dictPtr)) continue;
            if (HasPlausibleCount(dictPtr))
            {
                dictOk = true;
                break;
            }
        }
        if (!dictOk) return false;

        byte isDead = _memory.ReadByte(addr + 0xA9);
        return isDead <= 1;
    }

    /// <summary>Returns true if any known _count offset within the dictionary object holds a
    /// value in [50, 1000] and the corresponding entries pointer is valid.</summary>
    private bool HasPlausibleCount(ulong dictPtr)
    {
        uint[] countOffsets   = [0x20, 0x28, 0x18, 0x30];
        uint[] entriesOffsets = [0x18, 0x20, 0x10, 0x28];
        foreach (uint cOff in countOffsets)
        {
            int count = _memory.ReadInt32(dictPtr + cOff);
            if (count < 50 || count > 1000) continue;
            foreach (uint eOff in entriesOffsets)
            {
                ulong ePtr = _memory.ReadPointer(dictPtr + eOff);
                if (IsValidPointer(ePtr)) return true;
            }
        }
        return false;
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
                    for (int i = 0; i + 0x38 <= chunk.Length; i += 8)
                    {
                        // Try count at +0x20 and +0x28 (the two most likely positions).
                        int count = 0;
                        foreach (int cOff in new[] { 0x20, 0x28 })
                        {
                            int c = BitConverter.ToInt32(chunk, i + cOff);
                            if (c >= 50 && c <= 1000) { count = c; break; }
                        }
                        if (count == 0) continue;

                        // entries pointer at +0x18 or +0x20
                        ulong entriesPtr = 0;
                        foreach (int eOff in new[] { 0x18, 0x20 })
                        {
                            if (i + eOff + 8 > chunk.Length) continue;
                            ulong p = BitConverter.ToUInt64(chunk, i + eOff);
                            if (IsValidPointer(p)) { entriesPtr = p; break; }
                        }
                        if (entriesPtr == 0) continue;

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
        // Try each layout variant; return the best hit count found.
        int best = 0;
        foreach (var v in Variants)
        {
            int c = _memory.ReadInt32(dictAddr + v.CountOffset);
            if (c < 1 || c > 2000) continue;

            ulong entriesArrayPtr = _memory.ReadPointer(dictAddr + v.EntriesOffset);
            if (!IsValidPointer(entriesArrayPtr)) continue;

            int maxLength = _memory.ReadInt32(entriesArrayPtr + 0x18);
            if (maxLength <= 0 || maxLength > 8192)
                maxLength = _memory.ReadInt32(entriesArrayPtr + 0x8);
            if (maxLength <= 0 || maxLength > 8192) continue;

            ulong dataStart = entriesArrayPtr + v.ArrayDataOffset;
            int found = ProbeEntries(dataStart, count, maxLength, v).Count;
            if (found > best) best = found;
            if (best >= count) break;
        }
        return best;
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

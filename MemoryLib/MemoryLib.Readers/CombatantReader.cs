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

        Console.WriteLine($"[Combatant] Reading local combatant @ 0x{combatantAddr:X}");

        bool isDead = _memory.ReadBool(combatantAddr + 0xA9);

        // Log the dict pointer at +0x90 and scan all adjacent offsets +0x80..+0xA0
        ulong primaryDictPtr = _memory.ReadPointer(combatantAddr + 0x90);
        Console.WriteLine($"[Combatant] Dict pointer at +0x90: 0x{primaryDictPtr:X} " +
            $"({(IsValidPointer(primaryDictPtr) ? "VALID" : "INVALID")})");

        Console.WriteLine("[Combatant] Scanning combatant+0x80..0xA0 for valid pointers:");
        for (int off = 0x80; off <= 0xA0; off += 8)
        {
            ulong ptr = _memory.ReadPointer(combatantAddr + (ulong)off);
            if (IsValidPointer(ptr))
                Console.WriteLine($"  +0x{off:X2}: 0x{ptr:X}  <-- valid pointer");
            else
                Console.WriteLine($"  +0x{off:X2}: 0x{ptr:X}");
        }

        // Try the primary dict pointer (+0x90) and adjacent candidates (+0x88, +0x98) if needed.
        ulong dictPtr = ResolveDictPointer(combatantAddr);
        if (dictPtr == 0)
        {
            Console.WriteLine("[Combatant] No valid dict pointer found at +0x88/0x90/0x98 — aborting.");
            return null;
        }

        Console.WriteLine($"[Combatant] Using dict pointer: 0x{dictPtr:X}");
        var attributes = ReadAttributeDictionary(dictPtr);
        Console.WriteLine($"[Combatant] ReadAttributeDictionary returned {attributes.Count} entries.");

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

    // Layout variant descriptor used during probing (used by exhaustive fallback).
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

    /// <summary>
    /// Primary entry point: tries empirical stride detection first, falls back to
    /// exhaustive variant probing if the stride signal is too weak.
    /// </summary>
    private Dictionary<int, double> ReadAttributeDictionary(ulong dictAddr)
    {
        // ── Raw dump of first 0x40 bytes of the dictionary object ────────────
        Console.WriteLine($"[Dict] Raw bytes at dict 0x{dictAddr:X}:");
        byte[]? dictBytes = _memory.ReadBytes(dictAddr, 0x40);
        if (dictBytes == null)
        {
            Console.WriteLine("  <read failed>");
            return new Dictionary<int, double>();
        }
        for (int off = 0; off < 0x40; off += 4)
        {
            int i32 = BitConverter.ToInt32(dictBytes, off);
            Console.WriteLine($"  +0x{off:X2} = {i32,12} (0x{(uint)i32:X8})");
        }

        // ── Use known layout from live test: count@+0x20, entries ptr@+0x18 ──
        int count = BitConverter.ToInt32(dictBytes, 0x20);
        ulong entriesPtr = BitConverter.ToUInt64(dictBytes, 0x18);
        Console.WriteLine($"[Dict] Known layout: count={count} (dict+0x20), entriesPtr=0x{entriesPtr:X} (dict+0x18)");

        if (count < 1 || count > 2000 || !IsValidPointer(entriesPtr))
        {
            Console.WriteLine("[Dict] Known layout invalid — falling back to exhaustive probe.");
            return ReadAttributeDictionaryExhaustive(dictAddr);
        }

        // ── Read 8KB directly from entriesPtr — no IL2CPP array header assumed ──
        const int ScanSize = 8192;
        byte[]? eb = _memory.ReadBytes(entriesPtr, ScanSize);
        if (eb == null)
        {
            Console.WriteLine("[Dict] Entries read failed — falling back.");
            return ReadAttributeDictionaryExhaustive(dictAddr);
        }

        // ── Raw dump: first 256 bytes as qword hex + decoded double ──────────
        Console.WriteLine($"[Dict] First 256 bytes from entriesPtr 0x{entriesPtr:X}:");
        for (int off = 0; off < 256 && off + 8 <= eb.Length; off += 8)
        {
            ulong  qw = BitConverter.ToUInt64(eb, off);
            double d  = BitConverter.ToDouble(eb, off);
            string ds = (double.IsNaN(d) || double.IsInfinity(d)) ? "N/A" : $"{d:G8}";
            Console.WriteLine($"  +0x{off:X3}  0x{qw:X16}  double={ds}");
        }

        // ── Scan every 8-byte aligned offset for game-value doubles [1.0, 100000.0] ──
        var gameOffsets = new List<int>();
        for (int off = 0; off + 8 <= eb.Length; off += 8)
        {
            double d = BitConverter.ToDouble(eb, off);
            if (!double.IsNaN(d) && !double.IsInfinity(d) && d >= 1.0 && d <= 100_000.0)
                gameOffsets.Add(off);
        }
        Console.WriteLine($"[Dict] {gameOffsets.Count} game-value doubles in [1.0,100000.0] (first 20):");
        foreach (int off in gameOffsets.Take(20))
            Console.WriteLine($"  +0x{off:X3}  {BitConverter.ToDouble(eb, off):G8}");

        if (gameOffsets.Count < 4)
        {
            Console.WriteLine("[Dict] Too few game values to detect stride — falling back.");
            return ReadAttributeDictionaryExhaustive(dictAddr);
        }

        // ── Find most common delta between consecutive game-value offsets ─────
        var deltaCounts = new Dictionary<int, int>();
        for (int i = 1; i < gameOffsets.Count; i++)
        {
            int delta = gameOffsets[i] - gameOffsets[i - 1];
            if (delta > 0 && delta <= 256)
                deltaCounts[delta] = deltaCounts.GetValueOrDefault(delta) + 1;
        }

        int entryStride = 0, strideTally = 0;
        foreach (var (delta, tally) in deltaCounts)
        {
            if (tally > strideTally) { strideTally = tally; entryStride = delta; }
        }
        Console.WriteLine($"[Dict] Detected entry stride = {entryStride} bytes ({strideTally} votes)");

        if (entryStride < 8 || entryStride > 256 || strideTally < 2)
        {
            Console.WriteLine("[Dict] Stride signal too weak — falling back.");
            return ReadAttributeDictionaryExhaustive(dictAddr);
        }

        // ── Determine value offset within an entry (value is 8-byte aligned) ──
        int valOff = gameOffsets[0] % entryStride;
        Console.WriteLine($"[Dict] Value offset within entry = +0x{valOff:X} ({valOff})");

        // ── Try candidate key offsets (scan backward from value in 4-byte steps) ──
        // Standard .NET layout: hashCode(4)+next(4)+key(4)+[pad]+val(8)
        // → key is typically 4, 8, or 12 bytes before val.
        var keyOffCandidates = new List<int>();
        for (int kOff = valOff - 4; kOff >= 0; kOff -= 4)
            keyOffCandidates.Add(kOff);
        // Also try positions after the value (val-first layouts)
        if (valOff + 8 + 0 < entryStride) keyOffCandidates.Add(valOff + 8);
        if (valOff + 8 + 4 < entryStride) keyOffCandidates.Add(valOff + 12);

        int limit = Math.Min(count * 2, eb.Length / entryStride);

        Dictionary<int, double>? best = null;
        foreach (int keyOff in keyOffCandidates)
        {
            if (keyOff < 0 || keyOff + 4 > entryStride) continue;

            var candidate = new Dictionary<int, double>(count);
            for (int i = 0; i < limit; i++)
            {
                int entryBase = i * entryStride;
                int needEnd   = entryBase + Math.Max(valOff + 8, keyOff + 4);
                if (needEnd > eb.Length) break;

                double value = BitConverter.ToDouble(eb, entryBase + valOff);
                if (double.IsNaN(value) || double.IsInfinity(value) || Math.Abs(value) > 1e12) continue;

                int key = BitConverter.ToInt32(eb, entryBase + keyOff);
                if (key < 0 || key >= 10_000) continue;

                candidate.TryAdd(key, value);
                if (candidate.Count >= count) break;
            }

            Console.WriteLine($"[Dict] keyOff=+{keyOff} valOff=+{valOff} stride={entryStride} => {candidate.Count}/{count} entries");

            if (best == null || candidate.Count > best.Count)
                best = candidate;
        }

        if (best is { Count: > 0 })
        {
            Console.WriteLine($"[Dict] Empirical read succeeded: {best.Count} entries.");
            return best;
        }

        Console.WriteLine("[Dict] Empirical approach yielded no entries — falling back.");
        return ReadAttributeDictionaryExhaustive(dictAddr);
    }

    /// <summary>
    /// Exhaustive fallback: probes all (count, entries, header, entry-size) combos,
    /// assuming a standard IL2CPP array header precedes the entry data.
    /// </summary>
    private Dictionary<int, double> ReadAttributeDictionaryExhaustive(ulong dictAddr)
    {
        // ── find all plausible _count candidates (10..2000) ──────────────────
        Console.WriteLine("[Dict] Exhaustive probe — scanning +0x10..+0x40 for _count (10..2000):");
        var countCandidates = new List<(uint off, int count)>();
        for (uint off = 0x10; off <= 0x40; off += 4)
        {
            int val = _memory.ReadInt32(dictAddr + off);
            if (val >= 10 && val <= 2000)
            {
                Console.WriteLine($"  dict+0x{off:X2} = {val}  <-- plausible _count");
                countCandidates.Add((off, val));
            }
        }
        if (countCandidates.Count == 0)
            Console.WriteLine("  (none found)");

        // ── find all valid pointer candidates (potential _entries) ────────────
        Console.WriteLine("[Dict] Exhaustive probe — scanning +0x10..+0x40 for valid _entries pointers:");
        var entriesCandidates = new List<(uint off, ulong ptr)>();
        for (uint off = 0x10; off <= 0x40; off += 8)
        {
            ulong ptr = _memory.ReadPointer(dictAddr + off);
            if (IsValidPointer(ptr))
            {
                Console.WriteLine($"  dict+0x{off:X2} = 0x{ptr:X}  <-- valid pointer");
                entriesCandidates.Add((off, ptr));
            }
        }
        if (entriesCandidates.Count == 0)
            Console.WriteLine("  (none found)");

        // ── for each entries pointer, dump its array header ───────────────────
        foreach (var (eOff, entriesPtr) in entriesCandidates)
        {
            Console.WriteLine($"[Dict] Entries array at dict+0x{eOff:X2} = 0x{entriesPtr:X} (first 0x40 bytes):");
            byte[]? arrBytes = _memory.ReadBytes(entriesPtr, 0x40);
            if (arrBytes == null)
            {
                Console.WriteLine("  <read failed>");
            }
            else
            {
                Console.WriteLine($"  raw: {BitConverter.ToString(arrBytes)}");
                for (int i = 0; i < 0x40; i += 8)
                {
                    ulong qw = BitConverter.ToUInt64(arrBytes, i);
                    int   dw = BitConverter.ToInt32(arrBytes, i);
                    Console.WriteLine($"  +0x{i:X2}  qword=0x{qw:X16}  int32={dw}");
                }
            }
        }

        // ── try all (countOff, entriesOff, arrayHeaderSize, entrySize) combos ──
        int[] arrayDataOffsets = [0x20, 0x18, 0x10];
        int[] entrySizes       = [16, 20, 24, 28, 32];

        Dictionary<int, double>? best = null;
        int bestScore = 0;

        foreach (var (cOff, count) in countCandidates)
        {
            foreach (var (eOff, entriesPtr) in entriesCandidates)
            {
                if (cOff == eOff) continue;

                int maxLength = 0;
                foreach (int mlOff in new[] { 0x18, 0x10, 0x8 })
                {
                    int ml = _memory.ReadInt32(entriesPtr + (ulong)mlOff);
                    if (ml >= count && ml <= 8192) { maxLength = ml; break; }
                }
                if (maxLength == 0) maxLength = count * 2;

                foreach (int dataOff in arrayDataOffsets)
                {
                    foreach (int entrySize in entrySizes)
                    {
                        ulong dataStart = entriesPtr + (ulong)dataOff;

                        var keyValLayouts = entrySize switch
                        {
                            16 => new[] { (4, 8) },
                            20 => new[] { (8, 12) },
                            24 => new[] { (8, 16) },
                            28 => new[] { (8, 16), (8, 20) },
                            32 => new[] { (8, 16), (8, 24) },
                            _  => new[] { (8, 16) },
                        };

                        foreach (var (keyOff, valOff) in keyValLayouts)
                        {
                            var v = new DictVariant(
                                CountOffset:     cOff,
                                EntriesOffset:   eOff,
                                ArrayDataOffset: (uint)dataOff,
                                EntrySize:       entrySize,
                                ValueOffset:     (uint)valOff,
                                KeyOffset:       (uint)keyOff);

                            var result = ProbeEntries(dataStart, count, maxLength, v);
                            bool plausible = result.Count >= count / 2;

                            if (result.Count > 0)
                            {
                                Console.WriteLine(
                                    $"[Dict] count@+0x{cOff:X2}={count}, entries@+0x{eOff:X2}, " +
                                    $"data@+0x{dataOff:X}, entrySize={entrySize}, " +
                                    $"key@+{keyOff}, val@+{valOff} => {result.Count}/{count} entries" +
                                    (plausible ? "  <-- PLAUSIBLE" : ""));
                            }

                            if (plausible && result.Count > bestScore)
                            {
                                bestScore = result.Count;
                                best = result;
                                Console.WriteLine(
                                    $"[Dict] *** Best so far: count@+0x{cOff:X2}={count}, " +
                                    $"entries@+0x{eOff:X2}, data@+0x{dataOff:X}, " +
                                    $"entrySize={entrySize}, key@+{keyOff}, val@+{valOff} " +
                                    $"=> {result.Count} entries ***");
                            }
                        }
                    }
                }
            }
        }

        if (best == null)
        {
            Console.WriteLine("[Dict] Exhaustive probe found nothing — trying pre-built Variants:");
            foreach (var v in Variants)
            {
                int count = _memory.ReadInt32(dictAddr + v.CountOffset);
                if (count < 1 || count > 2000) continue;

                ulong entriesArrayPtr = _memory.ReadPointer(dictAddr + v.EntriesOffset);
                if (!IsValidPointer(entriesArrayPtr)) continue;

                int maxLength = _memory.ReadInt32(entriesArrayPtr + 0x18);
                if (maxLength <= 0 || maxLength > 8192)
                    maxLength = _memory.ReadInt32(entriesArrayPtr + 0x8);
                if (maxLength <= 0 || maxLength > 8192) continue;

                ulong dataStart = entriesArrayPtr + v.ArrayDataOffset;
                var result = ProbeEntries(dataStart, count, maxLength, v);
                if (result.Count >= count / 2)
                {
                    Console.WriteLine(
                        $"[Dict] Variant worked: count@+0x{v.CountOffset:X}, entries@+0x{v.EntriesOffset:X}, " +
                        $"data@array+0x{v.ArrayDataOffset:X}, entrySize={v.EntrySize}, " +
                        $"key@+{v.KeyOffset}, val@+{v.ValueOffset} => {result.Count}/{count} entries");
                    return result;
                }
            }

            Console.WriteLine($"[Dict] ReadAttributeDictionary: all probes failed for dict 0x{dictAddr:X}");
            return new Dictionary<int, double>();
        }

        return best;
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

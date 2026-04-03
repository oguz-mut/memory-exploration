using System;
using System.Diagnostics;
using MemoryLib;

var pid = ProcessMemory.FindGameProcess();
if (pid == null)
{
    Console.WriteLine("ERROR: WindowsPlayer / Gorgon process not found. Is the game running?");
    return;
}
Console.WriteLine($"Attaching to PID {pid}...");
using var memory = ProcessMemory.Open(pid.Value);
var scanner = new MemoryRegionScanner(memory);

// Warm up region cache before timed sections
Console.WriteLine("Warming up region cache...");
var warmupSw = Stopwatch.StartNew();
scanner.GetGameRegions();
warmupSw.Stop();
Console.WriteLine($"Cache warm ({scanner.GetGameRegions().Count} regions) in {warmupSw.Elapsed.TotalSeconds:F1}s");
Console.WriteLine();

// =====================================================================
// INVESTIGATION 1: Find a real Item object via "Councils" string
// =====================================================================
Console.WriteLine("=== INVESTIGATION 1: Trace 'Councils' string to Item object ===");
try
{
    var sw = Stopwatch.StartNew();
    var hits = scanner.ScanForUtf16String("Councils", maxResults: 20);
    sw.Stop();
    Console.WriteLine($"ScanForUtf16String('Councils'): {hits.Count} hit(s) in {sw.Elapsed.TotalSeconds:F1}s");

    if (hits.Count == 0)
    {
        Console.WriteLine("WARNING: No 'Councils' UTF-16 hits — skipping Investigation 1.");
    }
    else
    {
        int validStringsProcessed = 0;
        foreach (var hit in hits)
        {
            if (validStringsProcessed >= 2) break;

            ulong strObj = hit.Address - 0x14;
            string? str = memory.ReadMonoString(strObj);
            if (str != "Councils") continue;

            Console.WriteLine($"\n--- Valid 'Councils' Mono string at 0x{strObj:X} (chars at 0x{hit.Address:X}) ---");

            var ptrSw = Stopwatch.StartNew();
            var ptrs = scanner.ScanForPointerTo(strObj, maxResults: 20);
            ptrSw.Stop();
            Console.WriteLine($"  ScanForPointerTo(0x{strObj:X}): {ptrs.Count} pointer(s) in {ptrSw.Elapsed.TotalSeconds:F1}s");

            if (ptrs.Count == 0)
            {
                Console.WriteLine("  WARNING: No pointers to this string found.");
                continue;
            }

            int ptrsProcessed = 0;
            foreach (var ptr in ptrs)
            {
                if (ptrsProcessed >= 2) break;
                ulong P = ptr.Address;

                Console.WriteLine($"\n  === Pointer to 'Councils' string at 0x{P:X} ===");
                DumpAsFieldTable(memory, P - 0x40, 0x100);

                // Try P-0x18 and P-0x20 as ItemInfo base candidates
                foreach (ulong fieldOff in new ulong[] { 0x18, 0x20 })
                {
                    if (P < fieldOff) continue;
                    ulong infoBase = P - fieldOff;
                    Console.WriteLine($"\n  Candidate ItemInfo base at 0x{infoBase:X} (string ptr at +0x{fieldOff:X}):");
                    DumpAsFieldTable(memory, infoBase, 0x80);

                    ulong vt = memory.ReadPointer(infoBase);
                    if (vt > 0x1_0000_0000 && vt < 0x7FFF_FFFF_FFFF)
                        Console.WriteLine($"  ItemInfo vtable: 0x{vt:X}");

                    // Find what points to this ItemInfo
                    var itemPtrSw = Stopwatch.StartNew();
                    var itemPtrs = scanner.ScanForPointerTo(infoBase, maxResults: 10);
                    itemPtrSw.Stop();
                    Console.WriteLine($"  ScanForPointerTo(ItemInfo 0x{infoBase:X}): {itemPtrs.Count} pointer(s) in {itemPtrSw.Elapsed.TotalSeconds:F1}s");

                    int itemCandidates = 0;
                    foreach (var itemPtr in itemPtrs)
                    {
                        if (itemCandidates >= 2) break;
                        ulong IP = itemPtr.Address;
                        Console.WriteLine($"\n  === Potential Item object (points to this ItemInfo) pointer at 0x{IP:X} ===");
                        for (int tryOff = 0; tryOff <= 0x20; tryOff += 8)
                        {
                            ulong itemBase = IP - (ulong)tryOff;
                            Console.WriteLine($"  Item candidate base = 0x{itemBase:X} (info ptr at +0x{tryOff:X}):");
                            DumpAsFieldTable(memory, itemBase, 0x60);
                        }
                        itemCandidates++;
                    }
                }

                ptrsProcessed++;
            }

            validStringsProcessed++;
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"EXCEPTION in Investigation 1: {ex.Message}");
}

Console.WriteLine();

// =====================================================================
// INVESTIGATION 2: Find RuntimeSkillData objects via "Cooking" string
// =====================================================================
Console.WriteLine("=== INVESTIGATION 2: Trace 'Cooking' string to RuntimeSkillData ===");
try
{
    var sw = Stopwatch.StartNew();
    var hits = scanner.ScanForUtf16String("Cooking", maxResults: 10);
    sw.Stop();
    Console.WriteLine($"ScanForUtf16String('Cooking'): {hits.Count} hit(s) in {sw.Elapsed.TotalSeconds:F1}s");

    if (hits.Count == 0)
    {
        Console.WriteLine("WARNING: No 'Cooking' UTF-16 hits — skipping Investigation 2.");
    }
    else
    {
        int validStringsProcessed = 0;
        foreach (var hit in hits)
        {
            if (validStringsProcessed >= 2) break;

            ulong strObj = hit.Address - 0x14;
            string? str = memory.ReadMonoString(strObj);
            if (str != "Cooking") continue;

            Console.WriteLine($"\n--- Valid 'Cooking' Mono string at 0x{strObj:X} ---");

            var ptrSw = Stopwatch.StartNew();
            var ptrs = scanner.ScanForPointerTo(strObj, maxResults: 10);
            ptrSw.Stop();
            Console.WriteLine($"  ScanForPointerTo(0x{strObj:X}): {ptrs.Count} pointer(s) in {ptrSw.Elapsed.TotalSeconds:F1}s");

            if (ptrs.Count == 0)
            {
                Console.WriteLine("  WARNING: No pointers to this string found.");
                continue;
            }

            int ptrsProcessed = 0;
            foreach (var ptr in ptrs)
            {
                if (ptrsProcessed >= 2) break;
                ulong P = ptr.Address;

                Console.WriteLine($"\n  === Pointer to 'Cooking' string at 0x{P:X} ===");
                DumpAsFieldTable(memory, P - 0x40, 0x100);

                // Look for plausible level/maxLevel values (1-100) near the pointer
                Console.WriteLine($"  Nearby int scan (±0x40 from 0x{P:X}):");
                for (int off = -0x40; off <= 0x40; off += 4)
                {
                    long addr = (long)P + off;
                    if (addr < 0) continue;
                    int val = memory.ReadInt32((ulong)addr);
                    if (val >= 1 && val <= 100)
                        Console.WriteLine($"    Nearby int at {off:+0;-0}: {val} (plausible level/max?)");
                }

                ptrsProcessed++;
            }

            validStringsProcessed++;
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"EXCEPTION in Investigation 2: {ex.Message}");
}

Console.WriteLine();

// =====================================================================
// INVESTIGATION 3: Dump combatant dictionary area
// =====================================================================
Console.WriteLine("=== INVESTIGATION 3: Combatant dictionary attribute dump ===");
try
{
    ulong combatant = 0x653735CE30UL;

    bool isDead = memory.ReadBool(combatant + 0xA9);
    if (isDead)
    {
        Console.WriteLine($"WARNING: isDead flag at combatant+0xA9 is TRUE — combatant address 0x{combatant:X} may be stale. Proceeding anyway.");
    }
    else
    {
        Console.WriteLine($"Combatant 0x{combatant:X} verified: isDead=false");
    }

    ulong dictPtr = memory.ReadPointer(combatant + 0x90);
    Console.WriteLine($"Dict pointer at combatant+0x90: 0x{dictPtr:X}");

    if (dictPtr == 0 || dictPtr < 0x1_0000_0000)
    {
        Console.WriteLine("WARNING: Invalid dict pointer — skipping Investigation 3.");
    }
    else
    {
        Console.WriteLine($"\n--- Dict object at 0x{dictPtr:X} (first 0x60 bytes) ---");
        DumpAsFieldTable(memory, dictPtr, 0x60);

        // Primary entries pointer at dict+0x18
        ulong entriesPtr = memory.ReadPointer(dictPtr + 0x18);
        Console.WriteLine($"\n--- Entries pointer at dict+0x18: 0x{entriesPtr:X} ---");

        if (entriesPtr > 0x1_0000_0000 && entriesPtr < 0x7FFF_FFFF_FFFF)
        {
            Console.WriteLine($"\n=== Entries at 0x{entriesPtr:X} (1024 bytes) ===");
            DumpAsFieldTable(memory, entriesPtr, 1024);

            var bytes = memory.ReadBytes(entriesPtr, 1024);
            if (bytes != null)
            {
                Console.WriteLine($"\n=== Doubles found in entries area (8-byte aligned) ===");
                bool found8 = false;
                for (int i = 0; i <= bytes.Length - 8; i += 8)
                {
                    double d = BitConverter.ToDouble(bytes, i);
                    if (d >= 1.0 && d <= 100000.0 && !double.IsNaN(d) && !double.IsInfinity(d))
                    {
                        Console.WriteLine($"  offset +0x{i:X3}: {d:F4}");
                        found8 = true;
                    }
                }
                if (!found8) Console.WriteLine("  (none found at 8-byte alignment)");

                Console.WriteLine($"\n=== Doubles found in entries area (4-byte offset, 8-byte stride) ===");
                bool found4 = false;
                for (int i = 4; i <= bytes.Length - 8; i += 8)
                {
                    double d = BitConverter.ToDouble(bytes, i);
                    if (d >= 1.0 && d <= 100000.0 && !double.IsNaN(d) && !double.IsInfinity(d))
                    {
                        Console.WriteLine($"  offset +0x{i:X3} (4-aligned): {d:F4}");
                        found4 = true;
                    }
                }
                if (!found4) Console.WriteLine("  (none found at 4-byte offset)");
            }
            else
            {
                Console.WriteLine("WARNING: Could not read entries bytes.");
            }
        }
        else
        {
            Console.WriteLine($"WARNING: dict+0x18 entries pointer 0x{entriesPtr:X} looks invalid — skipping entries dump.");
        }

        // Alternate entries pointer at dict+0x38
        ulong altEntriesPtr = memory.ReadPointer(dictPtr + 0x38);
        Console.WriteLine($"\n--- Alternate entries pointer at dict+0x38: 0x{altEntriesPtr:X} ---");

        if (altEntriesPtr > 0x1_0000_0000 && altEntriesPtr < 0x7FFF_FFFF_FFFF && altEntriesPtr != entriesPtr)
        {
            Console.WriteLine($"\n=== Alt Entries at 0x{altEntriesPtr:X} (512 bytes) ===");
            DumpAsFieldTable(memory, altEntriesPtr, 512);

            var altBytes = memory.ReadBytes(altEntriesPtr, 512);
            if (altBytes != null)
            {
                Console.WriteLine($"\n=== Doubles in alt entries (8-byte aligned) ===");
                bool found = false;
                for (int i = 0; i <= altBytes.Length - 8; i += 8)
                {
                    double d = BitConverter.ToDouble(altBytes, i);
                    if (d >= 1.0 && d <= 100000.0 && !double.IsNaN(d) && !double.IsInfinity(d))
                    {
                        Console.WriteLine($"  offset +0x{i:X3}: {d:F4}");
                        found = true;
                    }
                }
                if (!found) Console.WriteLine("  (none found)");

                Console.WriteLine($"\n=== Doubles in alt entries (4-byte offset, 8-byte stride) ===");
                found = false;
                for (int i = 4; i <= altBytes.Length - 8; i += 8)
                {
                    double d = BitConverter.ToDouble(altBytes, i);
                    if (d >= 1.0 && d <= 100000.0 && !double.IsNaN(d) && !double.IsInfinity(d))
                    {
                        Console.WriteLine($"  offset +0x{i:X3} (4-aligned): {d:F4}");
                        found = true;
                    }
                }
                if (!found) Console.WriteLine("  (none found)");
            }
        }
        else
        {
            Console.WriteLine($"  (alt pointer 0x{altEntriesPtr:X} is invalid or same as primary — skipping)");
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"EXCEPTION in Investigation 3: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== Done ===");

// =====================================================================
// Helper: DumpAsFieldTable
// =====================================================================
static void DumpAsFieldTable(ProcessMemory memory, ulong addr, int size)
{
    var data = memory.ReadBytes(addr, size);
    if (data == null) { Console.WriteLine("  (read failed)"); return; }
    Console.WriteLine($"  {"Offset",-8} {"Hex8",-20} {"Int32",-12} {"Float",-12} {"UInt16",-8} {"Byte",-6} {"String?"}");
    Console.WriteLine($"  {new string('-', 90)}");
    for (int i = 0; i <= data.Length - 8; i += 8)
    {
        ulong qword  = BitConverter.ToUInt64(data, i);
        int   i32    = BitConverter.ToInt32(data, i);
        float f32    = BitConverter.ToSingle(data, i);
        ushort u16   = BitConverter.ToUInt16(data, i);
        byte  b      = data[i];
        string strInfo = "";
        if (qword > 0x1_0000_0000 && qword < 0x7FFF_FFFF_FFFF)
        {
            var s = memory.ReadMonoString(qword, maxLength: 40);
            if (s != null) strInfo = $"-> \"{s}\"";
            else           strInfo = $"-> ptr 0x{qword:X}";
        }
        Console.WriteLine($"  +0x{i:X3}    {qword:X16}  {i32,-12} {f32,-12:G6} {u16,-8} {b,-6} {strInfo}");
    }
}

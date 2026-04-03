using System.Text.Json;
using MemoryLib;
using MemoryLib.Models;

namespace MemoryLib.Readers;

public class InventoryReader
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private ulong _itemVtable;
    private readonly string _cacheDir;
    private readonly Dictionary<int, string> _itemDataByTypeId = new();

    public ulong ItemVtable => _itemVtable;
    public int OffsetItemCode => 0x10;
    public int OffsetStackCount => 0x1C;
    public int OffsetInternalNamePtr => 0x20;

    public InventoryReader(ProcessMemory memory, MemoryRegionScanner scanner)
    {
        _memory = memory;
        _scanner = scanner;
        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectGorgonTools");
    }

    public void LoadItemData(string jsonPath)
    {
        string? actualPath = ResolveItemsJsonPath(jsonPath);
        if (actualPath == null)
        {
            Console.Error.WriteLine($"[InventoryReader] items.json not found. Tried: {jsonPath}");
            return;
        }

        Console.WriteLine($"[InventoryReader] Loading items from: {actualPath}");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(actualPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                string suffix = prop.Name.StartsWith("item_") ? prop.Name[5..] : prop.Name;
                try
                {
                    // Pattern 1: key suffix is the numeric TypeID (e.g. "item_12345")
                    if (int.TryParse(suffix, out int keyTypeId))
                    {
                        string internalName = suffix;
                        if (prop.Value.TryGetProperty("InternalName", out var internalNameEl))
                            internalName = internalNameEl.GetString() ?? suffix;
                        _itemDataByTypeId[keyTypeId] = internalName;
                    }
                    // Pattern 2: key suffix is the name, TypeID is a field inside (e.g. "item_IronSword")
                    else if (prop.Value.TryGetProperty("TypeID", out var typeIdEl))
                    {
                        int typeId = typeIdEl.GetInt32();
                        _itemDataByTypeId[typeId] = suffix;
                    }
                }
                catch { }
            }
            Console.WriteLine($"[InventoryReader] Loaded {_itemDataByTypeId.Count} items from {actualPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InventoryReader] Failed to parse {actualPath}: {ex.Message}");
        }
    }

    private static string? ResolveItemsJsonPath(string primaryPath)
    {
        Console.WriteLine($"[InventoryReader] Checking path: {primaryPath} (exists: {File.Exists(primaryPath)})");
        if (File.Exists(primaryPath))
            return primaryPath;

        // Fallback 1: current working directory
        string cwdPath = Path.Combine(Environment.CurrentDirectory, "items.json");
        if (File.Exists(cwdPath))
        {
            Console.WriteLine($"[InventoryReader] Found items.json via CWD fallback: {cwdPath}");
            return cwdPath;
        }

        // Fallback 2: walk up from AppContext.BaseDirectory up to 6 levels
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 6; i++)
        {
            if (dir == null) break;
            string candidate = Path.Combine(dir, "items.json");
            if (File.Exists(candidate))
            {
                Console.WriteLine($"[InventoryReader] Found items.json by walking up {i + 1} level(s): {candidate}");
                return candidate;
            }
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }

    public bool AutoDiscover()
    {
        // Strategy 1 - Cache
        try
        {
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
            if (File.Exists(cachePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
                if (doc.RootElement.TryGetProperty("itemVtable", out var el))
                {
                    string? hex = el.GetString();
                    if (hex != null)
                    {
                        ulong val = Convert.ToUInt64(hex, 16);
                        if (val > 0x1_0000_0000 && val < 0x7FFF_FFFF_FFFF)
                        {
                            _itemVtable = val;
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy 2 - Value-pattern vtable scan (single pass, ~12s)
        Console.WriteLine("[InventoryReader] AutoDiscover: trying value-pattern vtable scan...");
        if (DiscoverViaValuePattern())
            return true;

        // Strategy 3 - Structural via item name (string probing, slow fallback)
        Console.WriteLine("[InventoryReader] AutoDiscover: falling back to string-probing strategy...");
        if (_itemDataByTypeId.Count == 0)
        {
            // Try to load items.json from common paths before giving up
            string? autoPath = ResolveItemsJsonPath(Path.Combine(AppContext.BaseDirectory, "items.json"));
            if (autoPath != null)
                LoadItemData(autoPath);
        }

        if (_itemDataByTypeId.Count == 0)
        {
            Console.Error.WriteLine("[InventoryReader] AutoDiscover: no item data loaded; skipping structural scan.");
            return false;
        }

        string[] candidates = { "IronSword", "Apple", "RedAster", "BasicStaff", "CottonCloth" };

        foreach (string candidate in candidates)
        {
            var hits = _scanner.ScanForUtf16String(candidate, maxResults: 30);
            foreach (var hit in hits)
            {
                ulong strObj = hit.Address - 0x14;
                var ptrs = _scanner.ScanForPointerTo(strObj, maxResults: 20);
                foreach (var ptrMatch in ptrs)
                {
                    ulong itemInfoBase = ptrMatch.Address - 0x20;
                    int typeId = _memory.ReadInt32(itemInfoBase + 0x10);
                    if (typeId <= 0 || typeId >= 1_000_000) continue;

                    var itemPtrs = _scanner.ScanForPointerTo(itemInfoBase, maxResults: 20);
                    foreach (var itemPtr in itemPtrs)
                    {
                        ulong itemBase = itemPtr.Address - 0x10;
                        if (!ValidateItemObject(itemBase)) continue;
                        _itemVtable = _memory.ReadPointer(itemBase);
                        SaveVtableCache();
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private bool DiscoverViaValuePattern()
    {
        // Single-pass scan: for each 8-byte aligned offset check all Item field constraints.
        // Item layout:
        //   +0x00  vtable pointer (must be in valid range)
        //   +0x10  ItemInfo* (must be valid pointer)
        //   +0x18  IID int (must be >0 and <10_000_000)
        //   +0x1C  StackSize ushort (must be 1..9999)
        //   +0x20  IsEquipped byte (must be 0 or 1)
        const int chunkSize   = 8 * 1024 * 1024;
        const int minRequired = 0x21; // need bytes 0x00..0x20 inclusive
        var vtableCounts = new Dictionary<ulong, int>();

        foreach (var region in _scanner.GetGameRegions())
        {
            ulong regionOffset = 0;
            while (regionOffset < region.Size)
            {
                ulong remaining = region.Size - regionOffset;
                int readSize = (int)Math.Min(remaining, (ulong)chunkSize);
                byte[]? chunk = _memory.ReadBytes(region.BaseAddress + regionOffset, readSize);
                regionOffset += (ulong)readSize;

                if (chunk == null || chunk.Length < minRequired) continue;

                int end = chunk.Length - minRequired;
                for (int i = 0; i <= end; i += 8)
                {
                    ulong vtable = BitConverter.ToUInt64(chunk, i);
                    if (vtable <= 0x1_0000_0000ul || vtable > 0x7FFF_FFFF_FFFFul) continue;

                    ulong infoPtr = BitConverter.ToUInt64(chunk, i + 0x10);
                    if (infoPtr <= 0x1_0000_0000ul || infoPtr > 0x7FFF_FFFF_FFFFul) continue;

                    int iid = BitConverter.ToInt32(chunk, i + 0x18);
                    if (iid <= 0 || iid >= 10_000_000) continue;

                    ushort stackSize = BitConverter.ToUInt16(chunk, i + 0x1C);
                    if (stackSize < 1 || stackSize > 9999) continue;

                    byte isEquipped = chunk[i + 0x20];
                    if (isEquipped > 1) continue;

                    vtableCounts.TryGetValue(vtable, out int count);
                    vtableCounts[vtable] = count + 1;
                }
            }
        }

        if (vtableCounts.Count == 0)
        {
            Console.WriteLine("[InventoryReader] ValuePattern: no candidates found.");
            return false;
        }

        var sorted = vtableCounts.OrderByDescending(kv => kv.Value).ToList();

        int logCount = Math.Min(10, sorted.Count);
        Console.WriteLine($"[InventoryReader] ValuePattern: top {logCount} vtable candidates:");
        for (int i = 0; i < logCount; i++)
            Console.WriteLine($"  [{i + 1}] 0x{sorted[i].Key:X} — {sorted[i].Value} hits");

        var best = sorted[0];
        if (best.Value < 3)
        {
            Console.WriteLine($"[InventoryReader] ValuePattern: best candidate has only {best.Value} hit(s) (need >=3). Skipping.");
            return false;
        }

        ulong candidateVtable = best.Key;
        Console.WriteLine($"[InventoryReader] ValuePattern: validating vtable 0x{candidateVtable:X} ({best.Value} hits)...");

        if (!ValidateVtableCandidate(candidateVtable))
        {
            Console.WriteLine($"[InventoryReader] ValuePattern: vtable 0x{candidateVtable:X} failed TypeID validation.");
            return false;
        }

        _itemVtable = candidateVtable;
        SaveVtableCache();
        return true;
    }

    private bool ValidateVtableCandidate(ulong candidateVtable)
    {
        // Re-scan to find real instances of this vtable and check ItemInfo->TypeID
        // against loaded item data. Stops after inspecting 10 objects.
        const int chunkSize   = 8 * 1024 * 1024;
        const int minRequired = 0x21;
        int found   = 0;
        int matched = 0;

        foreach (var region in _scanner.GetGameRegions())
        {
            if (found >= 10) break;
            ulong regionOffset = 0;
            while (regionOffset < region.Size && found < 10)
            {
                ulong remaining = region.Size - regionOffset;
                int readSize = (int)Math.Min(remaining, (ulong)chunkSize);
                ulong chunkBase = regionOffset;
                byte[]? chunk = _memory.ReadBytes(region.BaseAddress + regionOffset, readSize);
                regionOffset += (ulong)readSize;

                if (chunk == null || chunk.Length < minRequired) continue;

                int end = chunk.Length - minRequired;
                for (int i = 0; i <= end && found < 10; i += 8)
                {
                    if (BitConverter.ToUInt64(chunk, i) != candidateVtable) continue;

                    ulong objAddr = region.BaseAddress + chunkBase + (ulong)i;
                    ulong infoPtr = _memory.ReadPointer(objAddr + 0x10);
                    if (infoPtr <= 0x1_0000_0000ul || infoPtr > 0x7FFF_FFFF_FFFFul) continue;

                    int typeId    = _memory.ReadInt32(infoPtr + 0x10);
                    int iid       = _memory.ReadInt32(objAddr + 0x18);
                    ushort stack  = _memory.ReadUInt16(objAddr + 0x1C);
                    byte equipped = _memory.ReadByte(objAddr + 0x20);

                    found++;
                    bool knownType = _itemDataByTypeId.ContainsKey(typeId);
                    if (knownType) matched++;

                    Console.WriteLine($"  [Item {found}] addr=0x{objAddr:X} iid={iid} typeId={typeId} stack={stack} equipped={equipped} known={knownType}");
                }
            }
        }

        Console.WriteLine($"[InventoryReader] ValuePattern: {matched}/{found} items matched known TypeIDs.");
        return matched > 0;
    }

    private bool ValidateItemObject(ulong addr)
    {
        ulong vtable = _memory.ReadPointer(addr);
        if (vtable <= 0x1_0000_0000ul || vtable > 0x7FFF_FFFF_FFFF) return false;

        int iid = _memory.ReadInt32(addr + 0x18);
        ushort stackSize = _memory.ReadUInt16(addr + 0x1C);
        ulong infoPtr = _memory.ReadPointer(addr + 0x10);

        return iid > 0
            && stackSize >= 1 && stackSize <= 9999
            && infoPtr > 0x1_0000_0000ul && infoPtr < 0x7FFF_FFFF_FFFF;
    }

    private void SaveVtableCache()
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");

            var dict = new Dictionary<string, string>();

            if (File.Exists(cachePath))
            {
                try
                {
                    using var existing = JsonDocument.Parse(File.ReadAllText(cachePath));
                    foreach (var prop in existing.RootElement.EnumerateObject())
                        dict[prop.Name] = prop.Value.GetString() ?? "";
                }
                catch { }
            }

            dict["itemVtable"] = $"0x{_itemVtable:X}";
            File.WriteAllText(cachePath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public List<InventoryItemSnapshot>? ReadAllItems()
    {
        if (_itemVtable == 0) return null;

        var results = new List<InventoryItemSnapshot>();
        var seen = new HashSet<ulong>();
        const int chunkSize = 8 * 1024 * 1024;

        foreach (var region in _scanner.GetGameRegions())
        {
            ulong regionOffset = 0;
            while (regionOffset < region.Size)
            {
                ulong remaining = region.Size - regionOffset;
                int readSize = (int)Math.Min(remaining, (ulong)chunkSize);
                ulong chunkBase = regionOffset;
                byte[]? chunk = _memory.ReadBytes(region.BaseAddress + regionOffset, readSize);
                regionOffset += (ulong)readSize;

                if (chunk == null) continue;

                for (int i = 0; i <= chunk.Length - 8; i += 8)
                {
                    if (BitConverter.ToUInt64(chunk, i) != _itemVtable) continue;

                    ulong objAddr = region.BaseAddress + chunkBase + (ulong)i;
                    if (seen.Contains(objAddr)) continue;
                    seen.Add(objAddr);

                    if (!ValidateItemObject(objAddr)) continue;

                    ulong infoPtr = _memory.ReadPointer(objAddr + 0x10);
                    int typeId = _memory.ReadInt32(infoPtr + 0x10);
                    ushort stackSize = _memory.ReadUInt16(objAddr + 0x1C);
                    byte folderIdx = _memory.ReadByte(objAddr + 0x1F);
                    bool isEquipped = _memory.ReadBool(objAddr + 0x20);
                    ulong namePtr = _memory.ReadPointer(infoPtr + 0x20);
                    string internalName = _memory.ReadMonoString(namePtr)
                        ?? _itemDataByTypeId.GetValueOrDefault(typeId, $"item_{typeId}");

                    results.Add(new InventoryItemSnapshot
                    {
                        ObjectAddress = objAddr,
                        ItemCode = typeId,
                        StackCount = stackSize,
                        InternalName = internalName,
                        IsEquipped = isEquipped,
                        FolderIdx = folderIdx
                    });
                }
            }
        }

        return results.OrderBy(i => i.InternalName).ToList();
    }

    public void DumpObjectLayout(ulong addr)
    {
        Console.WriteLine($"Object layout at 0x{addr:X}:");
        Console.WriteLine($"  {"Offset",-8} {"Int32",-12} {"Float",-12} {"Pointer / String"}");
        Console.WriteLine("  " + new string('-', 60));

        for (int off = 0; off <= 0x80; off += 8)
        {
            ulong fieldAddr = addr + (ulong)off;
            int i32 = _memory.ReadInt32(fieldAddr);
            float f32 = _memory.ReadFloat(fieldAddr);
            ulong ptr = _memory.ReadPointer(fieldAddr);
            string extra = "";
            if (ptr > 0x1_0000_0000ul && ptr < 0x7FFF_FFFF_FFFF)
            {
                string? s = _memory.ReadMonoString(ptr, maxLength: 64);
                extra = s != null ? $"-> \"{s}\"" : $"-> 0x{ptr:X}";
            }
            Console.WriteLine($"  +0x{off:X2,-6} {i32,-12} {f32,-12:G6} {extra}");
        }
    }
}

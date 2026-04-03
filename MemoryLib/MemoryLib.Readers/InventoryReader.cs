using System.Text.Json;
using MemoryLib;
using MemoryLib.Models;

namespace MemoryLib.Readers;

public class InventoryReader
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private ulong _itemInfoVtable;
    private readonly string _cacheDir;
    private readonly Dictionary<int, string> _itemDataByTypeId = new();
    private readonly Dictionary<string, int> _typeIdByName = new();

    // ItemInfo field offsets confirmed from live game memory dump ("Councils" item):
    //   +0x00  vtable (0x221D1E2B610)
    //   +0x08  sync   (0 for all observed objects)
    //   +0x10  TypeID (int32; 0 for "Councils" currency — may need name fallback)
    //   +0x18  StaticName string ptr
    //   +0x20  unknown int (1 for "Councils")
    //   Object size: 0x40
    private const int OffsetItemInfoTypeId     = 0x10;
    private const int OffsetItemInfoStaticName = 0x18;

    public ulong ItemVtable => _itemInfoVtable;

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
                    if (int.TryParse(suffix, out int keyTypeId))
                    {
                        string internalName = suffix;
                        if (prop.Value.TryGetProperty("InternalName", out var internalNameEl))
                            internalName = internalNameEl.GetString() ?? suffix;
                        _itemDataByTypeId[keyTypeId] = internalName;
                        _typeIdByName[internalName] = keyTypeId;
                    }
                    else if (prop.Value.TryGetProperty("TypeID", out var typeIdEl))
                    {
                        int typeId = typeIdEl.GetInt32();
                        _itemDataByTypeId[typeId] = suffix;
                        _typeIdByName[suffix] = typeId;
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

        string cwdPath = Path.Combine(Environment.CurrentDirectory, "items.json");
        if (File.Exists(cwdPath))
        {
            Console.WriteLine($"[InventoryReader] Found items.json via CWD fallback: {cwdPath}");
            return cwdPath;
        }

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
        // Strategy 1 — Cache (key: itemInfoVtable)
        try
        {
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
            if (File.Exists(cachePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
                if (doc.RootElement.TryGetProperty("itemInfoVtable", out var el))
                {
                    string? hex = el.GetString();
                    if (hex != null)
                    {
                        ulong val = Convert.ToUInt64(hex, 16);
                        if (val > 0x1_0000_0000 && val < 0x7FFF_FFFF_FFFF)
                        {
                            _itemInfoVtable = val;
                            Console.WriteLine($"[InventoryReader] Loaded ItemInfo vtable from cache: 0x{_itemInfoVtable:X}");
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy 2 — Scan for known ItemInfo vtable (0x221D1E2B610), confirmed from live dump
        Console.WriteLine("[InventoryReader] AutoDiscover: scanning for known ItemInfo vtable (0x221D1E2B610)...");
        if (ScanForKnownItemInfoVtable())
            return true;

        Console.Error.WriteLine("[InventoryReader] AutoDiscover: ItemInfo vtable not found in process memory.");
        return false;
    }

    private bool ScanForKnownItemInfoVtable()
    {
        // The ItemInfo vtable was empirically confirmed at 0x221D1E2B610 from a live game dump
        // of the "Councils" item. Scan for this specific 8-byte value across all game regions.
        const ulong knownVtable = 0x221D1E2B610UL;
        const int chunkSize = 8 * 1024 * 1024;
        const int minRequired = 0x20; // need vtable(8) + sync(8) + typeId(8) + namePtr(8)

        int found = 0;
        int validated = 0;

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

                if (chunk == null || chunk.Length < minRequired) continue;

                int end = chunk.Length - minRequired;
                for (int i = 0; i <= end; i += 8)
                {
                    if (BitConverter.ToUInt64(chunk, i) != knownVtable) continue;

                    ulong objAddr = region.BaseAddress + chunkBase + (ulong)i;

                    // sync at +0x08 should be 0 or very small
                    ulong sync = _memory.ReadPointer(objAddr + 0x08);
                    if (sync > 0xFF) continue;

                    // StaticName ptr at +0x18 must be valid
                    ulong namePtr = _memory.ReadPointer(objAddr + OffsetItemInfoStaticName);
                    if (namePtr <= 0x1_0000_0000ul || namePtr > 0x7FFF_FFFF_FFFFul) continue;

                    string? name = _memory.ReadMonoString(namePtr, maxLength: 128);
                    if (string.IsNullOrEmpty(name)) continue;

                    int typeId = _memory.ReadInt32(objAddr + OffsetItemInfoTypeId);
                    bool knownType = _itemDataByTypeId.ContainsKey(typeId);

                    found++;
                    validated++;

                    if (found <= 10)
                        Console.WriteLine($"  [ItemInfo {found}] addr=0x{objAddr:X} typeId={typeId} name=\"{name}\" known={knownType}");
                }
            }
        }

        Console.WriteLine($"[InventoryReader] ItemInfoScan: found {validated} validated ItemInfo objects with vtable 0x{knownVtable:X}.");

        if (validated == 0)
            return false;

        _itemInfoVtable = knownVtable;
        SaveVtableCache();
        return true;
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

            dict["itemInfoVtable"] = $"0x{_itemInfoVtable:X}";
            File.WriteAllText(cachePath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[InventoryReader] Cached ItemInfo vtable 0x{_itemInfoVtable:X} → {cachePath}");
        }
        catch { }
    }

    public List<InventoryItemSnapshot>? ReadAllItems()
    {
        if (_itemInfoVtable == 0) return null;

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

                if (chunk == null || chunk.Length < 0x20) continue;

                int end = chunk.Length - 0x20;
                for (int i = 0; i <= end; i += 8)
                {
                    if (BitConverter.ToUInt64(chunk, i) != _itemInfoVtable) continue;

                    ulong objAddr = region.BaseAddress + chunkBase + (ulong)i;
                    if (seen.Contains(objAddr)) continue;
                    seen.Add(objAddr);

                    // Validate sync
                    ulong sync = _memory.ReadPointer(objAddr + 0x08);
                    if (sync > 0xFF) continue;

                    // Read StaticName
                    ulong namePtr = _memory.ReadPointer(objAddr + OffsetItemInfoStaticName);
                    if (namePtr <= 0x1_0000_0000ul || namePtr > 0x7FFF_FFFF_FFFFul) continue;

                    string? staticName = _memory.ReadMonoString(namePtr, maxLength: 128);
                    if (string.IsNullOrEmpty(staticName)) continue;

                    // Read TypeID; fall back to name lookup for currency/special items (TypeID==0)
                    int typeId = _memory.ReadInt32(objAddr + OffsetItemInfoTypeId);
                    if ((typeId <= 0 || !_itemDataByTypeId.ContainsKey(typeId))
                        && _typeIdByName.TryGetValue(staticName, out int lookedUp))
                    {
                        typeId = lookedUp;
                    }

                    string internalName = _itemDataByTypeId.GetValueOrDefault(typeId, staticName);

                    results.Add(new InventoryItemSnapshot
                    {
                        ObjectAddress = objAddr,
                        ItemCode      = typeId,
                        StackCount    = 1,       // ItemInfo has no stack count; Item wrapper needed
                        InternalName  = internalName,
                        IsEquipped    = false,   // ItemInfo has no equipped flag; Item wrapper needed
                        FolderIdx     = 0
                    });
                }
            }
        }

        Console.WriteLine($"[InventoryReader] ReadAllItems: found {results.Count} ItemInfo objects.");
        if (results.Count > 0)
            Console.WriteLine("[InventoryReader] WARNING: StackCount and IsEquipped default to 1/false — Item wrapper objects not located yet.");

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

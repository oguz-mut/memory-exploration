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

    private static readonly string[] ProbeItemNames = ["Councils", "Apple", "Guava", "BasicSword"];

    public bool AutoDiscover()
    {
        // Strategy 1 — Cache (key: itemInfoVtable); re-validate against live memory
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
                        if (IsValidPointer(val))
                        {
                            Console.WriteLine($"[InventoryReader] Validating cached ItemInfo vtable 0x{val:X}...");
                            if (ValidateVtable(val))
                            {
                                _itemInfoVtable = val;
                                Console.WriteLine($"[InventoryReader] Cache valid: using ItemInfo vtable 0x{_itemInfoVtable:X}");
                                return true;
                            }
                            Console.WriteLine("[InventoryReader] Cached vtable invalid (ASLR?), falling back to string-trace discovery.");
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy 2 — Discover vtable via known item name string tracing
        Console.WriteLine("[InventoryReader] AutoDiscover: starting string-trace discovery...");
        if (DiscoverVtableViaStringTrace())
            return true;

        Console.Error.WriteLine("[InventoryReader] AutoDiscover: ItemInfo vtable not found.");
        return false;
    }

    private static bool IsValidPointer(ulong val) => val > 0x1_0000_0000UL && val < 0x7FFF_FFFF_FFFFUL;

    // Quick validation: scan for vtable value and verify at least one valid ItemInfo instance.
    private bool ValidateVtable(ulong vtable)
    {
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
                    if (BitConverter.ToUInt64(chunk, i) != vtable) continue;

                    ulong objAddr = region.BaseAddress + chunkBase + (ulong)i;
                    if (_memory.ReadPointer(objAddr + 0x08) != 0) continue;

                    ulong namePtr = _memory.ReadPointer(objAddr + OffsetItemInfoStaticName);
                    if (!IsValidPointer(namePtr)) continue;

                    string? name = _memory.ReadMonoString(namePtr, maxLength: 128);
                    if (!string.IsNullOrEmpty(name))
                        return true;
                }
            }
        }
        return false;
    }

    // Strategy 2: trace from known item name strings to discover the ItemInfo vtable.
    //   For each probe name:
    //     1. Find UTF-16 occurrences → derive Mono string object addresses
    //     2. Find pointers to each string object (StaticName is at ItemInfo+0x18)
    //     3. Derive ItemInfo base = pointerAddr - 0x18, validate, extract vtable
    private bool DiscoverVtableViaStringTrace()
    {
        foreach (string probe in ProbeItemNames)
        {
            Console.WriteLine($"[InventoryReader] String-trace probe: \"{probe}\"");

            var strMatches = _scanner.ScanForUtf16String(probe, maxResults: 10);
            Console.WriteLine($"[InventoryReader]   UTF-16 occurrences: {strMatches.Count}");

            foreach (var strMatch in strMatches)
            {
                // ScanForUtf16String returns the address of the raw UTF-16 bytes.
                // Mono string layout: vtable(8) + sync(8) + length(4 at +0x10) + chars at +0x14
                // So chars are at strObj + 0x14, meaning strObj = strMatch.Address - 0x14
                ulong strObjAddr = strMatch.Address - 0x14;

                string? verified = _memory.ReadMonoString(strObjAddr, maxLength: 128);
                if (verified != probe) continue;

                Console.WriteLine($"[InventoryReader]   Valid Mono string at 0x{strObjAddr:X}");

                var ptrMatches = _scanner.ScanForPointerTo(strObjAddr, maxResults: 10);
                Console.WriteLine($"[InventoryReader]   Pointers to string object: {ptrMatches.Count}");

                foreach (var ptrMatch in ptrMatches)
                {
                    ulong pointerAddr = ptrMatch.Address;
                    if (pointerAddr < 0x18) continue;

                    // StaticName is at ItemInfo+0x18, so base = pointerAddr - 0x18
                    ulong itemInfoBase = pointerAddr - OffsetItemInfoStaticName;

                    ulong vtable = _memory.ReadPointer(itemInfoBase);
                    if (!IsValidPointer(vtable)) continue;

                    // sync at +0x08 must be 0
                    if (_memory.ReadPointer(itemInfoBase + 0x08) != 0) continue;

                    // Re-verify name matches
                    ulong namePtr = _memory.ReadPointer(itemInfoBase + OffsetItemInfoStaticName);
                    string? name = _memory.ReadMonoString(namePtr, maxLength: 128);
                    if (name != probe) continue;

                    Console.WriteLine($"[InventoryReader]   Found ItemInfo at 0x{itemInfoBase:X}, vtable=0x{vtable:X}");
                    _itemInfoVtable = vtable;
                    SaveVtableCache();
                    return true;
                }
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
        for (int i = 0; i < Math.Min(5, results.Count); i++)
        {
            var item = results[i];
            Console.WriteLine($"  [Item {i + 1}] name=\"{item.InternalName}\" typeId={item.ItemCode} addr=0x{item.ObjectAddress:X}");
        }
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

using System.Text.Json;
using MemoryLib;
using MemoryLib.Models;

namespace MemoryLib.Readers;

public class InventoryReader
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private ulong _itemInfoVtable;
    private ulong _itemWrapperVtable;
    private readonly string _cacheDir;
    private readonly Dictionary<int, string> _itemDataByTypeId = new();
    private readonly Dictionary<string, int> _typeIdByName = new();

    // tsysclientinfo.json data: powerIndex → (InternalName, tierIndex → EffectDescs)
    private readonly Dictionary<int, (string InternalName, Dictionary<int, string[]> Tiers)> _tsysPowers = new();

    // ItemInfo field offsets confirmed from live game memory dump ("Councils" item):
    //   +0x00  vtable (0x221D1E2B610)
    //   +0x08  sync   (0 for all observed objects)
    //   +0x10  TypeID (int32; 0 for "Councils" currency — may need name fallback)
    //   +0x18  StaticName string ptr
    //   +0x20  unknown int (1 for "Councils")
    //   Object size: 0x40
    private const int OffsetItemInfoTypeId     = 0x10;
    private const int OffsetItemInfoStaticName = 0x18;

    // Item wrapper field offsets from Cpp2IL dump (GorgonCore/Item.cs):
    //   +0x10 ItemInfo Info
    //   +0x18 int IID
    //   +0x1C ushort StackSize
    //   +0x1E byte Flags
    //   +0x1F byte FolderIdx
    //   +0x20 bool IsEquipped
    //   +0x30 Dictionary<ItemAttribute, Int64> ItemAttributes
    //   +0x38 Dictionary<ItemStringAttribute, String> ItemStringAttributes
    //   +0x40 Dictionary<String, Int64> EffectAttributes
    private const int OffsetItemInfo           = 0x10;
    private const int OffsetItemIID            = 0x18;
    private const int OffsetItemIsEquipped     = 0x20;
    private const int OffsetItemAttributes     = 0x30;

    // ItemAttribute enum keys (from GorgonCore/ItemAttribute.cs)
    private const int AttrValue      = 5;
    private const int AttrRarity     = 6;
    private const int AttrTsysLevel  = 29;
    private const int AttrAugmentId  = 161; // TSYS_IMBUE_POWER_INFO
    private const int AttrPower1     = 201;
    private const int AttrPower10    = 210;

    public ulong ItemVtable => _itemInfoVtable;
    public ulong ItemWrapperVtable => _itemWrapperVtable;

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

    // "Councils" intentionally excluded — it is a CurrencyItemInfo subclass (TypeID=0),
    // NOT a regular ItemInfo. Using it poisons the vtable cache.
    private static readonly string[] ProbeItemNames =
        ["Apple", "Guava", "BasicSword", "IronSword", "WoodHelm", "LeatherBoots",
         "CopperNecklace", "JuiceApple", "BaconStrip", "EmptyVial"];

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

    // Quick validation: scan for vtable value and verify at least one valid non-currency ItemInfo.
    // Currency items (CurrencyItemInfo subclass) have TypeID==0 — we reject vtables that
    // exclusively match currency objects.
    private bool ValidateVtable(ulong vtable)
    {
        int foundWithTypeId = 0;
        int foundCurrencyOnly = 0;
        const int chunkSize = 8 * 1024 * 1024;
        foreach (var region in _scanner.GetGameRegions())
        {
            if (foundWithTypeId >= 3) break;
            ulong regionOffset = 0;
            while (regionOffset < region.Size && foundWithTypeId < 3)
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
                    if (string.IsNullOrEmpty(name)) continue;

                    int typeId = _memory.ReadInt32(objAddr + OffsetItemInfoTypeId);
                    if (typeId > 0)
                        foundWithTypeId++;
                    else
                        foundCurrencyOnly++;

                    // Early exit: if we've checked 20+ objects and NONE have a real TypeID,
                    // this vtable is for a currency subclass — no point scanning further
                    if (foundCurrencyOnly >= 20 && foundWithTypeId == 0)
                        goto doneValidating;
                }
            }
        }
        doneValidating:

        // Reject if all found objects are currency (TypeID==0) — that's CurrencyItemInfo vtable
        if (foundWithTypeId == 0 && foundCurrencyOnly > 0)
        {
            Console.WriteLine($"[InventoryReader] ValidateVtable 0x{vtable:X}: only currency objects found — this is CurrencyItemInfo vtable, rejecting.");
            return false;
        }
        return foundWithTypeId > 0;
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

    /// <summary>
    /// Discovers the Item wrapper vtable by scanning for 8-byte pointer values that
    /// point into a known ItemInfo (at offset +0x10 of an Item wrapper). Each match's
    /// parent object is a candidate Item; we validate via IsEquipped bool and IID range,
    /// then record the vtable.
    /// Prereq: ItemInfo vtable must already be discovered (_itemInfoVtable != 0).
    /// </summary>
    public bool DiscoverItemWrapperVtable()
    {
        if (_itemInfoVtable == 0)
        {
            Console.WriteLine("[InventoryReader] DiscoverItemWrapperVtable: requires ItemInfo vtable first.");
            return false;
        }

        // Cache check
        try
        {
            string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
            if (File.Exists(cachePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
                if (doc.RootElement.TryGetProperty("itemWrapperVtable", out var el))
                {
                    ulong val = Convert.ToUInt64(el.GetString() ?? "0", 16);
                    if (IsValidPointer(val))
                    {
                        Console.WriteLine($"[InventoryReader] Validating cached Item wrapper vtable 0x{val:X}...");
                        if (ValidateItemWrapperVtable(val))
                        {
                            _itemWrapperVtable = val;
                            Console.WriteLine($"[InventoryReader] Cache valid: using Item wrapper vtable 0x{_itemWrapperVtable:X}");
                            return true;
                        }
                    }
                }
            }
        }
        catch { }

        // Strategy: find one ItemInfo instance, scan for pointers to it (those pointers
        // are the Info field inside Item wrappers at offset +0x10). Each parent is an Item
        // candidate; aggregate vtables and pick the most frequent.
        Console.WriteLine("[InventoryReader] Discovering Item wrapper vtable via ItemInfo back-reference...");

        // Find some ItemInfo instances quickly
        var itemInfoAddrs = new List<ulong>();
        const int chunkSize = 8 * 1024 * 1024;
        foreach (var region in _scanner.GetGameRegions())
        {
            if (itemInfoAddrs.Count >= 8) break;
            ulong regionOffset = 0;
            while (regionOffset < region.Size && itemInfoAddrs.Count < 8)
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
                    ulong addr = region.BaseAddress + chunkBase + (ulong)i;
                    if (_memory.ReadPointer(addr + 0x08) != 0) continue;
                    itemInfoAddrs.Add(addr);
                    if (itemInfoAddrs.Count >= 8) break;
                }
            }
        }

        Console.WriteLine($"[InventoryReader]   Found {itemInfoAddrs.Count} ItemInfo instances to probe.");

        // Sanity check: if 0 ItemInfo instances found, the vtable is wrong
        if (itemInfoAddrs.Count == 0)
        {
            Console.WriteLine("[InventoryReader] DiscoverItemWrapperVtable: no ItemInfo instances found — ItemInfo vtable may be wrong.");
            return false;
        }

        var vtableVotes = new Dictionary<ulong, int>();
        int totalPtrsFound = 0;
        foreach (var infoAddr in itemInfoAddrs)
        {
            // Pointers to this ItemInfo are the Item.Info field (at +0x10 of Item wrapper)
            var ptrs = _scanner.ScanForPointerTo(infoAddr, maxResults: 50);
            totalPtrsFound += ptrs.Count;
            foreach (var p in ptrs)
            {
                if (p.Address < OffsetItemInfo) continue;
                ulong itemBase = p.Address - OffsetItemInfo;

                ulong candVtable = _memory.ReadPointer(itemBase);
                if (!IsValidPointer(candVtable)) continue;
                if (_memory.ReadPointer(itemBase + 0x08) != 0) continue; // sync==0

                // Sanity: IID should be positive and < ~1 billion
                int iid = _memory.ReadInt32(itemBase + OffsetItemIID);
                if (iid <= 0 || iid > 1_000_000_000) continue;

                // Sanity: IsEquipped is a single byte (0 or 1)
                byte eq = _memory.ReadBytes(itemBase + OffsetItemIsEquipped, 1)?[0] ?? 255;
                if (eq > 1) continue;

                vtableVotes.TryGetValue(candVtable, out int votes);
                vtableVotes[candVtable] = votes + 1;
            }
        }

        // If zero total pointers found after probing all ItemInfo instances, the vtable is likely wrong.
        // A correct ItemInfo vtable will always have Item wrappers pointing at instances in memory.
        if (totalPtrsFound == 0)
        {
            Console.WriteLine("[InventoryReader] DiscoverItemWrapperVtable: no pointers found to any ItemInfo instance — ItemInfo vtable may be for a subclass with no Item wrappers (e.g. CurrencyItemInfo). Clearing bad vtable cache.");
            _itemInfoVtable = 0;
            // Delete stale cache entry so next run re-discovers
            try
            {
                string cachePath = Path.Combine(_cacheDir, "vtable_cache.json");
                if (File.Exists(cachePath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(cachePath));
                    var dict = new Dictionary<string, string>();
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name != "itemInfoVtable" && prop.Name != "itemWrapperVtable")
                            dict[prop.Name] = prop.Value.GetString() ?? "";
                    }
                    File.WriteAllText(cachePath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
            return false;
        }

        if (vtableVotes.Count == 0)
        {
            Console.WriteLine("[InventoryReader] No Item wrapper vtable candidates found.");
            return false;
        }

        var best = vtableVotes.OrderByDescending(kv => kv.Value).First();
        Console.WriteLine($"[InventoryReader] Top Item wrapper vtable candidate: 0x{best.Key:X} ({best.Value} votes)");
        foreach (var kv in vtableVotes.OrderByDescending(x => x.Value).Take(5))
            Console.WriteLine($"    0x{kv.Key:X}  votes={kv.Value}");

        if (!ValidateItemWrapperVtable(best.Key))
        {
            Console.WriteLine("[InventoryReader] Best candidate failed validation.");
            return false;
        }

        _itemWrapperVtable = best.Key;
        SaveItemWrapperVtableCache();
        return true;
    }

    private bool ValidateItemWrapperVtable(ulong vtable)
    {
        // Find at least one object with this vtable whose Info field points to ItemInfo vtable
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
                if (chunk == null || chunk.Length < 0x30) continue;

                int end = chunk.Length - 0x30;
                for (int i = 0; i <= end; i += 8)
                {
                    if (BitConverter.ToUInt64(chunk, i) != vtable) continue;

                    ulong itemBase = region.BaseAddress + chunkBase + (ulong)i;
                    ulong infoPtr = _memory.ReadPointer(itemBase + OffsetItemInfo);
                    if (!IsValidPointer(infoPtr)) continue;
                    if (_memory.ReadPointer(infoPtr) != _itemInfoVtable) continue;

                    int iid = _memory.ReadInt32(itemBase + OffsetItemIID);
                    if (iid > 0 && iid < 1_000_000_000)
                        return true;
                }
            }
        }
        return false;
    }

    private void SaveItemWrapperVtableCache()
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
            dict["itemWrapperVtable"] = $"0x{_itemWrapperVtable:X}";
            File.WriteAllText(cachePath, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[InventoryReader] Cached Item wrapper vtable 0x{_itemWrapperVtable:X}");
        }
        catch { }
    }

    /// <summary>
    /// Scans memory for Item wrapper instances, filters to IsEquipped=true, and reads each
    /// item's ItemAttributes dictionary to extract enchant power slots.
    /// </summary>
    public List<EquippedItemSnapshot>? ReadEquippedItems()
    {
        if (_itemWrapperVtable == 0)
        {
            Console.Error.WriteLine("[InventoryReader] ReadEquippedItems: Item wrapper vtable not discovered.");
            return null;
        }

        var results = new List<EquippedItemSnapshot>();
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
                if (chunk == null || chunk.Length < 0x40) continue;

                int end = chunk.Length - 0x40;
                for (int i = 0; i <= end; i += 8)
                {
                    if (BitConverter.ToUInt64(chunk, i) != _itemWrapperVtable) continue;

                    ulong itemBase = region.BaseAddress + chunkBase + (ulong)i;
                    if (!seen.Add(itemBase)) continue;

                    // Validate: sync==0, Info points to ItemInfo, IID > 0
                    if (_memory.ReadPointer(itemBase + 0x08) != 0) continue;
                    ulong infoPtr = _memory.ReadPointer(itemBase + OffsetItemInfo);
                    if (!IsValidPointer(infoPtr)) continue;
                    if (_memory.ReadPointer(infoPtr) != _itemInfoVtable) continue;

                    int iid = _memory.ReadInt32(itemBase + OffsetItemIID);
                    if (iid <= 0 || iid > 1_000_000_000) continue;

                    byte isEquipped = _memory.ReadBytes(itemBase + OffsetItemIsEquipped, 1)?[0] ?? 0;
                    if (isEquipped != 1) continue;

                    // Read ItemInfo.StaticName for display name
                    string staticName = "?";
                    ulong namePtr = _memory.ReadPointer(infoPtr + OffsetItemInfoStaticName);
                    if (IsValidPointer(namePtr))
                        staticName = _memory.ReadMonoString(namePtr, maxLength: 128) ?? "?";
                    int typeId = _memory.ReadInt32(infoPtr + OffsetItemInfoTypeId);
                    string internalName = _itemDataByTypeId.GetValueOrDefault(typeId, staticName);

                    // Read ItemAttributes Dict<int, long>
                    ulong dictPtr = _memory.ReadPointer(itemBase + OffsetItemAttributes);
                    var attrs = IsValidPointer(dictPtr)
                        ? ReadInt64Dictionary(dictPtr)
                        : new Dictionary<int, long>();

                    var powerSlots = new long[10];
                    for (int slot = 0; slot < 10; slot++)
                        powerSlots[slot] = attrs.GetValueOrDefault(AttrPower1 + slot, 0L);

                    results.Add(new EquippedItemSnapshot
                    {
                        ItemAddress     = itemBase,
                        ItemInfoAddress = infoPtr,
                        IID             = iid,
                        InternalName    = internalName,
                        TypeId          = typeId,
                        TsysLevel       = (int)attrs.GetValueOrDefault(AttrTsysLevel, 0L),
                        Rarity          = (int)attrs.GetValueOrDefault(AttrRarity, 0L),
                        AugmentId       = attrs.GetValueOrDefault(AttrAugmentId, 0L),
                        PowerSlots      = powerSlots,
                        RawAttributes   = attrs
                    });
                }
            }
        }

        Console.WriteLine($"[InventoryReader] ReadEquippedItems: {results.Count} equipped items.");
        return results.OrderBy(r => r.InternalName).ToList();
    }

    /// <summary>
    /// Structural scan for Item wrapper objects — does NOT require knowing the Item vtable.
    ///
    /// Strategy: build a HashSet of the ~10k ItemInfo object addresses we already found.
    /// Scan all memory: at each 8-byte aligned position i, read the 8 bytes at i+0x10 and
    /// check if they match any known ItemInfo address. If so, this is very likely an Item
    /// wrapper (Info field). Validate with sync==0, IID sanity, IsEquipped byte.
    ///
    /// Probability of random false match: 10721 / 2^64 ≈ 0 — essentially no false positives.
    /// All filtering happens inside the chunk (no extra syscalls except for confirmed matches).
    /// </summary>
    public List<EquippedItemSnapshot>? ReadEquippedItemsStructural(List<InventoryItemSnapshot> allItems)
    {
        if (_itemInfoVtable == 0)
        {
            Console.Error.WriteLine("[InventoryReader] ReadEquippedItemsStructural: ItemInfo vtable not discovered.");
            return null;
        }

        // Build set of all known ItemInfo object addresses for O(1) lookup in hot loop
        var itemInfoSet = new HashSet<ulong>(allItems.Select(i => i.ObjectAddress));
        Console.WriteLine($"[InventoryReader] Structural scan: {itemInfoSet.Count} ItemInfo addresses loaded, scanning...");

        var results = new List<EquippedItemSnapshot>();
        var allItems_ = new List<EquippedItemSnapshot>(); // all item wrappers found (not just equipped)
        var seen = new HashSet<ulong>();
        var vtableVotes = new Dictionary<ulong, int>();
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
                if (chunk == null || chunk.Length < 0x40) continue;

                int end = chunk.Length - 0x40;
                for (int i = 0; i <= end; i += 8)
                {
                    // Hot-path filter: check bytes at i+0x10 (Info field) against ItemInfo address set.
                    // BitConverter.ToUInt64 from chunk: no syscall.
                    ulong infoPtr = BitConverter.ToUInt64(chunk, i + 0x10);
                    if (!itemInfoSet.Contains(infoPtr)) continue;

                    // Candidate found — validate the rest from the chunk (still no syscall)
                    ulong objBase = region.BaseAddress + chunkBase + (ulong)i;
                    if (!seen.Add(objBase)) continue;

                    // sync at +0x08 must be 0
                    ulong sync = BitConverter.ToUInt64(chunk, i + 0x08);
                    if (sync != 0) continue;

                    // vtable at +0x00 must be a valid pointer
                    ulong vtable = BitConverter.ToUInt64(chunk, i);
                    if (!IsValidPointer(vtable)) continue;

                    // IID at +0x18 must be a positive int
                    int iid = BitConverter.ToInt32(chunk, i + 0x18);
                    if (iid <= 0 || iid > 1_000_000_000) continue;

                    // IsEquipped byte at +0x20 must be 0 or 1
                    byte isEq = chunk[i + 0x20];
                    if (isEq > 1) continue;

                    // Vote on Item wrapper vtable (for cache)
                    vtableVotes.TryGetValue(vtable, out int v);
                    vtableVotes[vtable] = v + 1;

                    if (isEq != 1) continue; // only process equipped items below

                    // Read display name and TypeID from ItemInfo (already validated infoPtr via set)
                    string staticName = "?";
                    ulong namePtr = _memory.ReadPointer(infoPtr + OffsetItemInfoStaticName);
                    if (IsValidPointer(namePtr))
                        staticName = _memory.ReadMonoString(namePtr, maxLength: 128) ?? "?";
                    int typeId = _memory.ReadInt32(infoPtr + OffsetItemInfoTypeId);
                    string internalName = _itemDataByTypeId.GetValueOrDefault(typeId, staticName);

                    // Read ItemAttributes Dict<int, long>
                    ulong dictPtr = _memory.ReadPointer(objBase + OffsetItemAttributes);
                    var attrs = IsValidPointer(dictPtr)
                        ? ReadInt64Dictionary(dictPtr)
                        : new Dictionary<int, long>();

                    var powerSlots = new long[10];
                    for (int slot = 0; slot < 10; slot++)
                        powerSlots[slot] = attrs.GetValueOrDefault(AttrPower1 + slot, 0L);

                    results.Add(new EquippedItemSnapshot
                    {
                        ItemAddress     = objBase,
                        ItemInfoAddress = infoPtr,
                        IID             = iid,
                        InternalName    = internalName,
                        TypeId          = typeId,
                        TsysLevel       = (int)attrs.GetValueOrDefault(AttrTsysLevel, 0L),
                        Rarity          = (int)attrs.GetValueOrDefault(AttrRarity, 0L),
                        AugmentId       = attrs.GetValueOrDefault(AttrAugmentId, 0L),
                        PowerSlots      = powerSlots,
                        RawAttributes   = attrs
                    });
                }
            }
        }

        // Cache the winning Item wrapper vtable for future runs
        if (vtableVotes.Count > 0)
        {
            var best = vtableVotes.OrderByDescending(kv => kv.Value).First();
            Console.WriteLine($"[InventoryReader] Item wrapper vtable (structural): 0x{best.Key:X} ({best.Value} votes)");
            _itemWrapperVtable = best.Key;
            SaveItemWrapperVtableCache();
        }

        Console.WriteLine($"[InventoryReader] Structural scan complete: {results.Count} equipped items.");
        return results.OrderBy(r => r.InternalName).ToList();
    }

    /// <summary>
    /// Reads a Mono/IL2CPP Dictionary&lt;int, long&gt; (used for ItemAttributes).
    ///
    /// IL2CPP Dictionary&lt;TKey,TValue&gt; field layout (after 0x10 Il2CppObject header):
    ///   +0x10  Entry[] _entries   ← pointer to array object
    ///   +0x18  int[] _buckets
    ///   +0x20  int _count
    ///   +0x24  int _freeList
    ///   +0x28  int _freeCount
    ///   +0x2C  int _version
    ///
    /// The entries array is an Il2CppArray (vtable8 + sync8 + maxLen8 = 0x18 header),
    /// so actual Entry data starts at entriesArrayPtr + 0x18 (sometimes +0x20 if bounds ptr present).
    ///
    /// Entry struct for Dictionary&lt;int, long&gt;:
    ///   +0x00  int hashCode  (negative = free slot)
    ///   +0x04  int next
    ///   +0x08  int key       (ItemAttribute enum value)
    ///   +0x0C  int _pad      (alignment for 8-byte value)
    ///   +0x10  long value
    ///   = 0x18 (24 bytes) per entry
    /// </summary>
    private Dictionary<int, long> ReadInt64Dictionary(ulong dictAddr)
    {
        var result = new Dictionary<int, long>();
        byte[]? dictBytes = _memory.ReadBytes(dictAddr, 0x48);
        if (dictBytes == null) return result;

        // count is at +0x20 in all known Unity IL2CPP versions
        int count = BitConverter.ToInt32(dictBytes, 0x20);
        if (count < 1 || count > 10000) return result;

        Dictionary<int, long>? best = null;

        // Try entries array pointer at +0x10 (primary) and +0x18 (fallback — some versions swap order)
        foreach (int dictFieldOff in new[] { 0x10, 0x18 })
        {
            if (dictFieldOff + 8 > dictBytes.Length) continue;
            ulong entriesArrayPtr = BitConverter.ToUInt64(dictBytes, dictFieldOff);
            if (!IsValidPointer(entriesArrayPtr)) continue;

            // Entries data starts after the Il2CppArray header.
            // Unity 6 IL2CPP: vtable(8)+sync(8)+bounds(8)+maxLen(8) = 0x20 header → entries at +0x20.
            // Try +0x20 first (correct for Unity 6), +0x18 as fallback.
            // IMPORTANT: try +0x20 before +0x18. If +0x18 is tried first, it reads 8 bytes early:
            // the "key" lands on hashCode (=key for small ints) and the "value" lands on the actual
            // key (as int64 with zero pad), making every value equal its key — a false win.
            foreach (int arrayDataOff in new[] { 0x20, 0x18 })
            {
                ulong dataStart = entriesArrayPtr + (ulong)arrayDataOff;
                int readSize = Math.Min(count * 0x20 + 0x100, 131072);
                byte[]? eb = _memory.ReadBytes(dataStart, readSize);
                if (eb == null) continue;

                // Primary: stride 0x18, key@+8, value@+16
                // Fallback: stride 0x20, key@+8, value@+16 or +24
                foreach (var (stride, keyOff, valOff) in new (int, int, int)[]
                {
                    (0x18, 0x08, 0x10),
                    (0x20, 0x08, 0x10),
                    (0x20, 0x08, 0x18),
                    (0x18, 0x00, 0x08),  // alternate: key first
                    (0x40, 0x08, 0x10),
                    (0x40, 0x08, 0x20),
                })
                {
                    if (stride < keyOff + 4 || stride < valOff + 8) continue;
                    int maxEntries = Math.Min(count * 3, eb.Length / stride);
                    if (maxEntries == 0) continue;

                    var candidate = new Dictionary<int, long>(count);
                    for (int i = 0; i < maxEntries; i++)
                    {
                        int entryBase = i * stride;
                        if (entryBase + Math.Max(keyOff + 4, valOff + 8) > eb.Length) break;

                        // Slot 0 of entries array may have hashCode=-1 (free) — skip
                        int hashCode = BitConverter.ToInt32(eb, entryBase);
                        if (hashCode < 0) continue;  // free / deleted slot

                        int key = BitConverter.ToInt32(eb, entryBase + keyOff);
                        // ItemAttribute keys: valid range ~1..2500 (see GorgonCore/ItemAttribute.cs)
                        if (key < 1 || key > 5000) continue;

                        long value = BitConverter.ToInt64(eb, entryBase + valOff);
                        candidate.TryAdd(key, value);
                        if (candidate.Count >= count) break;
                    }

                    if (best == null || candidate.Count > best.Count)
                        best = candidate;
                }
            }
        }

        // Uncomment for debugging:
        // if (best != null && best.Count > 0)
        //     Console.WriteLine($"[InventoryReader] ReadInt64Dictionary @ 0x{dictAddr:X}: found {best.Count}/{count} entries");

        return best ?? result;
    }

    /// <summary>
    /// Loads tsysclientinfo.json to enable power combined ID decoding.
    /// Power combined ID encoding: combinedId = powerIndex * 1000 + tierIndex
    /// </summary>
    public void LoadTsysData(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            // Check current working directory first (works when run from repo root)
            string cwdCand = Path.Combine(Environment.CurrentDirectory, "tsysclientinfo.json");
            if (File.Exists(cwdCand)) { jsonPath = cwdCand; goto found; }

            // Walk up from AppContext.BaseDirectory (works for debug builds)
            string? dir = AppContext.BaseDirectory;
            for (int i = 0; i < 8 && dir != null; i++)
            {
                string cand = Path.Combine(dir, "tsysclientinfo.json");
                if (File.Exists(cand)) { jsonPath = cand; goto found; }
                dir = Path.GetDirectoryName(dir);
            }
        }
        found:
        if (!File.Exists(jsonPath))
        {
            Console.Error.WriteLine($"[InventoryReader] tsysclientinfo.json not found: {jsonPath}");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            int loaded = 0;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                // Keys are "power_N"
                if (!prop.Name.StartsWith("power_")) continue;
                if (!int.TryParse(prop.Name[6..], out int powerIdx)) continue;

                string internalName = "";
                if (prop.Value.TryGetProperty("InternalName", out var inEl))
                    internalName = inEl.GetString() ?? "";

                var tiers = new Dictionary<int, string[]>();
                if (prop.Value.TryGetProperty("Tiers", out var tiersEl))
                {
                    foreach (var tier in tiersEl.EnumerateObject())
                    {
                        // Keys are "id_N"
                        if (!tier.Name.StartsWith("id_")) continue;
                        if (!int.TryParse(tier.Name[3..], out int tierIdx)) continue;

                        string[] descs = Array.Empty<string>();
                        if (tier.Value.TryGetProperty("EffectDescs", out var descEl) &&
                            descEl.ValueKind == JsonValueKind.Array)
                        {
                            descs = descEl.EnumerateArray()
                                .Select(e => e.GetString() ?? "")
                                .Where(s => s.Length > 0)
                                .ToArray();
                        }
                        tiers[tierIdx] = descs;
                    }
                }

                _tsysPowers[powerIdx] = (internalName, tiers);
                loaded++;
            }
            Console.WriteLine($"[InventoryReader] Loaded {loaded} tsys powers from {jsonPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[InventoryReader] Failed to parse tsysclientinfo.json: {ex.Message}");
        }
    }

    /// <summary>
    /// Decodes a power combined ID (stored in POWER1–POWER10 attributes).
    /// Returns (InternalName, EffectDescs) for the matched power+tier, or ("?", []) if not found.
    /// Encoding: combinedId = powerIndex * 1000 + tierIndex
    /// </summary>
    public (string InternalName, string[] EffectDescs) DecodePower(long combinedId)
    {
        if (combinedId <= 0 || _tsysPowers.Count == 0) return ("?", Array.Empty<string>());
        int powerIdx = (int)(combinedId / 1000);
        int tierIdx  = (int)(combinedId % 1000);
        if (!_tsysPowers.TryGetValue(powerIdx, out var power)) return ($"power_{powerIdx}[?]", Array.Empty<string>());
        power.Tiers.TryGetValue(tierIdx, out string[]? descs);
        return (power.InternalName, descs ?? Array.Empty<string>());
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

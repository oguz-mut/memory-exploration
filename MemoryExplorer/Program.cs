using System.Diagnostics;
using MemoryLib;
using MemoryLib.Models;
using MemoryLib.Readers;

Console.WriteLine("=== MemoryLib Reader Test ===");

var pid = ProcessMemory.FindGameProcess();
if (pid == null) { Console.WriteLine("Game not found."); return; }

using var memory = ProcessMemory.Open(pid.Value);
var scanner = new MemoryRegionScanner(memory);
Console.WriteLine($"PID {pid.Value}, {scanner.GetGameRegions().Count} regions");
Console.WriteLine();

var sw = Stopwatch.StartNew();

// --- InventoryReader setup ---
Console.WriteLine("=== InventoryReader ===");
sw.Restart();
var invReader = new InventoryReader(memory, scanner);
string itemsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "items.json");
if (File.Exists(itemsPath)) invReader.LoadItemData(itemsPath);
string tsysPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "tsysclientinfo.json");
invReader.LoadTsysData(tsysPath);
bool invOk = invReader.AutoDiscover();
sw.Stop();
Console.WriteLine($"AutoDiscover: {invOk} ({sw.Elapsed.TotalSeconds:F1}s)");

List<InventoryItemSnapshot>? itemInfos = null;
if (invOk)
{
    itemInfos = invReader.ReadAllItems();
    Console.WriteLine($"ItemInfo definitions: {itemInfos?.Count ?? 0}");
}
Console.WriteLine();

// --- Option A: structural scan for all Item wrappers ---
Console.WriteLine("=== Inventory (Option A — structural scan) ===");
sw.Restart();
List<InventoryItemSnapshot>? bagA = (invOk && itemInfos != null)
    ? invReader.ReadAllInventoryItems(itemInfos)
    : null;
sw.Stop();
Console.WriteLine($"ReadAllInventoryItems: {bagA?.Count ?? -1} items ({sw.Elapsed.TotalSeconds:F1}s)");
if (bagA != null && bagA.Count > 0)
    PrintInventory(bagA);
Console.WriteLine();

// --- Option B: exact list via UIInventoryController backing array ---
Console.WriteLine("=== Inventory (Option B — controller list) ===");
sw.Restart();
List<InventoryItemSnapshot>? bagB = (bagA != null)
    ? invReader.ReadInventoryViaControllerList(bagA)
    : null;
sw.Stop();
Console.WriteLine($"ReadInventoryViaControllerList: {bagB?.Count ?? -1} items ({sw.Elapsed.TotalSeconds:F1}s)");
if (bagB != null && bagB.Count > 0)
    PrintInventory(bagB);
Console.WriteLine();

// --- Equipped items with enchants ---
Console.WriteLine("=== Equipped Items (enchants) ===");
sw.Restart();
var equipped = (invOk && itemInfos != null) ? invReader.ReadEquippedItemsStructural(itemInfos) : null;
sw.Stop();
Console.WriteLine($"ReadEquippedItemsStructural: {equipped?.Count ?? -1} items ({sw.Elapsed.TotalSeconds:F1}s)");
if (equipped != null && equipped.Count > 0)
{
    foreach (var eq in equipped)
    {
        Console.WriteLine($"\n  [{eq.IID}] {eq.InternalName}  (TypeID={eq.TypeId} tsys={eq.TsysLevel} rarity={eq.Rarity})");
        int powerCount = eq.PowerSlots.Count(p => p != 0);
        if (powerCount > 0)
        {
            Console.WriteLine($"      enchants ({powerCount}):");
            for (int slot = 0; slot < 10; slot++)
            {
                long v = eq.PowerSlots[slot];
                if (v == 0) continue;
                var (name, descs) = invReader.DecodePower(v);
                string desc = descs.Length > 0 ? descs[0] : "(no desc)";
                Console.WriteLine($"        POWER{slot + 1,-2} [{name}]  {desc}");
            }
        }
    }
}
Console.WriteLine();
Console.WriteLine("=== Done ===");

// ── helpers ──────────────────────────────────────────────────────────────────

static void PrintInventory(List<InventoryItemSnapshot> items)
{
    int equipped  = items.Count(i => i.IsEquipped);
    int unequipped = items.Count - equipped;
    Console.WriteLine($"  {unequipped} bag items, {equipped} equipped");

    // Group by folder index for bag items
    var byFolder = items.Where(i => !i.IsEquipped)
                        .GroupBy(i => i.FolderIdx)
                        .OrderBy(g => g.Key);
    foreach (var folder in byFolder)
    {
        Console.WriteLine($"  [Folder {folder.Key}] ({folder.Count()} items)");
        foreach (var it in folder.OrderBy(i => i.InternalName).Take(20))
            Console.WriteLine($"    {it.InternalName,-35} x{it.StackCount,4}  iid={it.IID}");
        if (folder.Count() > 20)
            Console.WriteLine($"    ... +{folder.Count() - 20} more");
    }
}

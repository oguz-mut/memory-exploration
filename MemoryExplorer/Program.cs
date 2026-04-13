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
// SkillReader / CombatantReader / QuestReader skipped for equipped-items focus run.

// --- InventoryReader ---
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
List<InventoryItemSnapshot>? items = null;
if (invOk)
{
    items = invReader.ReadAllItems();
    if (items != null)
    {
        Console.WriteLine($"Found {items.Count} items:");
        foreach (var i in items.Take(15))
            Console.WriteLine($"  {i.InternalName,-30} code={i.ItemCode,6} x{i.StackCount}");
        if (items.Count > 15) Console.WriteLine($"  ... +{items.Count - 15} more");
    }
}
Console.WriteLine();

// --- Equipped Items (structural scan — no Item wrapper vtable required) ---
Console.WriteLine("=== EquippedItems ===");
sw.Restart();
var equipped = (invOk && items != null) ? invReader.ReadEquippedItemsStructural(items) : null;
sw.Stop();
Console.WriteLine($"ReadEquippedItemsStructural: {equipped?.Count ?? -1} items ({sw.Elapsed.TotalSeconds:F1}s)");
if (equipped != null && equipped.Count > 0)
{
    foreach (var eq in equipped)
    {
        Console.WriteLine($"\n  [{eq.IID}] {eq.InternalName}  (TypeID={eq.TypeId})");
        Console.WriteLine($"      addr=0x{eq.ItemAddress:X}  tsysLevel={eq.TsysLevel}  rarity={eq.Rarity}  augmentId=0x{eq.AugmentId:X}");
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
        if (eq.RawAttributes.Count > 0)
        {
            Console.WriteLine($"      raw attrs ({eq.RawAttributes.Count}):");
            foreach (var kv in eq.RawAttributes.OrderBy(x => x.Key).Take(20))
                Console.WriteLine($"        [{kv.Key,4}] = 0x{kv.Value:X16} ({kv.Value})");
            if (eq.RawAttributes.Count > 20)
                Console.WriteLine($"        ... +{eq.RawAttributes.Count - 20} more");
        }
    }
}
else
{
    Console.WriteLine("  No equipped items found (are you logged in with a character?)");
}
Console.WriteLine();

Console.WriteLine("\n=== Done ===");

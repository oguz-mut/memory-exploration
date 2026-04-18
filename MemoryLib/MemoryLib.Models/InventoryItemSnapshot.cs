namespace MemoryLib.Models;

public class InventoryItemSnapshot
{
    public ulong ObjectAddress { get; init; }  // ItemInfo addr (ReadAllItems) or Item wrapper addr (ReadAllInventoryItems)
    public int IID { get; init; }              // Item instance ID (0 for ItemInfo-only reads)
    public int ItemCode { get; init; }
    public int StackCount { get; init; }
    public string InternalName { get; init; } = "";
    public bool IsEquipped { get; init; }
    public byte FolderIdx { get; init; }
}

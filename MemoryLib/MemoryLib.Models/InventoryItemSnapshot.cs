namespace MemoryLib.Models;

public class InventoryItemSnapshot
{
    public ulong ObjectAddress { get; init; }
    public int ItemCode { get; init; }
    public int StackCount { get; init; }
    public string InternalName { get; init; } = "";
    public bool IsEquipped { get; init; }
    public byte FolderIdx { get; init; }
}

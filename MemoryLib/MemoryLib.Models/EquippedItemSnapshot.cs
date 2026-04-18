namespace MemoryLib.Models;

public class EquippedItemSnapshot
{
    public ulong ItemAddress { get; init; }
    public ulong ItemInfoAddress { get; init; }
    public int IID { get; init; }
    public string InternalName { get; init; } = "";
    public int TypeId { get; init; }
    public int TsysLevel { get; init; }
    public int Rarity { get; init; }
    public long AugmentId { get; init; }
    public long[] PowerSlots { get; init; } = new long[10];
    public Dictionary<int, long> RawAttributes { get; init; } = new();
}

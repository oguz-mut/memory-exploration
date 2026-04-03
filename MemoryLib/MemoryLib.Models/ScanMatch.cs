namespace MemoryLib.Models;

public class ScanMatch
{
    public ulong Address { get; init; }
    public MemoryRegion Region { get; init; } = null!;
    public ulong RegionOffset { get; init; }
}

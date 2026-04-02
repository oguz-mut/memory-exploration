namespace MemoryLib.Models;

public class EffectSnapshot
{
    public ulong ObjectAddress { get; init; }
    public int EffectIID { get; init; }
    public string Name { get; init; } = "";
    public float Duration { get; init; }
    public float RemainingTime { get; init; }
    public int StackCount { get; init; }
    public bool IsDebuff { get; init; }
}

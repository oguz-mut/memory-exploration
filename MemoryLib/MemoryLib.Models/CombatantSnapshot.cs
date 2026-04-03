namespace MemoryLib.Models;

public class CombatantSnapshot
{
    public ulong ObjectAddress { get; init; }
    public bool IsDead { get; init; }
    public bool IsLocalPlayer { get; init; }
    public Dictionary<int, double> Attributes { get; init; } = new();
    public double Health { get; init; }
    public double MaxHealth { get; init; }
    public double Power { get; init; }
    public double MaxPower { get; init; }
    public double Armor { get; init; }
    public double MaxArmor { get; init; }
}

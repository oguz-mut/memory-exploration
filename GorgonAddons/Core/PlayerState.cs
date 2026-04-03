namespace GorgonAddons.Core;
using MemoryLib.Models;

public class PlayerState
{
    public double HealthPercent { get; init; }
    public double PowerPercent { get; init; }
    public double ArmorPercent { get; init; }
    public double Health { get; init; }
    public double MaxHealth { get; init; }
    public double Power { get; init; }
    public double MaxPower { get; init; }
    public double Armor { get; init; }
    public double MaxArmor { get; init; }
    public bool IsDead { get; init; }
    public bool InCombat { get; init; }

    public static PlayerState FromCombatant(CombatantSnapshot? snapshot)
    {
        if (snapshot == null) return new PlayerState();
        var maxHp = snapshot.MaxHealth;
        var maxPow = snapshot.MaxPower;
        var maxArm = snapshot.MaxArmor;
        return new PlayerState
        {
            Health = snapshot.Health, MaxHealth = maxHp,
            HealthPercent = maxHp > 0 ? snapshot.Health / maxHp * 100 : 0,
            Power = snapshot.Power, MaxPower = maxPow,
            PowerPercent = maxPow > 0 ? snapshot.Power / maxPow * 100 : 0,
            Armor = snapshot.Armor, MaxArmor = maxArm,
            ArmorPercent = maxArm > 0 ? snapshot.Armor / maxArm * 100 : 0,
            IsDead = snapshot.IsDead,
            InCombat = false, // TODO: detect from attributes when combat mode attribute ID is known
        };
    }
}

namespace GorgonAddons.Macros;

public interface IConditionEvaluator
{
    bool IsInCombat();
    bool IsDead();
    double GetHealthPercent();
    double GetPowerPercent();
    bool HasBuff(string name);
    bool IsModifierHeld(string modifier);
    double GetSkillLevel(string skillName);
}

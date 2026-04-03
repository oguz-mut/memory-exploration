namespace GorgonAddons.Core;
using MemoryLib.Models;

public record SkillLevelUpEvent(SkillSnapshot Previous, SkillSnapshot Current);
public record ItemChangedEvent(InventoryItemSnapshot Item);
public record EffectEvent(EffectSnapshot Effect);
public record DeathEvent(bool IsDead);
public record CombatantChangedEvent(CombatantSnapshot Combatant);

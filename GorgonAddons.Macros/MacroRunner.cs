namespace GorgonAddons.Macros;

public class MacroRunner
{
    private readonly IConditionEvaluator _conditions;
    private readonly IActionExecutor _actions;
    private readonly Dictionary<string, int> _sequenceIndices = new();

    public MacroRunner(IConditionEvaluator conditions, IActionExecutor actions)
    {
        _conditions = conditions;
        _actions = actions;
    }

    public async Task Execute(MacroDefinition macro, CancellationToken ct = default)
    {
        if (macro.Lines.Count == 0) return;

        if (macro.IsSequence)
        {
            var idx = _sequenceIndices.GetValueOrDefault(macro.Name, 0);
            idx = Math.Clamp(idx, 0, macro.Lines.Count - 1);
            await ExecuteAction(macro.Lines[idx].Action, ct);
            _sequenceIndices[macro.Name] = (idx + 1) % macro.Lines.Count;
        }
        else
        {
            foreach (var line in macro.Lines)
            {
                if (line.Conditions.All(EvaluateCondition))
                {
                    await ExecuteAction(line.Action, ct);
                    return;
                }
            }
        }
    }

    public bool EvaluateCondition(MacroCondition condition)
    {
        bool result = condition.Type switch
        {
            ConditionType.Combat => _conditions.IsInCombat(),
            ConditionType.Dead => _conditions.IsDead(),
            ConditionType.Health => CompareNumeric(_conditions.GetHealthPercent(), condition),
            ConditionType.Power => CompareNumeric(_conditions.GetPowerPercent(), condition),
            ConditionType.Buff => _conditions.HasBuff(condition.StringArg),
            ConditionType.ModShift => _conditions.IsModifierHeld("shift"),
            ConditionType.ModCtrl => _conditions.IsModifierHeld("ctrl"),
            ConditionType.ModAlt => _conditions.IsModifierHeld("alt"),
            ConditionType.Skill => CompareNumeric(_conditions.GetSkillLevel(condition.StringArg), condition),
            _ => false
        };

        return condition.Negated ? !result : result;
    }

    private static bool CompareNumeric(double actual, MacroCondition condition) => condition.Comparison switch
    {
        ComparisonOp.LessThan => actual < condition.NumericArg,
        ComparisonOp.GreaterThan => actual > condition.NumericArg,
        _ => false
    };

    public async Task ExecuteAction(MacroAction action, CancellationToken ct = default)
    {
        switch (action.Type)
        {
            case ActionType.PressKey:
                _actions.PressKey(action.Argument);
                break;
            case ActionType.Target:
                await _actions.SendTarget(action.Argument, ct);
                break;
            case ActionType.Say:
                await _actions.SendSay(action.Argument, ct);
                break;
            case ActionType.Command:
                await _actions.SendCommand(action.Argument, ct);
                break;
            case ActionType.Wait:
                await _actions.Wait(action.NumericArgument, ct);
                break;
            case ActionType.Click:
                _actions.Click(action.NumericArgument, action.NumericArgument2);
                break;
            case ActionType.Notify:
                _actions.ShowNotification(action.Argument);
                break;
        }
    }

    public void ResetSequence(string macroName) => _sequenceIndices.Remove(macroName);

    public void ResetAllSequences() => _sequenceIndices.Clear();
}

namespace GorgonAddons.Macros;

public class MacroDefinition
{
    public string Name { get; init; } = "";
    public string Bind { get; init; } = "";
    public string FilePath { get; init; } = "";
    public bool IsSequence { get; init; }
    public List<MacroLine> Lines { get; init; } = new();
}

public class MacroLine
{
    public List<MacroCondition> Conditions { get; init; } = new();
    public MacroAction Action { get; init; } = new();
}

public class MacroCondition
{
    public ConditionType Type { get; init; }
    public bool Negated { get; init; }
    public string StringArg { get; init; } = "";
    public double NumericArg { get; init; }
    public ComparisonOp Comparison { get; init; }
}

public enum ConditionType { Combat, Dead, Health, Power, Buff, ModShift, ModCtrl, ModAlt, Skill }
public enum ComparisonOp { None, LessThan, GreaterThan }

public class MacroAction
{
    public ActionType Type { get; init; }
    public string Argument { get; init; } = "";
    public int NumericArgument { get; init; }
    public int NumericArgument2 { get; init; }
}

public enum ActionType { None, PressKey, Target, Say, Command, Wait, Click, Notify }

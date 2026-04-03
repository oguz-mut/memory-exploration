using System.Text.RegularExpressions;

namespace GorgonAddons.Macros;

public static class MacroParser
{
    private static readonly Regex ConditionBlockRegex = new(@"\[([^\]]+)\]", RegexOptions.Compiled);

    public static MacroDefinition Parse(string content, string filePath = "")
    {
        var parts = content.Split("\n---\n", 2, StringSplitOptions.None);
        if (parts.Length < 2)
            parts = content.Split("\r\n---\r\n", 2, StringSplitOptions.None);

        string headerText = parts.Length == 2 ? parts[0] : "";
        string bodyText = parts.Length == 2 ? parts[1] : parts[0];

        var (name, bind) = ParseHeader(headerText);
        var (isSequence, lines) = ParseBody(bodyText);

        return new MacroDefinition
        {
            Name = name,
            Bind = bind,
            FilePath = filePath,
            IsSequence = isSequence,
            Lines = lines
        };
    }

    private static (string name, string bind) ParseHeader(string header)
    {
        string name = "";
        string bind = "";

        foreach (var line in header.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                name = trimmed["name:".Length..].Trim();
            else if (trimmed.StartsWith("bind:", StringComparison.OrdinalIgnoreCase))
                bind = trimmed["bind:".Length..].Trim();
        }

        return (name, bind);
    }

    private static (bool isSequence, List<MacroLine> lines) ParseBody(string body)
    {
        bool isSequence = false;
        var lines = new List<MacroLine>();

        foreach (var rawLine in body.Split('\n'))
        {
            var trimmed = rawLine.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                if (trimmed.Equals("#sequence", StringComparison.OrdinalIgnoreCase))
                    isSequence = true;
                continue;
            }

            var line = ParseLine(trimmed);
            if (line is not null)
                lines.Add(line);
        }

        return (isSequence, lines);
    }

    private static MacroLine? ParseLine(string line)
    {
        var conditions = new List<MacroCondition>();

        // Extract all [condition] blocks from the start
        int pos = 0;
        while (pos < line.Length && line[pos] == '[')
        {
            int close = line.IndexOf(']', pos);
            if (close < 0) break;

            var condText = line[(pos + 1)..close];
            var cond = ParseCondition(condText);
            if (cond is not null)
                conditions.Add(cond);

            pos = close + 1;
        }

        // Remainder is the action
        var actionText = line[pos..].Trim();
        if (actionText.Length == 0) return null;

        var action = ParseAction(actionText);
        if (action is null) return null;

        return new MacroLine { Conditions = conditions, Action = action };
    }

    private static MacroCondition? ParseCondition(string text)
    {
        text = text.Trim();
        bool negated = false;

        if (text.StartsWith('!'))
        {
            negated = true;
            text = text[1..];
        }

        if (text.Equals("combat", StringComparison.OrdinalIgnoreCase))
            return new MacroCondition { Type = ConditionType.Combat, Negated = negated };

        if (text.Equals("dead", StringComparison.OrdinalIgnoreCase))
            return new MacroCondition { Type = ConditionType.Dead, Negated = negated };

        if (text.StartsWith("mod:", StringComparison.OrdinalIgnoreCase))
        {
            var mod = text["mod:".Length..].Trim().ToLowerInvariant();
            var type = mod switch
            {
                "shift" => ConditionType.ModShift,
                "ctrl" => ConditionType.ModCtrl,
                "alt" => ConditionType.ModAlt,
                _ => (ConditionType?)null
            };
            if (type is null) return null;
            return new MacroCondition { Type = type.Value, Negated = negated };
        }

        if (text.StartsWith("buff:", StringComparison.OrdinalIgnoreCase))
        {
            var buffName = text["buff:".Length..].Trim();
            return new MacroCondition { Type = ConditionType.Buff, Negated = negated, StringArg = buffName };
        }

        if (text.StartsWith("health", StringComparison.OrdinalIgnoreCase))
        {
            var (op, value) = ParseNumericComparison(text["health".Length..]);
            if (op == ComparisonOp.None) return null;
            return new MacroCondition { Type = ConditionType.Health, Negated = negated, Comparison = op, NumericArg = value };
        }

        if (text.StartsWith("power", StringComparison.OrdinalIgnoreCase))
        {
            var (op, value) = ParseNumericComparison(text["power".Length..]);
            if (op == ComparisonOp.None) return null;
            return new MacroCondition { Type = ConditionType.Power, Negated = negated, Comparison = op, NumericArg = value };
        }

        if (text.StartsWith("skill:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = text["skill:".Length..].Trim();
            // rest = "Sword > 50" or "Sword < 50"
            int ltIdx = rest.IndexOf('<');
            int gtIdx = rest.IndexOf('>');
            int opIdx = ltIdx >= 0 && (gtIdx < 0 || ltIdx < gtIdx) ? ltIdx : gtIdx;
            if (opIdx < 0) return null;

            var skillName = rest[..opIdx].Trim();
            var opChar = rest[opIdx];
            var valueText = rest[(opIdx + 1)..].Trim();
            if (!double.TryParse(valueText, out var numericValue)) return null;

            var op = opChar == '<' ? ComparisonOp.LessThan : ComparisonOp.GreaterThan;
            return new MacroCondition { Type = ConditionType.Skill, Negated = negated, StringArg = skillName, Comparison = op, NumericArg = numericValue };
        }

        return null;
    }

    private static (ComparisonOp op, double value) ParseNumericComparison(string text)
    {
        text = text.Trim();
        ComparisonOp op;
        string valueText;

        if (text.StartsWith('<'))
        {
            op = ComparisonOp.LessThan;
            valueText = text[1..].Trim();
        }
        else if (text.StartsWith('>'))
        {
            op = ComparisonOp.GreaterThan;
            valueText = text[1..].Trim();
        }
        else
        {
            return (ComparisonOp.None, 0);
        }

        if (!double.TryParse(valueText, out var value))
            return (ComparisonOp.None, 0);

        return (op, value);
    }

    private static MacroAction? ParseAction(string text)
    {
        if (!text.StartsWith('/')) return null;

        var spaceIdx = text.IndexOf(' ');
        string verb = spaceIdx < 0 ? text[1..] : text[1..spaceIdx];
        string arg = spaceIdx < 0 ? "" : text[(spaceIdx + 1)..].Trim();

        return verb.ToLowerInvariant() switch
        {
            "press" => new MacroAction { Type = ActionType.PressKey, Argument = arg },
            "target" => new MacroAction { Type = ActionType.Target, Argument = arg },
            "say" => new MacroAction { Type = ActionType.Say, Argument = arg },
            "cmd" => new MacroAction { Type = ActionType.Command, Argument = arg },
            "wait" => int.TryParse(arg, out var ms)
                ? new MacroAction { Type = ActionType.Wait, NumericArgument = ms }
                : null,
            "click" => ParseClickAction(arg),
            "notify" => new MacroAction { Type = ActionType.Notify, Argument = arg },
            _ => null
        };
    }

    private static MacroAction? ParseClickAction(string arg)
    {
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[0], out var x) || !int.TryParse(parts[1], out var y)) return null;
        return new MacroAction { Type = ActionType.Click, NumericArgument = x, NumericArgument2 = y };
    }
}

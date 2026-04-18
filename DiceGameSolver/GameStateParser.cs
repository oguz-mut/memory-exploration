using System.Text.RegularExpressions;
using DiceGameSolver.Models;

namespace DiceGameSolver;

public class GameStateParser
{
    private const string Marker = "ProcessTalkScreen(-346,";
    private const string GameTitle = "\"Dice Game\",";
    private const string BodyStart = "\"Dice Game\", \"";

    public virtual GameState? Parse(string logLine)
    {
        if (!logLine.Contains(Marker) || !logLine.Contains(GameTitle))
            return null;

        int markerIdx = logLine.IndexOf(BodyStart);
        if (markerIdx < 0) return null;
        int bodyBegin = markerIdx + BodyStart.Length;

        // Scan forward tracking backslash escapes; stop at " followed by , "
        int pos = bodyBegin;
        while (pos < logLine.Length)
        {
            if (logLine[pos] == '\\')
            {
                pos += 2;
                continue;
            }
            if (pos + 3 < logLine.Length
                && logLine[pos] == '"'
                && logLine[pos + 1] == ','
                && logLine[pos + 2] == ' '
                && logLine[pos + 3] == '"')
            {
                break;
            }
            pos++;
        }

        string rawBodyEscaped = logLine.Substring(bodyBegin, pos - bodyBegin);

        // Parse codes array [a,b,c,]
        int bracketOpen = logLine.IndexOf('[', pos);
        int bracketClose = bracketOpen >= 0 ? logLine.IndexOf(']', bracketOpen) : -1;
        int[] codes = Array.Empty<int>();
        if (bracketOpen >= 0 && bracketClose > bracketOpen)
        {
            string codesStr = logLine.Substring(bracketOpen + 1, bracketClose - bracketOpen - 1);
            codes = codesStr.Split(',')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Select(int.Parse)
                .ToArray();
        }

        // Unescape: \" → ", \n → newline, \\ → \  (in that order)
        string body = rawBodyEscaped
            .Replace("\\\"", "\"")
            .Replace("\\n", "\n")
            .Replace("\\\\", "\\");

        var state = new GameState
        {
            RawBody = body,
            AvailableResponseCodes = codes,
        };

        // Phase detection: Result > CashOut > Playing > Intro > Inactive
        if (body.Contains("YOU WIN!") || body.Contains("YOU LOSE."))
        {
            state.Phase = GamePhase.Result;
            state.Won = body.Contains("YOU WIN!");

            var blueMatches = Regex.Matches(body,
                @"<color=#9999FF>.*?sprite=""Dice"" index=(\d)",
                RegexOptions.Singleline);
            state.BluesRolled = blueMatches
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToArray();
        }
        else if (body.Contains("You cash out and receive "))
        {
            state.Phase = GamePhase.CashOut;
        }
        else if (body.Contains("Your score from red dice is"))
        {
            state.Phase = GamePhase.Playing;

            var redMatches = Regex.Matches(body, @"sprite=""Dice"" index=(\d)");
            state.RedDice = redMatches.Take(3)
                .Select(m => int.Parse(m.Groups[1].Value))
                .ToArray();

            var dealerMatch = Regex.Match(body,
                @"Dealer.s score is <color=yellow>(\d+)</color>");
            if (dealerMatch.Success)
                state.DealerCurScore = int.Parse(dealerMatch.Groups[1].Value);

            state.Raised = body.Contains("(with a -1 Hubris Penalty)")
                        || body.Contains("Hubris Penalty: -1");
            state.LosingBanner = body.Contains("currently losing");
        }
        else if (body.Contains("Play game?"))
        {
            state.Phase = GamePhase.Intro;
        }
        else if (body.Contains("Looks like somebody plays dice games here"))
        {
            state.Phase = GamePhase.Inactive;
        }

        return state;
    }

    public static void SelfTest()
    {
        var parser = new GameStateParser();

        // Scenario 1: Playing, no raise, winning — red dice [6,5,1], dealer 9, codes [101,123,121,122]
        string line1 = @"ProcessTalkScreen(-346, ""Dice Game"", ""Your score from red dice is <sprite=\""Dice\"" index=6><sprite=\""Dice\"" index=5><sprite=\""Dice\"" index=1> = 12. Dealer's score is <color=yellow>9</color>."", ""Continue"", [101,123,121,122,], System.String[], 1, Generic)";
        var s1 = parser.Parse(line1)!;
        Assert(s1.Phase == GamePhase.Playing, "s1 Phase");
        Assert(s1.RedDice.SequenceEqual(new[] { 6, 5, 1 }), "s1 RedDice");
        Assert(s1.DealerCurScore == 9, "s1 DealerCurScore");
        Assert(!s1.Raised, "s1 Raised");
        Assert(!s1.LosingBanner, "s1 LosingBanner");
        Assert(s1.AvailableResponseCodes.SequenceEqual(new[] { 101, 123, 121, 122 }), "s1 Codes");
        Console.WriteLine("[parser selftest] scenario 1 passed: Playing no raise winning");

        // Scenario 2: Playing, raised — (with a -1 Hubris Penalty), codes [123,121,122]
        string line2 = @"ProcessTalkScreen(-346, ""Dice Game"", ""Your score from red dice is <sprite=\""Dice\"" index=3><sprite=\""Dice\"" index=4><sprite=\""Dice\"" index=2> = 9. Dealer's score is <color=yellow>7</color>. (with a -1 Hubris Penalty)"", ""Continue"", [123,121,122,], System.String[], 1, Generic)";
        var s2 = parser.Parse(line2)!;
        Assert(s2.Raised, "s2 Raised");
        Console.WriteLine("[parser selftest] scenario 2 passed: Playing raised");

        // Scenario 3: Result WIN — inverted blue index=4, fresh blue index=1, codes [111,112]
        string line3 = @"ProcessTalkScreen(-346, ""Dice Game"", ""<color=red><sprite=\""Dice\"" index=3></color> Inverts <color=#9999FF><sprite=\""Dice\"" index=4></color> and <color=#9999FF><sprite=\""Dice\"" index=1></color> Is Fresh. <b>YOU WIN!</b>"", ""Done"", [111,112,], System.String[], 1, Generic)";
        var s3 = parser.Parse(line3)!;
        Assert(s3.Phase == GamePhase.Result, "s3 Phase");
        Assert(s3.Won, "s3 Won");
        Assert(s3.BluesRolled.SequenceEqual(new[] { 4, 1 }), "s3 BluesRolled");
        Console.WriteLine("[parser selftest] scenario 3 passed: Result WIN inverted+fresh blue");

        // Scenario 4: Result LOSE, codes [1]
        string line4 = @"ProcessTalkScreen(-346, ""Dice Game"", ""YOU LOSE."", ""OK"", [1,], System.String[], 1, Generic)";
        var s4 = parser.Parse(line4)!;
        Assert(s4.Phase == GamePhase.Result, "s4 Phase");
        Assert(!s4.Won, "s4 Won");
        Assert(s4.AvailableResponseCodes.SequenceEqual(new[] { 1 }), "s4 Codes");
        Console.WriteLine("[parser selftest] scenario 4 passed: Result LOSE");

        // Scenario 5: CashOut — 2000 Councils, codes [1,-1]
        string line5 = @"ProcessTalkScreen(-346, ""Dice Game"", ""You cash out and receive 2000 Councils."", ""OK"", [1,-1,], System.String[], 1, Generic)";
        var s5 = parser.Parse(line5)!;
        Assert(s5.Phase == GamePhase.CashOut, "s5 Phase");
        Assert(s5.AvailableResponseCodes.SequenceEqual(new[] { 1, -1 }), "s5 Codes");
        Console.WriteLine("[parser selftest] scenario 5 passed: CashOut");

        Console.WriteLine("[parser selftest] all 5 scenarios passed");
    }

    static void Assert(bool condition, string label)
    {
        if (!condition)
            throw new Exception($"[parser selftest] FAILED: {label}");
    }
}

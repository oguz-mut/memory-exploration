using System;
using System.Collections.Generic;
using System.Linq;
using DiceGameSolver.Models;

namespace DiceGameSolver;

public class DiceSolver
{
    public bool StopOnIntro { get; set; } = false;

    public virtual Decision Decide(GameState state)
    {
        return state.Phase switch
        {
            GamePhase.Inactive => new Decision
            {
                Action = DiceAction.NoOp,
                ResponseCode = 0,
                EV = 0,
                WinProbability = 0,
                Rationale = "dice mat occupied",
            },
            GamePhase.Intro => StopOnIntro
                ? new Decision { Action = DiceAction.NoOp, ResponseCode = 0, Rationale = "stop on intro" }
                : new Decision { Action = DiceAction.Play, ResponseCode = GameState.CodePlay, Rationale = "start game" },
            GamePhase.Result => state.Won
                ? new Decision { Action = DiceAction.PlayAgainWin, ResponseCode = GameState.CodePlayAgainWin, Rationale = "won — play again" }
                : new Decision { Action = DiceAction.PlayAgainLose, ResponseCode = GameState.CodePlay, Rationale = "lost — play again" },
            GamePhase.CashOut => new Decision
            {
                Action = DiceAction.PlayAgainWin,
                ResponseCode = GameState.CodePlay,
                Rationale = "cash out play again",
            },
            GamePhase.Playing => DecidePlaying(state),
            _ => new Decision { Action = DiceAction.NoOp, ResponseCode = 0, Rationale = "unknown phase" },
        };
    }

    protected Decision DecidePlaying(GameState state)
    {
        int baseScore = state.RedDice.Sum() - (state.Raised ? 1 : 0);
        int payout = state.Raised ? 6000 : 2000;
        int committed = state.Raised ? 2000 : 1000;
        var M = new HashSet<int>(state.RedDice);
        var codes = new HashSet<int>(state.AvailableResponseCodes);

        var options = new List<(Decision decision, int tiebreak)>();

        if (codes.Contains(GameState.CodeStandPat))
        {
            double pw = WinProbability(baseScore, state.DealerCurScore);
            double ev = pw * payout - committed;
            options.Add((new Decision
            {
                Action = DiceAction.StandPat,
                ResponseCode = GameState.CodeStandPat,
                WinProbability = pw,
                EV = ev,
                Rationale = $"stand pat: {pw:P1} @ {baseScore}",
            }, 0));
        }

        if (codes.Contains(GameState.CodeRollOne))
        {
            double pw = WinProbabilityAfterBlues(baseScore, state.DealerCurScore, M, 1);
            double ev = pw * payout - committed;
            options.Add((new Decision
            {
                Action = DiceAction.RollOne,
                ResponseCode = GameState.CodeRollOne,
                WinProbability = pw,
                EV = ev,
                Rationale = $"roll 1: {pw:P1} @ {baseScore}",
            }, 1));
        }

        if (codes.Contains(GameState.CodeRollTwo))
        {
            double pw = WinProbabilityAfterBlues(baseScore, state.DealerCurScore, M, 2);
            double ev = pw * payout - committed;
            options.Add((new Decision
            {
                Action = DiceAction.RollTwo,
                ResponseCode = GameState.CodeRollTwo,
                WinProbability = pw,
                EV = ev,
                Rationale = $"roll 2: {pw:P1} @ {baseScore}",
            }, 2));
        }

        if (codes.Contains(GameState.CodeRaise))
        {
            // Build hypothetical post-raise state: Raised=true applies -1 hubris to score
            // StandPat is offered post-raise only if post-raise score beats dealer's minimum possible final
            int postRaiseScore = state.RedDice.Sum() - 1;
            var postRaiseCodes = new List<int> { GameState.CodeRollOne, GameState.CodeRollTwo };
            if (postRaiseScore > state.DealerCurScore + 2)
                postRaiseCodes.Add(GameState.CodeStandPat);

            var postRaiseState = new GameState
            {
                Phase = GamePhase.Playing,
                RedDice = state.RedDice,
                DealerCurScore = state.DealerCurScore,
                Raised = true,
                LosingBanner = state.LosingBanner,
                AvailableResponseCodes = postRaiseCodes.ToArray(),
            };

            Decision sub = DecidePlaying(postRaiseState);
            options.Add((new Decision
            {
                Action = DiceAction.Raise,
                ResponseCode = GameState.CodeRaise,
                WinProbability = sub.WinProbability,
                EV = sub.EV,
                Rationale = $"raise then {sub.Action}: {sub.WinProbability:P1} EV {sub.EV:+#;-#;0}",
            }, 3));
        }

        if (options.Count == 0)
            return new Decision { Action = DiceAction.NoOp, ResponseCode = 0, Rationale = "no options available" };

        // Highest EV wins; tiebreak by simplicity: StandPat(0) < RollOne(1) < RollTwo(2) < Raise(3)
        var best = options.OrderByDescending(o => o.decision.EV).ThenBy(o => o.tiebreak).First();
        return best.decision;
    }

    // Enumerates all 36 dealer final-roll outcomes; wins when playerFinal >= dealerFinal (tie favors player)
    static double WinProbability(int playerFinal, int dealerCur)
    {
        int wins = 0;
        for (int a = 1; a <= 6; a++)
            for (int b = 1; b <= 6; b++)
                if (dealerCur + a + b <= playerFinal)
                    wins++;
        return wins / 36.0;
    }

    // Inverted if face is in M (subtract), fresh otherwise (add)
    static int ApplyBlue(int score, int face, HashSet<int> M)
        => M.Contains(face) ? score - face : score + face;

    // Enumerates 6^blues blue outcomes × 36 dealer outcomes; returns fraction that are wins
    static double WinProbabilityAfterBlues(int baseScore, int dealerCur, HashSet<int> M, int blues)
    {
        int wins = 0;
        int total = 0;

        if (blues == 1)
        {
            for (int f = 1; f <= 6; f++)
            {
                int s = ApplyBlue(baseScore, f, M);
                for (int a = 1; a <= 6; a++)
                    for (int b = 1; b <= 6; b++)
                    {
                        if (dealerCur + a + b <= s) wins++;
                        total++;
                    }
            }
        }
        else // blues == 2
        {
            for (int f1 = 1; f1 <= 6; f1++)
            {
                int mid = ApplyBlue(baseScore, f1, M);
                for (int f2 = 1; f2 <= 6; f2++)
                {
                    int s = ApplyBlue(mid, f2, M);
                    for (int a = 1; a <= 6; a++)
                        for (int b = 1; b <= 6; b++)
                        {
                            if (dealerCur + a + b <= s) wins++;
                            total++;
                        }
                }
            }
        }

        return (double)wins / total;
    }

    public static void SelfTest()
    {
        var solver = new DiceSolver();

        // Scenario 1: red=[1,1,1], dealer=5 — heavily losing position, best is RollTwo
        // (all blues fresh except face=1 which is inverted; rolling gives the most upside)
        {
            var state = new GameState
            {
                Phase = GamePhase.Playing,
                RedDice = new[] { 1, 1, 1 },
                DealerCurScore = 5,
                AvailableResponseCodes = new[] { GameState.CodeRollOne, GameState.CodeRollTwo },
            };
            var d = solver.Decide(state);
            if (d.Action != DiceAction.RollTwo)
                throw new Exception($"Scenario 1 failed: expected RollTwo, got {d.Action} (EV={d.EV:F1})");
            Console.WriteLine($"[solver selftest] 1 pass: {d.Action} EV={d.EV:+0.0;-0.0}");
        }

        // Scenario 2: red=[4,5,6], dealer=4 — strong winning position; all blues in M (inverted) hurt score
        {
            var state = new GameState
            {
                Phase = GamePhase.Playing,
                RedDice = new[] { 4, 5, 6 },
                DealerCurScore = 4,
                AvailableResponseCodes = new[] { GameState.CodeStandPat, GameState.CodeRollOne, GameState.CodeRollTwo },
            };
            var d = solver.Decide(state);
            if (d.Action != DiceAction.StandPat)
                throw new Exception($"Scenario 2 failed: expected StandPat, got {d.Action} (EV={d.EV:F1})");
            if (d.EV < 500)
                throw new Exception($"Scenario 2 failed: expected EV > 500, got {d.EV:F1}");
            Console.WriteLine($"[solver selftest] 2 pass: {d.Action} EV={d.EV:+0.0;-0.0}");
        }

        // Scenario 3: red=[2,3,4], dealer=11, no StandPat — must pick a rolling or raise action
        {
            var state = new GameState
            {
                Phase = GamePhase.Playing,
                RedDice = new[] { 2, 3, 4 },
                DealerCurScore = 11,
                LosingBanner = true,
                AvailableResponseCodes = new[] { GameState.CodeRaise, GameState.CodeRollOne, GameState.CodeRollTwo },
            };
            var d = solver.Decide(state);
            if (d.Action != DiceAction.RollOne && d.Action != DiceAction.RollTwo && d.Action != DiceAction.Raise)
                throw new Exception($"Scenario 3 failed: expected RollOne/RollTwo/Raise, got {d.Action}");
            Console.WriteLine($"[solver selftest] 3 pass: {d.Action} EV={d.EV:+0.0;-0.0}");
        }

        // Scenario 4: Result screen, won → PlayAgainWin (code 112)
        {
            var state = new GameState { Phase = GamePhase.Result, Won = true };
            var d = solver.Decide(state);
            if (d.Action != DiceAction.PlayAgainWin || d.ResponseCode != GameState.CodePlayAgainWin)
                throw new Exception($"Scenario 4 failed: got {d.Action} code={d.ResponseCode}");
            Console.WriteLine($"[solver selftest] 4 pass: {d.Action} code={d.ResponseCode}");
        }

        // Scenario 5: Result screen, lost → PlayAgainLose (code 1)
        {
            var state = new GameState { Phase = GamePhase.Result, Won = false };
            var d = solver.Decide(state);
            if (d.Action != DiceAction.PlayAgainLose || d.ResponseCode != GameState.CodePlay)
                throw new Exception($"Scenario 5 failed: got {d.Action} code={d.ResponseCode}");
            Console.WriteLine($"[solver selftest] 5 pass: {d.Action} code={d.ResponseCode}");
        }

        Console.WriteLine("[solver selftest] all 5 scenarios passed");
    }
}

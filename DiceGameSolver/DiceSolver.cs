using DiceGameSolver.Models;

namespace DiceGameSolver;

// Worker A (sprint/dice-solver) will replace this stub with the real enumerative EV engine.
// Contract: pure, no I/O. Given a GameState, return the recommended Decision.
public class DiceSolver
{
    public bool StopOnIntro { get; set; } = false;

    public virtual Decision Decide(GameState state)
    {
        return new Decision
        {
            Action = DiceAction.NoOp,
            ResponseCode = 0,
            EV = 0.0,
            WinProbability = 0.0,
            Rationale = "stub",
        };
    }

    public static void SelfTest() { System.Console.WriteLine("[solver] stub selftest"); }
}

using DiceGameSolver.Models;

namespace DiceGameSolver;

// Worker B (sprint/dice-parser) will replace this stub.
// Contract: Parse one log line. Returns null if not a dice-game TalkScreen.
public class GameStateParser
{
    public virtual GameState? Parse(string logLine) => null;

    public static void SelfTest() { System.Console.WriteLine("[parser] stub selftest"); }
}

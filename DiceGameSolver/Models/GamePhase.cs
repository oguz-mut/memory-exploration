namespace DiceGameSolver.Models;

public enum GamePhase
{
    Unknown,
    Inactive,    // "Looks like somebody plays dice games here. But not right now."
    Intro,       // "Play game?"
    Playing,     // "Your score from red dice is..."
    Result,      // body contains YOU WIN! or YOU LOSE.
    CashOut,     // "You cash out and receive N Councils"
}

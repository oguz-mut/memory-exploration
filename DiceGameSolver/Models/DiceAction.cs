namespace DiceGameSolver.Models;

public enum DiceAction
{
    NoOp,
    Play,
    Raise,
    RollOne,
    RollTwo,
    StandPat,
    CashOut,
    PlayAgainWin,
    PlayAgainLose,
    CloseAfterCashOut,
}

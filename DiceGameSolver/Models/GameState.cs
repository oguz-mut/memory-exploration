namespace DiceGameSolver.Models;

public sealed class GameState
{
    public GamePhase Phase { get; set; } = GamePhase.Unknown;
    public int[] RedDice { get; set; } = System.Array.Empty<int>();     // 3 faces 1..6 during Playing
    public int DealerCurScore { get; set; }                              // dealer score before final 2d6
    public int[] BluesRolled { get; set; } = System.Array.Empty<int>();  // blues parsed from Result screen
    public bool Raised { get; set; }                                     // hubris applied
    public bool LosingBanner { get; set; }                               // "You are currently losing"
    public bool Won { get; set; }                                        // only meaningful on Phase==Result
    public int[] AvailableResponseCodes { get; set; } = System.Array.Empty<int>();
    public string RawBody { get; set; } = string.Empty;                  // retained for debugging

    /// <summary>Stable identifier for the current screen — changes when the screen advances.</summary>
    public string Signature =>
        $"{Phase}|codes=[{string.Join(",", AvailableResponseCodes)}]|red=[{string.Join(",", RedDice)}]|dealer={DealerCurScore}|R={(Raised?1:0)}|W={(Won?1:0)}|B=[{string.Join(",", BluesRolled)}]";

    // Known response codes (see project_dice_game_protocol memory)
    public const int CodePlay = 1;          // Intro Play! / PlayAgain on loss
    public const int CodeRaise = 101;       // Raise Bet
    public const int CodeRollOne = 121;     // Roll 1 blue die
    public const int CodeRollTwo = 122;     // Roll 2 blue dice
    public const int CodeStandPat = 123;    // Stand Pat (only when currently winning)
    public const int CodeCashOut = 111;     // Cash Out on win
    public const int CodePlayAgainWin = 112;// Play Again on win
    public const int CodeClose = -1;        // Close from cash-out screen

    /// <summary>
    /// What phase should we expect after clicking this code from this state?
    /// Returns null if the combination isn't meaningful (we don't validate).
    /// </summary>
    public static GamePhase? ExpectedNextPhase(GameState pre, int code)
    {
        return (pre.Phase, code) switch
        {
            (GamePhase.Intro,   CodePlay)          => GamePhase.Playing,
            (GamePhase.Playing, CodeRaise)         => GamePhase.Playing,   // still playing, now raised
            (GamePhase.Playing, CodeRollOne)       => GamePhase.Result,
            (GamePhase.Playing, CodeRollTwo)       => GamePhase.Result,
            (GamePhase.Playing, CodeStandPat)      => GamePhase.Result,
            (GamePhase.Result,  CodeCashOut)       => GamePhase.CashOut,
            (GamePhase.Result,  CodePlayAgainWin)  => GamePhase.Playing,
            (GamePhase.Result,  CodePlay)          => GamePhase.Playing,   // lost, play again
            (GamePhase.CashOut, CodePlay)          => GamePhase.Playing,
            (GamePhase.CashOut, CodeClose)         => GamePhase.Inactive,
            _ => null,
        };
    }
}

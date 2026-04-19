namespace DiceGameSolver.Models;

public static class LayoutKey
{
    public static string For(GameState state) => state.Phase switch
    {
        GamePhase.Playing  => $"Playing:{state.AvailableResponseCodes.Length}:L={(state.LosingBanner ? 1 : 0)}:R={(state.Raised ? 1 : 0)}",
        GamePhase.Result   => $"Result:blues={state.BluesRolled.Length}:W={(state.Won ? 1 : 0)}:R={(state.Raised ? 1 : 0)}",
        GamePhase.Intro    => "Intro",
        GamePhase.CashOut  => "CashOut",
        GamePhase.Inactive => "Inactive",
        _                  => "Unknown",
    };
}

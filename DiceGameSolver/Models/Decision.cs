namespace DiceGameSolver.Models;

public sealed class Decision
{
    public DiceAction Action { get; set; }
    public int ResponseCode { get; set; }
    public double EV { get; set; }
    public double WinProbability { get; set; }
    public string Rationale { get; set; } = string.Empty;
}

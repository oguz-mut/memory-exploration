using System.Threading;
using System.Threading.Tasks;
using DiceGameSolver.Models;

namespace DiceGameSolver;

// Worker C (sprint/dice-clicker) will replace this stub with Win32 click automation + calibration.
public class ClickExecutor
{
    public string ExecutorStatus { get; protected set; } = "idle";

    public virtual Task CalibrateAsync(CancellationToken ct) => Task.CompletedTask;

    public virtual Task ClickResponseCodeAsync(int responseCode, GameState currentState, CancellationToken ct)
        => Task.CompletedTask;
}

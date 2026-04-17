using System.Threading.Channels;
using RunePuzzleSolver.Models;

namespace RunePuzzleSolver;

public class ClickExecutor
{
    public string ExecutorStatus { get; private set; } = "idle";

    public async Task RunAsync(Channel<GuessAction> input, PuzzleStateReader reader, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
}

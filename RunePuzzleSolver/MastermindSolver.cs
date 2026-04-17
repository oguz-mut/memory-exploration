using System.Threading.Channels;
using RunePuzzleSolver.Models;

namespace RunePuzzleSolver;

public class MastermindSolver
{
    public Channel<GuessAction> ActionChannel { get; } = Channel.CreateBounded<GuessAction>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

    public int CandidateCount { get; private set; }
    public string SolverStatus { get; private set; } = "idle";

    public async Task RunAsync(Channel<PuzzleState> input, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
}

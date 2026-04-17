using System.Threading.Channels;
using RunePuzzleSolver.Models;

namespace RunePuzzleSolver;

public class PuzzleStateReader
{
    public static readonly string[] Symbols = ["7", "B", "C", "F", "K", "M", "P", "Q", "S", "T", "W", "X"];

    public Channel<PuzzleState> StateChannel { get; } = Channel.CreateBounded<PuzzleState>(
        new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest });

    public int GuessRowCount { get; private set; }
    public PuzzleState? LastState { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
}

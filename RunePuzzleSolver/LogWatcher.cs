namespace RunePuzzleSolver;

public class LogWatcher
{
    public bool LastSolveDetected { get; private set; }
    public List<string> RecentItems { get; } = [];

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
    }
}

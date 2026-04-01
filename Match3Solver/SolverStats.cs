using System.Text.Json;

static class SolverStats
{
    private static readonly object _lock = new();
    private static readonly Dictionary<string, (int games, long totalPredictedScore, long totalSolveMs)> _byStrategy = new();
    private static int _prngFailures;

    public static void RecordSolve(string strategy, int predictedScore, long solveMs)
    {
        lock (_lock)
        {
            _byStrategy.TryGetValue(strategy, out var s);
            _byStrategy[strategy] = (s.games + 1, s.totalPredictedScore + predictedScore, s.totalSolveMs + solveMs);
        }
    }

    public static void RecordPrngFailure()
    {
        Interlocked.Increment(ref _prngFailures);
    }

    public static string GetJson()
    {
        Dictionary<string, object> strategies;
        int failures;
        lock (_lock)
        {
            strategies = _byStrategy.ToDictionary(
                kvp => kvp.Key,
                kvp => (object)new
                {
                    games = kvp.Value.games,
                    totalPredictedScore = kvp.Value.totalPredictedScore,
                    totalSolveMs = kvp.Value.totalSolveMs
                });
            failures = _prngFailures;
        }
        return JsonSerializer.Serialize(new { strategies, prngFailures = failures });
    }
}

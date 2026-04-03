using System.Text.Json;

static class MoveLog
{
    public static void LogMove(string settingsDir, int gameId, int turn, int predictedScore, SolverMove move, int[] actualBoard)
    {
        var line = JsonSerializer.Serialize(new
        {
            gameId,
            turn,
            predicted = predictedScore,
            move = new { x = move.X, y = move.Y, dir = move.Direction.ToString().ToLower(), scoreAfter = move.ScoreAfter },
            board = actualBoard
        });
        var path = Path.Combine(settingsDir, "match3_moves.jsonl");
        File.AppendAllText(path, line + "\n");
    }

    public static string GetMovesJson(string settingsDir, int gameId)
    {
        var path = Path.Combine(settingsDir, "match3_moves.jsonl");
        if (!File.Exists(path))
            return "[]";
        var lines = File.ReadAllLines(path);
        var matching = new List<string>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.GetProperty("gameId").GetInt32() == gameId)
                    matching.Add(line);
            }
            catch { }
        }
        return "[" + string.Join(",", matching) + "]";
    }
}

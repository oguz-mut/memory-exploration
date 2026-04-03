using System.Text.Json.Serialization;

class Match3Config
{
    [JsonPropertyName("Width")] public int Width { get; set; }
    [JsonPropertyName("Height")] public int Height { get; set; }
    [JsonPropertyName("Title")] public string Title { get; set; } = "";
    [JsonPropertyName("NumTurns")] public int NumTurns { get; set; }
    [JsonPropertyName("RandomSeed")] public int RandomSeed { get; set; }
    [JsonPropertyName("GiveRewards")] public bool GiveRewards { get; set; }
    [JsonPropertyName("PieceReqsPerTier")] public int[] PieceReqsPerTier { get; set; } = [];
    [JsonPropertyName("ScoreFor3s")] public int ScoreFor3s { get; set; }
    [JsonPropertyName("ScoreFor4s")] public int ScoreFor4s { get; set; }
    [JsonPropertyName("ScoreFor5s")] public int ScoreFor5s { get; set; }
    [JsonPropertyName("ScoreDeltasPerTier")] public int[] ScoreDeltasPerTier { get; set; } = [];
    [JsonPropertyName("ScoresPerChainLevel")] public int[] ScoresPerChainLevel { get; set; } = [];
    [JsonPropertyName("Pieces")] public PieceInfo[] Pieces { get; set; } = [];

    /// <summary>Per-piece item values (from items.json). Index matches Pieces[]. 0 = unknown.</summary>
    [JsonIgnore] public int[] PieceValues { get; set; } = [];
}

class PieceInfo
{
    [JsonPropertyName("IconID")] public int IconID { get; set; }
    [JsonPropertyName("Label")] public string Label { get; set; } = "";
    [JsonPropertyName("Tier")] public int Tier { get; set; }
}

class StepResults
{
    public readonly Dictionary<int, int> Match3s = new();
    public readonly Dictionary<int, int> Match4s = new();
    public readonly Dictionary<int, int> Match5s = new();
    public readonly List<MatchLocation> Matches = new();
    public bool HadMatch4OrMore;

    public void Clear()
    {
        Match3s.Clear(); Match4s.Clear(); Match5s.Clear();
        Matches.Clear(); HadMatch4OrMore = false;
    }
}

class SolverResult
{
    public List<SolverMove> BestMoves { get; set; } = new();
    public int PredictedScore { get; set; }
    public int PredictedTier { get; set; }
    public int StatesExplored { get; set; }
    public TimeSpan SolveTime { get; set; }
    public string Strategy { get; set; } = "";
}

class SolverMove
{
    public int X { get; set; }
    public int Y { get; set; }
    public MoveDir Direction { get; set; }
    public int ScoreAfter { get; set; }
    public string Description { get; set; } = "";
}

class GameSession
{
    public int SessionId { get; set; }
    public Match3Config Config { get; set; } = new();
    public int[]? InitialBoard { get; set; }
    public int NumPieceTypes { get; set; }
    public string[]? PieceLabels { get; set; }
    public SolverResult? Solution { get; set; }
    public SolveStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int GameScore { get; set; }
}

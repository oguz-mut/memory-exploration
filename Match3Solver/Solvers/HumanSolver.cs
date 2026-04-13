/// <summary>
/// Plays like a decent human: depth-1 evaluation with weighted randomness.
/// Aims for 2-3x entry value, not max score. Occasionally picks suboptimal moves
/// to avoid statistically impossible play patterns.
/// </summary>
class HumanSolver : ISolver
{
    private readonly Random _rng = new();

    /// <summary>Chance (0-1) of picking a random valid move instead of a good one.</summary>
    public double MistakeRate { get; set; } = 0.12;

    /// <summary>When not making a mistake, pick from the top N moves weighted by rank.</summary>
    public int TopN { get; set; } = 4;

    /// <summary>
    /// Whether to occasionally ignore extra-turn moves. A human doesn't always spot 4-matches.
    /// </summary>
    public double MissExtraTurnRate { get; set; } = 0.20;

    public SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000)
    {
        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
        {
            return new SolverResult
            {
                BestMoves = new List<SolverMove>(),
                PredictedScore = state.CascadeScore,
                StatesExplored = 0,
                Strategy = "HumanSolver (no moves)"
            };
        }

        // Score all moves at depth 1 — a human scans the board once
        var scored = new List<(int x, int y, MoveDir dir, int score, bool extraTurn)>();
        foreach (var (x, y, dir) in validMoves)
        {
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            scored.Add((x, y, dir, clone.CascadeScore, clone.IsExtraTurnEarned));
        }

        // Sort by score descending (extra-turn awareness is probabilistic, not guaranteed)
        scored.Sort((a, b) => b.score.CompareTo(a.score));

        // Occasionally "miss" extra-turn moves: demote them to their raw score
        // (a human doesn't always notice the 4-match possibility)
        if (_rng.NextDouble() < MissExtraTurnRate)
        {
            // Just sort purely by score, ignoring extra-turn priority
            // This is already what we did above — no special treatment
        }
        else
        {
            // Give extra-turn moves a boost (human notices the 4+ match sometimes)
            scored.Sort((a, b) =>
            {
                if (a.extraTurn != b.extraTurn) return b.extraTurn.CompareTo(a.extraTurn);
                return b.score.CompareTo(a.score);
            });
        }

        (int x, int y, MoveDir dir, int score, bool extraTurn) chosen;

        // Random mistake: pick any valid move
        if (_rng.NextDouble() < MistakeRate)
        {
            chosen = scored[_rng.Next(scored.Count)];
        }
        else
        {
            // Pick from top N with weighted probability: rank 1 most likely, rank N least likely
            int candidates = Math.Min(TopN, scored.Count);
            // Weights: [candidates, candidates-1, ..., 1] — triangular distribution
            int totalWeight = candidates * (candidates + 1) / 2;
            int roll = _rng.Next(totalWeight);
            int cumulative = 0;
            int pick = 0;
            for (int i = 0; i < candidates; i++)
            {
                cumulative += candidates - i;
                if (roll < cumulative) { pick = i; break; }
            }
            chosen = scored[pick];
        }

        string pickDesc = chosen == scored[0] ? "best" : $"rank {scored.IndexOf(chosen) + 1}/{scored.Count}";

        return new SolverResult
        {
            BestMoves = new List<SolverMove>
            {
                new SolverMove
                {
                    X = chosen.x,
                    Y = chosen.y,
                    Direction = chosen.dir,
                    ScoreAfter = chosen.score,
                    Description = $"({chosen.x},{chosen.y}) {chosen.dir} [{pickDesc}]"
                }
            },
            PredictedScore = chosen.score,
            StatesExplored = scored.Count,
            Strategy = $"HumanSolver ({pickDesc})"
        };
    }
}

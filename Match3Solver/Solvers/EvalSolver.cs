using System.Diagnostics;

class EvalSolver : ISolver
{
    private int _statesExplored;
    private const int SearchDepth = 4;

    public SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000)
    {
        _statesExplored = 0;
        var sw = Stopwatch.StartNew();

        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
        {
            return new SolverResult
            {
                BestMoves = new List<SolverMove>(),
                PredictedScore = state.Score,
                StatesExplored = 0,
                Strategy = "EvalSolver/Expectimax (no moves available)"
            };
        }

        double bestValue = double.NegativeInfinity;
        SolverMove? bestMove = null;

        foreach (var (x, y, dir) in validMoves)
        {
            if (sw.ElapsedMilliseconds >= timeBudgetMs) break;

            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;

            double value = Expectimax(clone, SearchDepth - 1, config, sw, timeBudgetMs);

            if (value > bestValue)
            {
                bestValue = value;
                bestMove = new SolverMove
                {
                    X = x,
                    Y = y,
                    Direction = dir,
                    ScoreAfter = clone.Score,
                    Description = $"Expectimax eval={value:F1}"
                };
            }
        }

        var bestMoves = bestMove != null ? new List<SolverMove> { bestMove } : new List<SolverMove>();

        return new SolverResult
        {
            BestMoves = bestMoves,
            PredictedScore = bestMove?.ScoreAfter ?? state.Score,
            StatesExplored = _statesExplored,
            Strategy = $"EvalSolver/Expectimax (depth {SearchDepth}, {sw.ElapsedMilliseconds}ms)"
        };
    }

    private double EvaluateBoard(SimGameState state, Match3Config config)
    {
        double score = state.Score * 10.0;

        // Available moves — more flexibility is better
        var validMoves = state.Board.GetAllValidMoves();
        score += validMoves.Count * 5.0;

        // Extra turn earned by the last move — highest-value event, scales with scarcity
        if (state.IsExtraTurnEarned)
            score += 300.0 + (state.TurnsRemaining > 0 ? 300.0 / state.TurnsRemaining : 0);

        // Turns remaining — more turns means more scoring opportunity
        score += state.TurnsRemaining * 15.0;

        // Piece clustering: count adjacent same-color pairs
        int width = state.Board.Width;
        int height = state.Board.Height;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int type = state.Board.Get(x, y);
                if (type < 0) continue;
                if (x + 1 < width && state.Board.Get(x + 1, y) == type) score += 2.0;
                if (y + 1 < height && state.Board.Get(x, y + 1) == type) score += 2.0;
            }
        }

        // Chain setup: weighted score for 4+/5+ match patterns, L-shapes, and T-shapes
        score += CountChainSetupScore(state.Board) * 50.0;

        // Tier progress: how close are we to the next tier?
        if (config.PieceReqsPerTier.Length > 0 && state.Tier < config.PieceReqsPerTier.Length)
        {
            int req = config.PieceReqsPerTier[state.Tier];
            if (req > 0)
            {
                int activePieces = state.Board.ActivePieceTypes;
                double tierProgress = 0.0;
                int tieredCount = 0;
                for (int i = 0; i < activePieces && i < state.TierPiecesMatched.Length; i++)
                {
                    tierProgress += Math.Min(1.0, (double)state.TierPiecesMatched[i] / req);
                    if (i < state.PiecesTiered.Length && state.PiecesTiered[i]) tieredCount++;
                }
                // Raw progress toward threshold for each piece type
                score += tierProgress * 30.0;
                // Large bonus when most/all piece types have hit their threshold (imminent tier-up)
                if (activePieces > 0)
                    score += ((double)tieredCount / activePieces) * 200.0;
            }
        }

        return score;
    }

    /// <summary>
    /// Returns a weighted setup score for 4+/5+ match potential, including gap patterns,
    /// L-shaped setups (3-in-a-row + orthogonal piece at an end), and T-shaped setups
    /// (3-in-a-row + orthogonal piece at the middle). 5-match gap patterns score 2×.
    /// </summary>
    private double CountChainSetupScore(SimBoard board)
    {
        double score = 0.0;
        int width = board.Width;
        int height = board.Height;

        // ---- Horizontal gap patterns ----
        for (int y = 0; y < height; y++)
        {
            // 4-wide window: AA_A and A_AA
            for (int x = 0; x < width - 3; x++)
            {
                int t0 = board.Get(x, y), t1 = board.Get(x + 1, y);
                int t2 = board.Get(x + 2, y), t3 = board.Get(x + 3, y);
                if (t0 < 0 || t1 < 0 || t2 < 0 || t3 < 0) continue;
                if (t0 == t1 && t1 == t3 && t2 != t0) score += 1.0; // AA_A
                if (t0 == t2 && t2 == t3 && t1 != t0) score += 1.0; // A_AA
            }
            // 5-wide window: A_AAA, AAA_A, AA_AA (higher value — potential 5-match)
            for (int x = 0; x < width - 4; x++)
            {
                int t0 = board.Get(x, y), t1 = board.Get(x + 1, y);
                int t2 = board.Get(x + 2, y), t3 = board.Get(x + 3, y);
                int t4 = board.Get(x + 4, y);
                if (t0 < 0 || t1 < 0 || t2 < 0 || t3 < 0 || t4 < 0) continue;
                if (t0 == t2 && t2 == t3 && t3 == t4 && t1 != t0) score += 2.0; // A_AAA
                if (t0 == t1 && t1 == t2 && t2 == t4 && t3 != t0) score += 2.0; // AAA_A
                if (t0 == t1 && t3 == t4 && t0 == t3 && t2 != t0) score += 2.0; // AA_AA
            }
        }

        // ---- Vertical gap patterns ----
        for (int x = 0; x < width; x++)
        {
            // 4-high window
            for (int y = 0; y < height - 3; y++)
            {
                int t0 = board.Get(x, y), t1 = board.Get(x, y + 1);
                int t2 = board.Get(x, y + 2), t3 = board.Get(x, y + 3);
                if (t0 < 0 || t1 < 0 || t2 < 0 || t3 < 0) continue;
                if (t0 == t1 && t1 == t3 && t2 != t0) score += 1.0;
                if (t0 == t2 && t2 == t3 && t1 != t0) score += 1.0;
            }
            // 5-high window
            for (int y = 0; y < height - 4; y++)
            {
                int t0 = board.Get(x, y), t1 = board.Get(x, y + 1);
                int t2 = board.Get(x, y + 2), t3 = board.Get(x, y + 3);
                int t4 = board.Get(x, y + 4);
                if (t0 < 0 || t1 < 0 || t2 < 0 || t3 < 0 || t4 < 0) continue;
                if (t0 == t2 && t2 == t3 && t3 == t4 && t1 != t0) score += 2.0;
                if (t0 == t1 && t1 == t2 && t2 == t4 && t3 != t0) score += 2.0;
                if (t0 == t1 && t3 == t4 && t0 == t3 && t2 != t0) score += 2.0;
            }
        }

        // ---- L-shaped and T-shaped setups ----
        // Horizontal 3-in-a-row with an orthogonal matching piece at an end (L) or middle (T)
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width - 2; x++)
            {
                int t = board.Get(x, y);
                if (t < 0 || board.Get(x + 1, y) != t || board.Get(x + 2, y) != t) continue;
                // L: piece adjacent to the left or right end (above/below)
                if (y > 0 && board.Get(x, y - 1) == t) score += 1.5;
                if (y < height - 1 && board.Get(x, y + 1) == t) score += 1.5;
                if (y > 0 && board.Get(x + 2, y - 1) == t) score += 1.5;
                if (y < height - 1 && board.Get(x + 2, y + 1) == t) score += 1.5;
                // T: piece adjacent to the middle (above/below)
                if (y > 0 && board.Get(x + 1, y - 1) == t) score += 1.5;
                if (y < height - 1 && board.Get(x + 1, y + 1) == t) score += 1.5;
            }
        }

        // Vertical 3-in-a-column with an orthogonal matching piece at an end (L) or middle (T)
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height - 2; y++)
            {
                int t = board.Get(x, y);
                if (t < 0 || board.Get(x, y + 1) != t || board.Get(x, y + 2) != t) continue;
                // L: piece adjacent to the top or bottom end (left/right)
                if (x > 0 && board.Get(x - 1, y) == t) score += 1.5;
                if (x < width - 1 && board.Get(x + 1, y) == t) score += 1.5;
                if (x > 0 && board.Get(x - 1, y + 2) == t) score += 1.5;
                if (x < width - 1 && board.Get(x + 1, y + 2) == t) score += 1.5;
                // T: piece adjacent to the middle (left/right)
                if (x > 0 && board.Get(x - 1, y + 1) == t) score += 1.5;
                if (x < width - 1 && board.Get(x + 1, y + 1) == t) score += 1.5;
            }
        }

        return score;
    }

    private double Expectimax(SimGameState state, int depth, Match3Config config, Stopwatch sw, int timeBudgetMs)
    {
        if (depth == 0 || state.IsGameOver || sw.ElapsedMilliseconds >= timeBudgetMs)
            return EvaluateBoard(state, config);

        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
            return EvaluateBoard(state, config);

        double bestValue = double.NegativeInfinity;

        foreach (var (x, y, dir) in validMoves)
        {
            if (sw.ElapsedMilliseconds >= timeBudgetMs) break;

            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;

            double value = Expectimax(clone, depth - 1, config, sw, timeBudgetMs);

            if (value > bestValue)
                bestValue = value;
        }

        return bestValue;
    }
}

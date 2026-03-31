using System.Diagnostics;

class EvalSolver
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

        // Potential 4+ matches — check for near-4-in-a-rows
        int potential4Matches = CountPotential4Matches(state.Board);
        score += potential4Matches * 50.0;

        // Tier progress: how close are we to the next tier?
        if (config.PieceReqsPerTier.Length > 0 && state.Tier < config.PieceReqsPerTier.Length)
        {
            int req = config.PieceReqsPerTier[state.Tier];
            if (req > 0)
            {
                double tierProgress = 0.0;
                int activePieces = state.Board.ActivePieceTypes;
                for (int i = 0; i < activePieces && i < state.TierPiecesMatched.Length; i++)
                {
                    tierProgress += Math.Min(1.0, (double)state.TierPiecesMatched[i] / req);
                }
                score += tierProgress * 30.0;
            }
        }

        return score;
    }

    /// <summary>
    /// Count board positions where a single swap could create a 4+ match.
    /// We check each valid move to see if it results in a 4+ match without
    /// actually stepping the board (just looking at the swap result).
    /// </summary>
    private int CountPotential4Matches(SimBoard board)
    {
        int count = 0;
        int width = board.Width;
        int height = board.Height;

        // Check horizontal runs with a gap: AAoA or AoAA patterns
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width - 3; x++)
            {
                int t0 = board.Get(x, y);
                int t1 = board.Get(x + 1, y);
                int t2 = board.Get(x + 2, y);
                int t3 = board.Get(x + 3, y);

                if (t0 < 0 || t1 < 0 || t2 < 0 || t3 < 0) continue;

                // Three same with one different in the middle or edge
                if (t0 == t1 && t1 == t3 && t2 != t0) count++; // AA_A
                if (t0 == t2 && t2 == t3 && t1 != t0) count++; // A_AA
            }
        }

        // Check vertical runs with a gap
        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height - 3; y++)
            {
                int t0 = board.Get(x, y);
                int t1 = board.Get(x, y + 1);
                int t2 = board.Get(x, y + 2);
                int t3 = board.Get(x, y + 3);

                if (t0 < 0 || t1 < 0 || t2 < 0 || t3 < 0) continue;

                if (t0 == t1 && t1 == t3 && t2 != t0) count++; // AA_A
                if (t0 == t2 && t2 == t3 && t1 != t0) count++; // A_AA
            }
        }

        return count;
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

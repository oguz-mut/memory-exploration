using System.Diagnostics;

// ═══════════════════════════════════════════════════════════════════
// MCTSSolver — Monte Carlo Tree Search with exact first cascade
//              + random playouts for remaining turns
// ═══════════════════════════════════════════════════════════════════

class MCTSSolver
{
    /// <summary>
    /// Solve using MCTS: exact first cascade (real PRNG clone) then random playouts.
    /// Time budget is split across all candidate moves, with a minimum of 5 playouts
    /// per move to ensure fairness.
    /// </summary>
    public SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();

        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
        {
            return new SolverResult
            {
                BestMoves = new List<SolverMove>(),
                PredictedScore = state.Score,
                StatesExplored = 0,
                Strategy = $"MCTS (0 playouts, {timeBudgetMs}ms) — no valid moves"
            };
        }

        // Per-move accumulators
        var scoreAfterFirstMove = new int[validMoves.Count];
        var totalPlayoutScore = new long[validMoves.Count];
        var playoutCount = new int[validMoves.Count];
        SimGameState[] statesAfterMove = new SimGameState[validMoves.Count];

        // Phase 1: exact cascade for every candidate move (uses real cloned PRNG)
        for (int i = 0; i < validMoves.Count; i++)
        {
            var (x, y, dir) = validMoves[i];
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            statesAfterMove[i] = clone;
            scoreAfterFirstMove[i] = clone.Score;
        }

        // Use a simple System.Random for random move selection — playouts don't need
        // a deterministic PRNG; the board's internal MonoRandom still handles piece
        // generation correctly via Clone().
        var rng = new Random();

        // Phase 2: distribute the remaining time across moves.
        // First, give each move its minimum 5 playouts, then keep cycling until time runs out.
        int totalPlayouts = 0;
        long deadlineMs = timeBudgetMs;

        // Minimum 5 playouts per move
        for (int i = 0; i < validMoves.Count; i++)
        {
            if (statesAfterMove[i].IsGameOver)
            {
                // No moves left after first — playout score == score after first move
                totalPlayoutScore[i] = scoreAfterFirstMove[i] * 5L;
                playoutCount[i] = 5;
                totalPlayouts += 5;
                continue;
            }

            for (int p = 0; p < 5; p++)
            {
                totalPlayoutScore[i] += RunPlayout(statesAfterMove[i], rng);
                playoutCount[i]++;
                totalPlayouts++;
            }
        }

        // Round-robin additional playouts until time budget is exhausted
        int idx = 0;
        while (sw.ElapsedMilliseconds < deadlineMs)
        {
            int i = idx % validMoves.Count;
            idx++;

            if (statesAfterMove[i].IsGameOver)
                continue;

            totalPlayoutScore[i] += RunPlayout(statesAfterMove[i], rng);
            playoutCount[i]++;
            totalPlayouts++;
        }

        // Pick the move with the highest average score
        int bestIdx = 0;
        double bestAvg = double.MinValue;
        for (int i = 0; i < validMoves.Count; i++)
        {
            double avg = playoutCount[i] > 0
                ? (double)totalPlayoutScore[i] / playoutCount[i]
                : 0.0;
            if (avg > bestAvg)
            {
                bestAvg = avg;
                bestIdx = i;
            }
        }

        var (bx, by, bdir) = validMoves[bestIdx];
        var bestMove = new SolverMove
        {
            X = bx,
            Y = by,
            Direction = bdir,
            ScoreAfter = scoreAfterFirstMove[bestIdx],
            Description = $"MCTS best ({playoutCount[bestIdx]} playouts, avg {bestAvg:F0})"
        };

        return new SolverResult
        {
            BestMoves = new List<SolverMove> { bestMove },
            PredictedScore = (int)Math.Round(bestAvg),
            StatesExplored = totalPlayouts,
            Strategy = $"MCTS ({totalPlayouts} playouts, {timeBudgetMs}ms)"
        };
    }

    /// <summary>
    /// Run a single random playout from the given post-first-move state to game over.
    /// Returns the final score of the playout.
    /// </summary>
    private static int RunPlayout(SimGameState startState, Random rng)
    {
        var playout = startState.Clone();

        while (!playout.IsGameOver)
        {
            var moves = playout.Board.GetAllValidMoves();
            if (moves.Count == 0) break;

            int pick = rng.Next(moves.Count);
            var (x, y, dir) = moves[pick];
            playout.MakeMove(x, y, dir);
        }

        return playout.Score;
    }
}

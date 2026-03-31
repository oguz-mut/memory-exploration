using System.Diagnostics;

// ═══════════════════════════════════════════════════════════════════
// MCTSSolver — Monte Carlo Tree Search with exact first cascade
//              + epsilon-greedy playouts, UCB1 allocation,
//              and extra turn bonus scoring
// ═══════════════════════════════════════════════════════════════════

class MCTSSolver
{
    private const double UCB1_C = 1.41;          // exploration constant sqrt(2)
    private const double SCORE_NORM = 10_000.0;  // normalisation for UCB1 balance
    private const double EPSILON = 0.30;         // fraction of random (exploration) playout moves
    private const int GREEDY_CANDIDATES = 5;     // candidates evaluated in greedy selection

    /// <summary>
    /// Solve using MCTS: exact first cascade (real PRNG clone), then UCB1-guided
    /// playout allocation with epsilon-greedy move selection inside each playout.
    /// Extra turns earned during playouts receive a score bonus.
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
                Strategy = $"MCTS-UCB1 (0 playouts, {timeBudgetMs}ms) — no valid moves"
            };
        }

        int n = validMoves.Count;

        // Per-move accumulators
        var scoreAfterFirstMove = new int[n];
        var totalPlayoutScore   = new long[n];
        var playoutCount        = new int[n];
        var statesAfterMove     = new SimGameState[n];

        // Bonus per extra turn earned during a playout
        int extraTurnBonus = config.ScoreFor4s * 2;

        // Phase 1: exact cascade for every candidate move (uses real cloned PRNG)
        for (int i = 0; i < n; i++)
        {
            var (x, y, dir) = validMoves[i];
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            statesAfterMove[i] = clone;
            scoreAfterFirstMove[i] = clone.Score;
        }

        var rng = new Random();
        int totalPlayouts = 0;

        // Phase 2: give each move the minimum 3 playouts (UCB1 initialisation)
        for (int i = 0; i < n; i++)
        {
            if (statesAfterMove[i].IsGameOver)
            {
                totalPlayoutScore[i] = scoreAfterFirstMove[i] * 3L;
                playoutCount[i] = 3;
                totalPlayouts += 3;
                continue;
            }

            for (int p = 0; p < 3; p++)
            {
                totalPlayoutScore[i] += RunPlayout(statesAfterMove[i], rng, extraTurnBonus);
                playoutCount[i]++;
                totalPlayouts++;
            }
        }

        // Phase 3: UCB1-guided playout allocation until time budget exhausted
        while (sw.ElapsedMilliseconds < timeBudgetMs)
        {
            // Select move with highest UCB1 score
            int pick = SelectUCB1(totalPlayoutScore, playoutCount, totalPlayouts, n);

            if (statesAfterMove[pick].IsGameOver)
            {
                // No improvement possible; give a small round-robin nudge so we
                // don't spin forever on a finished state
                pick = totalPlayouts % n;
                if (!statesAfterMove[pick].IsGameOver)
                {
                    totalPlayoutScore[pick] += RunPlayout(statesAfterMove[pick], rng, extraTurnBonus);
                    playoutCount[pick]++;
                    totalPlayouts++;
                }
                else
                {
                    totalPlayouts++; // avoid infinite loop when all states are over
                }
                continue;
            }

            totalPlayoutScore[pick] += RunPlayout(statesAfterMove[pick], rng, extraTurnBonus);
            playoutCount[pick]++;
            totalPlayouts++;
        }

        // Pick the move with the highest average score (exploitation — not UCB1)
        int bestIdx = 0;
        double bestAvg = double.MinValue;
        for (int i = 0; i < n; i++)
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
            Description = $"MCTS-UCB1 best ({playoutCount[bestIdx]} playouts, avg {bestAvg:F0})"
        };

        return new SolverResult
        {
            BestMoves = new List<SolverMove> { bestMove },
            PredictedScore = (int)Math.Round(bestAvg),
            StatesExplored = totalPlayouts,
            Strategy = $"MCTS-UCB1 ({totalPlayouts} playouts, {timeBudgetMs}ms)"
        };
    }

    // ───────────────────────────────────────────────────────────────
    // UCB1 selection
    // ───────────────────────────────────────────────────────────────

    private static int SelectUCB1(long[] totalScore, int[] counts, int totalN, int n)
    {
        double logTotal = Math.Log(totalN + 1); // +1 avoids log(0)
        double bestUCB  = double.MinValue;
        int    bestIdx  = 0;

        for (int i = 0; i < n; i++)
        {
            double avg = counts[i] > 0 ? (double)totalScore[i] / counts[i] : 0.0;
            double exploration = counts[i] > 0
                ? UCB1_C * Math.Sqrt(logTotal / counts[i])
                : double.MaxValue / 2; // unvisited → force visit

            double ucb = avg / SCORE_NORM + exploration;
            if (ucb > bestUCB)
            {
                bestUCB = ucb;
                bestIdx = i;
            }
        }

        return bestIdx;
    }

    // ───────────────────────────────────────────────────────────────
    // Epsilon-greedy playout with extra turn bonus
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Run a single playout from the given post-first-move state to game over.
    /// Uses epsilon-greedy move selection: 70% greedy (best of GREEDY_CANDIDATES
    /// random candidates), 30% fully random.
    /// Returns the playout score augmented with extra-turn bonuses.
    /// </summary>
    private static int RunPlayout(SimGameState startState, Random rng, int extraTurnBonus)
    {
        var playout = startState.Clone();
        int extraTurnCount = 0;

        while (!playout.IsGameOver)
        {
            var moves = playout.Board.GetAllValidMoves();
            if (moves.Count == 0) break;

            int pick;
            if (moves.Count == 1 || rng.NextDouble() < EPSILON)
            {
                // 30%: fully random
                pick = rng.Next(moves.Count);
            }
            else
            {
                // 70%: greedy — evaluate up to GREEDY_CANDIDATES random candidates,
                //      pick the one with the highest immediate score delta
                pick = GreedyPick(playout, moves, rng);
            }

            var (x, y, dir) = moves[pick];
            playout.MakeMove(x, y, dir);

            if (playout.IsExtraTurnEarned)
                extraTurnCount++;
        }

        return playout.Score + extraTurnCount * extraTurnBonus;
    }

    /// <summary>
    /// Sample up to GREEDY_CANDIDATES moves from the move list, evaluate each by
    /// cloning the state and calling MakeMove, then return the index of the move
    /// with the best immediate score delta.
    /// </summary>
    private static int GreedyPick(
        SimGameState state,
        List<(int x, int y, MoveDir dir)> moves,
        Random rng)
    {
        int candidateCount = Math.Min(GREEDY_CANDIDATES, moves.Count);

        // Reservoir-sample candidate indices without replacement
        int[] indices = new int[candidateCount];
        for (int i = 0; i < candidateCount; i++) indices[i] = i;
        for (int i = candidateCount; i < moves.Count; i++)
        {
            int j = rng.Next(i + 1);
            if (j < candidateCount) indices[j] = i;
        }

        int bestIdx   = indices[0];
        int bestDelta = int.MinValue;
        int baseScore = state.Score;

        for (int c = 0; c < candidateCount; c++)
        {
            int idx = indices[c];
            var (x, y, dir) = moves[idx];
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            int delta = clone.Score - baseScore;
            if (delta > bestDelta)
            {
                bestDelta = delta;
                bestIdx   = idx;
            }
        }

        return bestIdx;
    }
}

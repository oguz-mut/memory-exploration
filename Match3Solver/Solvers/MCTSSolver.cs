using System.Diagnostics;

// ═══════════════════════════════════════════════════════════════════
// MCTSSolver — Monte Carlo Tree Search with exact first cascade
//              + UCB1 parallel playouts, clone-based greedy move
//              selection, and adaptive time allocation
// ═══════════════════════════════════════════════════════════════════

class MCTSSolver
{
    private const double UCB1_C      = 1.41;        // exploration constant sqrt(2)
    private const double EPSILON     = 0.15;        // fraction of random (exploration) playout moves
    private const int    BONUS_MULTI = 5;           // multiplier for moves creating 4+ matches

    /// <summary>
    /// Solve using MCTS: exact first cascade (real PRNG clone), then UCB1-guided
    /// parallel playout allocation.  Move selection inside playouts uses a cheap
    /// neighbor-count heuristic instead of clone-5 epsilon-greedy.
    /// Adaptive time budget: 60 % more time when ≤2 turns remain, 40 % for 3 turns.
    /// </summary>
    public SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000)
    {
        if (timeBudgetMs <= 0) timeBudgetMs = 3000;

        // Task 3: Adaptive time allocation — endgame decisions are more impactful
        int adaptiveMs = state.TurnsRemaining switch
        {
            <= 2 => (int)(timeBudgetMs * 1.6),  // 4.8s at default budget
            3    => (int)(timeBudgetMs * 1.4),  // 4.2s
            4    => (int)(timeBudgetMs * 1.2),  // 3.6s
            _    => timeBudgetMs
        };

        var sw = Stopwatch.StartNew();

        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
        {
            return new SolverResult
            {
                BestMoves       = new List<SolverMove>(),
                PredictedScore  = state.Score,
                StatesExplored  = 0,
                Strategy        = $"MCTS-UCB1-Par (0 playouts, {adaptiveMs}ms) — no valid moves"
            };
        }

        int n = validMoves.Count;

        // Per-move accumulators (long to avoid overflow with many parallel playouts)
        var scoreAfterFirstMove = new int[n];
        var totalPlayoutScore   = new long[n];
        var playoutCount        = new int[n];
        var postMoveStates      = new SimGameState[n];

        int extraTurnBonus  = config.ScoreFor4s * BONUS_MULTI;
        int tierUpBonus     = config.ScoreFor5s * 10;
        int[] pieceReqsPerTier = config.PieceReqsPerTier;

        // ─────────────────────────────────────────────────────────────
        // Phase 1: exact cascade for every candidate move (real PRNG)
        // ─────────────────────────────────────────────────────────────
        for (int i = 0; i < n; i++)
        {
            var (x, y, dir) = validMoves[i];
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            postMoveStates[i]      = clone;
            scoreAfterFirstMove[i] = clone.Score;
        }

        // ─────────────────────────────────────────────────────────────
        // Phase 2: warm-up — 3 playouts per move (single-threaded)
        // ─────────────────────────────────────────────────────────────
        var warmupRng = new Random();
        int totalPlayoutsShared = 0;

        for (int i = 0; i < n; i++)
        {
            if (postMoveStates[i].IsGameOver)
            {
                totalPlayoutScore[i] = scoreAfterFirstMove[i] * 3L;
                playoutCount[i]      = 3;
                totalPlayoutsShared += 3;
                continue;
            }

            for (int p = 0; p < 3; p++)
            {
                totalPlayoutScore[i] += RunPlayout(postMoveStates[i], warmupRng, extraTurnBonus, tierUpBonus, pieceReqsPerTier);
                playoutCount[i]++;
                totalPlayoutsShared++;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Compute dynamic normalization from observed scores
        // ─────────────────────────────────────────────────────────────
        double maxObservedScore = 0.0;

        // Check phase 1 scores
        for (int i = 0; i < n; i++)
        {
            if (scoreAfterFirstMove[i] > maxObservedScore)
                maxObservedScore = scoreAfterFirstMove[i];
        }

        // Check warm-up playout averages
        for (int i = 0; i < n; i++)
        {
            if (playoutCount[i] > 0)
            {
                double avg = (double)totalPlayoutScore[i] / playoutCount[i];
                if (avg > maxObservedScore)
                    maxObservedScore = avg;
            }
        }

        double dynamicNorm = Math.Max(1000.0, maxObservedScore * 3.0);

        // ─────────────────────────────────────────────────────────────
        // Phase 3: parallel UCB1-guided playout loop
        // Task 1: multi-threaded execution
        // ─────────────────────────────────────────────────────────────
        int threadCount = Math.Max(1, Environment.ProcessorCount - 2);
        var threads     = new Thread[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadId = t;
            threads[t] = new Thread(() =>
            {
                // Each thread gets its own Random — Random is not thread-safe
                var rng = new Random(Environment.TickCount ^ threadId);

                while (sw.ElapsedMilliseconds < adaptiveMs)
                {
                    // Read shared arrays without lock — approximate UCB1 is fine
                    int snap = Volatile.Read(ref totalPlayoutsShared);
                    int pick = SelectUCB1(totalPlayoutScore, playoutCount, snap, n, dynamicNorm);

                    if (postMoveStates[pick].IsGameOver)
                    {
                        // Spin-safe: bump the counter so we don't loop forever
                        Interlocked.Increment(ref totalPlayoutsShared);
                        continue;
                    }

                    // Clone is independent — no shared mutation
                    var playoutState = postMoveStates[pick].Clone();
                    int score = RunPlayout(playoutState, rng, extraTurnBonus, tierUpBonus, pieceReqsPerTier);

                    Interlocked.Add(ref totalPlayoutScore[pick], score);
                    Interlocked.Increment(ref playoutCount[pick]);
                    Interlocked.Increment(ref totalPlayoutsShared);
                }
            });
            threads[t].IsBackground = true;
            threads[t].Start();
        }

        foreach (var th in threads) th.Join();

        int totalPlayouts = Volatile.Read(ref totalPlayoutsShared);

        // ─────────────────────────────────────────────────────────────
        // Select best move by highest average (exploitation)
        // ─────────────────────────────────────────────────────────────
        int    bestIdx = 0;
        double bestAvg = double.MinValue;
        for (int i = 0; i < n; i++)
        {
            double avg = playoutCount[i] > 0
                ? (double)totalPlayoutScore[i] / playoutCount[i]
                : 0.0;
            if (avg > bestAvg) { bestAvg = avg; bestIdx = i; }
        }

        var (bx, by, bdir) = validMoves[bestIdx];
        var bestMove = new SolverMove
        {
            X          = bx,
            Y          = by,
            Direction  = bdir,
            ScoreAfter = scoreAfterFirstMove[bestIdx],
            Description = $"MCTS-UCB1-Par best ({playoutCount[bestIdx]} playouts, avg {bestAvg:F0})"
        };

        return new SolverResult
        {
            BestMoves      = new List<SolverMove> { bestMove },
            PredictedScore = (int)Math.Round(bestAvg),
            StatesExplored = totalPlayouts,
            Strategy       = $"MCTS-UCB1-Par ({totalPlayouts} playouts, {adaptiveMs}ms, {threadCount}t)"
        };
    }

    // ───────────────────────────────────────────────────────────────
    // UCB1 selection
    // Reads shared arrays without lock — approximate reads are OK.
    // ───────────────────────────────────────────────────────────────

    private static int SelectUCB1(long[] totalScore, int[] counts, int totalN, int n, double dynamicNorm)
    {
        double logTotal = Math.Log(totalN + 1); // +1 avoids log(0)
        double bestUCB  = double.MinValue;
        int    bestIdx  = 0;

        for (int i = 0; i < n; i++)
        {
            int c = Volatile.Read(ref counts[i]);
            double avg = c > 0 ? (double)Volatile.Read(ref totalScore[i]) / c : 0.0;
            double exploration = c > 0
                ? UCB1_C * Math.Sqrt(logTotal / c)
                : double.MaxValue / 2; // unvisited → force visit

            double ucb = avg / dynamicNorm + exploration;
            if (ucb > bestUCB) { bestUCB = ucb; bestIdx = i; }
        }

        return bestIdx;
    }

    // ───────────────────────────────────────────────────────────────
    // Playout with lightweight neighbor-count heuristic
    // Task 2: replaces expensive clone-5 epsilon-greedy
    // ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Run a single playout to game over using:
    ///   15 % random (epsilon), 85 % greedy by neighbor-count heuristic.
    /// The heuristic scores each move by how long a run the swapped
    /// piece would create in each axis — O(board_dim) per move, no clone needed.
    /// 4+ length moves get a x5 bonus (extra-turn value).
    /// </summary>
    private static int RunPlayout(SimGameState startState, Random rng, int extraTurnBonus, int tierUpBonus, int[] pieceReqsPerTier)
    {
        var playout       = startState.Clone();
        int extraTurnCount = 0;

        while (!playout.IsGameOver)
        {
            var moves = playout.Board.GetAllValidMoves();
            if (moves.Count == 0) break;

            (int x, int y, MoveDir dir) chosen;
            if (moves.Count == 1 || rng.NextDouble() < EPSILON)
            {
                chosen = moves[rng.Next(moves.Count)];
            }
            else
            {
                chosen = PickPlayoutMove(playout, moves, rng, extraTurnBonus, tierUpBonus, pieceReqsPerTier);
            }

            playout.MakeMove(chosen.x, chosen.y, chosen.dir);

            if (playout.IsExtraTurnEarned)
                extraTurnCount++;
        }

        return playout.Score + extraTurnCount * extraTurnBonus;
    }

    /// <summary>
    /// Clone-based greedy: sample up to 5 random candidates, simulate each with
    /// full cascade via Clone()+MakeMove(), pick the one with highest score delta.
    /// Extra turns get a bonus multiplier. More expensive per call than a heuristic,
    /// but each playout plays realistically — quality over quantity.
    /// </summary>
    private static (int x, int y, MoveDir dir) PickPlayoutMove(
        SimGameState playout,
        List<(int x, int y, MoveDir dir)> moves,
        Random rng,
        int extraTurnBonus,
        int tierUpBonus,
        int[] pieceReqsPerTier)
    {
        int sampleSize = Math.Min(8, moves.Count);
        int bestIdx = 0;
        int bestScore = int.MinValue;

        // Fisher-Yates partial shuffle to get sampleSize random candidates
        for (int i = 0; i < sampleSize; i++)
        {
            int j = rng.Next(i, moves.Count);
            (moves[i], moves[j]) = (moves[j], moves[i]);
        }

        int baseLine    = playout.Score;
        int currentTier = playout.Tier;

        // Extra turns are more valuable when turns are scarce
        int effectiveExtraTurnBonus = playout.TurnsRemaining <= 3
            ? extraTurnBonus * 2
            : extraTurnBonus;

        for (int i = 0; i < sampleSize; i++)
        {
            var (mx, my, mdir) = moves[i];
            var clone = playout.Clone();
            clone.MakeMove(mx, my, mdir);
            int score = clone.Score - baseLine;

            if (clone.IsExtraTurnEarned)
                score += effectiveExtraTurnBonus;

            // Tier-progress weighting
            if (clone.Tier > currentTier)
            {
                // Tier-up achieved — large bonus reflecting unlocked items and score burst
                score += tierUpBonus;
            }
            else if (pieceReqsPerTier != null && currentTier < pieceReqsPerTier.Length)
            {
                int req = pieceReqsPerTier[currentTier];
                if (req > 0)
                {
                    // Sum piece-type progress made by this move
                    int progressDelta = 0;
                    int activePieces  = playout.Board.ActivePieceTypes;
                    for (int p = 0; p < activePieces
                                 && p < clone.TierPiecesMatched.Length
                                 && p < playout.TierPiecesMatched.Length; p++)
                    {
                        progressDelta += clone.TierPiecesMatched[p] - playout.TierPiecesMatched[p];
                    }

                    if (progressDelta > 0)
                        score += (tierUpBonus / req) * progressDelta;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }

        return moves[bestIdx];
    }
}

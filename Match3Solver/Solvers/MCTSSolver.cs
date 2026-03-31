using System.Diagnostics;

// ═══════════════════════════════════════════════════════════════════
// MCTSSolver — Monte Carlo Tree Search with exact first cascade
//              + UCB1 parallel playouts, lightweight neighbor-count
//              heuristic move selection, and adaptive time allocation
// ═══════════════════════════════════════════════════════════════════

class MCTSSolver
{
    private const double UCB1_C      = 1.41;        // exploration constant sqrt(2)
    private const double SCORE_NORM  = 10_000.0;    // normalisation for UCB1 balance
    private const double EPSILON     = 0.30;        // fraction of random (exploration) playout moves
    private const int    BONUS_MULTI = 3;           // multiplier for moves creating 4+ matches

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

        int extraTurnBonus = config.ScoreFor4s * 2;

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
                totalPlayoutScore[i] += RunPlayout(postMoveStates[i], warmupRng, extraTurnBonus);
                playoutCount[i]++;
                totalPlayoutsShared++;
            }
        }

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
                    int pick = SelectUCB1(totalPlayoutScore, playoutCount, snap, n);

                    if (postMoveStates[pick].IsGameOver)
                    {
                        // Spin-safe: bump the counter so we don't loop forever
                        Interlocked.Increment(ref totalPlayoutsShared);
                        continue;
                    }

                    // Clone is independent — no shared mutation
                    var playoutState = postMoveStates[pick].Clone();
                    int score = RunPlayout(playoutState, rng, extraTurnBonus);

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

    private static int SelectUCB1(long[] totalScore, int[] counts, int totalN, int n)
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

            double ucb = avg / SCORE_NORM + exploration;
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
    ///   30 % random (epsilon), 70 % greedy by neighbor-count heuristic.
    /// The heuristic scores each move by how long a run the swapped
    /// piece would create in each axis — O(board_dim) per move, no clone needed.
    /// 4+ length moves get a x3 bonus (extra-turn value).
    /// </summary>
    private static int RunPlayout(SimGameState startState, Random rng, int extraTurnBonus)
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
                chosen = PickPlayoutMove(playout.Board, moves, rng);
            }

            playout.MakeMove(chosen.x, chosen.y, chosen.dir);

            if (playout.IsExtraTurnEarned)
                extraTurnCount++;
        }

        return playout.Score + extraTurnCount * extraTurnBonus;
    }

    /// <summary>
    /// Score each move by the run length the swapped pieces would form, then
    /// return the move with the highest score.  No board clone required — the
    /// heuristic inspects neighbors without mutating the board.
    /// 4+ length bonus: multiply score by BONUS_MULTI (extra-turn value).
    /// </summary>
    private static (int x, int y, MoveDir dir) PickPlayoutMove(
        SimBoard board,
        List<(int x, int y, MoveDir dir)> moves,
        Random rng)
    {
        int bestMoveIdx = 0;
        int bestScore   = -1;

        for (int i = 0; i < moves.Count; i++)
        {
            var (mx, my, mdir) = moves[i];
            var target = SimBoard.DeltaByDir(mx, my, mdir);

            int type1 = board.Get(mx, my);
            int type2 = board.Get(target.X, target.Y);

            // Run length for piece1 landing at target position
            int run1 = CountRunLength(board, target.X, target.Y, type1);
            // Run length for piece2 landing at source position
            int run2 = CountRunLength(board, mx, my, type2);

            int score = run1 + run2;

            // Bonus for 4+ match (grants an extra turn)
            if (run1 >= 4 || run2 >= 4)
                score *= BONUS_MULTI;

            if (score > bestScore)
            {
                bestScore   = score;
                bestMoveIdx = i;
            }
        }

        return moves[bestMoveIdx];
    }

    /// <summary>
    /// Returns the maximum run length (horizontal or vertical) that a piece of
    /// <paramref name="type"/> would form if placed at (x, y), treating the
    /// current occupant as already replaced.  O(Width + Height).
    /// </summary>
    private static int CountRunLength(SimBoard board, int x, int y, int type)
    {
        // Horizontal run
        int left  = 0;
        int right = 0;
        for (int dx = x - 1; dx >= 0          && board.Get(dx, y) == type; dx--) left++;
        for (int dx = x + 1; dx < board.Width  && board.Get(dx, y) == type; dx++) right++;
        int horizLen = left + right + 1;

        // Vertical run
        int down = 0;
        int up   = 0;
        for (int dy = y - 1; dy >= 0           && board.Get(x, dy) == type; dy--) down++;
        for (int dy = y + 1; dy < board.Height  && board.Get(x, dy) == type; dy++) up++;
        int vertLen = down + up + 1;

        return Math.Max(horizLen, vertLen);
    }
}

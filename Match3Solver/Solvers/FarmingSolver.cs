using System.Diagnostics;

/// <summary>
/// Farming solver: maximizes item value collected while keeping game score under a cap.
///
/// Strategy:
/// 1. Never exceed ScoreCap — moves that would push score over get a massive penalty
/// 2. Maximize total item value (sum of matched pieces × their item value)
/// 3. Tier up when possible (unlocks higher-value pieces on the board)
/// 4. Extra turns are valuable (more turns = more items) but only if score budget allows
/// 5. When score is close to cap, prefer low-scoring moves that still match valuable pieces
/// </summary>
class FarmingSolver : ISolver
{
    private int _statesExplored;
    private Stopwatch _timer = new();
    private int _timeBudgetMs;
    private int _scoreCap;
    private int[] _pieceValues = [];

    public int ScoreCap { get; set; } = 1350;

    public SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000)
    {
        _statesExplored = 0;
        _timeBudgetMs = timeBudgetMs;
        _scoreCap = ScoreCap;
        _pieceValues = config.PieceValues.Length > 0 ? config.PieceValues : new int[config.Pieces.Length];
        _timer = Stopwatch.StartNew();

        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
        {
            return new SolverResult
            {
                BestMoves = new List<SolverMove>(),
                PredictedScore = state.Score,
                StatesExplored = 0,
                Strategy = $"Farming (no moves, cap={_scoreCap})"
            };
        }

        // ── Depth 1: evaluate all moves ──
        var moveEvals = new List<(int x, int y, MoveDir dir, int farmScore, int gameScore, bool extraTurn)>();
        foreach (var (x, y, dir) in validMoves)
        {
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;
            int fs = FarmingEval(clone);
            moveEvals.Add((x, y, dir, fs, clone.Score, clone.IsExtraTurnEarned));
        }

        // Sort by farming score descending
        moveEvals.Sort((a, b) => b.farmScore.CompareTo(a.farmScore));

        int maxDepthReached = 1;
        var best = moveEvals[0];
        SolverMove bestMove = MakeSolverMove(best.x, best.y, best.dir, best.gameScore, best.farmScore, 1);
        int bestFarmScore = best.farmScore;

        // ── Depth 2: top 15 + all extra-turn moves ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top2 = Math.Min(15, moveEvals.Count);
            var indices = new List<int>(Enumerable.Range(0, top2));
            for (int i = top2; i < moveEvals.Count; i++)
                if (moveEvals[i].extraTurn) indices.Add(i);

            int bestScore2 = bestFarmScore;
            SolverMove? bestMove2 = null;

            foreach (int i in indices)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                var (mx, my, mdir, _, _, _) = moveEvals[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                int remaining = clone1.IsExtraTurnEarned ? 2 : 1;
                int extensions = 4;
                int score = SearchBranch(clone1, remaining, extensions);
                if (score > bestScore2)
                {
                    bestScore2 = score;
                    bestMove2 = MakeSolverMove(mx, my, mdir, clone1.Score, score, 2);
                }
            }

            if (bestMove2 != null) { bestMove = bestMove2; bestFarmScore = bestScore2; maxDepthReached = 2; }
            else if (_timer.ElapsedMilliseconds < _timeBudgetMs) maxDepthReached = 2;
        }

        // ── Depth 3: top 8 + extra-turn moves ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top3 = Math.Min(8, moveEvals.Count);
            var indices = new List<int>(Enumerable.Range(0, top3));
            for (int i = top3; i < moveEvals.Count; i++)
                if (moveEvals[i].extraTurn) indices.Add(i);

            int bestScore3 = bestFarmScore;
            SolverMove? bestMove3 = null;

            foreach (int i in indices)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                var (mx, my, mdir, _, _, _) = moveEvals[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                int remaining = clone1.IsExtraTurnEarned ? 3 : 2;
                int extensions = 4;
                int score = SearchBranch(clone1, remaining, extensions);
                if (score > bestScore3)
                {
                    bestScore3 = score;
                    bestMove3 = MakeSolverMove(mx, my, mdir, clone1.Score, score, 3);
                }
            }

            if (bestMove3 != null) { bestMove = bestMove3; bestFarmScore = bestScore3; maxDepthReached = 3; }
            else if (_timer.ElapsedMilliseconds < _timeBudgetMs) maxDepthReached = 3;
        }

        // ── Depth 4: top 3 + extra-turn moves ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top4 = Math.Min(3, moveEvals.Count);
            var indices = new List<int>(Enumerable.Range(0, top4));
            for (int i = top4; i < moveEvals.Count; i++)
                if (moveEvals[i].extraTurn) indices.Add(i);

            int bestScore4 = bestFarmScore;
            SolverMove? bestMove4 = null;

            foreach (int i in indices)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                var (mx, my, mdir, _, _, _) = moveEvals[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                int remaining = clone1.IsExtraTurnEarned ? 4 : 3;
                int extensions = 4;
                int score = SearchBranch(clone1, remaining, extensions);
                if (score > bestScore4)
                {
                    bestScore4 = score;
                    bestMove4 = MakeSolverMove(mx, my, mdir, clone1.Score, score, 4);
                }
            }

            if (bestMove4 != null) { bestMove = bestMove4; maxDepthReached = 4; }
            else if (_timer.ElapsedMilliseconds < _timeBudgetMs) maxDepthReached = 4;
        }

        return new SolverResult
        {
            BestMoves = new List<SolverMove> { bestMove },
            PredictedScore = bestMove.ScoreAfter,
            StatesExplored = _statesExplored,
            Strategy = $"Farming depth {maxDepthReached} cap={_scoreCap} ({_timer.ElapsedMilliseconds}ms)"
        };
    }

    /// <summary>
    /// Evaluate a game state for farming purposes.
    /// Returns a score that balances item value, tier progress, and score cap compliance.
    /// </summary>
    private int FarmingEval(SimGameState state)
    {
        // ── Item value: the primary metric ──
        int itemValue = 0;
        for (int i = 0; i < state.TotalPiecesMatched.Length && i < _pieceValues.Length; i++)
            itemValue += state.TotalPiecesMatched[i] * _pieceValues[i];

        // ── Tier bonus: reaching higher tiers unlocks better pieces ──
        // Scale down compared to IterativeSolver — tier is means to an end, not the goal
        int tierBonus = state.Tier * 300;

        // ── Tier progress: how close to next tier-up ──
        int tierProgress = 0;
        if (state.TierPiecesMatched.Length > 0)
        {
            int activePieces = state.Board.ActivePieceTypes;
            int minProgress = int.MaxValue;
            for (int i = 0; i < activePieces && i < state.TierPiecesMatched.Length; i++)
                minProgress = Math.Min(minProgress, state.TierPiecesMatched[i]);
            if (minProgress != int.MaxValue)
                tierProgress = minProgress * 20; // reward balanced matching
        }

        // ── Extra turns: more turns = more items ──
        int turnsBonus = state.TurnsRemaining * 30;

        // ── Score cap penalty ──
        int capPenalty = 0;
        if (state.Score >= _scoreCap)
        {
            // Massive penalty for exceeding cap, scaled by how much we went over
            int overshoot = state.Score - _scoreCap;
            capPenalty = -50000 - overshoot * 100;
        }
        else
        {
            // Small bonus for staying further from cap (more room for future moves)
            int headroom = _scoreCap - state.Score;
            capPenalty = Math.Min(headroom / 5, 100); // gentle nudge, up to 100
        }

        return itemValue + tierBonus + tierProgress + turnsBonus + capPenalty;
    }

    private int SearchBranch(SimGameState state, int remainingDepth, int extraExtensionsLeft)
    {
        if (remainingDepth == 0 || _timer.ElapsedMilliseconds >= _timeBudgetMs)
            return FarmingEval(state);

        var moves = state.Board.GetAllValidMoves();
        if (moves.Count == 0) return FarmingEval(state);

        // Order moves at non-leaf levels
        if (remainingDepth > 1)
        {
            var ordered = new List<(int x, int y, MoveDir dir, int farmScore, bool extraTurn)>(moves.Count);
            foreach (var (x, y, dir) in moves)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                var probe = state.Clone();
                probe.MakeMove(x, y, dir);
                _statesExplored++;
                ordered.Add((x, y, dir, FarmingEval(probe), probe.IsExtraTurnEarned));
            }
            // Sort: prefer moves under cap, then by farming score
            ordered.Sort((a, b) => b.farmScore.CompareTo(a.farmScore));
            moves = ordered.Select(o => (o.x, o.y, o.dir)).ToList();
        }

        int bestScore = FarmingEval(state);
        foreach (var (x, y, dir) in moves)
        {
            if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;

            int nextDepth;
            int nextExtensions;
            if (clone.IsExtraTurnEarned && extraExtensionsLeft > 0)
            {
                nextDepth = remainingDepth;
                nextExtensions = extraExtensionsLeft - 1;
            }
            else
            {
                nextDepth = remainingDepth - 1;
                nextExtensions = extraExtensionsLeft;
            }

            int score = SearchBranch(clone, nextDepth, nextExtensions);
            if (score > bestScore) bestScore = score;
        }
        return bestScore;
    }

    private static SolverMove MakeSolverMove(int x, int y, MoveDir dir, int gameScore, int farmScore, int depth) =>
        new SolverMove
        {
            X = x,
            Y = y,
            Direction = dir,
            ScoreAfter = gameScore,
            Description = $"({x},{y}) {dir} depth={depth} farm={farmScore}"
        };
}

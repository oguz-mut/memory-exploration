using System.Diagnostics;

class IterativeSolver : ISolver
{
    private int _statesExplored;
    private Stopwatch _timer = new();
    private int _timeBudgetMs;

    public SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000)
    {
        _statesExplored = 0;
        _timeBudgetMs = timeBudgetMs;
        _timer = Stopwatch.StartNew();

        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
        {
            return new SolverResult
            {
                BestMoves = new List<SolverMove>(),
                PredictedScore = state.CascadeScore,
                StatesExplored = 0,
                Strategy = "IterativeSolver (no moves available)"
            };
        }

        int extraTurnBonus = config.ScoreFor3s * 3;

        // ── Depth 1: score all moves ──
        var moveScores = new List<(int x, int y, MoveDir dir, int score, bool extraTurn)>();
        foreach (var (x, y, dir) in validMoves)
        {
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;
            int extraTurnValue = clone.IsExtraTurnEarned ? extraTurnBonus : 0;
            moveScores.Add((x, y, dir, clone.CascadeScore + extraTurnValue, clone.IsExtraTurnEarned));
        }

        // Sort: extra-turn moves first, then by score descending
        moveScores.Sort((a, b) =>
        {
            if (a.extraTurn != b.extraTurn) return b.extraTurn.CompareTo(a.extraTurn);
            return b.score.CompareTo(a.score);
        });

        int maxDepthReached = 1;
        var best = moveScores[0];
        SolverMove bestFirstMove = MakeSolverMove(best.x, best.y, best.dir, best.score, 1);

        // ── Depth 2: top 15 moves + all extra-turn moves ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top2 = Math.Min(15, moveScores.Count);
            // Always include extra-turn moves even if outside top-N
            var depth2Indices = new List<int>(Enumerable.Range(0, top2));
            for (int i = top2; i < moveScores.Count; i++)
                if (moveScores[i].extraTurn) depth2Indices.Add(i);

            int bestScore2 = best.score;
            SolverMove? bestFirstMove2 = null;

            foreach (int i in depth2Indices)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

                var (mx, my, mdir, _, _) = moveScores[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                // Extra turn on first move: extend branch by 1, consume 1 extension slot
                int remaining = clone1.IsExtraTurnEarned ? 2 : 1;
                int extensions = 4;

                int etv = clone1.IsExtraTurnEarned ? extraTurnBonus : 0;
                int score = SearchBranch(clone1, remaining, extensions, extraTurnBonus) + etv;
                if (score > bestScore2)
                {
                    bestScore2 = score;
                    bestFirstMove2 = MakeSolverMove(mx, my, mdir, score, 2);
                }
            }

            if (bestFirstMove2 != null)
            {
                bestFirstMove = bestFirstMove2;
                maxDepthReached = 2;
            }
            else if (maxDepthReached < 2 && _timer.ElapsedMilliseconds < _timeBudgetMs)
            {
                maxDepthReached = 2;
            }
        }

        // ── Depth 3: top 8 moves + all extra-turn moves ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top3 = Math.Min(8, moveScores.Count);
            var depth3Indices = new List<int>(Enumerable.Range(0, top3));
            for (int i = top3; i < moveScores.Count; i++)
                if (moveScores[i].extraTurn) depth3Indices.Add(i);

            int bestScore3 = bestFirstMove.ScoreAfter;
            SolverMove? bestFirstMove3 = null;

            foreach (int i in depth3Indices)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

                var (mx, my, mdir, _, _) = moveScores[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                int remaining = clone1.IsExtraTurnEarned ? 3 : 2;
                int extensions = 4;

                int etv = clone1.IsExtraTurnEarned ? extraTurnBonus : 0;
                int score = SearchBranch(clone1, remaining, extensions, extraTurnBonus) + etv;
                if (score > bestScore3)
                {
                    bestScore3 = score;
                    bestFirstMove3 = MakeSolverMove(mx, my, mdir, score, 3);
                }
            }

            if (bestFirstMove3 != null)
            {
                bestFirstMove = bestFirstMove3;
                maxDepthReached = 3;
            }
            else if (maxDepthReached < 3 && _timer.ElapsedMilliseconds < _timeBudgetMs)
            {
                maxDepthReached = 3;
            }
        }

        // ── Depth 4: top 3 moves + all extra-turn moves ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top4 = Math.Min(3, moveScores.Count);
            var depth4Indices = new List<int>(Enumerable.Range(0, top4));
            for (int i = top4; i < moveScores.Count; i++)
                if (moveScores[i].extraTurn) depth4Indices.Add(i);

            int bestScore4 = bestFirstMove.ScoreAfter;
            SolverMove? bestFirstMove4 = null;

            foreach (int i in depth4Indices)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

                var (mx, my, mdir, _, _) = moveScores[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                int remaining = clone1.IsExtraTurnEarned ? 4 : 3;
                int extensions = 4;

                int etv = clone1.IsExtraTurnEarned ? extraTurnBonus : 0;
                int score = SearchBranch(clone1, remaining, extensions, extraTurnBonus) + etv;
                if (score > bestScore4)
                {
                    bestScore4 = score;
                    bestFirstMove4 = MakeSolverMove(mx, my, mdir, score, 4);
                }
            }

            if (bestFirstMove4 != null)
            {
                bestFirstMove = bestFirstMove4;
                maxDepthReached = 4;
            }
            else if (maxDepthReached < 4 && _timer.ElapsedMilliseconds < _timeBudgetMs)
            {
                maxDepthReached = 4;
            }
        }

        return new SolverResult
        {
            BestMoves = new List<SolverMove> { bestFirstMove },
            PredictedScore = bestFirstMove.ScoreAfter,
            StatesExplored = _statesExplored,
            Strategy = $"Iterative depth {maxDepthReached} ({_timer.ElapsedMilliseconds}ms)"
        };
    }

    /// <summary>
    /// Recursively searches for the best score reachable within remainingDepth moves.
    /// When a move earns an extra turn (IsExtraTurnEarned), the depth is not decremented —
    /// the free turn is explored at the same remaining depth. Capped at extraExtensionsLeft
    /// extra-turn extensions per path to prevent runaway searches.
    /// Extra-turn bonus is added to score comparisons to signal compounding value.
    /// </summary>
    private int SearchBranch(SimGameState state, int remainingDepth, int extraExtensionsLeft, int extraTurnBonus)
    {
        if (remainingDepth == 0 || _timer.ElapsedMilliseconds >= _timeBudgetMs)
            return state.CascadeScore;

        var moves = state.Board.GetAllValidMoves();
        if (moves.Count == 0) return state.CascadeScore;

        // Order moves at non-leaf levels: extra-turn moves first, then by score desc
        if (remainingDepth > 1)
        {
            var ordered = new List<(int x, int y, MoveDir dir, int score, bool extraTurn)>(moves.Count);
            foreach (var (x, y, dir) in moves)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                var probe = state.Clone();
                probe.MakeMove(x, y, dir);
                _statesExplored++;
                ordered.Add((x, y, dir, probe.CascadeScore, probe.IsExtraTurnEarned));
            }
            ordered.Sort((a, b) =>
            {
                if (a.extraTurn != b.extraTurn) return b.extraTurn.CompareTo(a.extraTurn);
                return b.score.CompareTo(a.score);
            });
            moves = ordered.Select(o => (o.x, o.y, o.dir)).ToList();
        }

        int bestScore = state.CascadeScore;
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
                nextDepth = remainingDepth;           // free turn: don't consume depth
                nextExtensions = extraExtensionsLeft - 1;
            }
            else
            {
                nextDepth = remainingDepth - 1;
                nextExtensions = extraExtensionsLeft;
            }

            int score = SearchBranch(clone, nextDepth, nextExtensions, extraTurnBonus);
            int bonus = clone.IsExtraTurnEarned ? extraTurnBonus : 0;
            if (score + bonus > bestScore) bestScore = score + bonus;
        }
        return bestScore;
    }

    private static SolverMove MakeSolverMove(int x, int y, MoveDir dir, int scoreAfter, int depth) =>
        new SolverMove
        {
            X = x,
            Y = y,
            Direction = dir,
            ScoreAfter = scoreAfter,
            Description = $"({x},{y}) {dir} depth={depth}"
        };
}

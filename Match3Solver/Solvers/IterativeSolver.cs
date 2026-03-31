using System.Diagnostics;

class IterativeSolver
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
                PredictedScore = state.Score,
                StatesExplored = 0,
                Strategy = "IterativeSolver (no moves available)"
            };
        }

        // ── Depth 1: score all moves ──
        var moveScores = new List<(int x, int y, MoveDir dir, int score, bool extraTurn)>();
        foreach (var (x, y, dir) in validMoves)
        {
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;
            moveScores.Add((x, y, dir, clone.Score, clone.IsExtraTurnEarned));
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

        // ── Depth 2: top 8 moves ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top2 = Math.Min(8, moveScores.Count);
            int bestScore2 = best.score;
            SolverMove? bestFirstMove2 = null;

            for (int i = 0; i < top2; i++)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

                var (mx, my, mdir, _, _) = moveScores[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                var secondMoves = clone1.Board.GetAllValidMoves();
                foreach (var (sx, sy, sdir) in secondMoves)
                {
                    if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                    var clone2 = clone1.Clone();
                    clone2.MakeMove(sx, sy, sdir);
                    _statesExplored++;

                    if (clone2.Score > bestScore2)
                    {
                        bestScore2 = clone2.Score;
                        bestFirstMove2 = MakeSolverMove(mx, my, mdir, clone2.Score, 2);
                    }
                }
            }

            if (bestFirstMove2 != null)
            {
                bestFirstMove = bestFirstMove2;
                maxDepthReached = 2;
            }
            else if (maxDepthReached < 2 && _timer.ElapsedMilliseconds < _timeBudgetMs)
            {
                // Depth 2 completed but didn't beat depth 1 — still count it
                maxDepthReached = 2;
            }
        }

        // ── Depth 3: top 5 moves, 3-level deep ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top3 = Math.Min(5, moveScores.Count);
            int bestScore3 = bestFirstMove.ScoreAfter;
            SolverMove? bestFirstMove3 = null;

            for (int i = 0; i < top3; i++)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

                var (mx, my, mdir, _, _) = moveScores[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                var secondMoves = clone1.Board.GetAllValidMoves();
                // Order second moves: extra-turn first, then by score
                var second2 = OrderMoves(clone1, secondMoves);

                foreach (var (sx, sy, sdir, _, _) in second2)
                {
                    if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                    var clone2 = clone1.Clone();
                    clone2.MakeMove(sx, sy, sdir);
                    _statesExplored++;

                    var thirdMoves = clone2.Board.GetAllValidMoves();
                    foreach (var (tx, ty, tdir) in thirdMoves)
                    {
                        if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                        var clone3 = clone2.Clone();
                        clone3.MakeMove(tx, ty, tdir);
                        _statesExplored++;

                        if (clone3.Score > bestScore3)
                        {
                            bestScore3 = clone3.Score;
                            bestFirstMove3 = MakeSolverMove(mx, my, mdir, clone3.Score, 3);
                        }
                    }
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

        // ── Depth 4: top 3 moves, 4-level deep ──
        if (_timer.ElapsedMilliseconds < _timeBudgetMs)
        {
            int top4 = Math.Min(3, moveScores.Count);
            int bestScore4 = bestFirstMove.ScoreAfter;
            SolverMove? bestFirstMove4 = null;

            for (int i = 0; i < top4; i++)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

                var (mx, my, mdir, _, _) = moveScores[i];
                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                var secondMoves = clone1.Board.GetAllValidMoves();
                var second2 = OrderMoves(clone1, secondMoves);

                foreach (var (sx, sy, sdir, _, _) in second2)
                {
                    if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                    var clone2 = clone1.Clone();
                    clone2.MakeMove(sx, sy, sdir);
                    _statesExplored++;

                    var thirdMoves = clone2.Board.GetAllValidMoves();
                    var third2 = OrderMoves(clone2, thirdMoves);

                    foreach (var (tx, ty, tdir, _, _) in third2)
                    {
                        if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                        var clone3 = clone2.Clone();
                        clone3.MakeMove(tx, ty, tdir);
                        _statesExplored++;

                        var fourthMoves = clone3.Board.GetAllValidMoves();
                        foreach (var (fx, fy, fdir) in fourthMoves)
                        {
                            if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
                            var clone4 = clone3.Clone();
                            clone4.MakeMove(fx, fy, fdir);
                            _statesExplored++;

                            if (clone4.Score > bestScore4)
                            {
                                bestScore4 = clone4.Score;
                                bestFirstMove4 = MakeSolverMove(mx, my, mdir, clone4.Score, 4);
                            }
                        }
                    }
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
    /// Orders a set of raw moves by evaluating each one on the given state:
    /// extra-turn moves first, then by resulting score descending.
    /// </summary>
    private List<(int x, int y, MoveDir dir, int score, bool extraTurn)> OrderMoves(
        SimGameState state, List<(int x, int y, MoveDir dir)> moves)
    {
        var scored = new List<(int x, int y, MoveDir dir, int score, bool extraTurn)>(moves.Count);
        foreach (var (x, y, dir) in moves)
        {
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;
            scored.Add((x, y, dir, clone.Score, clone.IsExtraTurnEarned));
        }
        scored.Sort((a, b) =>
        {
            if (a.extraTurn != b.extraTurn) return b.extraTurn.CompareTo(a.extraTurn);
            return b.score.CompareTo(a.score);
        });
        return scored;
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

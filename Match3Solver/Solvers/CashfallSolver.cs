using System.Diagnostics;

class CashfallSolver : ISolver
{
    private int _statesExplored;
    private Stopwatch _timer = new();
    private int _timeBudgetMs;

    public SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000)
    {
        _statesExplored = 0;
        _timeBudgetMs = timeBudgetMs;
        _timer = Stopwatch.StartNew();
        int target = config.TargetScore;
        int maxDepth = Math.Min(3, state.TurnsRemaining);

        var validMoves = state.Board.GetAllValidMoves();
        if (validMoves.Count == 0)
        {
            return new SolverResult
            {
                BestMoves = new List<SolverMove>(),
                PredictedScore = state.CascadeScore,
                StatesExplored = 0,
                Strategy = "CashfallSolver (no moves available)"
            };
        }

        // Score all depth-1 moves — sort descending so target hits come first.
        var moveScores = new List<(int x, int y, MoveDir dir, int score, bool extraTurn)>(validMoves.Count);
        foreach (var (x, y, dir) in validMoves)
        {
            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;
            moveScores.Add((x, y, dir, clone.CascadeScore, clone.IsExtraTurnEarned));
        }
        moveScores.Sort((a, b) => b.score.CompareTo(a.score));

        var top = moveScores[0];
        int bestScore = top.score;
        SolverMove bestFirstMove = MakeSolverMove(top.x, top.y, top.dir, bestScore, 1);
        int depthUsed = 1;
        bool hitTarget = bestScore >= target;

        if (hitTarget)
            return BuildResult(bestFirstMove, bestScore, 1, target, true);

        // Depth 2 and 3: DFS, short-circuit on first path that reaches target.
        for (int d = 2; d <= maxDepth && !hitTarget && _timer.ElapsedMilliseconds < _timeBudgetMs; d++)
        {
            foreach (var (mx, my, mdir, _, _) in moveScores)
            {
                if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

                var clone1 = state.Clone();
                clone1.MakeMove(mx, my, mdir);
                _statesExplored++;

                if (clone1.IsGameOver) continue;

                // If first move earns an extra turn, it consumed 1 of our 1 allowed extensions;
                // remaining search depth stays d (free turn didn't cost a regular turn slot).
                int nextDepth = clone1.IsExtraTurnEarned ? d : d - 1;
                int extensions = clone1.IsExtraTurnEarned ? 0 : 1;

                int score = SearchBranch(clone1, nextDepth, extensions, target);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestFirstMove = MakeSolverMove(mx, my, mdir, score, d);
                    depthUsed = d;
                }
                if (score >= target)
                {
                    hitTarget = true;
                    break;
                }
            }
        }

        return BuildResult(bestFirstMove, bestScore, depthUsed, target, hitTarget);
    }

    /// <summary>
    /// DFS returning the best terminal CascadeScore reachable from this state.
    /// Short-circuits as soon as a score >= target is found (satisficing).
    /// Extra-turn extensions are capped at 1 per path total — Cashfall has only 3 turns
    /// so runaway extension chains add noise rather than value.
    /// </summary>
    private int SearchBranch(SimGameState state, int remainingDepth, int extensionsLeft, int target)
    {
        if (state.IsGameOver || state.TurnsRemaining == 0 || remainingDepth == 0 || _timer.ElapsedMilliseconds >= _timeBudgetMs)
            return state.CascadeScore;

        var moves = state.Board.GetAllValidMoves();
        if (moves.Count == 0) return state.CascadeScore;

        // Score and sort moves for greedy ordering at every non-leaf level.
        var ordered = new List<(int x, int y, MoveDir dir, int score)>(moves.Count);
        foreach (var (x, y, dir) in moves)
        {
            if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;
            var probe = state.Clone();
            probe.MakeMove(x, y, dir);
            _statesExplored++;
            ordered.Add((x, y, dir, probe.CascadeScore));
        }
        ordered.Sort((a, b) => b.score.CompareTo(a.score));

        int bestScore = state.CascadeScore;
        foreach (var (x, y, dir, _) in ordered)
        {
            if (_timer.ElapsedMilliseconds >= _timeBudgetMs) break;

            var clone = state.Clone();
            clone.MakeMove(x, y, dir);
            _statesExplored++;

            int nextDepth;
            int nextExtensions;
            if (clone.IsExtraTurnEarned && extensionsLeft > 0)
            {
                nextDepth = remainingDepth;       // free turn: don't consume depth
                nextExtensions = extensionsLeft - 1;
            }
            else
            {
                nextDepth = remainingDepth - 1;
                nextExtensions = extensionsLeft;
            }

            int score = SearchBranch(clone, nextDepth, nextExtensions, target);
            if (score > bestScore) bestScore = score;
            if (bestScore >= target) return bestScore; // satisficing short-circuit
        }
        return bestScore;
    }

    private SolverResult BuildResult(SolverMove move, int score, int depth, int target, bool hit)
    {
        string label = hit ? "hit" : "fallback";
        return new SolverResult
        {
            BestMoves = new List<SolverMove> { move },
            PredictedScore = score,
            StatesExplored = _statesExplored,
            Strategy = $"CashfallSolver depth={depth} target={target} ({_timer.ElapsedMilliseconds}ms) {label}"
        };
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

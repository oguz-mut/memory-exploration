using System.Diagnostics;

class BeamSolver : ISolver
{
    private int _statesExplored;
    private int _bestScore;
    private List<SolverMove> _bestPath = new();
    private Stopwatch _solveTimer = new();
    private const int dfsTimeout = 10000;
    private const int beamTimeout = 5000;

    public SolverResult Solve(SimGameState initialState, Match3Config config, int timeBudgetMs = 5000)
    {
        _statesExplored = 0;
        _bestScore = 0;
        _bestPath = new List<SolverMove>();
        _solveTimer = Stopwatch.StartNew();
        int dfsTimeout = Math.Max(timeBudgetMs, 10000);
        int beamTimeout = timeBudgetMs;

        string strategy;
        if (config.NumTurns <= 4)
        {
            strategy = $"DFS (depth {config.NumTurns}, 10s limit)";
            DFS(initialState, config.NumTurns, new List<SolverMove>());
        }
        else if (config.NumTurns <= 8)
        {
            // Width 100 (down from 300): we only need best first move, re-solve each turn.
            // 5s cap: good-enough first move in 3s beats optimal in 25s.
            strategy = $"Beam search (width 100, depth {config.NumTurns}, 5s cap)";
            BeamSearch(initialState, config.NumTurns, 100);
        }
        else
        {
            // Lookahead 2 (down from 3): enough to find the best first move, much faster.
            // 5s cap ensures we return timely even on complex boards.
            strategy = $"Greedy + 2-turn lookahead (depth {config.NumTurns}, 5s cap)";
            GreedyLookahead(initialState, config.NumTurns, 2);
        }

        return new SolverResult
        {
            BestMoves = _bestPath,
            PredictedScore = _bestScore,
            StatesExplored = _statesExplored,
            Strategy = strategy
        };
    }

    private void DFS(SimGameState state, int turnsLeft, List<SolverMove> path)
    {
        _statesExplored++;
        if (_solveTimer.ElapsedMilliseconds > dfsTimeout) return;
        if (turnsLeft <= 0 || state.IsGameOver)
        {
            if (state.CascadeScore > _bestScore) { _bestScore = state.CascadeScore; _bestPath = new List<SolverMove>(path); }
            return;
        }
        var moves = state.Board.GetAllValidMoves();
        if (moves.Count == 0) { if (state.CascadeScore > _bestScore) { _bestScore = state.CascadeScore; _bestPath = new List<SolverMove>(path); } return; }

        foreach (var (x, y, dir) in moves)
        {
            var clone = state.Clone();
            int scoreBefore = clone.CascadeScore;
            clone.MakeMove(x, y, dir);
            int turnsUsed = clone.IsExtraTurnEarned ? 0 : 1;
            path.Add(new SolverMove { X = x, Y = y, Direction = dir, ScoreAfter = clone.CascadeScore, Description = $"({x},{y}) {dir} +{clone.CascadeScore - scoreBefore}" });
            DFS(clone, turnsLeft - turnsUsed, path);
            path.RemoveAt(path.Count - 1);
        }
    }

    private void BeamSearch(SimGameState initialState, int totalTurns, int beamWidth)
    {
        var beam = new List<(SimGameState state, List<SolverMove> path)> { (initialState, new List<SolverMove>()) };
        for (int turn = 0; turn < totalTurns; turn++)
        {
            if (_solveTimer.ElapsedMilliseconds > beamTimeout) break;
            var candidates = new List<(SimGameState state, List<SolverMove> path, int score)>();
            foreach (var (state, path) in beam)
            {
                if (state.IsGameOver) continue;
                foreach (var (x, y, dir) in state.Board.GetAllValidMoves())
                {
                    _statesExplored++;
                    var clone = state.Clone();
                    int scoreBefore = clone.CascadeScore;
                    clone.MakeMove(x, y, dir);
                    var newPath = new List<SolverMove>(path) { new() { X = x, Y = y, Direction = dir, ScoreAfter = clone.CascadeScore, Description = $"({x},{y}) {dir} +{clone.CascadeScore - scoreBefore}" } };
                    candidates.Add((clone, newPath, clone.CascadeScore));
                }
            }
            if (candidates.Count == 0) break;
            beam = candidates.OrderByDescending(c => c.score).Take(beamWidth).Select(c => (c.state, c.path)).ToList();
        }
        if (beam.Count > 0) { var best = beam.OrderByDescending(b => b.state.CascadeScore).First(); _bestScore = best.state.CascadeScore; _bestPath = best.path; }
    }

    private void GreedyLookahead(SimGameState initialState, int totalTurns, int lookahead)
    {
        var current = initialState;
        var path = new List<SolverMove>();
        for (int turn = 0; turn < totalTurns; turn++)
        {
            if (_solveTimer.ElapsedMilliseconds > beamTimeout) break;
            if (current.IsGameOver) break;
            var moves = current.Board.GetAllValidMoves();
            if (moves.Count == 0) break;
            int bestMoveScore = -1;
            (int x, int y, MoveDir dir) bestMove = moves[0];
            foreach (var (x, y, dir) in moves)
            {
                if (_solveTimer.ElapsedMilliseconds > beamTimeout) break;
                _statesExplored++;
                var clone = current.Clone();
                clone.MakeMove(x, y, dir);
                int score = MiniDFS(clone, Math.Min(lookahead, totalTurns - turn - 1));
                if (score > bestMoveScore) { bestMoveScore = score; bestMove = (x, y, dir); }
            }
            int scoreBefore = current.CascadeScore;
            current = current.Clone();
            current.MakeMove(bestMove.x, bestMove.y, bestMove.dir);
            path.Add(new SolverMove { X = bestMove.x, Y = bestMove.y, Direction = bestMove.dir, ScoreAfter = current.CascadeScore, Description = $"({bestMove.x},{bestMove.y}) {bestMove.dir} +{current.CascadeScore - scoreBefore}" });
        }
        _bestScore = current.CascadeScore;
        _bestPath = path;
    }

    private int MiniDFS(SimGameState state, int depth)
    {
        if (depth <= 0 || state.IsGameOver) return state.CascadeScore;
        var moves = state.Board.GetAllValidMoves();
        if (moves.Count == 0) return state.CascadeScore;
        int best = state.CascadeScore;
        foreach (var (x, y, dir) in moves) { _statesExplored++; var clone = state.Clone(); clone.MakeMove(x, y, dir); int score = MiniDFS(clone, depth - 1); if (score > best) best = score; }
        return best;
    }
}

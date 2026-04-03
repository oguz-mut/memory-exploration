interface ISolver
{
    SolverResult Solve(SimGameState state, Match3Config config, int timeBudgetMs = 3000);
}

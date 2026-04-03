using Xunit;

public class SimGameStateTests
{
    private static Match3Config MakeConfig(int numPieces = 3, int scoreFor3s = 10, int scoreFor4s = 25, int scoreFor5s = 50)
    {
        var pieces = new PieceInfo[numPieces];
        for (int i = 0; i < numPieces; i++)
            pieces[i] = new PieceInfo { IconID = i, Label = $"Piece{i}", Tier = 0 };

        return new Match3Config
        {
            Width = 4,
            Height = 4,
            Title = "Test",
            NumTurns = 10,
            RandomSeed = 42,
            GiveRewards = false,
            PieceReqsPerTier = [3],
            ScoreFor3s = scoreFor3s,
            ScoreFor4s = scoreFor4s,
            ScoreFor5s = scoreFor5s,
            ScoreDeltasPerTier = [0, 5],
            ScoresPerChainLevel = [1, 1, 2, 5],
            Pieces = pieces,
            PieceValues = new int[numPieces],
        };
    }

    private static SimGameState MakeGameWithBoard(int[] pieces, Match3Config? config = null)
    {
        config ??= MakeConfig();
        var rng = new MonoRandom(config.RandomSeed);
        var state = new SimGameState();
        state.StartFromMemoryWithTurns(config, pieces, rng, config.NumTurns);
        return state;
    }

    [Fact]
    public void MakeMove_IncreasesScore()
    {
        // Board designed so swap (2,0) Right creates a 3-match of 0s at y=0: [0, 0, 0, 1]
        var pieces = new int[]
        {
            0, 0, 1, 0,  // y=0: swap (2,0) Right → [0, 0, 0, 1] = 3-match
            1, 2, 1, 2,  // y=1
            2, 1, 2, 1,  // y=2
            1, 2, 1, 2,  // y=3
        };
        var config = MakeConfig();
        var state = MakeGameWithBoard(pieces, config);
        int scoreBefore = state.Score;

        // Verify the move is valid before executing
        Assert.True(state.Board.IsMoveValid(2, 0, MoveDir.Right),
            "Swap (2,0) Right should be valid — creates three 0s in a row");
        state.MakeMove(2, 0, MoveDir.Right);

        Assert.True(state.Score > scoreBefore,
            $"Score should increase after a 3-match. Before={scoreBefore}, After={state.Score}");
    }

    [Fact]
    public void MakeMove_DecrementsTurnsRemaining()
    {
        var pieces = new int[]
        {
            0, 0, 1, 0,
            1, 2, 0, 1,
            2, 1, 2, 0,
            0, 2, 1, 2,
        };
        var state = MakeGameWithBoard(pieces);
        int turnsBefore = state.TurnsRemaining;

        var moves = state.Board.GetAllValidMoves();
        Assert.NotEmpty(moves);
        var (x, y, dir) = moves[0];
        state.MakeMove(x, y, dir);

        // If it was an extra turn, turns stay same. Otherwise decrement.
        if (!state.IsExtraTurnEarned)
            Assert.Equal(turnsBefore - 1, state.TurnsRemaining);
        else
            Assert.Equal(turnsBefore, state.TurnsRemaining);
    }

    [Fact]
    public void MakeMove_IncrementsTurnsMade()
    {
        var pieces = new int[]
        {
            0, 0, 1, 0,
            1, 2, 0, 1,
            2, 1, 2, 0,
            0, 2, 1, 2,
        };
        var state = MakeGameWithBoard(pieces);
        Assert.Equal(0, state.TurnsMade);

        var moves = state.Board.GetAllValidMoves();
        Assert.NotEmpty(moves);
        state.MakeMove(moves[0].x, moves[0].y, moves[0].dir);

        Assert.Equal(1, state.TurnsMade);
    }

    [Fact]
    public void Clone_ProducesIndependentState()
    {
        var pieces = new int[]
        {
            0, 0, 1, 0,
            1, 2, 0, 1,
            2, 1, 2, 0,
            0, 2, 1, 2,
        };
        var state = MakeGameWithBoard(pieces);
        var clone = state.Clone();

        // Make a move on clone
        var moves = clone.Board.GetAllValidMoves();
        if (moves.Count > 0)
            clone.MakeMove(moves[0].x, moves[0].y, moves[0].dir);

        // Original should be unchanged
        Assert.Equal(0, state.Score);
        Assert.Equal(0, state.TurnsMade);
    }

    [Fact]
    public void IsGameOver_WhenNoTurnsRemaining()
    {
        var config = MakeConfig();
        config.NumTurns = 1; // Only 1 turn

        var pieces = new int[]
        {
            0, 0, 1, 0,
            1, 2, 0, 1,
            2, 1, 2, 0,
            0, 2, 1, 2,
        };
        var state = MakeGameWithBoard(pieces, config);

        var moves = state.Board.GetAllValidMoves();
        Assert.NotEmpty(moves);
        state.MakeMove(moves[0].x, moves[0].y, moves[0].dir);

        // After 1 move with 1 turn, game should be over (unless extra turn earned)
        if (!state.IsExtraTurnEarned)
            Assert.True(state.IsGameOver);
    }

    [Fact]
    public void CascadeScore_IncludesChainBonus()
    {
        var pieces = new int[]
        {
            0, 0, 1, 0,
            1, 2, 0, 1,
            2, 1, 2, 0,
            0, 2, 1, 2,
        };
        var state = MakeGameWithBoard(pieces);

        var moves = state.Board.GetAllValidMoves();
        Assert.NotEmpty(moves);
        state.MakeMove(moves[0].x, moves[0].y, moves[0].dir);

        // CascadeScore should be >= Score (includes chain bonus if chain > 1)
        Assert.True(state.CascadeScore >= state.Score);
    }

    [Fact]
    public void InitialState_HasCorrectDefaults()
    {
        var pieces = new int[]
        {
            0, 1, 2, 0,
            1, 2, 0, 1,
            2, 0, 1, 2,
            0, 1, 2, 0,
        };
        var state = MakeGameWithBoard(pieces);

        Assert.Equal(0, state.Score);
        Assert.Equal(0, state.TurnsMade);
        Assert.Equal(0, state.Tier);
        Assert.Equal(10, state.TurnsRemaining);
        Assert.False(state.IsGameOver);
        Assert.False(state.IsExtraTurnEarned);
    }
}

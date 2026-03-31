using Xunit;

public class SimBoardTests
{
    // Helper: create a 4x4 board with known layout
    // Pieces indexed by y*width+x (row-major, y=0 is bottom)
    private static SimBoard MakeBoard(int[] pieces, int numPieceTypes = 3)
    {
        int width = 4, height = 4;
        var pieceTiers = new int[numPieceTypes]; // all tier 0
        var rng = new MonoRandom(42);
        return new SimBoard(width, height, numPieceTypes, pieces, pieceTiers, rng);
    }

    [Fact]
    public void Get_ReturnsCorrectPiece()
    {
        // 4x4 board, row-major: y=0 bottom row
        var pieces = new int[]
        {
            0, 1, 2, 0,  // y=0
            1, 0, 1, 2,  // y=1
            2, 1, 0, 1,  // y=2
            0, 2, 1, 0,  // y=3
        };
        var board = MakeBoard(pieces);

        Assert.Equal(0, board.Get(0, 0)); // bottom-left
        Assert.Equal(1, board.Get(1, 0));
        Assert.Equal(2, board.Get(0, 2));
        Assert.Equal(0, board.Get(3, 3)); // top-right
    }

    [Fact]
    public void IsMoveValid_DetectsHorizontalMatch()
    {
        // Place a horizontal near-match: [0, 1, 0, 0] at y=0
        // Swapping (0,0) right makes [1, 0, 0, 0] — "0" at (1,0)(2,0)(3,0) = match
        var pieces = new int[]
        {
            0, 1, 0, 0,  // y=0: swap (0,0) Right → [1,0,0,0] = 3-match of 0
            1, 2, 1, 2,  // y=1
            2, 0, 2, 0,  // y=2
            0, 1, 0, 1,  // y=3
        };
        var board = MakeBoard(pieces);

        Assert.True(board.IsMoveValid(0, 0, MoveDir.Right));
    }

    [Fact]
    public void IsMoveValid_RejectsNoMatch()
    {
        // No swap from (0,0) Up creates a 3-match on this board
        var pieces = new int[]
        {
            0, 1, 2, 0,  // y=0
            1, 2, 0, 1,  // y=1
            2, 0, 1, 2,  // y=2
            0, 1, 2, 0,  // y=3
        };
        var board = MakeBoard(pieces);

        // Swap (0,0) Up: row0 becomes [1,1,2,0], row1 becomes [0,2,0,1]
        // row0: two 1s at (0,0)(1,0) — not 3. No match.
        Assert.False(board.IsMoveValid(0, 0, MoveDir.Up));
    }

    [Fact]
    public void IsMoveValid_RejectsOutOfBounds()
    {
        var pieces = new int[16];
        var board = MakeBoard(pieces);

        Assert.False(board.IsMoveValid(0, 0, MoveDir.Down));  // y-1 = out of bounds
        Assert.False(board.IsMoveValid(0, 0, MoveDir.Left));  // x-1 = out of bounds
        Assert.False(board.IsMoveValid(3, 3, MoveDir.Right)); // x+1 = out of bounds
        Assert.False(board.IsMoveValid(3, 3, MoveDir.Up));    // y+1 = out of bounds
    }

    [Fact]
    public void GetAllValidMoves_ReturnsNonEmpty()
    {
        // Board with an obvious match opportunity
        var pieces = new int[]
        {
            0, 0, 1, 0,  // y=0: swap (2,0) Right → [0,0,0,1] = match
            1, 2, 0, 1,  // y=1
            2, 1, 2, 0,  // y=2
            0, 2, 1, 2,  // y=3
        };
        var board = MakeBoard(pieces);
        var moves = board.GetAllValidMoves();

        Assert.NotEmpty(moves);
    }

    [Fact]
    public void Step_RemovesMatchesAndFills()
    {
        // Create a board with an existing 3-match in bottom row
        var pieces = new int[]
        {
            0, 0, 0, 1,  // y=0: three 0s = horizontal match
            1, 2, 1, 2,  // y=1
            2, 0, 2, 0,  // y=2
            1, 1, 0, 2,  // y=3
        };
        var board = MakeBoard(pieces);
        var results = new StepResults();

        bool hadMatch = board.Step(results);

        Assert.True(hadMatch);
        Assert.NotEmpty(results.Match3s);
        // After gravity, y=0 should now have pieces that fell from above + new fills
        // The 3 matched cells at (0,0)(1,0)(2,0) are replaced
    }

    [Fact]
    public void Clone_ProducesIndependentCopy()
    {
        var pieces = new int[]
        {
            0, 0, 0, 1,
            1, 2, 1, 2,
            2, 0, 2, 0,
            1, 1, 0, 2,
        };
        var board = MakeBoard(pieces);
        var clone = board.Clone();

        // Step the clone — original should be unchanged
        clone.Step(new StepResults());

        // Original still has the match pattern
        Assert.Equal(0, board.Get(0, 0));
        Assert.Equal(0, board.Get(1, 0));
        Assert.Equal(0, board.Get(2, 0));
    }
}

// SimBoard — Board simulation engine

class SimBoard
{
    public readonly int Width;
    public readonly int Height;
    public readonly int NumPieceTypes;
    public int ActivePieceTypes;
    private readonly int[] _pieces;
    private ICloneable _rng;
    private readonly int[] _pieceTiers;

    /// <summary>Initialize from memory-read board with known PRNG state.</summary>
    public SimBoard(int width, int height, int numPieceTypes, int[] pieces, int[] pieceTiers, MonoRandom rng)
    {
        Width = width;
        Height = height;
        NumPieceTypes = numPieceTypes;
        _pieceTiers = pieceTiers;
        ActivePieceTypes = pieceTiers.Count(t => t == 0);
        _pieces = (int[])pieces.Clone();
        _rng = rng;
    }

    private SimBoard(int width, int height, int numPieceTypes, int activePieceTypes, int[] pieces, ICloneable rng, int[] pieceTiers)
    {
        Width = width;
        Height = height;
        NumPieceTypes = numPieceTypes;
        ActivePieceTypes = activePieceTypes;
        _pieces = (int[])pieces.Clone();
        _rng = (ICloneable)rng.Clone();
        _pieceTiers = pieceTiers;
    }

    public void SetActivePieceTypes(int count) => ActivePieceTypes = count;

    private int GetIdx(int x, int y) => y * Width + x;

    public int Get(int x, int y) => _pieces[GetIdx(x, y)];
    private void Set(int x, int y, int type) => _pieces[GetIdx(x, y)] = type;

    private int NextPiece()
    {
        if (_rng is MonoRandom mono) return mono.Next(ActivePieceTypes);
        throw new InvalidOperationException("Unknown PRNG type");
    }

    public int[] ClonePieces() => (int[])_pieces.Clone();

    public SimBoard Clone() => new(Width, Height, NumPieceTypes, ActivePieceTypes, _pieces, _rng, _pieceTiers);

    // ── Move Validation ──

    public static XY DeltaByDir(int x, int y, MoveDir dir) => dir switch
    {
        MoveDir.Up => new XY(x, y + 1),
        MoveDir.Down => new XY(x, y - 1),
        MoveDir.Left => new XY(x - 1, y),
        MoveDir.Right => new XY(x + 1, y),
        _ => new XY(x, y)
    };

    private bool InBounds(int x, int y) => x >= 0 && x < Width && y >= 0 && y < Height;

    public bool IsMoveValid(int x, int y, MoveDir dir)
    {
        var target = DeltaByDir(x, y, dir);
        if (!InBounds(target.X, target.Y)) return false;
        Swap(x, y, target.X, target.Y);
        bool hasMatch = HasAnyMatch(x, y) || HasAnyMatch(target.X, target.Y);
        Swap(x, y, target.X, target.Y);
        return hasMatch;
    }

    private bool HasAnyMatch(int x, int y)
    {
        int type = Get(x, y);
        if (type < 0) return false;
        int count = 1;
        for (int dx = x - 1; dx >= 0 && Get(dx, y) == type; dx--) count++;
        for (int dx = x + 1; dx < Width && Get(dx, y) == type; dx++) count++;
        if (count >= 3) return true;
        count = 1;
        for (int dy = y - 1; dy >= 0 && Get(x, dy) == type; dy--) count++;
        for (int dy = y + 1; dy < Height && Get(x, dy) == type; dy++) count++;
        return count >= 3;
    }

    public List<(int x, int y, MoveDir dir)> GetAllValidMoves()
    {
        var moves = new List<(int, int, MoveDir)>();
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
            {
                if (x < Width - 1 && IsMoveValid(x, y, MoveDir.Right))
                    moves.Add((x, y, MoveDir.Right));
                if (y < Height - 1 && IsMoveValid(x, y, MoveDir.Up))
                    moves.Add((x, y, MoveDir.Up));
            }
        return moves;
    }

    // ── Match Detection ──

    public void GetPendingMatches(List<MatchLocation> matches)
    {
        matches.Clear();
        for (int y = 0; y < Height; y++)
        {
            int x = 0;
            while (x < Width)
            {
                int type = Get(x, y);
                if (type < 0) { x++; continue; }
                int len = 1;
                while (x + len < Width && Get(x + len, y) == type) len++;
                if (len >= 3)
                    matches.Add(new MatchLocation(new XY(x, y), type, MatchDirection.Horizontal, len));
                x += len;
            }
        }
        for (int x = 0; x < Width; x++)
        {
            int y = 0;
            while (y < Height)
            {
                int type = Get(x, y);
                if (type < 0) { y++; continue; }
                int len = 1;
                while (y + len < Height && Get(x, y + len) == type) len++;
                if (len >= 3)
                    matches.Add(new MatchLocation(new XY(x, y), type, MatchDirection.Vertical, len));
                y += len;
            }
        }
    }

    // ── Step: Remove matches, gravity, fill ──

    public bool Step(StepResults results)
    {
        var matches = new List<MatchLocation>();
        GetPendingMatches(matches);
        if (matches.Count == 0) return false;

        results.Clear();
        var dead = new bool[Width * Height];
        foreach (var match in matches)
        {
            results.Matches.Add(match);
            int len = match.Length;
            var dict = len >= 5 ? results.Match5s : len >= 4 ? results.Match4s : results.Match3s;
            dict.TryGetValue(match.Type, out int prev);
            dict[match.Type] = prev + 1;
            if (len >= 4) results.HadMatch4OrMore = true;

            for (int i = 0; i < len; i++)
            {
                int mx = match.Pos.X + (match.Dir == MatchDirection.Horizontal ? i : 0);
                int my = match.Pos.Y + (match.Dir == MatchDirection.Vertical ? i : 0);
                dead[GetIdx(mx, my)] = true;
            }
        }

        for (int i = 0; i < _pieces.Length; i++)
            if (dead[i]) _pieces[i] = -1;

        for (int x = 0; x < Width; x++)
        {
            int writeY = 0;
            for (int readY = 0; readY < Height; readY++)
            {
                int p = Get(x, readY);
                if (p >= 0) { Set(x, writeY, p); writeY++; }
            }
            for (int y = writeY; y < Height; y++)
                Set(x, y, NextPiece());
        }
        return true;
    }

    private void Swap(int x1, int y1, int x2, int y2)
    {
        int idx1 = GetIdx(x1, y1);
        int idx2 = GetIdx(x2, y2);
        (_pieces[idx1], _pieces[idx2]) = (_pieces[idx2], _pieces[idx1]);
    }

    public MoveResult MakeBasicMove(int x, int y, MoveDir dir)
    {
        var target = DeltaByDir(x, y, dir);
        if (!InBounds(target.X, target.Y)) return MoveResult.InvalidPosition;
        if (!IsMoveValid(x, y, dir)) return MoveResult.NoMatch;
        Swap(x, y, target.X, target.Y);
        return MoveResult.Success;
    }
}

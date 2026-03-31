// IL2CPP memory layout offsets for Match-3 game objects.
// Source: tools/Cpp2IL/output-cs/DiffableCs/Assembly-CSharp/Match3/
// All offsets relative to the object's base pointer.

static class Offsets
{
    // Managed object header (all IL2CPP objects)
    public const ulong Vtable      = 0x00;  // pointer to vtable
    public const ulong SyncBlock   = 0x08;  // IL2CPP sync block (0 when unused)

    // GameRulesConfig  (Assembly-CSharp/Match3/GameRulesConfig.cs)
    public static class Config
    {
        public const ulong Width          = 0x10;
        public const ulong Height         = 0x14;
        public const ulong Title          = 0x18;  // string*
        public const ulong NumTurns       = 0x20;
        public const ulong RandomSeed     = 0x24;
        public const ulong GiveRewards    = 0x28;
        public const ulong PieceReqs      = 0x30;  // Int32[]*
        public const ulong ScoreFor3s     = 0x38;
        public const ulong ScoreFor4s     = 0x3C;
        public const ulong ScoreFor5s     = 0x40;
        public const ulong ScoreDeltas    = 0x48;  // Int32[]*
        public const ulong ScoresPerChain = 0x50;  // Int32[]*
        public const ulong Pieces         = 0x58;  // GamePieceInfo[]*
    }

    // GameBoard  (Assembly-CSharp/Match3/GameBoard.cs)
    public static class Board
    {
        public const ulong Config   = 0x10;  // GameRulesConfig*
        public const ulong Width    = 0x20;
        public const ulong Height   = 0x24;
        public const ulong Pieces   = 0x28;  // GamePiece[]*
    }

    // GameStateSinglePlayer  (Assembly-CSharp/Match3/GameStateSinglePlayer.cs)
    public static class GameState
    {
        public const ulong Board          = 0x10;  // GameBoard*
        public const ulong Score          = 0x1C;
        public const ulong Chain          = 0x20;
        public const ulong TurnsRemaining = 0x24;
        public const ulong TurnsMade      = 0x28;
        public const ulong Tier           = 0x2C;
        public const ulong IsFreeTurn     = 0x30;
        public const ulong Rng            = 0x38;  // System.Random*
        public const ulong PiecesTiered   = 0x40;  // Boolean[]*
        public const ulong TotalPieces    = 0x48;  // Int32[]*
        public const ulong TierPieces     = 0x50;  // Int32[]*
        public const ulong ValidMoves     = 0x60;  // List<MoveInfo>*
        public const ulong Configuration  = 0x70;  // GameRulesConfig*
    }

    // System.Random (Mono Knuth subtractive generator)
    public static class MonoRng
    {
        public const ulong Inext      = 0x10;
        public const ulong Inextp     = 0x14;
        public const ulong SeedArray  = 0x18;  // Int32[]*
    }

    // Managed array header (System.Array layout in IL2CPP)
    public static class Array
    {
        public const ulong Length      = 0x18;  // int32 element count
        public const ulong FirstElem   = 0x20;  // first element (4 or 8 bytes each)
    }

    // GamePiece  (Assembly-CSharp/Match3/GamePiece.cs)
    public static class Piece
    {
        public const ulong Type = 0x10;  // PieceType (int)
        public const ulong X    = 0x14;
        public const ulong Y    = 0x18;
    }

    // Vtable / sync sanity thresholds
    public const ulong MinValidPtr = 0x10000;
}

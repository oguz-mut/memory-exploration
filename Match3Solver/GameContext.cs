// GameContext — mutable runtime state shared across Program.cs functions.
// Extracted to reduce the clutter of 21 top-level variable declarations (issue #3).
// All fields public so local functions close over gctx and read/write directly.

class GameContext
{
    // ── Session management ──
    public readonly object Lock = new();
    public GameSession? CurrentSession;
    public readonly List<GameSession> History = new();
    public CancellationTokenSource? GameCts;
    public TaskCompletionSource<bool>? NewGameSignal;

    // ── Memory access (cached between board reads) ──
    // GameMemory and MemScanner are MemoryLib types — kept in Program.cs to avoid
    // a direct reference to MemoryLib from this file (MemoryLib source is local-only).
    public ulong LastBoardAddr;
    public ulong LastGameStateAddr;

    // ── Runtime settings (set from CLI args) ──
    public SolverStrategy Strategy = SolverStrategy.Auto;
    public bool Autoloop;

    // ── Grid calibration (pixels at 1920×1080 base resolution) ──
    public int GridX = 35, GridY = 245, CellSize = 46;

    // ── Autoloop button positions (calibrated once by user) ──
    public int OkClickX, OkClickY;
    public int UseClickX, UseClickY;
    public int PlayClickX, PlayClickY;
    public bool AutoloopCalibrated;
}

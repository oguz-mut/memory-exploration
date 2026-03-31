using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MemoryLib;

// ── Configuration ──
string logDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon";
string logPath = Path.Combine(logDir, "Player.log");
string logPathPrev = Path.Combine(logDir, "Player-prev.log");
int port = 9881;

if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
    logPath = logPathPrev;
if (!File.Exists(logPath))
{
    Console.WriteLine("No log file found. Exiting.");
    return;
}

// ── Shared State ──
var _lock = new object();
GameSession? _currentSession = null;
var _history = new List<GameSession>();
var cts = new CancellationTokenSource();
ProcessMemory? _gameMemory = null;
MemoryRegionScanner? _memScanner = null;
ulong _lastBoardAddr = 0; // cached GameBoard address for fast re-reads
ulong _lastGameStateAddr = 0; // cached GameStateSinglePlayer address
CancellationTokenSource? _gameCts = null; // per-game CTS to cancel stale solve-move tasks
SolverStrategy _strategy = SolverStrategy.Auto;

// Grid calibration (at 1920x1080 base resolution)
int _gridX = 35, _gridY = 245, _cellSize = 46;
string settingsDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ProjectGorgonTools");
{
    var calFile = Path.Combine(settingsDir, "match3_grid.json");
    if (File.Exists(calFile))
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(calFile));
            if (doc.RootElement.TryGetProperty("gridX", out var gx)) _gridX = gx.GetInt32();
            if (doc.RootElement.TryGetProperty("gridY", out var gy)) _gridY = gy.GetInt32();
            if (doc.RootElement.TryGetProperty("cellSize", out var cs)) _cellSize = cs.GetInt32();
            Console.WriteLine($"[*] Loaded grid calibration: gridX={_gridX}, gridY={_gridY}, cellSize={_cellSize}");
        }
        catch { }
    }
}

Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
cts.Token.Register(() => { _gameMemory?.Dispose(); });

// ── Log Processing ──
var _match3Rx = new Regex(@"ProcessMatch3Start\((\d+),\s*""(.+)""\s*\)", RegexOptions.Compiled);

void ProcessLogLine(string line)
{
    var m = _match3Rx.Match(line);
    if (!m.Success) return;

    int sessionId = int.Parse(m.Groups[1].Value);
    string json = m.Groups[2].Value.Replace("\\\"", "\"");

    Match3Config? config;
    try
    {
        config = JsonSerializer.Deserialize<Match3Config>(json);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[!] Failed to parse Match3 config: {ex.Message}");
        return;
    }
    if (config == null) return;

    Console.WriteLine($"[*] Match-3 game detected: {config.Title} ({config.Width}x{config.Height}, {config.NumTurns} turns, seed={config.RandomSeed})");
    OnNewGame(sessionId, config);
}

void OnNewGame(int sessionId, Match3Config config)
{
    var session = new GameSession
    {
        SessionId = sessionId,
        Config = config,
        ReceivedAt = DateTime.Now,
        NumPieceTypes = config.Pieces.Length,
        PieceLabels = config.Pieces.Select(p => p.Label).ToArray(),
        Status = SolveStatus.Solving
    };

    // Cancel any previously running solve-move task and clear cached addresses
    _gameCts?.Cancel();
    _gameCts = new CancellationTokenSource();
    var gameCt = _gameCts.Token;
    _lastBoardAddr = 0;
    _lastGameStateAddr = 0;

    lock (_lock)
    {
        if (_currentSession != null)
            _history.Add(_currentSession);
        _currentSession = session;
    }

    Task.Run(async () =>
    {
        try
        {
        // Wait for game to create board objects in memory
        await Task.Delay(1000);

        var boardData = ReadBoardFromMemory(config);
        if (boardData == null)
        {
            session.Status = SolveStatus.Error;
            session.ErrorMessage = "Failed to read board from game memory (is the game running?)";
            Console.WriteLine("[!] Memory read failed");
            return;
        }

        var (pieces, rng) = boardData.Value;
        session.InitialBoard = pieces;
        Console.WriteLine($"[+] Board read from memory: {pieces.Length} cells");

        try
        {
            // Solve-move-reread loop: solve 1 move at a time from actual board state
            await Task.Delay(500);
            Console.WriteLine("[*] Entering solve-move loop...");
            int lastPredictedFirstMoveScore = -1;
            while (true)
            {
                // Check for cancellation (new game started)
                if (gameCt.IsCancellationRequested)
                {
                    Console.WriteLine("[*] Game task cancelled (new game detected)");
                    break;
                }

                // Read fresh board + turnsRemaining from game memory
                Console.WriteLine("[*] Reading board...");
                var fresh = QuickReadBoard(config);
                if (fresh == null)
                {
                    Console.WriteLine("[!] Can't read board — game may be over");
                    break;
                }
                var (curPieces, curRng, turnsLeft, curScore, curTier, curTurnsMade, curTotalMatched, curTierMatched) = fresh.Value;
                session.InitialBoard = curPieces;

                // Log compact board grid (top row first)
                {
                    var sbBoard = new StringBuilder("[board] ");
                    for (int y = config.Height - 1; y >= 0; y--)
                    {
                        sbBoard.Append($"row{y}:");
                        for (int x = 0; x < config.Width; x++)
                            sbBoard.Append(' ').Append(curPieces[y * config.Width + x]);
                        if (y > 0) sbBoard.Append(" |");
                    }
                    Console.WriteLine(sbBoard.ToString());
                }

                // Log score vs predicted divergence
                Console.WriteLine($"[score] game={curScore} predicted={lastPredictedFirstMoveScore} tier={curTier}");
                if (lastPredictedFirstMoveScore >= 0 && Math.Abs(curScore - lastPredictedFirstMoveScore) > 50)
                    Console.WriteLine($"[!] Score divergence: game={curScore} vs last_predicted={lastPredictedFirstMoveScore}");

                if (turnsLeft <= 0)
                {
                    Console.WriteLine("[*] No turns remaining — game over");
                    break;
                }

                // Solve from current state
                var sw = Stopwatch.StartNew();
                var state = new SimGameState();
                state.StartFromMemoryWithTurns(config, curPieces, curRng, turnsLeft, curScore, curTier, curTurnsMade, curTotalMatched, curTierMatched);

                var solveConfig = new Match3Config
                {
                    Width = config.Width, Height = config.Height, Title = config.Title,
                    NumTurns = turnsLeft, RandomSeed = config.RandomSeed,
                    GiveRewards = config.GiveRewards, PieceReqsPerTier = config.PieceReqsPerTier,
                    ScoreFor3s = config.ScoreFor3s, ScoreFor4s = config.ScoreFor4s, ScoreFor5s = config.ScoreFor5s,
                    ScoreDeltasPerTier = config.ScoreDeltasPerTier, ScoresPerChainLevel = config.ScoresPerChainLevel,
                    Pieces = config.Pieces
                };

                SolverResult result;
                string stratLabel;
                if (_strategy == SolverStrategy.Auto)
                {
                    // Auto: use MCTS for low turns (≤4), Iterative for everything else.
                    // Beam search plans 15 turns ahead assuming perfect PRNG — fiction in practice.
                    // Iterative with move ordering finds better first moves and respects PRNG reality.
                    if (turnsLeft <= 4)
                    {
                        var mcts = new MCTSSolver();
                        result = mcts.Solve(state, solveConfig, 5000); // more time for few turns
                        stratLabel = "mcts";
                    }
                    else
                    {
                        var iter = new IterativeSolver();
                        result = iter.Solve(state, solveConfig, 5000);
                        stratLabel = "iterative";
                    }
                }
                else
                {
                    // Explicit strategy
                    result = _strategy switch
                    {
                        SolverStrategy.MCTS => new MCTSSolver().Solve(state, solveConfig),
                        SolverStrategy.Eval => new EvalSolver().Solve(state, solveConfig),
                        SolverStrategy.Iterative => new IterativeSolver().Solve(state, solveConfig),
                        _ => new Match3Solver().Solve(state, solveConfig) // Beam
                    };
                    stratLabel = _strategy.ToString().ToLower();
                }

                sw.Stop();
                result.SolveTime = sw.Elapsed;
                session.Solution = result;
                session.Status = SolveStatus.Solved;

                if (result.BestMoves.Count == 0)
                {
                    Console.WriteLine("[*] No valid moves — game over");
                    break;
                }

                Console.WriteLine($"[+] {turnsLeft} turns left [{stratLabel}]: solved in {sw.ElapsedMilliseconds}ms — best score: {result.PredictedScore} ({result.BestMoves.Count} moves, {result.StatesExplored} states)");

                // Track the score predicted for after this move for divergence detection next iteration
                lastPredictedFirstMoveScore = result.BestMoves[0].ScoreAfter;

                // Validate first move against the ACTUAL read board before executing
                var firstMove = result.BestMoves[0];
                var validateBoard = new SimBoard(config.Width, config.Height, config.Pieces.Length,
                    curPieces, config.Pieces.Select(p => p.Tier).ToArray(), (MonoRandom)curRng.Clone());
                if (!validateBoard.IsMoveValid(firstMove.X, firstMove.Y, firstMove.Direction))
                {
                    Console.WriteLine($"[!] Move ({firstMove.X},{firstMove.Y}) {firstMove.Direction} is INVALID on actual board — PRNG divergence detected");
                    // Log board for diagnostics
                    var sb = new StringBuilder("[!] Board state: ");
                    for (int y = config.Height - 1; y >= 0; y--)
                    {
                        for (int x = 0; x < config.Width; x++)
                            sb.Append(curPieces[y * config.Width + x]).Append(' ');
                        if (y > 0) sb.Append("| ");
                    }
                    Console.WriteLine(sb.ToString());
                    // Also log all valid moves from the actual board
                    var validMoves = validateBoard.GetAllValidMoves();
                    Console.WriteLine($"[!] Valid moves on actual board ({validMoves.Count}): {string.Join(", ", validMoves.Select(m => $"({m.x},{m.y}){m.dir}"))}");
                    // Re-read board and retry this iteration instead of aborting
                    Console.WriteLine("[*] Re-reading board and re-solving...");
                    await Task.Delay(1000);
                    continue;
                }

                // Check cancellation before executing mouse move
                if (gameCt.IsCancellationRequested)
                {
                    Console.WriteLine("[*] Game task cancelled before move execution");
                    break;
                }

                // Execute only the FIRST move
                bool success = await GameAutoPlayer.ExecuteSingleMove(firstMove, config, () =>
                {
                    var r = QuickReadBoard(config);
                    return r.HasValue ? (r.Value.pieces, r.Value.rng) : ((int[], MonoRandom)?)null;
                }, _gridX, _gridY, _cellSize, curPieces);

                if (success)
                    session.ConsecutiveFailures = 0;

                if (!success)
                {
                    session.ConsecutiveFailures++;
                    if (session.ConsecutiveFailures >= 3)
                    {
                        Console.WriteLine("[!] 3 consecutive move failures — aborting");
                        break;
                    }
                    Console.WriteLine($"[!] Move failed ({session.ConsecutiveFailures}/3) — re-reading and retrying...");
                    await Task.Delay(1000);
                    continue;
                }
            }

            Console.WriteLine("[+] Auto-play complete");
        }
        catch (Exception ex)
        {
            session.Status = SolveStatus.Error;
            session.ErrorMessage = ex.Message;
            Console.WriteLine($"[!] Solver error: {ex.Message}");
        }
        } // end try (OperationCanceledException wrapper)
        catch (OperationCanceledException)
        {
            Console.WriteLine("[*] Game task cancelled via CancellationToken");
        }
    });
}

// ── Memory Reader ──

ProcessMemory? EnsureGameConnection()
{
    if (_gameMemory != null)
    {
        try
        {
            // Quick check: process still alive
            var proc = Process.GetProcessById(_gameMemory.ProcessId);
            if (proc.HasExited) throw new Exception();
        }
        catch { _gameMemory.Dispose(); _gameMemory = null; _memScanner = null; }
    }

    if (_gameMemory == null)
    {
        var pid = ProcessMemory.FindGameProcess();
        if (pid == null) return null;
        _gameMemory = ProcessMemory.Open(pid.Value);
        _memScanner = new MemoryRegionScanner(_gameMemory);
    }
    return _gameMemory;
}

(int[] pieces, MonoRandom rng)? ReadBoardFromMemory(Match3Config config)
{
    var mem = EnsureGameConnection();
    if (mem == null || _memScanner == null)
    {
        Console.WriteLine("[!] Game process not found");
        return null;
    }
    _memScanner.InvalidateRegionCache();

    // Helpers for nullable reads
    int ri32(ulong addr) => mem.ReadInt32(addr) ?? throw new Exception($"Read failed at 0x{addr:X}");
    long ri64(ulong addr) => mem.ReadInt64(addr) ?? throw new Exception($"Read failed at 0x{addr:X}");
    ulong rptr(ulong addr) => mem.ReadPointer(addr) ?? throw new Exception($"Read failed at 0x{addr:X}");

    // Step 1: Scan for RandomSeed value in memory (with retry — game may still be allocating)
    var seedBytes = BitConverter.GetBytes(config.RandomSeed);
    List<MemoryLib.Models.ScanMatch> seedHits = new();
    for (int attempt = 0; attempt < 3; attempt++)
    {
        _memScanner.InvalidateRegionCache();
        seedHits = _memScanner.ScanForBytePattern(seedBytes, maxResults: 200);
        Console.WriteLine($"[*] Attempt {attempt + 1}: Found {seedHits.Count} RandomSeed hits in Private regions");
        if (seedHits.Count > 0) break;
        Thread.Sleep(1500); // wait for game to allocate objects
    }

    if (seedHits.Count == 0)
    {
        // Broaden search to ALL readable regions
        var allRegions = mem.EnumerateRegions().Where(r => r.IsReadable && r.Size > 4096).ToList();
        Console.WriteLine($"[*] Broadening search to {allRegions.Count} readable regions (was Private-only)");
        long totalScanned = 0;
        foreach (var region in allRegions)
        {
            long offset = 0;
            while (offset < (long)region.Size)
            {
                int toRead = (int)Math.Min((long)region.Size - offset, 8 * 1024 * 1024);
                var buf = new byte[toRead];
                int read = mem.ReadBytes(region.BaseAddress + (ulong)offset, buf, toRead);
                if (read < 4) break;
                totalScanned += read;
                for (int i = 0; i <= read - 4; i++)
                {
                    if (buf[i] == seedBytes[0] && buf[i + 1] == seedBytes[1] &&
                        buf[i + 2] == seedBytes[2] && buf[i + 3] == seedBytes[3])
                    {
                        ulong addr = region.BaseAddress + (ulong)offset + (ulong)i;
                        seedHits.Add(new MemoryLib.Models.ScanMatch { Address = addr, Region = region, RegionOffset = offset + i });
                    }
                }
                offset += toRead - 3;
                if (toRead < 8 * 1024 * 1024) break;
            }
            if (seedHits.Count > 0) break; // found at least one
        }
        Console.WriteLine($"[*] Broad scan: {seedHits.Count} hits across {totalScanned / 1024 / 1024}MB");
    }

    // Step 2: Validate each hit as a GameRulesConfig object (seed is at +0x24)
    ulong configAddr = 0;
    foreach (var hit in seedHits)
    {
        ulong baseAddr = hit.Address - 0x24;
        try
        {
            int width = ri32(baseAddr + 0x10);
            int height = ri32(baseAddr + 0x14);
            int numTurns = ri32(baseAddr + 0x20);
            int scoreFor3s = ri32(baseAddr + 0x38);
            ulong vtable = rptr(baseAddr);
            ulong sync = rptr(baseAddr + 0x08);

            if (width == config.Width && height == config.Height &&
                numTurns == config.NumTurns && scoreFor3s == config.ScoreFor3s &&
                vtable > 0x10000 && sync == 0)
            {
                configAddr = baseAddr;
                Console.WriteLine($"[+] GameRulesConfig found at 0x{configAddr:X}");
                break;
            }
        }
        catch { /* invalid address, skip */ }
    }

    if (configAddr == 0)
    {
        Console.WriteLine("[!] GameRulesConfig not found in memory");
        return null;
    }

    // Step 3: Find GameBoard by scanning for pointers to config (Config is at GameBoard+0x10)
    var configPtrHits = _memScanner.ScanForPointerTo(configAddr, maxResults: 50);
    Console.WriteLine($"[*] Found {configPtrHits.Count} pointers to config");

    ulong boardAddr = 0;
    foreach (var hit in configPtrHits)
    {
        // Try as GameBoard (Config at +0x10)
        ulong candidateBoard = hit.Address - 0x10;
        try
        {
            int bWidth = ri32(candidateBoard + 0x20);
            int bHeight = ri32(candidateBoard + 0x24);
            ulong vtable = rptr(candidateBoard);
            ulong sync = rptr(candidateBoard + 0x08);
            ulong piecesPtr = rptr(candidateBoard + 0x28);

            if (bWidth == config.Width && bHeight == config.Height &&
                vtable > 0x10000 && sync == 0 && piecesPtr > 0x10000)
            {
                boardAddr = candidateBoard;
                _lastBoardAddr = boardAddr;
                Console.WriteLine($"[+] GameBoard found at 0x{boardAddr:X} (via Config@+0x10)");
                break;
            }
        }
        catch { }

        // Try as GameStateSinglePlayer (Configuration at +0x70) → Board at +0x10
        ulong candidateGS = hit.Address - 0x70;
        try
        {
            ulong gsVtable = rptr(candidateGS);
            ulong gsSync = rptr(candidateGS + 0x08);
            ulong gsBoardPtr = rptr(candidateGS + 0x10);
            int turnsRem = ri32(candidateGS + 0x24);

            if (gsVtable > 0x10000 && gsSync == 0 && gsBoardPtr > 0x10000 && turnsRem == config.NumTurns)
            {
                // Follow board pointer
                int bWidth = ri32(gsBoardPtr + 0x20);
                int bHeight = ri32(gsBoardPtr + 0x24);
                ulong bVtable = rptr(gsBoardPtr);
                ulong bSync = rptr(gsBoardPtr + 0x08);
                ulong piecesPtr = rptr(gsBoardPtr + 0x28);

                if (bWidth == config.Width && bHeight == config.Height &&
                    bVtable > 0x10000 && bSync == 0 && piecesPtr > 0x10000)
                {
                    boardAddr = gsBoardPtr;
                    _lastBoardAddr = boardAddr;
                    Console.WriteLine($"[+] GameBoard found at 0x{boardAddr:X} (via GameState@+0x70→Board@+0x10)");
                    break;
                }
            }
        }
        catch { }
    }

    if (boardAddr == 0)
    {
        Console.WriteLine("[!] GameBoard not found in memory");
        return null;
    }

    // Step 4: Read Pieces[] array from GameBoard+0x28
    ulong piecesArrayPtr = rptr(boardAddr + 0x28);
    long arrayLength = ri64(piecesArrayPtr + 0x18);
    int expectedLen = config.Width * config.Height;

    if (arrayLength != expectedLen)
    {
        Console.WriteLine($"[!] Pieces array length mismatch: got {arrayLength}, expected {expectedLen}");
        return null;
    }

    // Read element pointers (IL2CPP ref array: elements start at +0x20, each 8 bytes)
    var pieces = new int[expectedLen];
    for (int i = 0; i < expectedLen; i++)
    {
        ulong piecePtr = rptr(piecesArrayPtr + 0x20 + (ulong)(i * 8));
        if (piecePtr == 0) { pieces[i] = -1; continue; }

        int pieceType = ri32(piecePtr + 0x10);
        int px = ri32(piecePtr + 0x14);
        int py = ri32(piecePtr + 0x18);

        if (pieceType < -1 || pieceType > 9 || px < 0 || px >= config.Width || py < 0 || py >= config.Height)
        {
            Console.WriteLine($"[!] Invalid piece at index {i}: type={pieceType} x={px} y={py}");
            return null;
        }
        pieces[py * config.Width + px] = pieceType;
    }

    Console.WriteLine($"[+] Read {expectedLen} pieces from memory");

    // Step 5: Find GameStateSinglePlayer and read PRNG state (Config at +0x70)
    MonoRandom rng;
    ulong gameStateAddr = 0;
    foreach (var hit in configPtrHits)
    {
        ulong candidate = hit.Address - 0x70;
        try
        {
            ulong boardPtr = rptr(candidate + 0x10);
            int score = ri32(candidate + 0x1C);
            int turnsRem = ri32(candidate + 0x24);
            ulong vtable = rptr(candidate);
            ulong sync = rptr(candidate + 0x08);

            if (boardPtr == boardAddr && turnsRem == config.NumTurns &&
                score >= 0 && vtable > 0x10000 && sync == 0)
            {
                gameStateAddr = candidate;
                _lastGameStateAddr = gameStateAddr;
                Console.WriteLine($"[+] GameStateSinglePlayer found at 0x{gameStateAddr:X}");
                break;
            }
        }
        catch { }
    }

    // Also try a fresh pointer scan for config at +0x70
    if (gameStateAddr == 0)
    {
        var configPtrHits2 = _memScanner.ScanForPointerTo(configAddr, maxResults: 100);
        foreach (var hit in configPtrHits2)
        {
            ulong candidate = hit.Address - 0x70;
            try
            {
                ulong boardPtr = rptr(candidate + 0x10);
                int turnsRem = ri32(candidate + 0x24);
                ulong vtable = rptr(candidate);
                ulong sync = rptr(candidate + 0x08);

                if (boardPtr == boardAddr && turnsRem == config.NumTurns &&
                    vtable > 0x10000 && sync == 0)
                {
                    gameStateAddr = candidate;
                    _lastGameStateAddr = gameStateAddr;
                    Console.WriteLine($"[+] GameStateSinglePlayer found at 0x{gameStateAddr:X}");
                    break;
                }
            }
            catch { }
        }
    }

    if (gameStateAddr != 0)
    {
        try
        {
            ulong rndPtr = rptr(gameStateAddr + 0x38);
            int inext = ri32(rndPtr + 0x10);
            int inextp = ri32(rndPtr + 0x14);
            ulong seedArrayPtr = rptr(rndPtr + 0x18);
            long seedArrayLen = ri64(seedArrayPtr + 0x18);

            if (seedArrayLen == 56 && inext >= 0 && inext < 56 && inextp >= 0 && inextp < 56)
            {
                var seedArray = new int[56];
                for (int i = 0; i < 56; i++)
                    seedArray[i] = ri32(seedArrayPtr + 0x20 + (ulong)(i * 4));

                rng = new MonoRandom(seedArray, inext, inextp);
                Console.WriteLine($"[+] PRNG state read: inext={inext} inextp={inextp}");
                return (pieces, rng);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to read PRNG state: {ex.Message}");
        }
    }

    // Fallback PRNG from seed (cascades will be approximate)
    Console.WriteLine("[*] Using fallback PRNG from seed");
    rng = new MonoRandom(config.RandomSeed);
    for (int i = 0; i < expectedLen + 20; i++) rng.Next(config.Pieces.Length);
    return (pieces, rng);
}

/// <summary>Fast re-read of the board from cached addresses. Also reads live PRNG and game state.</summary>
(int[] pieces, MonoRandom rng, int turnsRemaining, int score, int tier, int turnsMade, int[] totalPiecesMatched, int[] tierPiecesMatched)? QuickReadBoard(Match3Config config)
{
    if (_lastBoardAddr == 0 || _gameMemory == null) return null;
    var mem = _gameMemory;

    int ri32(ulong addr) => mem.ReadInt32(addr) ?? throw new Exception();
    long ri64(ulong addr) => mem.ReadInt64(addr) ?? throw new Exception();
    ulong rptr(ulong addr) => mem.ReadPointer(addr) ?? throw new Exception();

    try
    {
        // Read PRNG state FIRST — before pieces and game state — so we capture the
        // state that will generate the NEXT cascade fill, not one already advanced by
        // a concurrent cascade fill happening between our reads.
        MonoRandom rng;
        bool rngReadOk = false;
        if (_lastGameStateAddr != 0)
        {
            try
            {
                ulong rndPtr = rptr(_lastGameStateAddr + 0x38);
                int inext = ri32(rndPtr + 0x10);
                int inextp = ri32(rndPtr + 0x14);
                ulong seedArrayPtr = rptr(rndPtr + 0x18);
                long seedArrayLen = ri64(seedArrayPtr + 0x18);

                if (seedArrayLen == 56 && inext >= 0 && inext < 56 && inextp >= 0 && inextp < 56)
                {
                    var seedArray = new int[56];
                    for (int i = 0; i < 56; i++)
                        seedArray[i] = ri32(seedArrayPtr + 0x20 + (ulong)(i * 4));
                    rng = new MonoRandom(seedArray, inext, inextp);
                    rngReadOk = true;
                    Console.WriteLine($"[prng] inext={inext} inextp={inextp}");
                }
                else
                {
                    rng = new MonoRandom(config.RandomSeed);
                }
            }
            catch
            {
                rng = new MonoRandom(config.RandomSeed);
            }
        }
        else
        {
            rng = new MonoRandom(config.RandomSeed);
        }

        if (!rngReadOk)
            Console.WriteLine("[!] QuickReadBoard: falling back to seed-based PRNG — cascade predictions may diverge");

        // Read pieces array after PRNG so PRNG snapshot precedes any cascade fill
        ulong piecesArrayPtr = rptr(_lastBoardAddr + 0x28);
        long arrayLength = ri64(piecesArrayPtr + 0x18);
        int expectedLen = config.Width * config.Height;
        if (arrayLength != expectedLen) return null;

        var pieces = new int[expectedLen];
        for (int i = 0; i < expectedLen; i++)
        {
            ulong piecePtr = rptr(piecesArrayPtr + 0x20 + (ulong)(i * 8));
            if (piecePtr == 0) { pieces[i] = -1; continue; }
            int pieceType = ri32(piecePtr + 0x10);
            int px = ri32(piecePtr + 0x14);
            int py = ri32(piecePtr + 0x18);
            if (pieceType < -1 || pieceType > 9 || px < 0 || px >= config.Width || py < 0 || py >= config.Height)
                return null;
            pieces[py * config.Width + px] = pieceType;
        }

        // Read live game state from GameStateSinglePlayer
        int turnsRemaining = config.NumTurns;
        int score = 0;
        int tier = 0;
        int turnsMade = 0;
        int[] totalPiecesMatched = new int[config.Pieces.Length];
        int[] tierPiecesMatched = new int[config.Pieces.Length];

        if (_lastGameStateAddr != 0)
        {
            turnsRemaining = ri32(_lastGameStateAddr + 0x24);
            score = ri32(_lastGameStateAddr + 0x1C);
            turnsMade = ri32(_lastGameStateAddr + 0x28);
            tier = ri32(_lastGameStateAddr + 0x2C);

            // Read Int32[] totalPiecesMatched at +0x48
            try
            {
                ulong totalMatchedPtr = rptr(_lastGameStateAddr + 0x48);
                long totalMatchedLen = ri64(totalMatchedPtr + 0x18);
                if (totalMatchedLen == config.Pieces.Length)
                {
                    for (int i = 0; i < config.Pieces.Length; i++)
                        totalPiecesMatched[i] = ri32(totalMatchedPtr + 0x20 + (ulong)(i * 4));
                }
            }
            catch { /* use zeroed array if read fails */ }

            // Read Int32[] tierPiecesMatched at +0x50
            try
            {
                ulong tierMatchedPtr = rptr(_lastGameStateAddr + 0x50);
                long tierMatchedLen = ri64(tierMatchedPtr + 0x18);
                if (tierMatchedLen == config.Pieces.Length)
                {
                    for (int i = 0; i < config.Pieces.Length; i++)
                        tierPiecesMatched[i] = ri32(tierMatchedPtr + 0x20 + (ulong)(i * 4));
                }
            }
            catch { /* use zeroed array if read fails */ }
        }

        return (pieces, rng, turnsRemaining, score, tier, turnsMade, totalPiecesMatched, tierPiecesMatched);
    }
    catch
    {
        return null;
    }
}

// ── Log Tail Loop ──
async Task TailLog(CancellationToken ct)
{
    Console.WriteLine($"[*] Tailing {logPath}");

    // Scan tail of log for a recent ProcessMatch3Start (catches already-open games)
    {
        string? lastMatch3Line = null;
        using var scanFs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long seekBack = scanFs.Length; // scan entire file for existing games
        scanFs.Seek(-seekBack, SeekOrigin.End);
        using var scanReader = new StreamReader(scanFs);
        scanReader.ReadLine(); // discard partial line
        while (scanReader.ReadLine() is { } line)
        {
            if (_match3Rx.IsMatch(line))
                lastMatch3Line = line;
        }
        if (lastMatch3Line != null)
        {
            Console.WriteLine("[*] Found existing Match-3 game in log, processing...");
            ProcessLogLine(lastMatch3Line);
        }
    }

    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);

    while (!ct.IsCancellationRequested)
    {
        string? line = await reader.ReadLineAsync(ct);
        if (line != null)
        {
            ProcessLogLine(line);
        }
        else
        {
            try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

// ── HTTP Server ──
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    Console.WriteLine($"[*] Dashboard at http://localhost:{port}/");

    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync().WaitAsync(ct); }
        catch (OperationCanceledException) { break; }

        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            if (req.Url?.AbsolutePath == "/api/state")
            {
                GameSession? session;
                lock (_lock) { session = _currentSession; }
                var apiObj = BuildApiState(session);
                var json = JsonSerializer.Serialize(apiObj, new JsonSerializerOptions { WriteIndented = false });
                var buf = Encoding.UTF8.GetBytes(json);
                resp.ContentType = "application/json";
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf, ct);
            }
            else
            {
                var buf = Encoding.UTF8.GetBytes(DashboardHtml.PAGE);
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf, ct);
            }
            resp.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] HTTP error: {ex.Message}");
        }
    }
    listener.Stop();
}

object BuildApiState(GameSession? session)
{
    if (session == null)
        return new { status = "waiting", message = "Waiting for ProcessMatch3Start in Player.log..." };

    var config = session.Config;
    var sol = session.Solution;

    return new
    {
        status = session.Status.ToString().ToLower(),
        error = session.ErrorMessage,
        source = "memory",
        sessionId = session.SessionId,
        receivedAt = session.ReceivedAt.ToString("HH:mm:ss"),
        config = new
        {
            title = config.Title,
            width = config.Width,
            height = config.Height,
            numTurns = config.NumTurns,
            randomSeed = config.RandomSeed,
            giveRewards = config.GiveRewards,
            scoreFor3s = config.ScoreFor3s,
            scoreFor4s = config.ScoreFor4s,
            scoreFor5s = config.ScoreFor5s,
            scoreDeltasPerTier = config.ScoreDeltasPerTier,
            scoresPerChainLevel = config.ScoresPerChainLevel,
            pieceReqsPerTier = config.PieceReqsPerTier,
            pieces = config.Pieces.Select(p => new { label = p.Label, iconId = p.IconID, tier = p.Tier }).ToArray()
        },
        board = session.InitialBoard,
        numPieceTypes = session.NumPieceTypes,
        pieceLabels = session.PieceLabels,
        solution = sol == null ? null : new
        {
            predictedScore = sol.PredictedScore,
            predictedTier = sol.PredictedTier,
            statesExplored = sol.StatesExplored,
            solveTimeMs = (int)sol.SolveTime.TotalMilliseconds,
            strategy = sol.Strategy,
            moves = sol.BestMoves.Select(m => new
            {
                x = m.X,
                y = m.Y,
                direction = m.Direction.ToString().ToLower(),
                scoreAfter = m.ScoreAfter,
                description = m.Description
            }).ToArray()
        }
    };
}

// ── Start ──
Console.WriteLine("=== Match-3 Solver for Project Gorgon ===");
Console.WriteLine($"Port: {port} | Board source: game memory");

// If no saved calibration, prompt user to click two corners
if (!File.Exists(Path.Combine(settingsDir, "match3_grid.json")))
{
    Console.WriteLine();
    Console.WriteLine("╔═══════════════════════════════════════════════════════╗");
    Console.WriteLine("║  GRID CALIBRATION NEEDED (one-time setup)            ║");
    Console.WriteLine("║                                                      ║");
    Console.WriteLine("║  1. Open a Match-3 board in-game                     ║");
    Console.WriteLine("║  2. Press ENTER here when ready                      ║");
    Console.WriteLine("║  3. Click the CENTER of the TOP-LEFT cell            ║");
    Console.WriteLine("║  4. Click the CENTER of the BOTTOM-RIGHT cell        ║");
    Console.WriteLine("╚═══════════════════════════════════════════════════════╝");
    Console.ReadLine();

    Console.WriteLine("[*] Click the CENTER of the TOP-LEFT cell now...");
    var p1 = GameAutoPlayer.WaitForClick();
    Console.WriteLine($"[+] Top-left: ({p1.X}, {p1.Y})");

    Console.WriteLine("[*] Click the CENTER of the BOTTOM-RIGHT cell now...");
    var p2 = GameAutoPlayer.WaitForClick();
    Console.WriteLine($"[+] Bottom-right: ({p2.X}, {p2.Y})");

    // Calculate grid from two corner centers
    // Top-left cell center = (gridX + cellSize/2, gridY + cellSize/2)
    // Bottom-right cell center = (gridX + 6*cellSize + cellSize/2, gridY + 6*cellSize + cellSize/2)
    int cellW = (p2.X - p1.X) / 6;
    int cellH = (p2.Y - p1.Y) / 6;
    _cellSize = (cellW + cellH) / 2; // average
    _gridX = p1.X - _cellSize / 2;
    _gridY = p1.Y - _cellSize / 2;

    Directory.CreateDirectory(settingsDir);
    File.WriteAllText(Path.Combine(settingsDir, "match3_grid.json"),
        JsonSerializer.Serialize(new { gridX = _gridX, gridY = _gridY, cellSize = _cellSize }));
    Console.WriteLine($"[+] Calibration saved: gridX={_gridX}, gridY={_gridY}, cellSize={_cellSize}");
}


// Parse strategy from args
foreach (var arg in args)
{
    if (arg.StartsWith("--strategy="))
    {
        var val = arg.Substring("--strategy=".Length);
        if (Enum.TryParse<SolverStrategy>(val, true, out var s)) _strategy = s;
        else Console.WriteLine($"[!] Unknown strategy: {val}. Using Auto.");
    }
}
Console.WriteLine($"[*] Solver strategy: {_strategy}");

var logTask = Task.Run(() => TailLog(cts.Token));
var httpTask = Task.Run(() => RunHttpServer(cts.Token));
await Task.WhenAll(logTask, httpTask);

// ═══════════════════════════════════════════════════════════════════
// Data Models
// ═══════════════════════════════════════════════════════════════════

enum MoveDir { Up = 0, Down = 1, Left = 2, Right = 3 }
enum MoveResult { Success = 0, InvalidPosition, NoMatch, OtherError }
enum MatchDirection { Horizontal = 0, Vertical = 1 }
enum SolveStatus { Solving, Solved, Error }
enum SolverStrategy { Auto, Beam, MCTS, Eval, Iterative }

readonly record struct XY(int X, int Y);

readonly record struct MatchLocation(XY Pos, int Type, MatchDirection Dir, int Length);

class Match3Config
{
    [JsonPropertyName("Width")] public int Width { get; set; }
    [JsonPropertyName("Height")] public int Height { get; set; }
    [JsonPropertyName("Title")] public string Title { get; set; } = "";
    [JsonPropertyName("NumTurns")] public int NumTurns { get; set; }
    [JsonPropertyName("RandomSeed")] public int RandomSeed { get; set; }
    [JsonPropertyName("GiveRewards")] public bool GiveRewards { get; set; }
    [JsonPropertyName("PieceReqsPerTier")] public int[] PieceReqsPerTier { get; set; } = [];
    [JsonPropertyName("ScoreFor3s")] public int ScoreFor3s { get; set; }
    [JsonPropertyName("ScoreFor4s")] public int ScoreFor4s { get; set; }
    [JsonPropertyName("ScoreFor5s")] public int ScoreFor5s { get; set; }
    [JsonPropertyName("ScoreDeltasPerTier")] public int[] ScoreDeltasPerTier { get; set; } = [];
    [JsonPropertyName("ScoresPerChainLevel")] public int[] ScoresPerChainLevel { get; set; } = [];
    [JsonPropertyName("Pieces")] public PieceInfo[] Pieces { get; set; } = [];
}

class PieceInfo
{
    [JsonPropertyName("IconID")] public int IconID { get; set; }
    [JsonPropertyName("Label")] public string Label { get; set; } = "";
    [JsonPropertyName("Tier")] public int Tier { get; set; }
}

class StepResults
{
    public readonly Dictionary<int, int> Match3s = new();
    public readonly Dictionary<int, int> Match4s = new();
    public readonly Dictionary<int, int> Match5s = new();
    public readonly List<MatchLocation> Matches = new();
    public bool HadMatch4OrMore;

    public void Clear()
    {
        Match3s.Clear(); Match4s.Clear(); Match5s.Clear();
        Matches.Clear(); HadMatch4OrMore = false;
    }
}

class SolverResult
{
    public List<SolverMove> BestMoves { get; set; } = new();
    public int PredictedScore { get; set; }
    public int PredictedTier { get; set; }
    public int StatesExplored { get; set; }
    public TimeSpan SolveTime { get; set; }
    public string Strategy { get; set; } = "";
}

class SolverMove
{
    public int X { get; set; }
    public int Y { get; set; }
    public MoveDir Direction { get; set; }
    public int ScoreAfter { get; set; }
    public string Description { get; set; } = "";
}

class GameSession
{
    public int SessionId { get; set; }
    public Match3Config Config { get; set; } = new();
    public int[]? InitialBoard { get; set; }
    public int NumPieceTypes { get; set; }
    public string[]? PieceLabels { get; set; }
    public SolverResult? Solution { get; set; }
    public SolveStatus Status { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime ReceivedAt { get; set; }
    public int ConsecutiveFailures { get; set; }
}

// ═══════════════════════════════════════════════════════════════════
// MonoRandom — Mono's System.Random (Knuth subtractive generator)
// ═══════════════════════════════════════════════════════════════════

class MonoRandom : ICloneable
{
    private const int MBIG = int.MaxValue;
    private const int MSEED = 161803398;

    private int[] _seedArray = new int[56];
    private int _inext;
    private int _inextp;
    public int CallCount { get; private set; }

    public MonoRandom(int seed)
    {
        int ii;
        int mj, mk;

        mj = MSEED - Math.Abs(seed);
        _seedArray[55] = mj;
        mk = 1;
        for (int i = 1; i < 55; i++)
        {
            ii = (21 * i) % 55;
            _seedArray[ii] = mk;
            mk = mj - mk;
            if (mk < 0) mk += MBIG;
            mj = _seedArray[ii];
        }
        for (int k = 1; k <= 4; k++)
        {
            for (int i = 1; i < 56; i++)
            {
                _seedArray[i] -= _seedArray[1 + (i + 30) % 55];
                if (_seedArray[i] < 0) _seedArray[i] += MBIG;
            }
        }
        _inext = 0;
        _inextp = 21;
        CallCount = 0;
    }

    /// <summary>Reconstruct from memory-read PRNG state.</summary>
    public MonoRandom(int[] seedArray, int inext, int inextp)
    {
        Array.Copy(seedArray, _seedArray, 56);
        _inext = inext;
        _inextp = inextp;
        CallCount = 0;
    }

    private MonoRandom() { CallCount = 0; }

    private int InternalSample()
    {
        int retVal;
        int locINext = _inext;
        int locINextp = _inextp;

        if (++locINext >= 56) locINext = 1;
        if (++locINextp >= 56) locINextp = 1;

        retVal = _seedArray[locINext] - _seedArray[locINextp];
        if (retVal < 0) retVal += MBIG;

        _seedArray[locINext] = retVal;
        _inext = locINext;
        _inextp = locINextp;

        return retVal;
    }

    public int Next(int maxValue)
    {
        CallCount++;
        return (int)(InternalSample() * (1.0 / MBIG) * maxValue);
    }

    public object Clone()
    {
        var clone = new MonoRandom();
        Array.Copy(_seedArray, clone._seedArray, 56);
        clone._inext = _inext;
        clone._inextp = _inextp;
        clone.CallCount = CallCount;
        return clone;
    }
}

// ═══════════════════════════════════════════════════════════════════
// SimBoard — Board simulation engine
// ═══════════════════════════════════════════════════════════════════

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

// ═══════════════════════════════════════════════════════════════════
// SimGameState — Full game state with scoring
// ═══════════════════════════════════════════════════════════════════

class SimGameState
{
    public SimBoard Board { get; private set; } = null!;
    public int Score { get; private set; }
    public int Chain { get; private set; }
    public int TurnsRemaining { get; private set; }
    public int TurnsMade { get; private set; }
    public int Tier { get; private set; }
    public bool IsFreeTurn { get; private set; }
    public bool IsExtraTurnEarned { get; private set; }
    public bool IsGameOver { get; private set; }
    public int[] TotalPiecesMatched { get; private set; } = [];
    public int[] TierPiecesMatched { get; private set; } = [];
    public bool[] PiecesTiered { get; private set; } = [];
    private Match3Config _config = null!;

    /// <summary>Initialize from a memory-read board.</summary>
    public void StartFromMemory(Match3Config config, int[] pieces, MonoRandom rng)
    {
        StartFromMemoryWithTurns(config, pieces, rng, config.NumTurns);
    }

    public void StartFromMemoryWithTurns(Match3Config config, int[] pieces, MonoRandom rng, int turnsLeft)
        => StartFromMemoryWithTurns(config, pieces, rng, turnsLeft, 0, 0, 0, new int[config.Pieces.Length], new int[config.Pieces.Length]);

    /// <summary>Initialize from a memory-read board with full live game state (score, tier, match counters).</summary>
    public void StartFromMemoryWithTurns(Match3Config config, int[] pieces, MonoRandom rng, int turnsLeft,
        int score, int tier, int turnsMade, int[] totalPiecesMatched, int[] tierPiecesMatched)
    {
        _config = config;
        var pieceTiers = config.Pieces.Select(p => p.Tier).ToArray();
        // Detect active piece types from what's actually on the board
        int maxType = pieces.Max();
        int activePieces = Math.Max(maxType + 1, pieceTiers.Count(t => t == 0));
        Board = new SimBoard(config.Width, config.Height, config.Pieces.Length, pieces, pieceTiers, rng);
        Board.SetActivePieceTypes(activePieces);
        Score = score;
        Chain = 0;
        TurnsRemaining = turnsLeft;
        TurnsMade = turnsMade;
        Tier = tier;
        IsFreeTurn = false;
        IsExtraTurnEarned = false;
        IsGameOver = false;
        TotalPiecesMatched = totalPiecesMatched.Length == config.Pieces.Length
            ? (int[])totalPiecesMatched.Clone() : new int[config.Pieces.Length];
        TierPiecesMatched = tierPiecesMatched.Length == config.Pieces.Length
            ? (int[])tierPiecesMatched.Clone() : new int[config.Pieces.Length];
        PiecesTiered = new bool[config.Pieces.Length];
        // Reconstruct PiecesTiered from live tierPiecesMatched so FindTierUp() knows
        // which pieces already met the threshold in the current tier
        if (tier < config.PieceReqsPerTier.Length)
        {
            int req = config.PieceReqsPerTier[tier];
            for (int i = 0; i < activePieces && i < TierPiecesMatched.Length; i++)
                PiecesTiered[i] = TierPiecesMatched[i] >= req;
        }
    }

    public SimGameState() { }

    public SimGameState Clone()
    {
        return new SimGameState
        {
            Board = Board.Clone(),
            Score = Score, Chain = Chain,
            TurnsRemaining = TurnsRemaining, TurnsMade = TurnsMade,
            Tier = Tier, IsFreeTurn = IsFreeTurn,
            IsExtraTurnEarned = IsExtraTurnEarned, IsGameOver = IsGameOver,
            TotalPiecesMatched = (int[])TotalPiecesMatched.Clone(),
            TierPiecesMatched = (int[])TierPiecesMatched.Clone(),
            PiecesTiered = (bool[])PiecesTiered.Clone(),
            _config = _config
        };
    }

    public MoveResult MakeMove(int x, int y, MoveDir dir)
    {
        if (IsGameOver) return MoveResult.OtherError;
        var result = Board.MakeBasicMove(x, y, dir);
        if (result != MoveResult.Success) return result;

        IsExtraTurnEarned = false;
        // Chain starts at 1: the game's chain field is 1 for the initial swap matches,
        // 2 for first cascade, etc. ScoresPerChainLevel is 1-indexed in the game.
        // Live data showed ~10pt gap per move consistent with off-by-one on chain index.
        Chain = 1;
        var stepResults = new StepResults();
        while (Board.Step(stepResults))
        {
            AddToScore(stepResults);
            Chain++;
        }

        TurnsMade++;
        if (!IsExtraTurnEarned) TurnsRemaining--;
        FindTierUp();
        if (TurnsRemaining <= 0) IsGameOver = true;
        else if (Board.GetAllValidMoves().Count == 0) IsGameOver = true;
        return MoveResult.Success;
    }

    private void AddToScore(StepResults info)
    {
        int tierDelta = (_config.ScoreDeltasPerTier.Length > 0 && Tier < _config.ScoreDeltasPerTier.Length)
            ? _config.ScoreDeltasPerTier[Tier] : 0;
        int chainMultiplier = (_config.ScoresPerChainLevel.Length > 0)
            ? _config.ScoresPerChainLevel[Math.Min(Chain, _config.ScoresPerChainLevel.Length - 1)] : 1;

        void ScoreMatches(Dictionary<int, int> matches, int baseScore)
        {
            foreach (var (type, count) in matches)
                Score += (baseScore + tierDelta) * chainMultiplier * count;
        }

        ScoreMatches(info.Match3s, _config.ScoreFor3s);
        ScoreMatches(info.Match4s, _config.ScoreFor4s);
        ScoreMatches(info.Match5s, _config.ScoreFor5s);
        if (info.HadMatch4OrMore) IsExtraTurnEarned = true;

        foreach (var match in info.Matches)
        {
            int type = match.Type;
            if (type >= 0 && type < TotalPiecesMatched.Length)
            {
                TotalPiecesMatched[type] += match.Length;
                TierPiecesMatched[type] += match.Length;
            }
        }
    }

    private void FindTierUp()
    {
        if (_config.PieceReqsPerTier.Length == 0 || Tier >= _config.PieceReqsPerTier.Length) return;
        int req = _config.PieceReqsPerTier[Tier];
        bool allMet = true;
        for (int i = 0; i < Board.ActivePieceTypes; i++)
        {
            if (TierPiecesMatched[i] >= req) PiecesTiered[i] = true;
            else allMet = false;
        }
        if (allMet)
        {
            Tier++;
            TierPiecesMatched = new int[_config.Pieces.Length];
            PiecesTiered = new bool[_config.Pieces.Length];
            int active = 0;
            for (int i = 0; i < _config.Pieces.Length; i++)
                if (_config.Pieces[i].Tier <= Tier) active++;
            Board.SetActivePieceTypes(active);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════
// Match3Solver — DFS / Beam Search
// ═══════════════════════════════════════════════════════════════════

class Match3Solver
{
    private int _statesExplored;
    private int _bestScore;
    private List<SolverMove> _bestPath = new();
    private Stopwatch _solveTimer = new();
    private const int SOLVE_TIMEOUT_MS = 10000;
    private const int BEAM_TIMEOUT_MS = 5000;

    public SolverResult Solve(SimGameState initialState, Match3Config config)
    {
        _statesExplored = 0;
        _bestScore = 0;
        _bestPath = new List<SolverMove>();
        _solveTimer = Stopwatch.StartNew();

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
        if (_solveTimer.ElapsedMilliseconds > SOLVE_TIMEOUT_MS) return;
        if (turnsLeft <= 0 || state.IsGameOver)
        {
            if (state.Score > _bestScore) { _bestScore = state.Score; _bestPath = new List<SolverMove>(path); }
            return;
        }
        var moves = state.Board.GetAllValidMoves();
        if (moves.Count == 0) { if (state.Score > _bestScore) { _bestScore = state.Score; _bestPath = new List<SolverMove>(path); } return; }

        foreach (var (x, y, dir) in moves)
        {
            var clone = state.Clone();
            int scoreBefore = clone.Score;
            clone.MakeMove(x, y, dir);
            int turnsUsed = clone.IsExtraTurnEarned ? 0 : 1;
            path.Add(new SolverMove { X = x, Y = y, Direction = dir, ScoreAfter = clone.Score, Description = $"({x},{y}) {dir} +{clone.Score - scoreBefore}" });
            DFS(clone, turnsLeft - turnsUsed, path);
            path.RemoveAt(path.Count - 1);
        }
    }

    private void BeamSearch(SimGameState initialState, int totalTurns, int beamWidth)
    {
        var beam = new List<(SimGameState state, List<SolverMove> path)> { (initialState, new List<SolverMove>()) };
        for (int turn = 0; turn < totalTurns; turn++)
        {
            if (_solveTimer.ElapsedMilliseconds > BEAM_TIMEOUT_MS) break;
            var candidates = new List<(SimGameState state, List<SolverMove> path, int score)>();
            foreach (var (state, path) in beam)
            {
                if (state.IsGameOver) continue;
                foreach (var (x, y, dir) in state.Board.GetAllValidMoves())
                {
                    _statesExplored++;
                    var clone = state.Clone();
                    int scoreBefore = clone.Score;
                    clone.MakeMove(x, y, dir);
                    var newPath = new List<SolverMove>(path) { new() { X = x, Y = y, Direction = dir, ScoreAfter = clone.Score, Description = $"({x},{y}) {dir} +{clone.Score - scoreBefore}" } };
                    candidates.Add((clone, newPath, clone.Score));
                }
            }
            if (candidates.Count == 0) break;
            beam = candidates.OrderByDescending(c => c.score).Take(beamWidth).Select(c => (c.state, c.path)).ToList();
        }
        if (beam.Count > 0) { var best = beam.OrderByDescending(b => b.state.Score).First(); _bestScore = best.state.Score; _bestPath = best.path; }
    }

    private void GreedyLookahead(SimGameState initialState, int totalTurns, int lookahead)
    {
        var current = initialState;
        var path = new List<SolverMove>();
        for (int turn = 0; turn < totalTurns; turn++)
        {
            if (_solveTimer.ElapsedMilliseconds > BEAM_TIMEOUT_MS) break;
            if (current.IsGameOver) break;
            var moves = current.Board.GetAllValidMoves();
            if (moves.Count == 0) break;
            int bestMoveScore = -1;
            (int x, int y, MoveDir dir) bestMove = moves[0];
            foreach (var (x, y, dir) in moves)
            {
                if (_solveTimer.ElapsedMilliseconds > BEAM_TIMEOUT_MS) break;
                _statesExplored++;
                var clone = current.Clone();
                clone.MakeMove(x, y, dir);
                int score = MiniDFS(clone, Math.Min(lookahead, totalTurns - turn - 1));
                if (score > bestMoveScore) { bestMoveScore = score; bestMove = (x, y, dir); }
            }
            int scoreBefore = current.Score;
            current = current.Clone();
            current.MakeMove(bestMove.x, bestMove.y, bestMove.dir);
            path.Add(new SolverMove { X = bestMove.x, Y = bestMove.y, Direction = bestMove.dir, ScoreAfter = current.Score, Description = $"({bestMove.x},{bestMove.y}) {bestMove.dir} +{current.Score - scoreBefore}" });
        }
        _bestScore = current.Score;
        _bestPath = path;
    }

    private int MiniDFS(SimGameState state, int depth)
    {
        if (depth <= 0 || state.IsGameOver) return state.Score;
        var moves = state.Board.GetAllValidMoves();
        if (moves.Count == 0) return state.Score;
        int best = state.Score;
        foreach (var (x, y, dir) in moves) { _statesExplored++; var clone = state.Clone(); clone.MakeMove(x, y, dir); int score = MiniDFS(clone, depth - 1); if (score > best) best = score; }
        return best;
    }
}

// ═══════════════════════════════════════════════════════════════════
// Dashboard HTML
// ═══════════════════════════════════════════════════════════════════

static class DashboardHtml
{
    public const string PAGE = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Match-3 Solver</title>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#0a0e17;color:#e0e0e0;font-family:'Segoe UI',system-ui,sans-serif;padding:16px}
h1{color:#ffd700;font-size:1.4em;margin-bottom:8px}
.status{padding:8px 16px;border-radius:6px;font-weight:bold;display:inline-block;margin-bottom:12px}
.status.waiting{background:#1a1a2e;color:#888}
.status.solving{background:#2a1a00;color:#ffa500}
.status.solved{background:#0a2a0a;color:#4caf50}
.status.error{background:#2a0a0a;color:#f44336}
.source-badge{background:#0a2a0a;color:#4caf50;padding:4px 12px;border-radius:4px;font-size:12px;display:inline-block;margin-left:8px}
.container{display:grid;grid-template-columns:auto 1fr;gap:16px;max-width:1200px}
.board-panel{background:#12162a;border:1px solid #2a2e4a;border-radius:8px;padding:16px}
.info-panel{background:#12162a;border:1px solid #2a2e4a;border-radius:8px;padding:16px}
.board{display:inline-grid;gap:2px;margin:8px 0}
.cell{width:44px;height:44px;border-radius:6px;display:flex;align-items:center;justify-content:center;font-size:11px;font-weight:bold;color:#fff;text-shadow:0 1px 2px rgba(0,0,0,.6);position:relative}
.cell.highlight{outline:3px solid #ffd700;outline-offset:-1px;z-index:1}
.cell .arrow{position:absolute;font-size:18px;color:#ffd700;filter:drop-shadow(0 0 4px #ffd700)}
.colors{display:flex;gap:6px;flex-wrap:wrap;margin:8px 0;font-size:12px}
.colors span{padding:2px 8px;border-radius:4px;font-weight:bold}
h2{color:#ccc;font-size:1.1em;margin:12px 0 6px}
.move{padding:6px 10px;margin:3px 0;border-radius:4px;background:#1a1e2e;border-left:3px solid #555;font-family:monospace;font-size:13px}
.move.best{border-left-color:#ffd700;background:#1a1a0a}
.move .badge{background:#ffd700;color:#000;padding:1px 6px;border-radius:3px;font-size:10px;margin-right:6px}
.config-grid{display:grid;grid-template-columns:auto 1fr;gap:2px 12px;font-size:13px}
.config-grid .label{color:#888}
.config-grid .val{color:#ddd;font-family:monospace}
.stats{font-size:12px;color:#888;margin-top:8px}
.score-big{font-size:2em;color:#ffd700;font-weight:bold}
</style>
</head>
<body>
<h1>Match-3 Solver</h1>
<div id="root"><div class="status waiting">Connecting...</div></div>
<script>
const PC = ['#e74c3c','#3498db','#2ecc71','#f39c12','#9b59b6','#1abc9c','#e67e22','#e91e63','#00bcd4','#8bc34a'];
const AR = {right:'\u2192',left:'\u2190',up:'\u2191',down:'\u2193'};

function render(d) {
  const root = document.getElementById('root');
  if (d.status === 'waiting') { root.innerHTML = '<div class="status waiting">' + d.message + '</div>'; return; }
  const c = d.config, sol = d.solution;
  const firstMove = sol && sol.moves.length > 0 ? sol.moves[0] : null;

  let boardHtml = '';
  if (d.board) {
    boardHtml = '<div class="board" style="grid-template-columns:repeat('+c.width+',44px)">';
    for (let y = c.height - 1; y >= 0; y--) {
      for (let x = 0; x < c.width; x++) {
        const p = d.board[y * c.width + x];
        const color = p >= 0 ? PC[p % PC.length] : '#333';
        const label = d.pieceLabels && p >= 0 ? d.pieceLabels[p] : '';
        const short = label.length > 5 ? label.substring(0, 5) : label;
        let hl = '', arrow = '';
        if (firstMove && firstMove.x === x && firstMove.y === y) {
          hl = ' highlight';
          arrow = '<span class="arrow">' + (AR[firstMove.direction] || '') + '</span>';
        }
        boardHtml += '<div class="cell' + hl + '" style="background:' + color + '">' + short + arrow + '</div>';
      }
    }
    boardHtml += '</div>';
  }

  let colorsHtml = '<div class="colors">';
  if (c.pieces) c.pieces.forEach((p, i) => {
    colorsHtml += '<span style="background:' + PC[i % PC.length] + '">' + p.label + '</span>';
  });
  colorsHtml += '</div>';

  let movesHtml = '';
  if (sol && sol.moves.length > 0) {
    sol.moves.forEach((m, i) => {
      const cls = i === 0 ? 'move best' : 'move';
      const badge = i === 0 ? '<span class="badge">NEXT</span>' : '';
      movesHtml += '<div class="' + cls + '">' + badge + '#' + (i+1) + ': ' + m.description + ' (score: ' + m.scoreAfter + ')</div>';
    });
  }

  root.innerHTML = `
    <div class="status ${d.status}">${d.status.toUpperCase()}${d.error ? ': ' + d.error : ''}</div>
    <span class="source-badge">Source: ${d.source || 'memory'}</span>
    <div class="container">
      <div class="board-panel">
        <h2>${c.title || 'Match-3'} <span style="color:#888;font-size:.8em">(${c.width}x${c.height})</span></h2>
        ${colorsHtml}
        ${boardHtml}
        <div class="stats">Seed: ${c.randomSeed} | Session: ${d.sessionId} | ${d.receivedAt}</div>
      </div>
      <div class="info-panel">
        <h2>Predicted Score</h2>
        <div class="score-big">${sol ? sol.predictedScore : '...'}</div>
        <h2>Optimal Moves (${c.numTurns} turns)</h2>
        ${movesHtml || '<div style="color:#666">Solving...</div>'}
        ${sol ? '<div class="stats">' + sol.strategy + ' | ' + sol.statesExplored.toLocaleString() + ' states | ' + sol.solveTimeMs + 'ms</div>' : ''}
        <h2>Scoring</h2>
        <div class="config-grid">
          <span class="label">3-match:</span><span class="val">${c.scoreFor3s}</span>
          <span class="label">4-match:</span><span class="val">${c.scoreFor4s}</span>
          <span class="label">5-match:</span><span class="val">${c.scoreFor5s}</span>
          <span class="label">Tier deltas:</span><span class="val">[${(c.scoreDeltasPerTier||[]).join(', ')}]</span>
          <span class="label">Chain mult:</span><span class="val">[${(c.scoresPerChainLevel||[]).join(', ')}]</span>
          <span class="label">Tier reqs:</span><span class="val">[${(c.pieceReqsPerTier||[]).join(', ')}]</span>
        </div>
      </div>
    </div>`;
}

async function poll() {
  try { const r = await fetch('/api/state'); render(await r.json()); }
  catch(e) { document.getElementById('root').innerHTML = '<div class="status error">Connection lost</div>'; }
}
setInterval(poll, 2000);
poll();
</script>
</body>
</html>
""";
}

// ═══════════════════════════════════════════════════════════════════
// GameAutoPlayer — Finds board on screen and executes moves via mouse
// ═══════════════════════════════════════════════════════════════════

static class GameAutoPlayer
{
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// Polls the board every <paramref name="pollIntervalMs"/> ms until two consecutive reads
    /// are identical (board settled after animations), or <paramref name="timeoutMs"/> elapses.
    /// Returns the settled board state, or the last read on timeout (with a warning logged).
    /// </summary>
    static bool PiecesEqual(int[] a, int[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    static async Task<(int[] pieces, MonoRandom rng)?> WaitForBoardSettle(
        Func<(int[] pieces, MonoRandom rng)?> readBoard,
        int initialDelayMs = 2000,
        int pollIntervalMs = 300,
        int timeoutMs = 8000)
    {
        await Task.Delay(initialDelayMs);

        var first = readBoard();
        if (first == null) return null;

        var elapsed = initialDelayMs;
        var prev = first;
        int stableCount = 0; // require 2 consecutive identical reads

        while (elapsed < timeoutMs)
        {
            await Task.Delay(pollIntervalMs);
            elapsed += pollIntervalMs;

            var next = readBoard();
            if (next == null) return prev;

            if (PiecesEqual(prev.Value.pieces, next.Value.pieces))
            {
                stableCount++;
                if (stableCount >= 2) // two consecutive identical reads = truly settled
                {
                    Console.WriteLine($"[~] Board settled after {elapsed}ms ({stableCount} stable reads)");
                    return next;
                }
            }
            else
            {
                stableCount = 0; // reset — board is still changing
            }

            prev = next;
        }

        Console.WriteLine($"[!] Board settle timeout after {elapsed}ms — using last read");
        return prev;
    }

    /// <summary>
    /// Finds the board grid on screen and executes each move as a mouse drag.
    /// Uses the "Lootmaster" or "Cashfall" title text in the popup header to anchor position.
    /// </summary>
    public static async Task ExecuteMoves(List<SolverMove> moves, Match3Config config, Func<(int[] pieces, MonoRandom rng)?> readBoard, int baseGridX, int baseGridY, int baseCellSize)
    {
        int boardW = config.Width, boardH = config.Height;

        // Use screen coordinates directly (game is fullscreen)
        int winW = GetSystemMetrics(0); // SM_CXSCREEN
        int winH = GetSystemMetrics(1); // SM_CYSCREEN
        if (winW <= 0 || winH <= 0) { Console.WriteLine("[!] Auto-play: can't get screen size"); return; }

        double scaleX = winW / 1920.0, scaleY = winH / 1080.0;
        int cellSize = (int)(baseCellSize * Math.Min(scaleX, scaleY));
        int gridX = (int)(baseGridX * scaleX);
        int gridY = (int)(baseGridY * scaleY);

        Console.WriteLine($"[+] Auto-play: screen={winW}x{winH}, grid=({gridX},{gridY}), cell={cellSize}px");
        await Task.Delay(300);

        // Read initial board state for comparison
        var preBoard = readBoard();
        if (preBoard == null) { Console.WriteLine("[!] Auto-play: can't read initial board"); return; }
        var currentPieces = preBoard.Value.pieces;

        for (int i = 0; i < moves.Count; i++)
        {
            var move = moves[i];
            int srcScreenX = gridX + move.X * cellSize + cellSize / 2;
            int srcScreenY = gridY + (boardH - 1 - move.Y) * cellSize + cellSize / 2;

            var target = SimBoard.DeltaByDir(move.X, move.Y, move.Direction);
            int dstScreenX = gridX + target.X * cellSize + cellSize / 2;
            int dstScreenY = gridY + (boardH - 1 - target.Y) * cellSize + cellSize / 2;

            Console.WriteLine($"[>] Move {i + 1}/{moves.Count}: ({move.X},{move.Y}) {move.Direction} → screen ({srcScreenX},{srcScreenY})→({dstScreenX},{dstScreenY})");

            // Bring game window to foreground
            FocusGameWindow();

            // Mouse drag
            SetCursorPos(srcScreenX, srcScreenY);
            await Task.Delay(80);
            MouseDown();
            await Task.Delay(120);
            int steps = 10;
            for (int s = 1; s <= steps; s++)
            {
                SetCursorPos(
                    srcScreenX + (dstScreenX - srcScreenX) * s / steps,
                    srcScreenY + (dstScreenY - srcScreenY) * s / steps);
                await Task.Delay(15);
            }
            await Task.Delay(80);
            MouseUp();

            // Wait for match + cascade animations to complete
            var postBoard = await WaitForBoardSettle(readBoard);
            if (postBoard == null)
            {
                Console.WriteLine($"[!] Move {i + 1}: can't read board after move (game over?)");
                break;
            }

            int changed = 0;
            for (int j = 0; j < currentPieces.Length && j < postBoard.Value.pieces.Length; j++)
                if (currentPieces[j] != postBoard.Value.pieces[j]) changed++;

            if (changed == 0)
            {
                Console.WriteLine($"[!] Move {i + 1}: board UNCHANGED — move missed! Retrying with longer drag...");
                await Task.Delay(1000);
                // Overshoot by 30% for more reliable swipe
                int overX = dstScreenX + (dstScreenX - srcScreenX) * 3 / 10;
                int overY = dstScreenY + (dstScreenY - srcScreenY) * 3 / 10;
                SetCursorPos(srcScreenX, srcScreenY);
                await Task.Delay(120);
                MouseDown();
                await Task.Delay(200);
                for (int s = 1; s <= steps; s++)
                {
                    SetCursorPos(
                        srcScreenX + (overX - srcScreenX) * s / steps,
                        srcScreenY + (overY - srcScreenY) * s / steps);
                    await Task.Delay(25);
                }
                await Task.Delay(120);
                MouseUp();
                postBoard = await WaitForBoardSettle(readBoard);

                if (postBoard != null)
                {
                    changed = 0;
                    for (int j = 0; j < currentPieces.Length && j < postBoard.Value.pieces.Length; j++)
                        if (currentPieces[j] != postBoard.Value.pieces[j]) changed++;
                }
                if (changed == 0)
                {
                    Console.WriteLine($"[!] Move {i + 1}: retry also failed — aborting auto-play");
                    break;
                }
            }

            Console.WriteLine($"[+] Move {i + 1}: verified — {changed} cells changed");
            currentPieces = postBoard!.Value.pieces;
        }

        Console.WriteLine($"[+] Auto-play: done");
    }

    /// <summary>Execute a single move, verify via memory. Returns true if board changed.</summary>
    public static async Task<bool> ExecuteSingleMove(SolverMove move, Match3Config config, Func<(int[] pieces, MonoRandom rng)?> readBoard, int baseGridX, int baseGridY, int baseCellSize, int[]? knownPrePieces = null)
    {
        int boardW = config.Width, boardH = config.Height;
        int winW = GetSystemMetrics(0), winH = GetSystemMetrics(1);
        double scaleX = winW / 1920.0, scaleY = winH / 1080.0;
        int cellSize = (int)(baseCellSize * Math.Min(scaleX, scaleY));
        int gridX = (int)(baseGridX * scaleX);
        int gridY = (int)(baseGridY * scaleY);

        // Use already-read pre-move board if provided, otherwise read fresh
        int[] prePieces;
        if (knownPrePieces != null)
        {
            prePieces = knownPrePieces;
        }
        else
        {
            var preMaybe = readBoard();
            if (preMaybe == null) return false;
            prePieces = preMaybe.Value.pieces;
        }

        int srcScreenX = gridX + move.X * cellSize + cellSize / 2;
        int srcScreenY = gridY + (boardH - 1 - move.Y) * cellSize + cellSize / 2;
        var target = SimBoard.DeltaByDir(move.X, move.Y, move.Direction);
        int dstScreenX = gridX + target.X * cellSize + cellSize / 2;
        int dstScreenY = gridY + (boardH - 1 - target.Y) * cellSize + cellSize / 2;

        Console.WriteLine($"[>] ({move.X},{move.Y}) {move.Direction} → screen ({srcScreenX},{srcScreenY})→({dstScreenX},{dstScreenY})");

        // Bring game window to foreground
        FocusGameWindow();

        // Execute drag
        SetCursorPos(srcScreenX, srcScreenY);
        await Task.Delay(80);
        MouseDown();
        await Task.Delay(120);
        int steps = 10;
        for (int s = 1; s <= steps; s++)
        {
            SetCursorPos(
                srcScreenX + (dstScreenX - srcScreenX) * s / steps,
                srcScreenY + (dstScreenY - srcScreenY) * s / steps);
            await Task.Delay(15);
        }
        await Task.Delay(80);
        MouseUp();

        // Wait for match + cascade animations to complete
        var postMaybe = await WaitForBoardSettle(readBoard);
        if (postMaybe == null) return false;
        int changed = 0;
        for (int j = 0; j < prePieces.Length && j < postMaybe.Value.pieces.Length; j++)
            if (prePieces[j] != postMaybe.Value.pieces[j]) changed++;

        if (changed == 0)
        {
            var target2 = SimBoard.DeltaByDir(move.X, move.Y, move.Direction);
            int srcType = (move.X >= 0 && move.X < boardW && move.Y >= 0 && move.Y < boardH) ? prePieces[move.Y * boardW + move.X] : -99;
            int dstType = (target2.X >= 0 && target2.X < boardW && target2.Y >= 0 && target2.Y < boardH) ? prePieces[target2.Y * boardW + target2.X] : -99;
            Console.WriteLine($"[!] Move ({move.X},{move.Y}) {move.Direction}: src_type={srcType} dst_type={dstType} — game rejected swap");
            Console.WriteLine($"[!] Board UNCHANGED — retrying with overshoot...");
            await Task.Delay(500);
            int overX = dstScreenX + (dstScreenX - srcScreenX) * 3 / 10;
            int overY = dstScreenY + (dstScreenY - srcScreenY) * 3 / 10;
            SetCursorPos(srcScreenX, srcScreenY);
            await Task.Delay(120);
            MouseDown();
            await Task.Delay(200);
            for (int s = 1; s <= steps; s++)
            {
                SetCursorPos(
                    srcScreenX + (overX - srcScreenX) * s / steps,
                    srcScreenY + (overY - srcScreenY) * s / steps);
                await Task.Delay(25);
            }
            await Task.Delay(120);
            MouseUp();
            postMaybe = await WaitForBoardSettle(readBoard);

            if (postMaybe == null) return false;
            changed = 0;
            for (int j = 0; j < prePieces.Length && j < postMaybe.Value.pieces.Length; j++)
                if (prePieces[j] != postMaybe.Value.pieces[j]) changed++;
            if (changed == 0) { Console.WriteLine($"[!] Retry also failed — move ({move.X},{move.Y}) {move.Direction} board still unchanged"); return false; }
        }

        Console.WriteLine($"[+] Verified — {changed} cells changed");
        return true;
    }

    static void FocusGameWindow()
    {
        IntPtr hwnd = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            try
            {
                var proc = Process.GetProcessById((int)pid);
                if (proc.ProcessName.Equals("WindowsPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    hwnd = h;
                    return false; // stop enumeration
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    const int VK_LBUTTON = 0x01;

    /// <summary>Wait for the user to click (left mouse button press), return cursor position.</summary>
    public static System.Drawing.Point WaitForClick()
    {
        // Wait for button to be released first (in case already held)
        while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) Thread.Sleep(10);
        // Wait for button press
        while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) Thread.Sleep(10);
        GetCursorPos(out var pt);
        // Wait for release
        while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) Thread.Sleep(10);
        return new System.Drawing.Point(pt.X, pt.Y);
    }

    static void MouseDown()
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    static void MouseUp()
    {
        var input = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    /// <summary>
    /// Captures the game window and saves a BMP with red crosshairs at every cell center.
    /// Opens the file so the user can verify grid alignment.
    /// </summary>
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Shows a transparent overlay with a 7x7 red grid. User drags/resizes it over the board,
    /// presses Enter to save. Arrow keys nudge 1px, Shift+Arrow nudges 5px.
    /// +/- changes cell size. Esc cancels.
    /// Returns (gridX, gridY, cellSize) or null if cancelled.
    /// </summary>
    public static (int gridX, int gridY, int cellSize)? ShowCalibrationOverlay(int boardW, int boardH, int initGridX, int initGridY, int initCellSize)
    {
        (int, int, int)? result = null;
        var thread = new Thread(() =>
        {
            Application.EnableVisualStyles();
            var form = new CalibrationForm(boardW, boardH, initGridX, initGridY, initCellSize);
            Application.Run(form);
            if (form.Confirmed)
                result = (form.GridX, form.GridY, form.CellSize);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}

class CalibrationForm : Form
{
    public int GridX, GridY, CellSize;
    public bool Confirmed;
    private readonly int _boardW, _boardH;

    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    public CalibrationForm(int boardW, int boardH, int gridX, int gridY, int cellSize)
    {
        _boardW = boardW; _boardH = boardH;
        GridX = gridX; GridY = gridY; CellSize = cellSize;

        Text = "Match-3 Grid Calibration — Arrow keys to move, +/- to resize, Enter to save, Esc to cancel";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(0, 0);
        Size = new System.Drawing.Size(Screen.PrimaryScreen!.Bounds.Width, Screen.PrimaryScreen.Bounds.Height);
        TopMost = true;
        BackColor = System.Drawing.Color.Black;
        TransparencyKey = System.Drawing.Color.Black;
        Opacity = 1.0;
        DoubleBuffered = true;
        KeyPreview = true;

        // Make click-through except for key events
        Load += (_, _) =>
        {
            int exStyle = GetWindowLong(Handle, -20);
            SetWindowLong(Handle, -20, exStyle | 0x20 | 0x80); // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW
        };

        KeyDown += OnKey;
        var timer = new System.Windows.Forms.Timer { Interval = 50 };
        timer.Tick += (_, _) => Invalidate();
        timer.Start();
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        int step = e.Shift ? 5 : 1;
        switch (e.KeyCode)
        {
            case Keys.Left: GridX -= step; break;
            case Keys.Right: GridX += step; break;
            case Keys.Up: GridY -= step; break;
            case Keys.Down: GridY += step; break;
            case Keys.Oemplus: case Keys.Add: CellSize++; break;
            case Keys.OemMinus: case Keys.Subtract: CellSize = Math.Max(10, CellSize - 1); break;
            case Keys.Enter: Confirmed = true; Close(); break;
            case Keys.Escape: Close(); break;
        }
        e.Handled = true;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        using var pen = new System.Drawing.Pen(System.Drawing.Color.Red, 2);
        using var thinPen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(180, 255, 0, 0), 1);
        using var font = new System.Drawing.Font("Consolas", 10);
        using var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(200, 255, 255, 0));
        using var bgBrush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(150, 0, 0, 0));

        // Draw grid lines
        for (int i = 0; i <= _boardW; i++)
            g.DrawLine(i == 0 || i == _boardW ? pen : thinPen, GridX + i * CellSize, GridY, GridX + i * CellSize, GridY + _boardH * CellSize);
        for (int j = 0; j <= _boardH; j++)
            g.DrawLine(j == 0 || j == _boardH ? pen : thinPen, GridX, GridY + j * CellSize, GridX + _boardW * CellSize, GridY + j * CellSize);

        // Draw crosshairs at cell centers
        for (int cy = 0; cy < _boardH; cy++)
        for (int cx = 0; cx < _boardW; cx++)
        {
            int x = GridX + cx * CellSize + CellSize / 2;
            int y = GridY + cy * CellSize + CellSize / 2;
            g.DrawLine(pen, x - 3, y, x + 3, y);
            g.DrawLine(pen, x, y - 3, x, y + 3);
        }

        // Info text
        string info = $"gridX={GridX}  gridY={GridY}  cellSize={CellSize}  |  Arrows=move  +/-=resize  Enter=save  Esc=cancel";
        var textSize = g.MeasureString(info, font);
        g.FillRectangle(bgBrush, 10, Height - 40, textSize.Width + 10, textSize.Height + 6);
        g.DrawString(info, font, brush, 15, Height - 37);
    }
}

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
bool _autoloop = false;
TaskCompletionSource<bool>? _newGameSignal = null; // signaled when a new ProcessMatch3Start arrives

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

// ── Item Value Lookup (for piece value prioritization) ──
Dictionary<string, int> _itemValues = new(StringComparer.OrdinalIgnoreCase);
{
    var itemsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "items.json");
    if (!File.Exists(itemsPath))
        itemsPath = Path.Combine(Directory.GetCurrentDirectory(), "items.json");
    if (File.Exists(itemsPath))
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(itemsPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.TryGetProperty("Name", out var nameEl) &&
                    prop.Value.TryGetProperty("Value", out var valEl))
                {
                    string name = nameEl.GetString() ?? "";
                    if (name.Length > 0 && valEl.TryGetInt32(out int val))
                        _itemValues.TryAdd(name, val);
                }
            }
            Console.WriteLine($"[*] Loaded {_itemValues.Count} item values from items.json");
        }
        catch (Exception ex) { Console.WriteLine($"[!] Failed to load items.json: {ex.Message}"); }
    }
    else
    {
        Console.WriteLine("[!] items.json not found — piece value prioritization disabled");
    }
}

void ResolvePieceValues(Match3Config config)
{
    config.PieceValues = new int[config.Pieces.Length];
    for (int i = 0; i < config.Pieces.Length; i++)
    {
        string label = config.Pieces[i].Label;
        // Handle "x3" bundles: "Amethyst x3" -> 3 * value of "Amethyst"
        int multiplier = 1;
        if (label.EndsWith(" x3")) { multiplier = 3; label = label[..^3]; }
        if (_itemValues.TryGetValue(label, out int val))
            config.PieceValues[i] = val * multiplier;
    }
}

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

    ResolvePieceValues(config);
    Console.WriteLine($"[*] Match-3 game detected: {config.Title} ({config.Width}x{config.Height}, {config.NumTurns} turns, seed={config.RandomSeed})");
    // Log tier-rush plan: show what we're racing toward
    Console.WriteLine("[*] Tier-rush plan:");
    for (int tier = 0; tier <= config.PieceReqsPerTier.Length; tier++)
    {
        var tierPieces = config.Pieces.Select((p, i) => (p, val: i < config.PieceValues.Length ? config.PieceValues[i] : 0, i))
            .Where(x => x.p.Tier == tier)
            .OrderByDescending(x => x.val).ToArray();
        if (tierPieces.Length == 0) continue;
        string req = tier < config.PieceReqsPerTier.Length ? $"(need {config.PieceReqsPerTier[tier]} each)" : "(max tier)";
        int bestVal = tierPieces.Max(x => x.val);
        Console.WriteLine($"  Tier {tier} {req}:");
        foreach (var (p, val, idx) in tierPieces)
            Console.WriteLine($"    [{idx}] {p.Label,-40} val={val}{(val == bestVal && val > 200 ? " <<<" : "")}");
    }
    OnNewGame(sessionId, config);
}

void OnNewGame(int sessionId, Match3Config config)
{
    // Signal any waiting autoloop that a new game has started
    _newGameSignal?.TrySetResult(true);

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
            if (_autoloop && !gameCt.IsCancellationRequested)
            {
                Console.WriteLine("[autoloop] Stale game — attempting to start a new one...");
                await AutoStartNewGame(gameCt);
            }
            return;
        }

        var (pieces, rng) = boardData.Value;
        session.InitialBoard = pieces;
        Console.WriteLine($"[+] Board read from memory: {pieces.Length} cells");

        // Early check: if game is already over (stale game from log), skip to autoloop
        {
            var earlyCheck = QuickReadBoard(config);
            if (earlyCheck != null && earlyCheck.Value.turnsRemaining <= 0)
            {
                Console.WriteLine("[*] Game already over (stale) — skipping to autoloop");
                if (_autoloop && !gameCt.IsCancellationRequested)
                    await AutoStartNewGame(gameCt);
                return;
            }
        }

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
                    Pieces = config.Pieces,
                    PieceValues = config.PieceValues
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
                        int iterBudget = turnsLeft switch
                        {
                            >= 12 => 7000,  // early game: tier setup is critical
                            >= 8  => 5000,  // mid game: standard budget
                            _     => 4000,  // late game: diminishing returns
                        };
                        result = iter.Solve(state, solveConfig, iterBudget);
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
                        _ => new BeamSolver().Solve(state, solveConfig) // Beam
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

                // Track the raw game score predicted for after this move for divergence detection
                // (EffectiveScore includes item values, but the game only reports raw Score)
                {
                    var predClone = state.Clone();
                    predClone.MakeMove(result.BestMoves[0].X, result.BestMoves[0].Y, result.BestMoves[0].Direction);
                    lastPredictedFirstMoveScore = predClone.Score;
                }

                // Validate first move against the ACTUAL read board before executing
                var firstMove = result.BestMoves[0];
                var validateBoard = new SimBoard(config.Width, config.Height, config.Pieces.Length,
                    curPieces, config.Pieces.Select(p => p.Tier).ToArray(), (MonoRandom)curRng.Clone());
                // Set active piece types based on current tier (same as solver state)
                int valActivePieces = config.Pieces.Count(p => p.Tier <= curTier);
                validateBoard.SetActivePieceTypes(valActivePieces);
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

            // Autoloop: click through Game Over → Use machine → Play Game
            if (_autoloop && !gameCt.IsCancellationRequested)
            {
                await AutoStartNewGame(gameCt);
            }
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

// ── Autoloop: Start New Game ──

// Autoloop click positions (loaded from/saved to settings)
int _okClickX = 0, _okClickY = 0;
int _useClickX = 0, _useClickY = 0;
int _playClickX = 0, _playClickY = 0;
bool _autoloopCalibrated = false;

void LoadAutoloopCalibration()
{
    var calFile = Path.Combine(settingsDir, "autoloop_clicks.json");
    if (File.Exists(calFile))
    {
        try
        {
            var doc = JsonDocument.Parse(File.ReadAllText(calFile));
            _okClickX = doc.RootElement.GetProperty("okX").GetInt32();
            _okClickY = doc.RootElement.GetProperty("okY").GetInt32();
            _useClickX = doc.RootElement.GetProperty("useX").GetInt32();
            _useClickY = doc.RootElement.GetProperty("useY").GetInt32();
            _playClickX = doc.RootElement.GetProperty("playX").GetInt32();
            _playClickY = doc.RootElement.GetProperty("playY").GetInt32();
            _autoloopCalibrated = true;
            Console.WriteLine($"[autoloop] Loaded click positions: OK=({_okClickX},{_okClickY}) Use=({_useClickX},{_useClickY}) Play=({_playClickX},{_playClickY})");
        }
        catch { }
    }
}

void CalibrateAutoloop()
{
    Console.WriteLine();
    Console.WriteLine("=== Autoloop Calibration ===");
    Console.WriteLine("You need to click 3 buttons so the solver knows where they are.");
    Console.WriteLine("Make sure the game is visible.");
    Console.WriteLine();

    Console.WriteLine("Step 1: Click the OK button on the Game Over dialog.");
    Console.Write("  Waiting for your click... ");
    var ok = GameAutoPlayer.WaitForClick();
    _okClickX = ok.X; _okClickY = ok.Y;
    Console.WriteLine($"OK at ({ok.X},{ok.Y})");

    Console.WriteLine("Step 2: Click the USE button on the machine nameplate.");
    Console.Write("  Waiting for your click... ");
    var use = GameAutoPlayer.WaitForClick();
    _useClickX = use.X; _useClickY = use.Y;
    Console.WriteLine($"Use at ({use.X},{use.Y})");

    Console.WriteLine("Step 3: Click the PLAY GAME button on the dialog panel.");
    Console.Write("  Waiting for your click... ");
    var play = GameAutoPlayer.WaitForClick();
    _playClickX = play.X; _playClickY = play.Y;
    Console.WriteLine($"Play at ({play.X},{play.Y})");

    _autoloopCalibrated = true;

    // Save
    Directory.CreateDirectory(settingsDir);
    var json = $"{{\"okX\":{_okClickX},\"okY\":{_okClickY},\"useX\":{_useClickX},\"useY\":{_useClickY},\"playX\":{_playClickX},\"playY\":{_playClickY}}}";
    File.WriteAllText(Path.Combine(settingsDir, "autoloop_clicks.json"), json);
    Console.WriteLine("[autoloop] Calibration saved! Will reuse next time.");
    Console.WriteLine();
}

async Task AutoStartNewGame(CancellationToken ct)
{
    if (!_autoloopCalibrated)
    {
        LoadAutoloopCalibration();
        if (!_autoloopCalibrated)
            CalibrateAutoloop();
    }

    Console.WriteLine("[autoloop] Starting new game...");

    // Step 1: Click OK on Game Over
    GameAutoPlayer.FocusGameWindow();
    await Task.Delay(500);
    Console.WriteLine($"[autoloop] Clicking OK at ({_okClickX},{_okClickY})");
    GameAutoPlayer.ClickAt(_okClickX, _okClickY);
    await Task.Delay(1500);

    // Step 2: Click Use on machine
    Console.WriteLine($"[autoloop] Clicking Use at ({_useClickX},{_useClickY})");
    GameAutoPlayer.ClickAt(_useClickX, _useClickY);
    await Task.Delay(2000);

    // Step 3: Click Play Game
    Console.WriteLine($"[autoloop] Clicking Play at ({_playClickX},{_playClickY})");
    GameAutoPlayer.ClickAt(_playClickX, _playClickY);

    // Step 4: Wait for ProcessMatch3Start in log
    _newGameSignal = new TaskCompletionSource<bool>();
    Console.WriteLine("[autoloop] Waiting for new game to start...");
    var timeoutTask = Task.Delay(15000, ct);
    var completed = await Task.WhenAny(_newGameSignal.Task, timeoutTask);
    if (completed == timeoutTask)
    {
        Console.WriteLine("[autoloop] Timeout — retrying Use + Play...");
        GameAutoPlayer.FocusGameWindow();
        await Task.Delay(300);
        GameAutoPlayer.ClickAt(_useClickX, _useClickY);
        await Task.Delay(2000);
        GameAutoPlayer.ClickAt(_playClickX, _playClickY);

        _newGameSignal = new TaskCompletionSource<bool>();
        timeoutTask = Task.Delay(10000, ct);
        completed = await Task.WhenAny(_newGameSignal.Task, timeoutTask);
        if (completed == timeoutTask)
            Console.WriteLine("[autoloop] Still no new game — start manually.");
    }
    else
    {
        Console.WriteLine("[autoloop] New game detected!");
    }
    _newGameSignal = null;
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
        ulong baseAddr = hit.Address - Offsets.Config.RandomSeed;
        try
        {
            int width = ri32(baseAddr + Offsets.Config.Width);
            int height = ri32(baseAddr + Offsets.Config.Height);
            int numTurns = ri32(baseAddr + Offsets.Config.NumTurns);
            int scoreFor3s = ri32(baseAddr + Offsets.Config.ScoreFor3s);
            ulong vtable = rptr(baseAddr + Offsets.Vtable);
            ulong sync = rptr(baseAddr + Offsets.SyncBlock);

            if (width == config.Width && height == config.Height &&
                numTurns == config.NumTurns && scoreFor3s == config.ScoreFor3s &&
                vtable > Offsets.MinValidPtr && sync == 0)
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
        ulong candidateBoard = hit.Address - Offsets.Board.Config;
        try
        {
            int bWidth = ri32(candidateBoard + Offsets.Board.Width);
            int bHeight = ri32(candidateBoard + Offsets.Board.Height);
            ulong vtable = rptr(candidateBoard + Offsets.Vtable);
            ulong sync = rptr(candidateBoard + Offsets.SyncBlock);
            ulong piecesPtr = rptr(candidateBoard + Offsets.Board.Pieces);

            if (bWidth == config.Width && bHeight == config.Height &&
                vtable > Offsets.MinValidPtr && sync == 0 && piecesPtr > Offsets.MinValidPtr)
            {
                boardAddr = candidateBoard;
                _lastBoardAddr = boardAddr;
                Console.WriteLine($"[+] GameBoard found at 0x{boardAddr:X} (via Config@+0x10)");
                break;
            }
        }
        catch { }

        // Try as GameStateSinglePlayer (Configuration at +0x70) → Board at +0x10
        ulong candidateGS = hit.Address - Offsets.GameState.Configuration;
        try
        {
            ulong gsVtable = rptr(candidateGS + Offsets.Vtable);
            ulong gsSync = rptr(candidateGS + Offsets.SyncBlock);
            ulong gsBoardPtr = rptr(candidateGS + Offsets.GameState.Board);
            int turnsRem = ri32(candidateGS + Offsets.GameState.TurnsRemaining);

            if (gsVtable > Offsets.MinValidPtr && gsSync == 0 && gsBoardPtr > Offsets.MinValidPtr && turnsRem > 0 && turnsRem <= config.NumTurns)
            {
                // Follow board pointer
                int bWidth = ri32(gsBoardPtr + Offsets.Board.Width);
                int bHeight = ri32(gsBoardPtr + Offsets.Board.Height);
                ulong bVtable = rptr(gsBoardPtr + Offsets.Vtable);
                ulong bSync = rptr(gsBoardPtr + Offsets.SyncBlock);
                ulong piecesPtr = rptr(gsBoardPtr + Offsets.Board.Pieces);

                if (bWidth == config.Width && bHeight == config.Height &&
                    bVtable > Offsets.MinValidPtr && bSync == 0 && piecesPtr > Offsets.MinValidPtr)
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

    // Step 4: Read Pieces[] array from GameBoard
    ulong piecesArrayPtr = rptr(boardAddr + Offsets.Board.Pieces);
    long arrayLength = ri64(piecesArrayPtr + Offsets.Array.Length);
    int expectedLen = config.Width * config.Height;

    if (arrayLength != expectedLen)
    {
        Console.WriteLine($"[!] Pieces array length mismatch: got {arrayLength}, expected {expectedLen}");
        return null;
    }

    // Read element pointers (IL2CPP ref array: elements start at Array.FirstElem, each 8 bytes)
    var pieces = new int[expectedLen];
    for (int i = 0; i < expectedLen; i++)
    {
        ulong piecePtr = rptr(piecesArrayPtr + Offsets.Array.FirstElem + (ulong)(i * 8));
        if (piecePtr == 0) { pieces[i] = -1; continue; }

        int pieceType = ri32(piecePtr + Offsets.Piece.Type);
        int px = ri32(piecePtr + Offsets.Piece.X);
        int py = ri32(piecePtr + Offsets.Piece.Y);

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
        ulong candidate = hit.Address - Offsets.GameState.Configuration;
        try
        {
            ulong boardPtr = rptr(candidate + Offsets.GameState.Board);
            int score = ri32(candidate + Offsets.GameState.Score);
            int turnsRem = ri32(candidate + Offsets.GameState.TurnsRemaining);
            ulong vtable = rptr(candidate + Offsets.Vtable);
            ulong sync = rptr(candidate + Offsets.SyncBlock);

            if (boardPtr == boardAddr && turnsRem > 0 && turnsRem <= config.NumTurns &&
                score >= 0 && vtable > Offsets.MinValidPtr && sync == 0)
            {
                gameStateAddr = candidate;
                _lastGameStateAddr = gameStateAddr;
                Console.WriteLine($"[+] GameStateSinglePlayer found at 0x{gameStateAddr:X} (turns={turnsRem}/{config.NumTurns})");
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
            ulong candidate = hit.Address - Offsets.GameState.Configuration;
            try
            {
                ulong boardPtr = rptr(candidate + Offsets.GameState.Board);
                int turnsRem = ri32(candidate + Offsets.GameState.TurnsRemaining);
                ulong vtable = rptr(candidate + Offsets.Vtable);
                ulong sync = rptr(candidate + Offsets.SyncBlock);

                if (boardPtr == boardAddr && turnsRem > 0 && turnsRem <= config.NumTurns &&
                    vtable > Offsets.MinValidPtr && sync == 0)
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
            ulong rndPtr = rptr(gameStateAddr + Offsets.GameState.Rng);
            int inext = ri32(rndPtr + Offsets.MonoRng.Inext);
            int inextp = ri32(rndPtr + Offsets.MonoRng.Inextp);
            ulong seedArrayPtr = rptr(rndPtr + Offsets.MonoRng.SeedArray);
            long seedArrayLen = ri64(seedArrayPtr + Offsets.Array.Length);

            if (seedArrayLen == 56 && inext >= 0 && inext < 56 && inextp >= 0 && inextp < 56)
            {
                var seedArray = new int[56];
                for (int i = 0; i < 56; i++)
                    seedArray[i] = ri32(seedArrayPtr + Offsets.Array.FirstElem + (ulong)(i * 4));

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
        MonoRandom rng = new MonoRandom(config.RandomSeed);
        bool rngReadOk = false;
        int expectedLen = config.Width * config.Height;
        int[] pieces = new int[expectedLen];

        for (int attempt = 0; attempt < 3; attempt++)
        {
            // Step 1: Read PRNG state
            int inext0 = -1, inextp0 = -1;
            MonoRandom? attemptRng = null;
            if (_lastGameStateAddr != 0)
            {
                try
                {
                    ulong rndPtr = rptr(_lastGameStateAddr + Offsets.GameState.Rng);
                    inext0 = ri32(rndPtr + Offsets.MonoRng.Inext);
                    inextp0 = ri32(rndPtr + Offsets.MonoRng.Inextp);
                    ulong seedArrayPtr = rptr(rndPtr + Offsets.MonoRng.SeedArray);
                    long seedArrayLen = ri64(seedArrayPtr + Offsets.Array.Length);

                    if (seedArrayLen == 56 && inext0 >= 0 && inext0 < 56 && inextp0 >= 0 && inextp0 < 56)
                    {
                        var seedArray = new int[56];
                        for (int i = 0; i < 56; i++)
                            seedArray[i] = ri32(seedArrayPtr + Offsets.Array.FirstElem + (ulong)(i * 4));
                        attemptRng = new MonoRandom(seedArray, inext0, inextp0);
                    }
                }
                catch { }
            }

            // Step 2: Read pieces array
            ulong piecesArrayPtr = rptr(_lastBoardAddr + Offsets.Board.Pieces);
            long arrayLength = ri64(piecesArrayPtr + Offsets.Array.Length);
            if (arrayLength != expectedLen) return null;

            var attemptPieces = new int[expectedLen];
            for (int i = 0; i < expectedLen; i++)
            {
                ulong piecePtr = rptr(piecesArrayPtr + Offsets.Array.FirstElem + (ulong)(i * 8));
                if (piecePtr == 0) { attemptPieces[i] = -1; continue; }
                int pieceType = ri32(piecePtr + Offsets.Piece.Type);
                int px = ri32(piecePtr + Offsets.Piece.X);
                int py = ri32(piecePtr + Offsets.Piece.Y);
                if (pieceType < -1 || pieceType > 9 || px < 0 || px >= config.Width || py < 0 || py >= config.Height)
                    return null;
                attemptPieces[py * config.Width + px] = pieceType;
            }

            // Step 3: Re-read inext/inextp to detect PRNG drift during pieces read
            bool drifted = false;
            if (attemptRng != null && _lastGameStateAddr != 0)
            {
                try
                {
                    ulong rndPtr = rptr(_lastGameStateAddr + Offsets.GameState.Rng);
                    int inext1 = ri32(rndPtr + Offsets.MonoRng.Inext);
                    int inextp1 = ri32(rndPtr + Offsets.MonoRng.Inextp);
                    if (inext1 != inext0 || inextp1 != inextp0)
                    {
                        Console.WriteLine($"[!] PRNG drift detected (inext: {inext0}->{inext1}), retrying...");
                        drifted = true;
                    }
                }
                catch { }
            }

            // Commit this attempt's results (best effort even if drifted on last retry)
            pieces = attemptPieces;
            if (attemptRng != null)
            {
                rng = attemptRng;
                rngReadOk = true;
                Console.WriteLine($"[prng] inext={inext0} inextp={inextp0}");
            }

            if (!drifted) break;
        }

        if (!rngReadOk)
            Console.WriteLine("[!] QuickReadBoard: falling back to seed-based PRNG — cascade predictions may diverge");

        // Read live game state from GameStateSinglePlayer
        int turnsRemaining = config.NumTurns;
        int score = 0;
        int tier = 0;
        int turnsMade = 0;
        int[] totalPiecesMatched = new int[config.Pieces.Length];
        int[] tierPiecesMatched = new int[config.Pieces.Length];

        if (_lastGameStateAddr != 0)
        {
            turnsRemaining = ri32(_lastGameStateAddr + Offsets.GameState.TurnsRemaining);
            score = ri32(_lastGameStateAddr + Offsets.GameState.Score);
            turnsMade = ri32(_lastGameStateAddr + Offsets.GameState.TurnsMade);
            tier = ri32(_lastGameStateAddr + Offsets.GameState.Tier);

            // Read Int32[] totalPiecesMatched
            try
            {
                ulong totalMatchedPtr = rptr(_lastGameStateAddr + Offsets.GameState.TotalPieces);
                long totalMatchedLen = ri64(totalMatchedPtr + Offsets.Array.Length);
                if (totalMatchedLen == config.Pieces.Length)
                {
                    for (int i = 0; i < config.Pieces.Length; i++)
                        totalPiecesMatched[i] = ri32(totalMatchedPtr + Offsets.Array.FirstElem + (ulong)(i * 4));
                }
            }
            catch { /* use zeroed array if read fails */ }

            // Read Int32[] tierPiecesMatched
            try
            {
                ulong tierMatchedPtr = rptr(_lastGameStateAddr + Offsets.GameState.TierPieces);
                long tierMatchedLen = ri64(tierMatchedPtr + Offsets.Array.Length);
                if (tierMatchedLen == config.Pieces.Length)
                {
                    for (int i = 0; i < config.Pieces.Length; i++)
                        tierPiecesMatched[i] = ri32(tierMatchedPtr + Offsets.Array.FirstElem + (ulong)(i * 4));
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
            pieces = config.Pieces.Select((p, i) => new { label = p.Label, iconId = p.IconID, tier = p.Tier, itemValue = i < config.PieceValues.Length ? config.PieceValues[i] : 0 }).ToArray()
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


// Parse args
foreach (var arg in args)
{
    if (arg.StartsWith("--strategy="))
    {
        var val = arg.Substring("--strategy=".Length);
        if (Enum.TryParse<SolverStrategy>(val, true, out var s)) _strategy = s;
        else Console.WriteLine($"[!] Unknown strategy: {val}. Using Auto.");
    }
    if (arg == "--autoloop") _autoloop = true;
}
Console.WriteLine($"[*] Solver strategy: {_strategy}");

var logTask = Task.Run(() => TailLog(cts.Token));
var httpTask = Task.Run(() => RunHttpServer(cts.Token));
await Task.WhenAll(logTask, httpTask);

// Extracted classes: SimGameState.cs, Solvers/BeamSolver.cs, DashboardHtml.cs, GameAutoPlayer.cs
// See: Models/Enums.cs, Models/DataModels.cs, MonoRandom.cs, SimBoard.cs, Solvers/ISolver.cs

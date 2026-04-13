using Looper;

// ── Configuration ──
string logDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon";
string logPath = Path.Combine(logDir, "Player.log");
string logPathPrev = Path.Combine(logDir, "Player-prev.log");

if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
    logPath = logPathPrev;
if (!File.Exists(logPath))
{
    Console.WriteLine("No Player.log found. Exiting.");
    return;
}

// Defaults
string match3Target = "lootmaster";
string bettingTarget = "kuzavek";
// Parse args
foreach (var arg in args)
{
    if (arg.StartsWith("--machine="))
        match3Target = arg["--machine=".Length..];
    if (arg.StartsWith("--betting="))
        bettingTarget = arg["--betting=".Length..];
}

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

var log = new LogWatcher(logPath);

// Start log watcher in background
_ = Task.Run(() => log.Run(ct));
await Task.Delay(500); // let log watcher start

// ── Main Loop ──
Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║           LOOPER — Casino Routine            ║");
Console.WriteLine("╠══════════════════════════════════════════════╣");
Console.WriteLine($"║  Match-3 target: {match3Target,-28}║");
Console.WriteLine($"║  Betting target: {bettingTarget,-28}║");
Console.WriteLine("╠══════════════════════════════════════════════╣");
Console.WriteLine("║  F5  = Done betting → go to Match-3         ║");
Console.WriteLine("║  F6  = Done Match-3 → go to Kuzavek         ║");
Console.WriteLine("║  ESC = Quit                                  ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine();

var state = LoopState.Idle;

Console.WriteLine("[*] Press F5 to navigate to Match-3, F6 to navigate to Kuzavek, ESC to quit.");

while (!ct.IsCancellationRequested)
{
    switch (state)
    {
        case LoopState.Idle:
        {
            var key = await InputSender.WaitForHotkey(ct);
            switch (key)
            {
                case HotKey.F5:
                    state = LoopState.NavigatingToMatch3;
                    break;
                case HotKey.F6:
                    state = LoopState.NavigatingToBetting;
                    break;
                case HotKey.Escape:
                    Console.WriteLine("[*] Quitting...");
                    cts.Cancel();
                    break;
            }
            break;
        }

        case LoopState.NavigatingToMatch3:
        {
            Console.WriteLine();
            Console.WriteLine($"[>] Navigating to Match-3 machine: {match3Target}");
            await Navigator.GoTo(match3Target, ct);

            // Wait a bit for auto-walk, then wait for the Match3Solver to pick up games
            Console.WriteLine("[*] Auto-walking... Match3Solver (running separately with --autoloop) will handle games.");
            Console.WriteLine("[*] Press F6 when you want to go bet at Kuzavek, or ESC to quit.");
            state = LoopState.PlayingMatch3;
            break;
        }

        case LoopState.PlayingMatch3:
        {
            // In this state, Match3Solver is handling the actual games.
            // We just wait for the user to press F6 to go betting, or ESC to quit.
            var key = await InputSender.WaitForHotkey(ct);
            switch (key)
            {
                case HotKey.F6:
                    state = LoopState.NavigatingToBetting;
                    break;
                case HotKey.F5:
                    // Re-navigate to match3 (maybe walked away or need to retarget)
                    state = LoopState.NavigatingToMatch3;
                    break;
                case HotKey.Escape:
                    Console.WriteLine("[*] Quitting...");
                    cts.Cancel();
                    break;
            }
            break;
        }

        case LoopState.NavigatingToBetting:
        {
            Console.WriteLine();
            Console.WriteLine($"[>] Navigating to betting NPC: {bettingTarget}");
            await Navigator.GoTo(bettingTarget, ct);

            Console.WriteLine("[*] Auto-walking to Kuzavek... Place your bets manually.");
            Console.WriteLine("[*] Press F5 when done betting to go back to Match-3, or ESC to quit.");
            state = LoopState.Betting;
            break;
        }

        case LoopState.Betting:
        {
            // Manual betting — wait for user to signal done
            var key = await InputSender.WaitForHotkey(ct);
            switch (key)
            {
                case HotKey.F5:
                    state = LoopState.NavigatingToMatch3;
                    break;
                case HotKey.F6:
                    // Re-navigate to betting (retarget)
                    state = LoopState.NavigatingToBetting;
                    break;
                case HotKey.Escape:
                    Console.WriteLine("[*] Quitting...");
                    cts.Cancel();
                    break;
            }
            break;
        }
    }
}

Console.WriteLine($"[*] Session complete. Match-3 games detected: {log.Match3GamesPlayed}");

enum LoopState
{
    Idle,
    NavigatingToMatch3,
    PlayingMatch3,
    NavigatingToBetting,
    Betting
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DiceGameSolver.Models;

namespace DiceGameSolver;

public sealed class ClickCalibration
{
    public System.Drawing.Point IntroPlay { get; set; }          // Intro screen: Play! Bet 1000
    public System.Drawing.Point PlayingRaise { get; set; }       // Playing: Raise Bet (code 101)
    public System.Drawing.Point PlayingStandPat { get; set; }    // Playing: Stand Pat (code 123) - optional
    public System.Drawing.Point PlayingRoll1 { get; set; }       // Playing: Roll 1 die (code 121)
    public System.Drawing.Point PlayingRoll2 { get; set; }       // Playing: Roll 2 dice (code 122)
    public System.Drawing.Point ResultPlayAgainWin { get; set; } // Result Won: Play again (code 112)
    public System.Drawing.Point ResultPlayAgainLose { get; set; }// Result Lost: Play Again (code 1)
    public System.Drawing.Point Dismiss { get; set; }            // empty dialog area for dice-overlay dismiss
    public bool StandPatSkipped { get; set; }

    private static string FilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProjectGorgonTools",
            "dicegame_calibration.json");

    public static ClickCalibration? Load()
    {
        var path = FilePath;
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ClickCalibration>(File.ReadAllText(path)); }
        catch { return null; }
    }

    public void Save()
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public class ClickExecutor
{
    public string ExecutorStatus { get; private set; } = "idle";
    public ClickCalibration? Calibration { get; private set; }
    public int PostClickDelayMs { get; set; } = 2000;  // max wait for state advance per attempt
    public int DismissDelayMs { get; set; } = 300;
    public int MaxRetries { get; set; } = 3;

    /// <summary>Optional learned positions; consulted before calibration fallback.</summary>
    public LearnedPositions? LearnedPositions { get; set; }

    /// <summary>True if the most recent click used a learned position (vs calibrated).</summary>
    public bool LastClickUsedLearned { get; private set; }

    /// <summary>Program.cs sets this to return the latest parsed state (for advance detection).</summary>
    public Func<Models.GameState?>? LatestStateProvider { get; set; }

    // ── Win32 P/Invoke ───────────────────────────────────────────────────────
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP   = 0x0004;
    const int  VK_ESCAPE            = 0x1B;
    const int  VK_SPACE             = 0x20;
    const int  VK_S                 = 0x53;

    [StructLayout(LayoutKind.Sequential)] struct RECT       { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT      { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT      { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

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
                { hwnd = h; return false; }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
    }

    static void MouseDown()
    {
        var inp = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } };
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }

    static void MouseUp()
    {
        var inp = new INPUT { type = 0, mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } };
        SendInput(1, [inp], Marshal.SizeOf<INPUT>());
    }

    static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(80);
        MouseDown();
        Thread.Sleep(60);
        MouseUp();
    }

    static void ClickAt(System.Drawing.Point pt) => ClickAt(pt.X, pt.Y);

    // ── Key detection helpers ────────────────────────────────────────────────

    /// <summary>
    /// Waits for SPACE key press and returns cursor position at the moment of press.
    /// Pressing ESC throws OperationCanceledException.
    /// Debounces: waits for key up + 200ms after capture.
    /// </summary>
    static async Task<System.Drawing.Point> WaitForSpaceAsync(CancellationToken ct)
    {
        // Wait for SPACE to be released if currently held
        while ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                throw new OperationCanceledException("Calibration cancelled by ESC");
            if ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0)
            {
                GetCursorPos(out var pt);
                while ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0)
                    await Task.Delay(20, ct);
                await Task.Delay(200, ct); // debounce
                return new System.Drawing.Point(pt.X, pt.Y);
            }
            await Task.Delay(20, ct);
        }
    }

    /// <summary>
    /// Waits for SPACE (capture point), S (skip), or ESC (cancel).
    /// Returns (point, skipped=false) on SPACE, (null, skipped=true) on S.
    /// </summary>
    static async Task<(System.Drawing.Point? point, bool skipped)> WaitForSpaceOrSkipAsync(CancellationToken ct)
    {
        // Wait for both SPACE and S to be released
        while ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0 || (GetAsyncKeyState(VK_S) & 0x8000) != 0)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                throw new OperationCanceledException("Calibration cancelled by ESC");
            if ((GetAsyncKeyState(VK_S) & 0x8000) != 0)
            {
                while ((GetAsyncKeyState(VK_S) & 0x8000) != 0)
                    await Task.Delay(20, ct);
                await Task.Delay(200, ct);
                return (null, true);
            }
            if ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0)
            {
                GetCursorPos(out var pt);
                while ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0)
                    await Task.Delay(20, ct);
                await Task.Delay(200, ct);
                return (new System.Drawing.Point(pt.X, pt.Y), false);
            }
            await Task.Delay(20, ct);
        }
    }

    static char ReadYN()
    {
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Y) return 'Y';
            if (k.Key == ConsoleKey.N) return 'N';
        }
    }

    // ── Calibration ──────────────────────────────────────────────────────────
    public async Task CalibrateAsync(CancellationToken ct)
    {
        var saved = ClickCalibration.Load();
        if (saved != null)
        {
            Calibration = saved;
            Console.WriteLine("[clicker] Loaded saved calibration.");
            return;
        }

        Console.WriteLine("[clicker] No saved calibration found. Starting console calibration...");
        Calibration = await RunConsoleCalibrationAsync(ct);
        Console.WriteLine("[clicker] Calibration complete.");
    }

    static async Task<ClickCalibration> RunConsoleCalibrationAsync(CancellationToken ct)
    {
        var cal = new ClickCalibration();

        // ── 1 of 8: Intro screen — Play button ────────────────────────────────
        Console.WriteLine("");
        Console.WriteLine("=== Calibration 1 of 8: Intro screen — Play button ===");
        Console.WriteLine("");
        Console.WriteLine("  Open the dice mat dialog in-game and wait for the");
        Console.WriteLine("  \"Play game?\" screen to appear (with Total Winnings /");
        Console.WriteLine("  Total Games Played text).");
        Console.WriteLine("");
        Console.WriteLine("  Hover your mouse cursor over the [Play! Bet 1000 Councils]");
        Console.WriteLine("  button. Then press SPACE.");
        Console.WriteLine("");
        Console.WriteLine("  (ESC cancels)");
        {
            var p = await WaitForSpaceAsync(ct);
            cal.IntroPlay = p;
            Console.WriteLine($"  captured ({p.X}, {p.Y})");
        }

        Console.WriteLine("");
        Console.WriteLine("  >> Click [Play! Bet 1000 Councils] in-game now to start a round.");
        Console.WriteLine("  >> Wait for the gameplay screen to appear");
        Console.WriteLine("     (\"Your score from red dice is...\"), then continue.");

        // ── 2 of 8: Gameplay — Raise Bet ──────────────────────────────────────
        Console.WriteLine("");
        Console.WriteLine("=== Calibration 2 of 8: Gameplay screen — Raise Bet button ===");
        Console.WriteLine("");
        Console.WriteLine("  The gameplay screen should be visible.");
        Console.WriteLine("");
        Console.WriteLine("  Hover your mouse cursor over the [Raise Bet] button.");
        Console.WriteLine("  Then press SPACE.");
        Console.WriteLine("");
        Console.WriteLine("  (ESC cancels)");
        {
            var p = await WaitForSpaceAsync(ct);
            cal.PlayingRaise = p;
            Console.WriteLine($"  captured ({p.X}, {p.Y})");
        }

        // ── 3 of 8: Gameplay — Stand Pat (skippable) ──────────────────────────
        Console.WriteLine("");
        Console.WriteLine("=== Calibration 3 of 8: Gameplay screen — Stand Pat button ===");
        Console.WriteLine("");
        Console.WriteLine("  Stand Pat only appears when you are currently winning.");
        Console.WriteLine("  If it is not visible right now, press S to skip.");
        Console.WriteLine("");
        Console.WriteLine("  Hover your mouse cursor over the [Stand Pat] button and press");
        Console.WriteLine("  SPACE — or press S to skip this capture.");
        Console.WriteLine("");
        Console.WriteLine("  (S = skip, ESC = cancel)");
        {
            var (p, skipped) = await WaitForSpaceOrSkipAsync(ct);
            if (skipped)
            {
                cal.StandPatSkipped = true;
                Console.WriteLine("  skipped (Stand Pat will fall back to Roll 1 die at runtime)");
            }
            else
            {
                cal.PlayingStandPat = p!.Value;
                Console.WriteLine($"  captured ({p.Value.X}, {p.Value.Y})");
            }
        }

        // ── 4 of 8: Gameplay — Roll 1 die ─────────────────────────────────────
        Console.WriteLine("");
        Console.WriteLine("=== Calibration 4 of 8: Gameplay screen — Roll 1 die button ===");
        Console.WriteLine("");
        Console.WriteLine("  Hover your mouse cursor over the [Roll 1 die] button.");
        Console.WriteLine("  Then press SPACE.");
        Console.WriteLine("");
        Console.WriteLine("  (ESC cancels)");
        {
            var p = await WaitForSpaceAsync(ct);
            cal.PlayingRoll1 = p;
            Console.WriteLine($"  captured ({p.X}, {p.Y})");
        }

        // ── 5 of 8: Gameplay — Roll 2 dice ────────────────────────────────────
        Console.WriteLine("");
        Console.WriteLine("=== Calibration 5 of 8: Gameplay screen — Roll 2 dice button ===");
        Console.WriteLine("");
        Console.WriteLine("  Hover your mouse cursor over the [Roll 2 dice] button.");
        Console.WriteLine("  Then press SPACE.");
        Console.WriteLine("");
        Console.WriteLine("  (ESC cancels)");
        {
            var p = await WaitForSpaceAsync(ct);
            cal.PlayingRoll2 = p;
            Console.WriteLine($"  captured ({p.X}, {p.Y})");
        }

        // ── 6 of 8: Dismiss area ───────────────────────────────────────────────
        Console.WriteLine("");
        Console.WriteLine("=== Calibration 6 of 8: Empty dialog area (dismiss point) ===");
        Console.WriteLine("");
        Console.WriteLine("  Hover your mouse cursor over an empty area of the dice dialog");
        Console.WriteLine("  (not over any button). This position is used to dismiss the");
        Console.WriteLine("  dice roll overlay before clicking a button.");
        Console.WriteLine("  Then press SPACE.");
        Console.WriteLine("");
        Console.WriteLine("  (ESC cancels)");
        {
            var p = await WaitForSpaceAsync(ct);
            cal.Dismiss = p;
            Console.WriteLine($"  captured ({p.X}, {p.Y})");
        }

        // ── 7+8 of 8: Result screens — Win and Lose (loop until both captured) ─
        Console.WriteLine("");
        Console.WriteLine("  >> Play the round out in-game until the result screen appears.");
        Console.WriteLine("  >> It will show either YOU WIN! or YOU LOSE.");
        Console.WriteLine("  >> You must capture the [Play again] button from BOTH screens.");
        Console.WriteLine("  >> Keep playing rounds until you have seen both outcomes.");

        int resultStep = 7;
        while (cal.ResultPlayAgainWin == default || cal.ResultPlayAgainLose == default)
        {
            string stillNeedBefore = (cal.ResultPlayAgainWin == default && cal.ResultPlayAgainLose == default)
                ? "WIN and LOSE screens"
                : cal.ResultPlayAgainWin == default ? "WIN screen" : "LOSE screen";

            Console.WriteLine("");
            Console.WriteLine($"=== Calibration {resultStep} of 8: Result screen — Play Again button ===");
            Console.WriteLine($"    (Still need: {stillNeedBefore})");
            Console.WriteLine("");
            Console.WriteLine("  On the result screen (YOU WIN! or YOU LOSE), hover your");
            Console.WriteLine("  mouse cursor over the [Play again] button. Then press SPACE.");
            Console.WriteLine("");
            Console.WriteLine("  (ESC cancels)");

            var p = await WaitForSpaceAsync(ct);
            Console.WriteLine($"  captured ({p.X}, {p.Y})");

            Console.WriteLine("");
            Console.WriteLine("  Was that the WIN screen (YOU WIN!)? Press Y for Yes, N for No (Lose screen).");
            char answer = ReadYN();

            if (answer == 'Y')
            {
                Console.WriteLine("  Y — saving as ResultPlayAgainWin.");
                if (cal.ResultPlayAgainWin != default)
                    Console.WriteLine("  (WIN already captured — discarding duplicate, please capture the other screen)");
                else
                {
                    cal.ResultPlayAgainWin = p;
                    resultStep++;
                }
            }
            else
            {
                Console.WriteLine("  N — saving as ResultPlayAgainLose.");
                if (cal.ResultPlayAgainLose != default)
                    Console.WriteLine("  (LOSE already captured — discarding duplicate, please capture the other screen)");
                else
                {
                    cal.ResultPlayAgainLose = p;
                    resultStep++;
                }
            }

            if (cal.ResultPlayAgainWin == default || cal.ResultPlayAgainLose == default)
            {
                string stillNeedAfter = (cal.ResultPlayAgainWin == default && cal.ResultPlayAgainLose == default)
                    ? "both screens"
                    : cal.ResultPlayAgainWin == default ? "the WIN screen" : "the LOSE screen";
                Console.WriteLine($"  >> Still need {stillNeedAfter}. Play another round and press SPACE on the next result screen.");
            }
        }

        cal.Save();

        Console.WriteLine("");
        Console.WriteLine("=== Calibration Complete — Saved ===");
        Console.WriteLine("");
        Console.WriteLine($"  IntroPlay           ({cal.IntroPlay.X}, {cal.IntroPlay.Y})");
        Console.WriteLine($"  PlayingRaise        ({cal.PlayingRaise.X}, {cal.PlayingRaise.Y})");
        if (cal.StandPatSkipped)
            Console.WriteLine($"  PlayingStandPat     (skipped — will use Roll1 at runtime)");
        else
            Console.WriteLine($"  PlayingStandPat     ({cal.PlayingStandPat.X}, {cal.PlayingStandPat.Y})");
        Console.WriteLine($"  PlayingRoll1        ({cal.PlayingRoll1.X}, {cal.PlayingRoll1.Y})");
        Console.WriteLine($"  PlayingRoll2        ({cal.PlayingRoll2.X}, {cal.PlayingRoll2.Y})");
        Console.WriteLine($"  ResultPlayAgainWin  ({cal.ResultPlayAgainWin.X}, {cal.ResultPlayAgainWin.Y})");
        Console.WriteLine($"  ResultPlayAgainLose ({cal.ResultPlayAgainLose.X}, {cal.ResultPlayAgainLose.Y})");
        Console.WriteLine($"  Dismiss             ({cal.Dismiss.X}, {cal.Dismiss.Y})");
        Console.WriteLine("");

        return cal;
    }

    // ── Click execution ──────────────────────────────────────────────────────
    public async Task ClickResponseCodeAsync(int responseCode, GameState currentState, CancellationToken ct)
    {
        if (Calibration is null)
        {
            ExecutorStatus = "WARNING: not calibrated";
            Console.WriteLine($"[clicker] {ExecutorStatus}");
            return;
        }

        // ── 1. Try learned position first ────────────────────────────────────
        var layoutKey = LayoutKey.For(currentState);
        var learned = LearnedPositions?.Lookup(layoutKey, responseCode);

        // ── 2. Calibration fallback ───────────────────────────────────────────
        System.Drawing.Point? calibratedTarget = (currentState.Phase, responseCode) switch
        {
            (GamePhase.Intro,   GameState.CodePlay)         => Calibration.IntroPlay,
            (GamePhase.Playing, GameState.CodeRaise)        => Calibration.PlayingRaise,
            (GamePhase.Playing, GameState.CodeStandPat)     => Calibration.StandPatSkipped
                                                                ? Calibration.PlayingRoll1
                                                                : Calibration.PlayingStandPat,
            (GamePhase.Playing, GameState.CodeRollOne)      => Calibration.PlayingRoll1,
            (GamePhase.Playing, GameState.CodeRollTwo)      => Calibration.PlayingRoll2,
            (GamePhase.Result,  GameState.CodePlayAgainWin) => Calibration.ResultPlayAgainWin,
            (GamePhase.Result,  GameState.CodePlay)         => Calibration.ResultPlayAgainLose,
            (GamePhase.CashOut, GameState.CodePlay)         => Calibration.ResultPlayAgainLose,
            _ => (System.Drawing.Point?)null
        };

        if (learned is null && calibratedTarget is null)
        {
            ExecutorStatus = $"WARNING: no mapping for phase={currentState.Phase} code={responseCode}";
            Console.WriteLine($"[clicker] {ExecutorStatus}");
            return;
        }

        bool usingLearned = learned.HasValue;
        LastClickUsedLearned = usingLearned;

        if (usingLearned)
            Console.WriteLine($"[clicker] using learned position for {layoutKey}/{responseCode} @ ({learned!.Value.X},{learned.Value.Y})");

        if (currentState.Phase == GamePhase.Playing && responseCode == GameState.CodeStandPat && !usingLearned && Calibration.StandPatSkipped)
            Console.WriteLine("[clicker] WARNING: Stand Pat not calibrated — using Roll1 position (reduced EV)");

        var target = (usingLearned ? learned : calibratedTarget)!.Value;
        var preSig = currentState.Signature;

        Console.WriteLine($"[clicker] START phase={currentState.Phase} code={responseCode} learned={usingLearned} dismiss=({Calibration.Dismiss.X},{Calibration.Dismiss.Y}) target=({target.X},{target.Y})");
        Console.WriteLine($"[clicker] preSig: {preSig}");

        FocusGameWindow();
        ClickObserver.SetSuppressed();
        Console.WriteLine($"[clicker] clicking dismiss ({Calibration.Dismiss.X},{Calibration.Dismiss.Y})");
        ClickAt(Calibration.Dismiss);
        await Task.Delay(DismissDelayMs, ct);

        // Y-offset scan: Result screens shift buttons by variable rows.
        // When using a learned position, we Y-scan from the learned Y (not calibrated Y).
        // For non-Result phases with learned position, cap attempts at 1 (fast path).
        var yOffsets = currentState.Phase == GamePhase.Result
            ? new[] { 0, -40, +40, -80, +80 }
            : new[] { 0, 0, 0, 0, 0 };

        int maxAttempts = usingLearned
            ? (currentState.Phase == GamePhase.Result ? yOffsets.Length : 1)
            : MaxRetries;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            int dy = yOffsets[Math.Min(attempt - 1, yOffsets.Length - 1)];
            var actual = new System.Drawing.Point(target.X, target.Y + dy);
            ExecutorStatus = $"attempt {attempt}/{maxAttempts} phase={currentState.Phase} code={responseCode} dy={dy}";
            Console.WriteLine($"[clicker] attempt {attempt}/{maxAttempts}: click target ({actual.X},{actual.Y}) dy={dy}");
            ClickObserver.SetSuppressed();
            ClickAt(actual);

            // Poll for state advance (signature change) up to PostClickDelayMs
            var deadline = DateTime.UtcNow.AddMilliseconds(PostClickDelayMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
                var latest = LatestStateProvider?.Invoke();
                if (latest is not null && latest.Signature != preSig)
                {
                    Console.WriteLine($"[clicker] ADVANCED after {attempt} attempt(s) dy={dy} -> {latest.Signature}");
                    ExecutorStatus = "idle";
                    return;
                }
            }

            Console.WriteLine($"[clicker] attempt {attempt} no advance, state still {preSig}");
            // On retry, re-dismiss in case a dice overlay re-appeared
            if (attempt < maxAttempts)
            {
                ClickObserver.SetSuppressed();
                ClickAt(Calibration.Dismiss);
                await Task.Delay(DismissDelayMs, ct);
            }
        }

        Console.WriteLine($"[clicker] STUCK: {maxAttempts} attempts exhausted at {preSig}");
        ExecutorStatus = $"STUCK at {preSig}";
    }
}

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
    public int PostClickDelayMs { get; set; } = 3000;  // max wait for state advance per attempt
    public int DismissDelayMs { get; set; } = 800;
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

    /// <summary>Given pre-state and the observed post-phase, infer which code was actually pressed.</summary>
    static int? InferHitCode(GameState pre, GamePhase postPhase)
    {
        foreach (var candidateCode in pre.AvailableResponseCodes)
        {
            var expected = GameState.ExpectedNextPhase(pre, candidateCode);
            if (expected.HasValue && expected.Value == postPhase)
                return candidateCode;
        }
        return null;
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

        var target = (usingLearned ? learned : calibratedTarget)!.Value;
        var preSig = currentState.Signature;

        // ── PLAN ─────────────────────────────────────────────────────────────
        int obsCount = 0;
        if (usingLearned && LearnedPositions is not null)
            LearnedPositions.CoverageCounts().TryGetValue((layoutKey, responseCode), out obsCount);
        string source = usingLearned ? $"learned median of {obsCount}" : "calibrated fallback";
        Console.WriteLine($"[plan] target: code={responseCode} at ({target.X},{target.Y}) [{source}]");
        Console.WriteLine($"[plan] preSig: {preSig}");

        if (currentState.Phase == GamePhase.Playing && responseCode == GameState.CodeStandPat && !usingLearned && Calibration.StandPatSkipped)
            Console.WriteLine("[plan] WARN: Stand Pat not calibrated — falling back to Roll1 position");

        // ── EXECUTE ──────────────────────────────────────────────────────────
        Console.WriteLine("[execute] 1/3: focus game window");
        FocusGameWindow();
        await Task.Delay(200, ct);

        Console.WriteLine($"[execute] 2/3: dismiss overlay at ({Calibration.Dismiss.X},{Calibration.Dismiss.Y})");
        ClickObserver.SetSuppressed();
        ClickAt(Calibration.Dismiss);
        await Task.Delay(DismissDelayMs, ct);

        // Direction-aware offset scan. Row height ~40px, so ±40 hits adjacent buttons.
        // We bias the scan AWAY from adjacent buttons, toward empty body/space, based on
        // our target code's position in AvailableResponseCodes.
        int idx = Array.IndexOf(currentState.AvailableResponseCodes, responseCode);
        bool hasAbove = idx > 0;
        bool hasBelow = idx >= 0 && idx < currentState.AvailableResponseCodes.Length - 1;

        var offsets = new List<(int dx, int dy)> { (0, 0) };
        // Always try small within-row drift first (safe — stays on correct button).
        offsets.Add((0, -20)); offsets.Add((0, +20));

        // Then bias toward the safe direction (no adjacent button that way).
        if (!hasAbove && hasBelow)
        {
            // Safe to scan UP (nothing above). Dangerous to scan down (+40 = adjacent).
            offsets.AddRange(new[] { (0, -40), (0, -80), (0, -120), (0, -160) });
            // Only try below at big offsets that skip past the neighbor.
            offsets.AddRange(new[] { (0, +80), (0, +120), (0, +160) });
        }
        else if (hasAbove && !hasBelow)
        {
            // Safe to scan DOWN. Dangerous up.
            offsets.AddRange(new[] { (0, +40), (0, +80), (0, +120), (0, +160) });
            offsets.AddRange(new[] { (0, -80), (0, -120), (0, -160) });
        }
        else if (!hasAbove && !hasBelow)
        {
            // Only button — scan freely both directions.
            offsets.AddRange(new[] { (0, -40), (0, +40), (0, -80), (0, +80), (0, -120), (0, +120) });
        }
        else
        {
            // Sandwiched (has both neighbors) — avoid ±40 entirely; jump past adjacent rows.
            offsets.AddRange(new[] { (0, -80), (0, +80), (0, -120), (0, +120), (0, -160), (0, +160) });
        }
        // X drift fallbacks at the end.
        offsets.AddRange(new[] { (-30, 0), (+30, 0) });

        int maxAttempts = offsets.Count;  // exhaust the full scan before giving up

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var (dx, dy) = offsets[attempt - 1];
            var actual = new System.Drawing.Point(target.X + dx, target.Y + dy);
            string label = (dx == 0 && dy == 0) ? "center" : $"dx={dx:+#;-#;0} dy={dy:+#;-#;0}";
            ExecutorStatus = $"attempt {attempt}/{maxAttempts} code={responseCode} {label}";
            Console.WriteLine($"[execute] 3/3: click attempt {attempt}/{maxAttempts} at ({actual.X},{actual.Y}) [{label}]");

            ClickObserver.SetSuppressed();
            GetCursorPos(out var preCursor);
            ClickAt(actual);
            GetCursorPos(out var postCursor);
            if (postCursor.X != actual.X || postCursor.Y != actual.Y)
                Console.WriteLine($"[execute]   cursor landed at ({postCursor.X},{postCursor.Y}) — expected ({actual.X},{actual.Y}) — possible focus issue");

            // ── VERIFY ──────────────────────────────────────────────────────
            Console.WriteLine($"[verify] waiting up to {PostClickDelayMs}ms for state to advance...");
            var deadline = DateTime.UtcNow.AddMilliseconds(PostClickDelayMs);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100, ct);
                var latest = LatestStateProvider?.Invoke();
                if (latest is not null && latest.Signature != preSig)
                {
                    // Validate we hit the INTENDED button, not an adjacent one.
                    var expected = GameState.ExpectedNextPhase(currentState, responseCode);
                    if (expected.HasValue && latest.Phase != expected.Value)
                    {
                        Console.WriteLine($"[verify] ⚠ WRONG BUTTON: clicked code={responseCode} expecting {expected} but got {latest.Phase}");
                        // Infer which code we actually hit and record the position against IT.
                        int? actualCode = InferHitCode(currentState, latest.Phase);
                        if (actualCode.HasValue && LearnedPositions is not null)
                        {
                            LearnedPositions.Record(layoutKey, actualCode.Value, actual);
                            LearnedPositions.Save();
                            Console.WriteLine($"[verify]   silver lining: learned {layoutKey}/{actualCode.Value} @ ({actual.X},{actual.Y}) from mis-click");
                        }
                        else
                        {
                            Console.WriteLine($"[verify]   NOT recording ({actual.X},{actual.Y})");
                        }
                        Console.WriteLine($"[verify]   stopping scan; next cycle will handle the new state");
                        ExecutorStatus = $"wrong-button {currentState.Phase}->{latest.Phase}";
                        return;
                    }
                    Console.WriteLine($"[verify] ✓ advanced after attempt {attempt} [dx={dx} dy={dy}] -> {latest.Phase}");
                    Console.WriteLine($"[verify]   new sig: {latest.Signature}");
                    LearnedPositions?.Record(layoutKey, responseCode, actual);
                    LearnedPositions?.Save();
                    Console.WriteLine($"[verify]   learned {layoutKey}/{responseCode} @ ({actual.X},{actual.Y})");
                    ExecutorStatus = "idle";
                    return;
                }
            }

            Console.WriteLine($"[verify] ✗ no advance after {PostClickDelayMs}ms, still at {preSig}");
            if (attempt < maxAttempts)
            {
                var (ndx, ndy) = offsets[attempt];
                Console.WriteLine($"[retry] re-dismiss and try dx={ndx:+#;-#;0} dy={ndy:+#;-#;0}");
                ClickObserver.SetSuppressed();
                ClickAt(Calibration.Dismiss);
                await Task.Delay(DismissDelayMs, ct);
            }
        }

        Console.WriteLine($"[verify] STUCK: all {maxAttempts} attempts failed, state never advanced from {preSig}");
        ExecutorStatus = $"STUCK at {preSig}";
    }
}

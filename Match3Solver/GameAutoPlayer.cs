using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

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
        int initialDelayMs = 1200,
        int pollIntervalMs = 200,
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

    public static void FocusGameWindow()
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

    /// <summary>Click at a screen coordinate.</summary>
    public static void ClickAt(int screenX, int screenY)
    {
        SetCursorPos(screenX, screenY);
        Thread.Sleep(80);
        MouseDown();
        Thread.Sleep(60);
        MouseUp();
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

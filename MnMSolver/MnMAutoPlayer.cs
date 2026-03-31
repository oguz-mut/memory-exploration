using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MnMSolver;

static class MnMAutoPlayer
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────

    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int nIndex);

    const int VK_LBUTTON = 0x01;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

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

    // ── Calibration state ────────────────────────────────────────────────────

    static int _dialogBaseX, _dialogBaseY, _buttonSpacingY;
    static bool _calibrated;

    static readonly string CalibrationFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectGorgonTools", "mnm_dialog_calibration.json");

    // ── Autoloop state ────────────────────────────────────────────────────────

    static int _dismissX, _dismissY;
    static int _useX, _useY;
    static int _playX, _playY;
    static bool _autoloopCalibrated;

    static readonly string AutoloopFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectGorgonTools", "mnm_autoloop_calibration.json");

    // ── Properties ────────────────────────────────────────────────────────────

    public static bool IsCalibrated => _calibrated;
    public static bool IsAutoloopCalibrated => _autoloopCalibrated;

    // ── Core click methods ────────────────────────────────────────────────────

    /// <summary>Simple click at screen coordinates.</summary>
    static void ClickAt(int screenX, int screenY)
    {
        SetCursorPos(screenX, screenY);
        Thread.Sleep(50);
        MouseDown();
        Thread.Sleep(80);
        MouseUp();
    }

    /// <summary>Click with random +-3px jitter and a 300–1000ms post-delay.</summary>
    public static async Task ClickWithJitter(int screenX, int screenY, Random rng)
    {
        int jx = screenX + rng.Next(-3, 4);
        int jy = screenY + rng.Next(-3, 4);
        SetCursorPos(jx, jy);
        await Task.Delay(rng.Next(40, 90));
        MouseDown();
        await Task.Delay(rng.Next(60, 120));
        MouseUp();
        await Task.Delay(rng.Next(300, 1001));
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

    // ── Window focus ──────────────────────────────────────────────────────────

    /// <summary>Find the WindowsPlayer process window and bring it to the foreground.</summary>
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

    // ── Input capture ─────────────────────────────────────────────────────────

    /// <summary>Poll until a left mouse button click, return cursor position.</summary>
    public static Point WaitForClick()
    {
        // Wait for any currently-held button to release
        while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) Thread.Sleep(10);
        // Wait for press
        while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) Thread.Sleep(10);
        GetCursorPos(out var pt);
        // Wait for release
        while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) Thread.Sleep(10);
        return new Point(pt.X, pt.Y);
    }

    // ── Dialog button calibration ─────────────────────────────────────────────

    /// <summary>Load dialog calibration from JSON file. Returns true on success.</summary>
    public static bool LoadCalibration()
    {
        if (!File.Exists(CalibrationFile)) return false;
        try
        {
            var json = File.ReadAllText(CalibrationFile);
            var doc  = JsonDocument.Parse(json).RootElement;
            _dialogBaseX    = doc.GetProperty("baseX").GetInt32();
            _dialogBaseY    = doc.GetProperty("baseY").GetInt32();
            _buttonSpacingY = doc.GetProperty("spacingY").GetInt32();
            _calibrated = true;
            Console.WriteLine($"[+] Dialog calibration loaded: base=({_dialogBaseX},{_dialogBaseY}) spacingY={_buttonSpacingY}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to load dialog calibration: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Interactive calibration: asks the user to click the first dialog button,
    /// then the second, to compute base position and vertical spacing. Saves to JSON.
    /// </summary>
    public static void CalibrateDialog()
    {
        Console.WriteLine("[~] Dialog calibration: open an M&M NPC dialog with at least 2 buttons.");
        Console.WriteLine("    Click the FIRST button now...");
        var p1 = WaitForClick();
        Console.WriteLine($"    First button: ({p1.X}, {p1.Y})");

        Console.WriteLine("    Click the SECOND button now...");
        var p2 = WaitForClick();
        Console.WriteLine($"    Second button: ({p2.X}, {p2.Y})");

        _dialogBaseX    = p1.X;
        _dialogBaseY    = p1.Y;
        _buttonSpacingY = p2.Y - p1.Y;
        _calibrated     = true;

        Directory.CreateDirectory(Path.GetDirectoryName(CalibrationFile)!);
        var json = JsonSerializer.Serialize(new
        {
            baseX    = _dialogBaseX,
            baseY    = _dialogBaseY,
            spacingY = _buttonSpacingY
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(CalibrationFile, json);

        Console.WriteLine($"[+] Dialog calibration saved: base=({_dialogBaseX},{_dialogBaseY}) spacingY={_buttonSpacingY}");
    }

    /// <summary>
    /// Click the dialog button at <paramref name="buttonIndex"/> (0-based) with human-like jitter.
    /// </summary>
    public static async Task ClickDialogButton(int buttonIndex, Random rng)
    {
        if (!_calibrated)
            throw new InvalidOperationException("Dialog not calibrated. Call LoadCalibration() or CalibrateDialog() first.");

        int x = _dialogBaseX;
        int y = _dialogBaseY + buttonIndex * _buttonSpacingY;
        FocusGameWindow();
        await ClickWithJitter(x, y, rng);
    }

    // ── Autoloop calibration ──────────────────────────────────────────────────

    /// <summary>Load autoloop calibration from JSON file. Returns true on success.</summary>
    public static bool LoadAutoloopCalibration()
    {
        if (!File.Exists(AutoloopFile)) return false;
        try
        {
            var json = File.ReadAllText(AutoloopFile);
            var doc  = JsonDocument.Parse(json).RootElement;
            _dismissX = doc.GetProperty("dismissX").GetInt32();
            _dismissY = doc.GetProperty("dismissY").GetInt32();
            _useX     = doc.GetProperty("useX").GetInt32();
            _useY     = doc.GetProperty("useY").GetInt32();
            _playX    = doc.GetProperty("playX").GetInt32();
            _playY    = doc.GetProperty("playY").GetInt32();
            _autoloopCalibrated = true;
            Console.WriteLine($"[+] Autoloop calibration loaded: dismiss=({_dismissX},{_dismissY}) use=({_useX},{_useY}) play=({_playX},{_playY})");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to load autoloop calibration: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Interactive calibration for the 3-click game restart sequence:
    /// dismiss result dialog → use NPC item → play game button.
    /// </summary>
    public static void CalibrateAutoloop()
    {
        Console.WriteLine("[~] Autoloop calibration — 3 clicks required.");
        Console.WriteLine("    Click the DISMISS / close button (end-of-game result dialog)...");
        var dismiss = WaitForClick();
        Console.WriteLine($"    Dismiss: ({dismiss.X}, {dismiss.Y})");

        Console.WriteLine("    Click the USE button (on the NPC item to re-open the game)...");
        var use = WaitForClick();
        Console.WriteLine($"    Use: ({use.X}, {use.Y})");

        Console.WriteLine("    Click the PLAY button (start new game)...");
        var play = WaitForClick();
        Console.WriteLine($"    Play: ({play.X}, {play.Y})");

        _dismissX = dismiss.X; _dismissY = dismiss.Y;
        _useX     = use.X;     _useY     = use.Y;
        _playX    = play.X;    _playY    = play.Y;
        _autoloopCalibrated = true;

        Directory.CreateDirectory(Path.GetDirectoryName(AutoloopFile)!);
        var json = JsonSerializer.Serialize(new
        {
            dismissX = _dismissX, dismissY = _dismissY,
            useX     = _useX,     useY     = _useY,
            playX    = _playX,    playY    = _playY
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(AutoloopFile, json);

        Console.WriteLine($"[+] Autoloop calibration saved.");
    }

    /// <summary>
    /// Execute the 3-step sequence to start a new game:
    /// dismiss → wait 1.5s → use → wait 2s → play.
    /// </summary>
    public static async Task StartNewGame(Random rng)
    {
        if (!_autoloopCalibrated)
            throw new InvalidOperationException("Autoloop not calibrated. Call LoadAutoloopCalibration() or CalibrateAutoloop() first.");

        FocusGameWindow();

        // 1. Dismiss end-of-game result dialog
        await ClickWithJitter(_dismissX, _dismissY, rng);
        await Task.Delay(1500);

        // 2. Use NPC item to re-open game
        await ClickWithJitter(_useX, _useY, rng);
        await Task.Delay(2000);

        // 3. Click play to start
        await ClickWithJitter(_playX, _playY, rng);

        Console.WriteLine("[+] StartNewGame: dismiss → use → play sent.");
    }
}

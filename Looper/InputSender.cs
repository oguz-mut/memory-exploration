using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Looper;

/// <summary>
/// Sends keyboard and mouse input to the game window.
/// Handles typing chat commands, pressing Enter, MB5 (Use), and hotkey detection.
/// </summary>
static class InputSender
{
    // ── Win32 P/Invoke ──

    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint INPUT_KEYBOARD = 1;
    const uint INPUT_MOUSE = 0;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;
    const uint MOUSEEVENTF_XDOWN = 0x0080;
    const uint MOUSEEVENTF_XUP = 0x0100;
    const uint XBUTTON2 = 2; // MB5 (forward button)

    // Virtual key codes
    const ushort VK_RETURN = 0x0D;
    const int VK_F5 = 0x74;
    const int VK_F6 = 0x75;
    const int VK_ESCAPE = 0x1B;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public INPUTUNION u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    // ── Window Focus ──

    /// <summary>Bring the game window to the foreground.</summary>
    public static bool FocusGameWindow()
    {
        IntPtr hwnd = FindGameWindow();
        if (hwnd == IntPtr.Zero) return false;
        SetForegroundWindow(hwnd);
        return true;
    }

    static IntPtr FindGameWindow()
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
                    return false;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return hwnd;
    }

    // ── Keyboard Input ──

    /// <summary>Press and release Enter.</summary>
    public static void PressEnter()
    {
        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_RETURN } }
        };
        var up = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_RETURN, dwFlags = KEYEVENTF_KEYUP } }
        };
        SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
    }

    /// <summary>Type a string using Unicode character input events.</summary>
    public static void TypeText(string text)
    {
        foreach (char c in text)
        {
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } }
            };
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            };
            SendInput(2, [down, up], Marshal.SizeOf<INPUT>());
            Thread.Sleep(10); // small delay between keystrokes
        }
    }

    // ── Mouse Input ──

    /// <summary>Click Mouse Button 5 (forward/XBUTTON2).</summary>
    public static void ClickMB5()
    {
        var down = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = XBUTTON2, dwFlags = MOUSEEVENTF_XDOWN } }
        };
        var up = new INPUT
        {
            type = INPUT_MOUSE,
            u = new INPUTUNION { mi = new MOUSEINPUT { mouseData = XBUTTON2, dwFlags = MOUSEEVENTF_XUP } }
        };
        SendInput(1, [down], Marshal.SizeOf<INPUT>());
        Thread.Sleep(80);
        SendInput(1, [up], Marshal.SizeOf<INPUT>());
    }

    // ── Hotkey Detection ──

    /// <summary>Check if F5 is currently pressed.</summary>
    public static bool IsF5Pressed() => (GetAsyncKeyState(VK_F5) & 0x8000) != 0;

    /// <summary>Check if F6 is currently pressed.</summary>
    public static bool IsF6Pressed() => (GetAsyncKeyState(VK_F6) & 0x8000) != 0;

    /// <summary>Check if Escape is currently pressed.</summary>
    public static bool IsEscapePressed() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

    /// <summary>Wait for a hotkey press, polling at 50ms intervals. Returns the key that was pressed.</summary>
    public static async Task<HotKey> WaitForHotkey(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsF5Pressed()) { await WaitForRelease(VK_F5, ct); return HotKey.F5; }
            if (IsF6Pressed()) { await WaitForRelease(VK_F6, ct); return HotKey.F6; }
            if (IsEscapePressed()) { await WaitForRelease(VK_ESCAPE, ct); return HotKey.Escape; }
            try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
        }
        return HotKey.Escape;
    }

    static async Task WaitForRelease(int vk, CancellationToken ct)
    {
        while ((GetAsyncKeyState(vk) & 0x8000) != 0 && !ct.IsCancellationRequested)
            await Task.Delay(20, ct);
    }
}

enum HotKey { F5, F6, Escape }

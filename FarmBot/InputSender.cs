using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FarmBot;

static class InputSender
{
    // ── Win32 P/Invoke ──

    [DllImport("user32.dll")] static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    const uint INPUT_KEYBOARD = 1;
    const uint INPUT_MOUSE = 0;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;

    const ushort VK_RETURN = 0x0D;
    const ushort VK_SHIFT = 0x10;
    const ushort VK_OEM_COMMA = 0xBC; // , and <
    const ushort VK_OEM_7 = 0xDE;    // ' and "
    const int VK_F5 = 0x74;
    const int VK_ESCAPE = 0x1B;
    const int VK_C = 0x43;
    const int VK_X = 0x58;

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public INPUTUNION u; }

    [StructLayout(LayoutKind.Explicit)]
    struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk; public ushort wScan; public uint dwFlags;
        public uint time; public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy; public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // ── Window ──

    static IntPtr _cachedHwnd;

    public static bool FocusGameWindow()
    {
        var hwnd = FindGameWindow();
        if (hwnd == IntPtr.Zero) return false;
        SetForegroundWindow(hwnd);
        return true;
    }

    static IntPtr FindGameWindow()
    {
        // Re-validate cache
        if (_cachedHwnd != IntPtr.Zero && IsWindowVisible(_cachedHwnd))
            return _cachedHwnd;

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
        _cachedHwnd = hwnd;
        return hwnd;
    }

    public static RECT GetGameWindowRect()
    {
        var hwnd = FindGameWindow();
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
            return rect;
        return default;
    }

    // ── Keyboard ──

    static void KeyDown(ushort vk)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    static void KeyUp(ushort vk)
    {
        var input = new INPUT { type = INPUT_KEYBOARD, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } } };
        SendInput(1, [input], Marshal.SizeOf<INPUT>());
    }

    public static void PressKey(ushort vk)
    {
        KeyDown(vk);
        Thread.Sleep(10);
        KeyUp(vk);
    }

    /// <summary>Press Shift + key (for < and " which are Shift+, and Shift+')</summary>
    public static void PressShiftKey(ushort vk)
    {
        KeyDown(VK_SHIFT);
        Thread.Sleep(10);
        KeyDown(vk);
        Thread.Sleep(10);
        KeyUp(vk);
        Thread.Sleep(10);
        KeyUp(VK_SHIFT);
    }

    /// <summary>Press &lt; (select next target)</summary>
    public static void PressSelectNext()
    {
        PressShiftKey(VK_OEM_COMMA); // Shift + , = <
    }

    /// <summary>Press " (interact)</summary>
    public static void PressInteract()
    {
        PressShiftKey(VK_OEM_7); // Shift + ' = "
    }

    public static void PressEnter() => PressKey(VK_RETURN);
    public static void PressC() => PressKey((ushort)VK_C);
    public static void PressX() => PressKey((ushort)VK_X);

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
            Thread.Sleep(10);
        }
    }

    // ── Mouse ──

    public static void LeftClick(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(30);
        var down = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } };
        var up = new INPUT { type = INPUT_MOUSE, u = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } };
        SendInput(1, [down], Marshal.SizeOf<INPUT>());
        Thread.Sleep(60);
        SendInput(1, [up], Marshal.SizeOf<INPUT>());
    }

    public static void DoubleClick(int x, int y)
    {
        LeftClick(x, y);
        Thread.Sleep(80);
        LeftClick(x, y);
    }

    // ── Hotkey Detection ──

    public static bool IsF5Pressed() => (GetAsyncKeyState(VK_F5) & 0x8000) != 0;
    public static bool IsEscapePressed() => (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

    public static async Task<HotKey> WaitForHotkey(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (IsF5Pressed()) { await WaitForRelease(VK_F5, ct); return HotKey.F5; }
            if (IsEscapePressed()) { await WaitForRelease(VK_ESCAPE, ct); return HotKey.Escape; }
            try { await Task.Delay(50, ct); } catch (OperationCanceledException) { break; }
        }
        return HotKey.Escape;
    }

    /// <summary>Non-blocking check — returns a hotkey if one is pressed right now, else null.</summary>
    public static HotKey? CheckHotkey()
    {
        if (IsEscapePressed()) return HotKey.Escape;
        if (IsF5Pressed()) return HotKey.F5;
        return null;
    }

    static async Task WaitForRelease(int vk, CancellationToken ct)
    {
        while ((GetAsyncKeyState(vk) & 0x8000) != 0 && !ct.IsCancellationRequested)
            await Task.Delay(20, ct);
    }
}

enum HotKey { F5, Escape }

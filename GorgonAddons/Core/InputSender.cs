namespace GorgonAddons.Core;

using System.Diagnostics;
using System.Runtime.InteropServices;

public static class InputSender
{
    // ── Win32 constants ───────────────────────────────────────────────────────

    const uint INPUT_MOUSE    = 0;
    const uint INPUT_KEYBOARD = 1;

    const uint KEYEVENTF_KEYUP   = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;

    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP   = 0x0004;

    // ── Win32 structs ─────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public INPUTUNION data;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    static extern bool SetCursorPos(int X, int Y);

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ── Window helpers ────────────────────────────────────────────────────────

    public static IntPtr FindGameWindow()
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

    public static void FocusGameWindow()
    {
        var hwnd = FindGameWindow();
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    // ── Keyboard input ────────────────────────────────────────────────────────

    public static void PressKey(string keyName)
    {
        uint vk = MapKeyName(keyName);
        if (vk == 0) return;

        var down = new INPUT
        {
            type = INPUT_KEYBOARD,
            data = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = 0 } }
        };
        var up = new INPUT
        {
            type = INPUT_KEYBOARD,
            data = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = KEYEVENTF_KEYUP } }
        };
        SendInput(1, [down], Marshal.SizeOf<INPUT>());
        Thread.Sleep(10);
        SendInput(1, [up], Marshal.SizeOf<INPUT>());
    }

    public static void PressEnter() => PressKey("Enter");

    public static async Task TypeText(string text)
    {
        foreach (char c in text)
        {
            var down = new INPUT
            {
                type = INPUT_KEYBOARD,
                data = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE } }
            };
            var up = new INPUT
            {
                type = INPUT_KEYBOARD,
                data = new INPUTUNION { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } }
            };
            SendInput(1, [down], Marshal.SizeOf<INPUT>());
            SendInput(1, [up], Marshal.SizeOf<INPUT>());
            await Task.Delay(10);
        }
    }

    public static async Task SendCommand(string command)
    {
        FocusGameWindow();
        await Task.Delay(200);
        PressEnter();
        await Task.Delay(300);
        await TypeText(command);
        await Task.Delay(100);
        PressEnter();
    }

    // ── Mouse input ───────────────────────────────────────────────────────────

    public static async Task Click(int x, int y)
    {
        SetCursorPos(x, y);
        var down = new INPUT
        {
            type = INPUT_MOUSE,
            data = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } }
        };
        SendInput(1, [down], Marshal.SizeOf<INPUT>());
        await Task.Delay(80);
        var up = new INPUT
        {
            type = INPUT_MOUSE,
            data = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } }
        };
        SendInput(1, [up], Marshal.SizeOf<INPUT>());
    }

    // ── Modifier key state ────────────────────────────────────────────────────

    public static bool IsShiftHeld() => (GetAsyncKeyState(0x10) & 0x8000) != 0;
    public static bool IsCtrlHeld()  => (GetAsyncKeyState(0x11) & 0x8000) != 0;
    public static bool IsAltHeld()   => (GetAsyncKeyState(0x12) & 0x8000) != 0;

    // ── Key name → VK mapping ─────────────────────────────────────────────────

    static uint MapKeyName(string keyName) => keyName.ToUpperInvariant() switch
    {
        "0" => 0x30, "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34,
        "5" => 0x35, "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39,
        "F1"  => 0x70, "F2"  => 0x71, "F3"  => 0x72, "F4"  => 0x73,
        "F5"  => 0x74, "F6"  => 0x75, "F7"  => 0x76, "F8"  => 0x77,
        "F9"  => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
        "ENTER"  => 0x0D,
        "ESCAPE" => 0x1B,
        "SPACE"  => 0x20,
        "TAB"    => 0x09,
        "SHIFT"  => 0x10,
        "CTRL"   => 0x11,
        "ALT"    => 0x12,
        "-"      => 0xBD,
        "="      => 0xBB,
        "["      => 0xDB,
        "]"      => 0xDD,
        var s when s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z' => (uint)s[0],
        _ => 0
    };
}

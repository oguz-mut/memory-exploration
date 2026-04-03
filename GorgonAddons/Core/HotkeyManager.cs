namespace GorgonAddons.Core;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

public sealed class HotkeyManager : IDisposable
{
    // ── Win32 constants ───────────────────────────────────────────────────────

    const uint MOD_ALT     = 0x0001;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_SHIFT   = 0x0004;

    const uint WM_HOTKEY   = 0x0312;
    const uint WM_APP_REG  = 0x8001;
    const uint WM_APP_STOP = 0x8002;

    // ── Win32 types ───────────────────────────────────────────────────────────

    delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    // ── P/Invoke ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassW(ref WNDCLASS wndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, IntPtr hWndParent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hWnd);

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly Dictionary<int, Action> _callbacks = new();
    private readonly ConcurrentQueue<(int id, uint mods, uint vk, Action cb)> _pending = new();
    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _thread;
    private int _nextId = 0;
    private readonly ManualResetEventSlim _ready = new(false);
    private WndProcDelegate? _wndProcDelegate; // prevent GC collection
    private bool _disposed;

    // ── Public API ────────────────────────────────────────────────────────────

    public void Start()
    {
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "HotkeyManager" };
        _thread.Start();
        _ready.Wait();
    }

    public void Register(string hotkeySpec, Action callback)
    {
        var (mods, vk) = ParseHotkey(hotkeySpec);
        int id = Interlocked.Increment(ref _nextId);
        _pending.Enqueue((id, mods, vk, callback));
        if (_hwnd != IntPtr.Zero)
            PostMessageW(_hwnd, WM_APP_REG, IntPtr.Zero, IntPtr.Zero);
    }

    public void UnregisterAll()
    {
        foreach (var id in _callbacks.Keys.ToList())
        {
            if (_hwnd != IntPtr.Zero)
                UnregisterHotKey(_hwnd, id);
        }
        _callbacks.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
            PostMessageW(_hwnd, WM_APP_STOP, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(2000);
        _ready.Dispose();
    }

    // ── Message loop (background thread) ─────────────────────────────────────

    private void MessageLoop()
    {
        string className = $"GorgonAddons_HK_{Environment.ProcessId}";

        _wndProcDelegate = WndProcCallback;
        var wc = new WNDCLASS
        {
            lpfnWndProc   = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            lpszClassName = className,
        };
        RegisterClassW(ref wc);

        // HWND_MESSAGE = (HWND)(-3): message-only window, never shown
        _hwnd = CreateWindowExW(0, className, null, 0, 0, 0, 0, 0,
            new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // Register any hotkeys queued before Start() returned
        ProcessPendingRegistrations();
        _ready.Set();

        int ret;
        MSG msg;
        while ((ret = GetMessage(out msg, IntPtr.Zero, 0, 0)) != 0)
        {
            if (ret == -1) break;

            if (msg.message == WM_APP_REG)
            {
                ProcessPendingRegistrations();
            }
            else if (msg.message == WM_APP_STOP)
            {
                break;
            }
            else
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }

        foreach (var id in _callbacks.Keys)
            UnregisterHotKey(_hwnd, id);

        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
    }

    private void ProcessPendingRegistrations()
    {
        while (_pending.TryDequeue(out var reg))
        {
            RegisterHotKey(_hwnd, reg.id, reg.mods, reg.vk);
            _callbacks[reg.id] = reg.cb;
        }
    }

    private IntPtr WndProcCallback(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_callbacks.TryGetValue(id, out var cb))
                Task.Run(cb);
            return IntPtr.Zero;
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    // ── Hotkey parsing ────────────────────────────────────────────────────────

    private static (uint mods, uint vk) ParseHotkey(string spec)
    {
        uint mods = 0;
        var parts = spec.Split('+').Select(p => p.Trim()).ToArray();
        string keyPart = parts[^1];

        foreach (var part in parts[..^1])
        {
            mods |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => MOD_CONTROL,
                "shift"             => MOD_SHIFT,
                "alt"               => MOD_ALT,
                _                   => 0u
            };
        }

        return (mods, MapVk(keyPart));
    }

    private static uint MapVk(string key) => key.ToUpperInvariant() switch
    {
        "F1"  => 0x70, "F2"  => 0x71, "F3"  => 0x72, "F4"  => 0x73,
        "F5"  => 0x74, "F6"  => 0x75, "F7"  => 0x76, "F8"  => 0x77,
        "F9"  => 0x78, "F10" => 0x79, "F11" => 0x7A, "F12" => 0x7B,
        "ENTER"  or "RETURN" => 0x0D,
        "ESCAPE" or "ESC"    => 0x1B,
        "SPACE"              => 0x20,
        "TAB"                => 0x09,
        "0" => 0x30, "1" => 0x31, "2" => 0x32, "3" => 0x33, "4" => 0x34,
        "5" => 0x35, "6" => 0x36, "7" => 0x37, "8" => 0x38, "9" => 0x39,
        "-"  => 0xBD,
        "="  => 0xBB,
        "["  => 0xDB,
        "]"  => 0xDD,
        var s when s.Length == 1 && s[0] >= 'A' && s[0] <= 'Z' => (uint)s[0],
        _ => 0
    };
}

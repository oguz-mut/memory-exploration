using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using DiceGameSolver.Models;

namespace DiceGameSolver;

public sealed class ClickCalibration
{
    public System.Drawing.Point Button1 { get; set; }
    public System.Drawing.Point Button2 { get; set; }
    public System.Drawing.Point Button3 { get; set; }
    public System.Drawing.Point Button4 { get; set; }
    public System.Drawing.Point Dismiss  { get; set; }

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
    const int  VK_LBUTTON           = 0x01;
    const int  VK_ESCAPE            = 0x1B;

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

    static void TryInvoke(Form form, Action action)
    {
        try { if (!form.IsDisposed) form.Invoke(action); }
        catch (ObjectDisposedException) { }
    }

    // ── Calibration ──────────────────────────────────────────────────────────
    public Task CalibrateAsync(CancellationToken ct)
    {
        var saved = ClickCalibration.Load();
        if (saved != null)
        {
            Calibration = saved;
            Console.WriteLine("[clicker] Loaded saved calibration.");
            return Task.CompletedTask;
        }

        Console.WriteLine("[clicker] Running calibration...");
        Calibration = RunCalibration();
        Console.WriteLine("[clicker] Calibration complete.");
        return Task.CompletedTask;
    }

    ClickCalibration RunCalibration()
    {
        ClickCalibration? result = null;
        bool cancelled = false;

        var staThread = new Thread(() =>
        {
            Application.EnableVisualStyles();

            var form = new Form
            {
                Text            = "Dice Game Calibration",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                TopMost         = true,
                Width           = 480,
                Height          = 220,
                StartPosition   = FormStartPosition.Manual,
                MaximizeBox     = false,
                MinimizeBox     = false,
            };
            // position bottom-right so it doesn't cover the dice dialog
            var area = Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            form.Location = new System.Drawing.Point(area.Right - form.Width - 20, area.Bottom - form.Height - 20);

            var instrLabel = new Label
            {
                Text =
                    "Dice Game Button Calibration\n" +
                    "Click each position in order:\n" +
                    "  1. Top-most button slot\n" +
                    "  2. Second button slot\n" +
                    "  3. Third button slot\n" +
                    "  4. Fourth button slot (click even if invisible)\n" +
                    "  5. Dismiss area (empty dialog space to clear overlays)\n" +
                    "ESC to cancel.",
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 130,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding   = new Padding(8, 4, 8, 0),
            };

            var statusLabel = new Label
            {
                Text      = "Click position 1: top-most button slot (1/5)",
                AutoSize  = false,
                Dock      = DockStyle.Bottom,
                Height    = 30,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding   = new Padding(8, 0, 8, 0),
            };

            form.Controls.Add(statusLabel);
            form.Controls.Add(instrLabel);

            var cal = new ClickCalibration();
            int clickIndex = 0;
            string[] prompts =
            [
                "Click position 1: top-most button slot (1/5)",
                "Click position 2: second button slot (2/5)",
                "Click position 3: third button slot (3/5)",
                "Click position 4: fourth button slot (4/5)",
                "Click position 5: dismiss area (5/5)",
            ];

            var bgThread = new Thread(() =>
            {
                while (clickIndex < 5 && !cancelled)
                {
                    if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                    {
                        cancelled = true;
                        TryInvoke(form, form.Close);
                        return;
                    }

                    // wait for any previous press to release
                    while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 && !cancelled)
                    {
                        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { cancelled = true; TryInvoke(form, form.Close); return; }
                        Thread.Sleep(10);
                    }

                    // wait for press
                    while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0 && !cancelled)
                    {
                        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { cancelled = true; TryInvoke(form, form.Close); return; }
                        Thread.Sleep(10);
                    }
                    if (cancelled) return;

                    GetCursorPos(out var pt);

                    // wait for release
                    while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) Thread.Sleep(10);

                    var point = new System.Drawing.Point(pt.X, pt.Y);
                    switch (clickIndex)
                    {
                        case 0: cal.Button1 = point; break;
                        case 1: cal.Button2 = point; break;
                        case 2: cal.Button3 = point; break;
                        case 3: cal.Button4 = point; break;
                        case 4: cal.Dismiss  = point; break;
                    }

                    clickIndex++;

                    if (clickIndex < 5)
                    {
                        string statusText = prompts[clickIndex];
                        TryInvoke(form, () => statusLabel.Text = statusText);
                    }
                    else
                    {
                        cal.Save();
                        result = cal;
                        TryInvoke(form, form.Close);
                    }
                }
            }) { IsBackground = true };

            form.Shown += (_, _) => bgThread.Start();
            Application.Run(form);

            cancelled = true; // stop bgThread if form closed externally
            bgThread.Join(2000);
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (result == null)
            throw new OperationCanceledException("Calibration cancelled");
        return result;
    }

    // ── Click execution ──────────────────────────────────────────────────────
    public async Task ClickResponseCodeAsync(int responseCode, GameState currentState, CancellationToken ct)
    {
        int index = Array.IndexOf(currentState.AvailableResponseCodes, responseCode);
        if (index < 0)
        {
            ExecutorStatus = $"WARNING: code {responseCode} not in AvailableResponseCodes";
            Console.WriteLine($"[clicker] {ExecutorStatus}");
            return;
        }
        if (index > 3)
        {
            ExecutorStatus = $"WARNING: slot index {index} > 3, no button configured";
            Console.WriteLine($"[clicker] {ExecutorStatus}");
            return;
        }
        if (Calibration is null)
        {
            ExecutorStatus = "WARNING: not calibrated";
            Console.WriteLine($"[clicker] {ExecutorStatus}");
            return;
        }

        ExecutorStatus = $"clicking slot {index + 1} for code {responseCode}";
        FocusGameWindow();
        ClickAt(Calibration.Dismiss);
        await Task.Delay(300, ct);

        var btn = index switch
        {
            0 => Calibration.Button1,
            1 => Calibration.Button2,
            2 => Calibration.Button3,
            _ => Calibration.Button4,
        };
        ClickAt(btn);
        await Task.Delay(800, ct);
        ExecutorStatus = "idle";
    }
}

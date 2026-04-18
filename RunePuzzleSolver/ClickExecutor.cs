using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows.Forms;
using RunePuzzleSolver.Models;

namespace RunePuzzleSolver;

public class ClickExecutor
{
    public string ExecutorStatus { get; private set; } = "idle";

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

    [StructLayout(LayoutKind.Sequential)] struct RECT  { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
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

    CalibrationData RunCalibration()
    {
        CalibrationData? result = null;
        bool cancelled = false;

        var staThread = new Thread(() =>
        {
            Application.EnableVisualStyles();

            var form = new Form
            {
                Text            = "Rune Puzzle Calibration",
                FormBorderStyle = FormBorderStyle.FixedDialog,
                TopMost         = true,
                Width           = 450,
                Height          = 210,
                StartPosition   = FormStartPosition.CenterScreen,
                MaximizeBox     = false,
                MinimizeBox     = false,
            };

            var instrLabel = new Label
            {
                Text      = "Rune Puzzle Calibration\nClick each rune button in order:\n7  B  C  F  K  M  P  Q  S  T  W  X\nthen click Submit.\nESC to cancel.",
                AutoSize  = false,
                Dock      = DockStyle.Top,
                Height    = 100,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding   = new Padding(8, 4, 8, 0),
            };

            var statusLabel = new Label
            {
                Text      = "Waiting for click 1/13: rune 7",
                AutoSize  = false,
                Dock      = DockStyle.Bottom,
                Height    = 30,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft,
                Padding   = new Padding(8, 0, 8, 0),
            };

            form.Controls.Add(statusLabel);
            form.Controls.Add(instrLabel);

            var calibration = new CalibrationData { RunePositions = new (int X, int Y)[12] };
            int clickIndex = 0;
            string[] names = [.. PuzzleStateReader.Symbols, "Submit"]; // 13 entries

            var bgThread = new Thread(() =>
            {
                while (clickIndex < 13 && !cancelled)
                {
                    // Check ESC
                    if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                    {
                        cancelled = true;
                        TryInvoke(form, form.Close);
                        return;
                    }

                    // Wait for any previous press to release
                    while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0 && !cancelled)
                    {
                        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { cancelled = true; TryInvoke(form, form.Close); return; }
                        Thread.Sleep(10);
                    }

                    // Wait for press
                    while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0 && !cancelled)
                    {
                        if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) { cancelled = true; TryInvoke(form, form.Close); return; }
                        Thread.Sleep(10);
                    }
                    if (cancelled) return;

                    GetCursorPos(out var pt);

                    // Wait for release
                    while ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0) Thread.Sleep(10);

                    if (clickIndex < 12)
                        calibration.RunePositions[clickIndex] = (pt.X, pt.Y);
                    else
                        calibration.SubmitPosition = (pt.X, pt.Y);

                    clickIndex++;

                    if (clickIndex < 13)
                    {
                        int next = clickIndex;
                        string statusText = next < 12
                            ? $"Waiting for click {next + 1}/13: rune {names[next]}"
                            : "Waiting for click 13/13: Submit button";
                        TryInvoke(form, () => statusLabel.Text = statusText);
                    }
                    else
                    {
                        calibration.Save();
                        result = calibration;
                        TryInvoke(form, form.Close);
                    }
                }
            }) { IsBackground = true };

            form.Shown += (_, _) => bgThread.Start();
            Application.Run(form);

            cancelled = true; // stop bgThread if form was closed externally
            bgThread.Join(2000);
        });

        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        staThread.Join();

        if (result == null)
            throw new OperationCanceledException("Calibration cancelled");
        return result;
    }

    static void TryInvoke(Form form, Action action)
    {
        try { if (!form.IsDisposed) form.Invoke(action); }
        catch (ObjectDisposedException) { }
    }

    public async Task RunAsync(Channel<GuessAction> input, PuzzleStateReader reader, CancellationToken ct)
    {
        var calibration = CalibrationData.Load();
        if (calibration.RunePositions.Length != 12 ||
            (calibration.SubmitPosition.X == 0 && calibration.SubmitPosition.Y == 0))
        {
            Console.WriteLine("[clicker] Running calibration...");
            calibration = RunCalibration();
            Console.WriteLine("[clicker] Calibration complete.");
        }

        await foreach (var action in input.Reader.ReadAllAsync(ct))
        {
            ExecutorStatus = $"clicking: {string.Join("", action.SymbolIndices.Select(i => PuzzleStateReader.Symbols[i]))}";
            FocusGameWindow();

            foreach (var symbolIndex in action.SymbolIndices)
            {
                var pos = calibration.RunePositions[symbolIndex];
                ClickAt(pos.X, pos.Y);
                await Task.Delay(150, ct);
            }

            ClickAt(calibration.SubmitPosition.X, calibration.SubmitPosition.Y);
            Console.WriteLine($"[clicker] submitted: {string.Join("", action.SymbolIndices.Select(i => PuzzleStateReader.Symbols[i]))}");

            ExecutorStatus = "waiting for confirmation";
            int preCount = reader.GuessRowCount;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (reader.GuessRowCount == preCount && DateTime.UtcNow < deadline)
                await Task.Delay(200, ct);

            if (reader.GuessRowCount == preCount)
                Console.WriteLine("[clicker] WARNING: guess not confirmed by memory after 5s");

            await Task.Delay(300, ct);
            ExecutorStatus = "idle";
        }
    }
}

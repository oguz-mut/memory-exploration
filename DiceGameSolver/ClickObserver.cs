using System.Runtime.InteropServices;
using System.Threading.Channels;
using DiceGameSolver.Models;

namespace DiceGameSolver;

public sealed class ClickObserver
{
    public bool Enabled { get; set; }
    public Channel<ClickEvent> ObservationChannel { get; } = Channel.CreateUnbounded<ClickEvent>();

    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    const int VK_LBUTTON = 0x01;

    // ── Suppression (set by ClickExecutor before programmatic clicks) ────────
    private static long _suppressedUntilTicks = 0;

    /// <summary>Mark programmatic-click suppression for 200 ms from now.</summary>
    public static void SetSuppressed()
        => Interlocked.Exchange(ref _suppressedUntilTicks, DateTime.UtcNow.AddMilliseconds(200).Ticks);

    /// <summary>True while the suppression window is active.</summary>
    public static bool IsSuppressed()
        => DateTime.UtcNow.Ticks < Interlocked.Read(ref _suppressedUntilTicks);

    // ── Polling loop ─────────────────────────────────────────────────────────
    public async Task RunAsync(CancellationToken ct)
    {
        bool wasDown = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool isDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

                // Record rising edge only when enabled and not suppressed
                if (isDown && !wasDown && Enabled && !IsSuppressed())
                {
                    GetCursorPos(out var pt);
                    ObservationChannel.Writer.TryWrite(new ClickEvent
                    {
                        X = pt.X,
                        Y = pt.Y,
                        TimestampUtc = DateTime.UtcNow,
                    });
                }

                wasDown = isDown;
                await Task.Delay(10, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Console.WriteLine($"[observer] {ex.Message}"); }
        }
    }
}

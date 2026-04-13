using System.Text.RegularExpressions;

namespace Looper;

/// <summary>
/// Tails Player.log and fires signals when casino-relevant events are detected.
/// Tracks: ProcessMatch3Start (new game), ProcessStartInteraction (arrived at NPC/machine),
/// and game-over patterns (no new game after timeout = out of tickets).
/// </summary>
class LogWatcher
{
    readonly string _logPath;

    // Signals
    TaskCompletionSource<bool>? _interactionSignal;
    TaskCompletionSource<bool>? _match3Signal;
    readonly object _lock = new();

    // Counters
    int _match3GamesPlayed;
    DateTime _lastMatch3Start = DateTime.MinValue;

    // Regex patterns
    static readonly Regex Match3StartRx = new(
        @"ProcessMatch3Start\(\d+,\s*""",
        RegexOptions.Compiled);

    static readonly Regex StartInteractionRx = new(
        @"ProcessStartInteraction\(",
        RegexOptions.Compiled);

    static readonly Regex EndInteractionRx = new(
        @"ProcessEndInteraction\(",
        RegexOptions.Compiled);

    public int Match3GamesPlayed => _match3GamesPlayed;
    public DateTime LastMatch3Start => _lastMatch3Start;

    public LogWatcher(string logPath)
    {
        _logPath = logPath;
    }

    /// <summary>Start tailing the log file in the background.</summary>
    public async Task Run(CancellationToken ct)
    {
        if (!File.Exists(_logPath))
        {
            Console.WriteLine($"[log] Log file not found: {_logPath}");
            return;
        }

        using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(0, SeekOrigin.End); // Start at end — only watch new lines
        using var reader = new StreamReader(fs);

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line != null)
            {
                ProcessLine(line);
            }
            else
            {
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    void ProcessLine(string line)
    {
        if (Match3StartRx.IsMatch(line))
        {
            _match3GamesPlayed++;
            _lastMatch3Start = DateTime.Now;
            Console.WriteLine($"[log] Match-3 game #{_match3GamesPlayed} detected");
            lock (_lock)
            {
                _match3Signal?.TrySetResult(true);
                _match3Signal = null;
            }
        }

        if (StartInteractionRx.IsMatch(line))
        {
            Console.WriteLine($"[log] Interaction started");
            lock (_lock)
            {
                _interactionSignal?.TrySetResult(true);
                _interactionSignal = null;
            }
        }
    }

    // ── Signal API ──

    public void ClearInteractionSignal()
    {
        lock (_lock)
        {
            _interactionSignal?.TrySetCanceled();
            _interactionSignal = null;
        }
    }

    public Task WaitForInteraction(CancellationToken ct)
    {
        lock (_lock)
        {
            _interactionSignal = new TaskCompletionSource<bool>();
            ct.Register(() => _interactionSignal.TrySetCanceled());
            return _interactionSignal.Task;
        }
    }

    public void ClearMatch3Signal()
    {
        lock (_lock)
        {
            _match3Signal?.TrySetCanceled();
            _match3Signal = null;
        }
    }

    public Task WaitForMatch3Start(CancellationToken ct)
    {
        lock (_lock)
        {
            _match3Signal = new TaskCompletionSource<bool>();
            ct.Register(() => _match3Signal.TrySetCanceled());
            return _match3Signal.Task;
        }
    }
}

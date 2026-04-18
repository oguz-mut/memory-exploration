using System.Text.RegularExpressions;

namespace FarmBot;

/// <summary>
/// Tails Player.log for garden-relevant events:
/// - Plant state changes (ProcessUpdateDescription)
/// - Item additions (ProcessAddItem) — detects harvested produce arriving in inventory
/// - Interaction start/end
/// - "out of water" or similar error messages
/// </summary>
class FarmLogWatcher
{
    readonly string _logPath;
    readonly object _lock = new();

    // Signals
    TaskCompletionSource<bool>? _interactionSignal;

    // Track last harvested plant name so we know what to replant
    readonly Queue<string> _harvestedPlants = new();

    // Regex patterns
    // ProcessUpdateDescription(entityId, "State PlantName", "", "ActionName", UseItem, "Model(Scale=N)", 0)
    static readonly Regex PlantUpdateRx = new(
        @"ProcessUpdateDescription\(\d+,\s*""(\w+)\s+(.+?)"",\s*"""",\s*""(.+?)""",
        RegexOptions.Compiled);

    // ProcessStartInteraction(
    static readonly Regex StartInteractionRx = new(
        @"ProcessStartInteraction\(",
        RegexOptions.Compiled);

    // ProcessEndInteraction(
    static readonly Regex EndInteractionRx = new(
        @"ProcessEndInteraction\(",
        RegexOptions.Compiled);

    // ProcessScreenText — catches "out of water" or similar messages
    static readonly Regex ScreenTextRx = new(
        @"ProcessScreenText\(\w+,\s*""(.+?)""",
        RegexOptions.Compiled);

    // ProcessAddItem — item added to inventory (harvested produce)
    static readonly Regex AddItemRx = new(
        @"ProcessAddItem\((\w+)\(",
        RegexOptions.Compiled);

    public bool NeedsWater { get; private set; }

    public FarmLogWatcher(string logPath)
    {
        _logPath = logPath;
    }

    public async Task Run(CancellationToken ct)
    {
        if (!File.Exists(_logPath))
        {
            Console.WriteLine($"[log] Log file not found: {_logPath}");
            return;
        }

        using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(0, SeekOrigin.End);
        using var reader = new StreamReader(fs);

        while (!ct.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(ct);
            if (line != null)
                ProcessLine(line);
            else
            {
                try { await Task.Delay(200, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    void ProcessLine(string line)
    {
        // Plant state updates
        var plantMatch = PlantUpdateRx.Match(line);
        if (plantMatch.Success)
        {
            var state = plantMatch.Groups[1].Value;   // Thirsty, Growing, Hungry, Blooming, Ripe
            var plant = plantMatch.Groups[2].Value;    // Plant name
            var action = plantMatch.Groups[3].Value;   // Water, Fertilize, Harvest X, Pick X
            Console.WriteLine($"[log] Plant: {plant} — State: {state}, Action: {action}");
        }

        // Interaction signals
        if (StartInteractionRx.IsMatch(line))
        {
            lock (_lock)
            {
                _interactionSignal?.TrySetResult(true);
                _interactionSignal = null;
            }
        }

        // Screen text — check for water-related messages
        var textMatch = ScreenTextRx.Match(line);
        if (textMatch.Success)
        {
            var msg = textMatch.Groups[1].Value;
            if (msg.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (msg.IndexOf("out of", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 msg.IndexOf("need", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 msg.IndexOf("don't have", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 msg.IndexOf("no ", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                Console.WriteLine($"[log] Detected: out of water — \"{msg}\"");
                NeedsWater = true;
            }
        }
    }

    /// <summary>Dequeue a harvested plant name, or null if none pending.</summary>
    public string? DequeueHarvest()
    {
        lock (_lock)
        {
            return _harvestedPlants.Count > 0 ? _harvestedPlants.Dequeue() : null;
        }
    }

    public void ClearWaterFlag() => NeedsWater = false;

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
}

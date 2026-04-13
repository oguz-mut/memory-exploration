namespace Looper;

/// <summary>
/// Navigates to targets in-game by typing /target commands and pressing Use (MB5).
/// Watches Player.log for ProcessStartInteraction to confirm arrival.
/// </summary>
static class Navigator
{
    /// <summary>
    /// Navigate to a target by typing /target {name} in chat, then pressing MB5 (Use).
    /// Sequence: focus game → Enter (open chat) → type /target X → Enter (send) → delay → MB5 (Use/auto-walk).
    /// </summary>
    public static async Task GoTo(string targetName, CancellationToken ct)
    {
        Console.WriteLine($"[nav] Targeting: {targetName}");

        if (!InputSender.FocusGameWindow())
        {
            Console.WriteLine("[nav] Game window not found!");
            return;
        }
        await Task.Delay(200, ct);

        // Open chat
        InputSender.PressEnter();
        await Task.Delay(300, ct);

        // Type the /target command
        InputSender.TypeText($"/target {targetName}");
        await Task.Delay(100, ct);

        // Send the command
        InputSender.PressEnter();
        await Task.Delay(500, ct);

        // Press MB5 (Use) — this triggers auto-walk to the targeted entity
        Console.WriteLine("[nav] Pressing Use (MB5)...");
        InputSender.ClickMB5();

        Console.WriteLine($"[nav] Sent /target {targetName} + Use — auto-walking...");
    }

    /// <summary>
    /// Navigate and wait for arrival by watching for ProcessStartInteraction in the log.
    /// Returns true if interaction started within timeout, false if timed out.
    /// </summary>
    public static async Task<bool> GoToAndWait(string targetName, LogWatcher log, int timeoutMs, CancellationToken ct)
    {
        // Clear any pending interaction signals
        log.ClearInteractionSignal();

        await GoTo(targetName, ct);

        // Wait for ProcessStartInteraction (arrival confirmation)
        Console.WriteLine($"[nav] Waiting for interaction (up to {timeoutMs / 1000}s)...");
        var interactionTask = log.WaitForInteraction(ct);
        var timeoutTask = Task.Delay(timeoutMs, ct);

        var completed = await Task.WhenAny(interactionTask, timeoutTask);
        if (completed == interactionTask)
        {
            Console.WriteLine("[nav] Interaction started — arrived at target.");
            return true;
        }

        Console.WriteLine("[nav] Timeout waiting for interaction.");
        return false;
    }
}

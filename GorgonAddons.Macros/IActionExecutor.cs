namespace GorgonAddons.Macros;

public interface IActionExecutor
{
    void PressKey(string key);
    Task SendTarget(string name, CancellationToken ct = default);
    Task SendSay(string text, CancellationToken ct = default);
    Task SendCommand(string command, CancellationToken ct = default);
    Task Wait(int milliseconds, CancellationToken ct = default);
    void Click(int x, int y);
    void ShowNotification(string text);
}

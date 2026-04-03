using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using GorgonAddons.Addons;
using GorgonAddons.Core;
using MemoryLib;

// 1. Find game process
var processes = Process.GetProcessesByName("WindowsPlayer");
if (processes.Length == 0)
{
    Console.WriteLine("[GorgonAddons] WindowsPlayer not found. Is the game running?");
    return;
}
var gameProcess = processes[0];
int pid = gameProcess.Id;

// 2. Create ProcessMemory + MemoryRegionScanner
var memory = ProcessMemory.Open(pid);
var scanner = new MemoryRegionScanner(memory);

// 3. Create GameContext
var ctx = new GameContext(memory, scanner);
ctx.Initialize();
ctx.StartPolling(500);

Console.WriteLine($"[GorgonAddons] Connected to WindowsPlayer (PID: {pid})");
Console.WriteLine($"[GorgonAddons] Game state initialized — {ctx.Skills.Count} skills, {ctx.Inventory.Count} items, {ctx.Effects.Count} effects");

// 4. HotkeyManager
var hotkeys = new HotkeyManager();
hotkeys.Start();

// 5. Load keybinds
var keybinds = new Dictionary<string, string>(); // hotkey → macroFile
string keybindsPath = Path.Combine("addons", "keybinds.json");
if (File.Exists(keybindsPath))
{
    try
    {
        var json = File.ReadAllText(keybindsPath);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string key = prop.Name;
            string macroFile = prop.Value.GetString() ?? "";
            keybinds[key] = macroFile;
            hotkeys.Register(key, () => Console.WriteLine($"[GorgonAddons] Hotkey {key} fired → {macroFile}"));
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GorgonAddons] Failed to load keybinds.json: {ex.Message}");
    }
}

if (keybinds.Count > 0)
{
    var pairs = string.Join(", ", keybinds.Select(kv => $"{kv.Key} → {Path.GetFileName(kv.Value)}"));
    Console.WriteLine($"[GorgonAddons] Registered hotkeys: {pairs}");
}

// 6. AddonLoader
var loader = new AddonLoader(Path.Combine("addons", "plugins"));
loader.DiscoverAndLoad();
loader.InitializeAll(ctx);
var addonNames = loader.ListAddons().Select(a => a.addon.Name).ToList();
Console.WriteLine($"[GorgonAddons] Loaded addons: {(addonNames.Count == 0 ? "(none)" : string.Join(", ", addonNames))}");

// 7. HttpListener
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:9882/");
listener.Start();
Console.WriteLine("[GorgonAddons] HTTP API on http://localhost:9882/");
Console.WriteLine("[GorgonAddons] Press Ctrl+C to exit");

_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext? httpCtx = null;
        try { httpCtx = await listener.GetContextAsync(); }
        catch { break; }
        _ = Task.Run(() => HandleRequest(httpCtx, ctx, loader, keybinds));
    }
});

// 8. Main loop
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

try
{
    while (!cts.IsCancellationRequested)
    {
        loader.TickAll(ctx);
        await Task.Delay(500, cts.Token);
    }
}
catch (OperationCanceledException) { }

// 9. Shutdown
Console.WriteLine("[GorgonAddons] Shutting down...");
loader.ShutdownAll();
hotkeys.Dispose();
ctx.Dispose();
memory.Dispose();
listener.Stop();

// ── HTTP request handler ─────────────────────────────────────────────────────

static void HandleRequest(HttpListenerContext httpCtx, GameContext ctx, AddonLoader loader, Dictionary<string, string> keybinds)
{
    var req  = httpCtx.Request;
    var resp = httpCtx.Response;
    string path = req.Url?.AbsolutePath ?? "/";

    try
    {
        byte[] body;
        string contentType;

        if (path == "/")
        {
            contentType = "text/html; charset=utf-8";
            body = Encoding.UTF8.GetBytes(BuildStatusHtml(ctx, loader, keybinds));
        }
        else if (path == "/api/state")
        {
            contentType = "application/json";
            body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                health        = ctx.Player.Health,
                maxHealth     = ctx.Player.MaxHealth,
                healthPercent = ctx.Player.HealthPercent,
                power         = ctx.Player.Power,
                maxPower      = ctx.Player.MaxPower,
                powerPercent  = ctx.Player.PowerPercent,
                armor         = ctx.Player.Armor,
                maxArmor      = ctx.Player.MaxArmor,
                armorPercent  = ctx.Player.ArmorPercent,
                isDead        = ctx.Player.IsDead,
                inCombat      = ctx.Player.InCombat,
                skillCount    = ctx.Skills.Count,
                effectCount   = ctx.Effects.Count,
            }));
        }
        else if (path == "/api/addons")
        {
            contentType = "application/json";
            body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                loader.ListAddons().Select(a => new
                {
                    name    = a.addon.Name,
                    version = a.addon.Version,
                    enabled = a.enabled,
                })
            ));
        }
        else if (path == "/api/macros")
        {
            contentType = "application/json";
            body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                keybinds.Select(kv => new { hotkey = kv.Key, macro = kv.Value })
            ));
        }
        else
        {
            resp.StatusCode = 404;
            body = "Not Found"u8.ToArray();
            contentType = "text/plain";
        }

        resp.ContentType = contentType;
        resp.ContentLength64 = body.Length;
        resp.OutputStream.Write(body);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[GorgonAddons] HTTP error: {ex.Message}");
    }
    finally
    {
        resp.Close();
    }
}

static string BuildStatusHtml(GameContext ctx, AddonLoader loader, Dictionary<string, string> keybinds)
{
    var sb = new StringBuilder();
    sb.Append("<!DOCTYPE html><html><head><title>GorgonAddons Status</title></head><body>");
    sb.Append("<h1>GorgonAddons</h1>");
    sb.Append("<h2>Player State</h2>");
    sb.Append($"<p>HP: {ctx.Player.Health:F0}/{ctx.Player.MaxHealth:F0} ({ctx.Player.HealthPercent:F1}%) | " +
              $"Power: {ctx.Player.Power:F0}/{ctx.Player.MaxPower:F0} ({ctx.Player.PowerPercent:F1}%) | " +
              $"Armor: {ctx.Player.Armor:F0}/{ctx.Player.MaxArmor:F0} ({ctx.Player.ArmorPercent:F1}%)</p>");
    sb.Append($"<p>Dead: {ctx.Player.IsDead} | In Combat: {ctx.Player.InCombat} | " +
              $"Skills: {ctx.Skills.Count} | Effects: {ctx.Effects.Count}</p>");
    sb.Append("<h2>Loaded Addons</h2><ul>");
    foreach (var (addon, enabled) in loader.ListAddons())
        sb.Append($"<li>{addon.Name} v{addon.Version} [{(enabled ? "enabled" : "disabled")}]</li>");
    if (loader.ListAddons().Count == 0)
        sb.Append("<li>(none)</li>");
    sb.Append("</ul><h2>Registered Hotkeys</h2><ul>");
    foreach (var (key, macro) in keybinds)
        sb.Append($"<li>{key} → {macro}</li>");
    if (keybinds.Count == 0)
        sb.Append("<li>(none)</li>");
    sb.Append("</ul></body></html>");
    return sb.ToString();
}

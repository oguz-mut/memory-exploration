using DiceGameSolver;
using DiceGameSolver.Models;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// --selftest-* flags
if (args.Contains("--selftest-solver")) { DiceSolver.SelfTest(); return; }
if (args.Contains("--selftest-parser")) { GameStateParser.SelfTest(); return; }

// --recalibrate deletes saved calibration
if (args.Contains("--recalibrate"))
{
    var cal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectGorgonTools", "dicegame_calibration.json");
    if (File.Exists(cal)) File.Delete(cal);
}

var watcher = new LogWatcher();
var parser  = new GameStateParser();
var solver  = new DiceSolver { StopOnIntro = args.Contains("--stop-on-intro") };
var clicker = new ClickExecutor();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Mutable dashboard state
GameState? lastState    = null;
Decision?  lastDecision = null;
int gamesPlayed = 0, gamesWon = 0, sessionProfit = 0;
bool autoPlay   = false;
var recentLines = new ConcurrentQueue<string>();

clicker.LatestStateProvider = () => lastState;
await clicker.CalibrateAsync(cts.Token);

var tailTask = Task.Run(() => watcher.RunAsync(cts.Token));
var pipelineTask = Task.Run(async () =>
{
    await foreach (var line in watcher.RawLineChannel.Reader.ReadAllAsync(cts.Token))
    {
        recentLines.Enqueue(line);
        while (recentLines.Count > 20) recentLines.TryDequeue(out _);
        var state = parser.Parse(line);
        if (state is null) continue;
        lastState = state;
        if (state.Phase == GamePhase.Result) { gamesPlayed++; if (state.Won) gamesWon++; }
        if (state.Phase == GamePhase.CashOut)
        {
            var m = System.Text.RegularExpressions.Regex.Match(state.RawBody, @"receive (\d+) Councils");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var paid)) sessionProfit += paid;
        }
        var decision = solver.Decide(state);
        lastDecision = decision;
        Console.WriteLine($"[main] {state.Phase} -> {decision.Action} (code {decision.ResponseCode}) EV={decision.EV:+#;-#;0} p={decision.WinProbability:P1} :: {decision.Rationale}");
        if (autoPlay && decision.Action != DiceAction.NoOp && decision.ResponseCode != 0)
        {
            try { await clicker.ClickResponseCodeAsync(decision.ResponseCode, state, cts.Token); }
            catch (Exception ex) { Console.WriteLine($"[main] click failed: {ex.Message}"); }
        }
    }
});

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:9883/");
listener.Start();
Console.WriteLine("[main] DiceGameSolver dashboard at http://localhost:9883/");

var httpTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            var ctx = await listener.GetContextAsync();
            if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url?.AbsolutePath == "/toggle")
            {
                autoPlay = !autoPlay;
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                continue;
            }
            if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url?.AbsolutePath == "/delay")
            {
                using var reader = new StreamReader(ctx.Request.InputStream, ctx.Request.ContentEncoding);
                var form = await reader.ReadToEndAsync();
                // form body: "post=1200&dismiss=300"
                var pairs = form.Split('&');
                foreach (var p in pairs)
                {
                    var kv = p.Split('=');
                    if (kv.Length != 2) continue;
                    if (!int.TryParse(Uri.UnescapeDataString(kv[1]), out var val)) continue;
                    val = Math.Clamp(val, 0, 10000);
                    if (kv[0] == "post") clicker.PostClickDelayMs = val;
                    else if (kv[0] == "dismiss") clicker.DismissDelayMs = val;
                    else if (kv[0] == "retries") clicker.MaxRetries = Math.Clamp(val, 1, 10);
                }
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                continue;
            }
            var html  = BuildDashboard(lastState, lastDecision, clicker, gamesPlayed, gamesWon, sessionProfit, autoPlay, recentLines);
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length, cts.Token);
            ctx.Response.Close();
        }
        catch (Exception ex) when (!cts.Token.IsCancellationRequested)
        {
            Console.WriteLine($"[main] HTTP error: {ex.Message}");
        }
    }
});

await Task.WhenAny(tailTask, pipelineTask, httpTask);

static string BuildDashboard(
    GameState? s, Decision? d, ClickExecutor clicker,
    int gamesPlayed, int gamesWon, int sessionProfit,
    bool autoPlay, ConcurrentQueue<string> recentLines)
{
    var sb = new StringBuilder();
    double winRate = gamesPlayed > 0 ? (double)gamesWon / gamesPlayed * 100.0 : 0.0;
    string autoLabel = autoPlay ? "<span style='color:#4caf50'>ON</span>"  : "<span style='color:#f44336'>OFF</span>";
    string autoBtn   = autoPlay ? "DISABLE" : "ENABLE";
    string autoCls   = autoPlay ? "btn-on"  : "btn-off";

    sb.Append("""
        <!DOCTYPE html><html><head><meta charset="utf-8">
        <title>Dice Game Solver (:9883)</title>
        <script>
          setInterval(function(){
            var a = document.activeElement;
            if (a && (a.tagName === 'INPUT' || a.tagName === 'BUTTON' || a.tagName === 'SELECT' || a.tagName === 'TEXTAREA')) return;
            location.reload();
          }, 1000);
        </script>
        <style>
        body  { background:#1a1a2e; color:#e0e0e0; font-family:monospace; padding:20px; }
        h1    { color:#a0c4ff; margin-bottom:10px; }
        .panel { background:#16213e; border:1px solid #0f3460; padding:12px; margin-bottom:14px; border-radius:4px; }
        .row  { display:flex; gap:24px; flex-wrap:wrap; margin-bottom:14px; }
        .stat { background:#16213e; border:1px solid #0f3460; padding:10px 16px; border-radius:4px; min-width:120px; }
        .stat-label { font-size:11px; color:#888; }
        .stat-value { font-size:18px; font-weight:bold; color:#a0c4ff; }
        form   { display:inline; }
        button { font-family:monospace; font-size:14px; padding:8px 18px; border:none; border-radius:4px; cursor:pointer; }
        .btn-off { background:#f44336; color:#fff; }
        .btn-on  { background:#4caf50; color:#fff; }
        pre  { background:#0d0d1a; padding:10px; border-radius:4px; font-size:12px; overflow-x:auto; white-space:pre-wrap; word-break:break-all; max-height:300px; overflow-y:auto; }
        .warn { color:#ffb300; }
        .good { color:#4caf50; }
        .bad  { color:#f44336; }
        </style></head><body>
        <h1>Dice Game Solver <span style='color:#666'>(:9883)</span></h1>
        """);

    // Auto-play toggle + click-timing knobs
    sb.Append($"<div class='panel'><b>Auto-Play:</b> {autoLabel} &nbsp;");
    sb.Append($"<form method='POST' action='/toggle'><button class='{autoCls}'>{autoBtn}</button></form>");
    sb.Append("<form method='POST' action='/delay' style='margin-top:10px'>");
    sb.Append($"&nbsp;&nbsp;<label>post-click wait (ms): <input type='number' name='post' min='0' max='10000' step='50' value='{clicker.PostClickDelayMs}' style='width:80px'></label>");
    sb.Append($"&nbsp;&nbsp;<label>dismiss wait (ms): <input type='number' name='dismiss' min='0' max='5000' step='50' value='{clicker.DismissDelayMs}' style='width:80px'></label>");
    sb.Append($"&nbsp;&nbsp;<label>retries: <input type='number' name='retries' min='1' max='10' step='1' value='{clicker.MaxRetries}' style='width:50px'></label>");
    sb.Append("&nbsp;&nbsp;<button type='submit' style='background:#0f3460;color:#fff'>save</button>");
    sb.Append("</form></div>");

    // Stats row
    string profitStr = sessionProfit == 0 ? "0" : $"{sessionProfit:+#;-#;0}";
    sb.Append("<div class='row'>");
    sb.Append($"<div class='stat'><div class='stat-label'>GAMES</div><div class='stat-value'>{gamesPlayed}</div></div>");
    sb.Append($"<div class='stat'><div class='stat-label'>WIN RATE</div><div class='stat-value'>{winRate:F1}%</div></div>");
    sb.Append($"<div class='stat'><div class='stat-label'>SESSION PROFIT</div><div class='stat-value'>{profitStr} <span style='font-size:12px'>Councils</span></div></div>");
    sb.Append($"<div class='stat'><div class='stat-label'>EXECUTOR</div><div class='stat-value' style='font-size:13px'>{WebUtility.HtmlEncode(clicker.ExecutorStatus)}</div></div>");
    sb.Append("</div>");

    // State panel
    sb.Append("<div class='panel'><b>Game State</b><br>");
    if (s is null)
    {
        sb.Append("<span style='color:#888'>awaiting first dice-game interaction...</span>");
    }
    else
    {
        sb.Append($"Phase: <b>{s.Phase}</b><br>");
        sb.Append($"<span style='font-size:11px;color:#888'>sig: {WebUtility.HtmlEncode(s.Signature)}</span><br>");
        if (s.Phase == GamePhase.Playing)
        {
            string redStr = s.RedDice.Length > 0
                ? $"Red [{string.Join("][", s.RedDice)}] = {s.RedDice.Sum()}"
                : "Red (none)";
            sb.Append($"{redStr}<br>");
            sb.Append($"Dealer Score: {s.DealerCurScore}<br>");
            if (s.Raised)       sb.Append("<span class='warn'>Raised (hubris applied)</span><br>");
            if (s.LosingBanner) sb.Append("<span class='bad'>You are currently losing</span><br>");
        }
        else if (s.Phase == GamePhase.Result)
        {
            sb.Append(s.Won
                ? "<span class='good'>YOU WIN!</span><br>"
                : "<span class='bad'>YOU LOSE.</span><br>");
            if (s.BluesRolled.Length > 0)
                sb.Append($"Blues rolled: [{string.Join(", ", s.BluesRolled)}]<br>");
        }
        if (s.AvailableResponseCodes.Length > 0)
            sb.Append($"Response codes: [{string.Join(", ", s.AvailableResponseCodes)}]<br>");
    }
    sb.Append("</div>");

    // Decision panel
    sb.Append("<div class='panel'><b>Decision</b><br>");
    if (d is null)
    {
        sb.Append("<span style='color:#888'>no decision yet</span>");
    }
    else
    {
        string evSign = d.EV >= 0 ? "+" : "";
        sb.Append($"Action: <b>{d.Action}</b> (code {d.ResponseCode})<br>");
        sb.Append($"EV: {evSign}{d.EV:F2} Councils &nbsp; Win%: {d.WinProbability:P1}<br>");
        sb.Append($"Rationale: {WebUtility.HtmlEncode(d.Rationale)}<br>");
    }
    sb.Append("</div>");

    // Log tail
    sb.Append("<div class='panel'><b>Log Tail (last 20)</b><pre>");
    foreach (var line in recentLines)
        sb.Append(WebUtility.HtmlEncode(line) + "\n");
    sb.Append("</pre></div>");

    sb.Append("</body></html>");
    return sb.ToString();
}

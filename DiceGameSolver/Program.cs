using DiceGameSolver;
using DiceGameSolver.Models;
using System;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Channels;
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

var watcher          = new LogWatcher();
var parser           = new GameStateParser();
var solver           = new DiceSolver { StopOnIntro = args.Contains("--stop-on-intro") };
var clicker          = new ClickExecutor();
var learnedPositions = LearnedPositions.Load();
var observer         = new ClickObserver();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Mutable dashboard state
GameState? lastState    = null;
Decision?  lastDecision = null;
int gamesPlayed = 0, gamesWon = 0, sessionProfit = 0;
bool autoPlay      = false;
bool learnMode     = true;   // ON by default — record from first interaction
bool forceRedecide = false;  // set when autoPlay flips ON so the current state gets re-evaluated
var recentLines = new ConcurrentQueue<string>();

// Wire up clicker with learned positions
clicker.LatestStateProvider = () => lastState;
clicker.LearnedPositions    = learnedPositions;

observer.Enabled = learnMode;

await clicker.CalibrateAsync(cts.Token);

// Channel from parserTask → correlationTask carrying (state, arrivedAt)
var stateChannel = Channel.CreateUnbounded<(GameState State, DateTime ArrivedAt)>();

var tailTask = Task.Run(() => watcher.RunAsync(cts.Token));

// Parser task — fast, non-blocking. Updates lastState + stats as soon as lines arrive.
var parserTask = Task.Run(async () =>
{
    await foreach (var line in watcher.RawLineChannel.Reader.ReadAllAsync(cts.Token))
    {
        recentLines.Enqueue(line);
        while (recentLines.Count > 20) recentLines.TryDequeue(out _);
        var state = parser.Parse(line);
        if (state is null) continue;
        if (state.Phase == GamePhase.Result) { gamesPlayed++; if (state.Won) gamesWon++; }
        if (state.Phase == GamePhase.CashOut)
        {
            var m = System.Text.RegularExpressions.Regex.Match(state.RawBody, @"receive (\d+) Councils");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var paid)) sessionProfit += paid;
        }
        lastState = state;
        // Feed correlation task
        stateChannel.Writer.TryWrite((state, DateTime.UtcNow));
    }
});

// Decision task — watches lastState.Signature; re-analyzes from scratch on every change.
// Narrates: state → options → decision → plan → execute → verify.
var decisionTask = Task.Run(async () =>
{
    string lastSig = "";
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(80, cts.Token);
        var s = lastState;
        if (s is null) continue;
        if (s.Signature == lastSig && !forceRedecide) continue;
        forceRedecide = false;
        lastSig = s.Signature;

        // Let the UI settle (dice animation finishing etc.) before deciding.
        await Task.Delay(500, cts.Token);
        // Re-read in case state advanced during settle-wait.
        s = lastState ?? s;
        lastSig = s.Signature;

        // ── ANALYZE ──────────────────────────────────────────────────────────
        Console.WriteLine("");
        Console.WriteLine("──────────────────────────────────────────────────────────────");
        Console.WriteLine($"[analyze] phase={s.Phase} sig={s.Signature}");
        if (s.Phase == GamePhase.Playing)
        {
            int pscore = (s.RedDice.Length > 0 ? s.RedDice.Sum() : 0) - (s.Raised ? 1 : 0);
            Console.WriteLine($"[analyze]   red dice: [{string.Join(",", s.RedDice)}] = {pscore}{(s.Raised ? " (incl -1 hubris)" : "")}");
            Console.WriteLine($"[analyze]   dealer current: {s.DealerCurScore} (will add 2d6, expect +7)");
            Console.WriteLine($"[analyze]   raised: {s.Raised}  losing banner: {s.LosingBanner}");
            Console.WriteLine($"[analyze]   options: [{string.Join(",", s.AvailableResponseCodes)}]");
        }
        else if (s.Phase == GamePhase.Result)
        {
            Console.WriteLine($"[analyze]   {(s.Won ? "WON" : "LOST")} with blues [{string.Join(",", s.BluesRolled)}]");
        }

        // ── DECIDE ───────────────────────────────────────────────────────────
        var decision = solver.Decide(s);
        lastDecision = decision;
        Console.WriteLine($"[decide] {decision.Action} (code {decision.ResponseCode})  EV={decision.EV:+#;-#;0}  p(win)={decision.WinProbability:P1}");
        Console.WriteLine($"[decide]   rationale: {decision.Rationale}");

        if (!autoPlay)
        {
            Console.WriteLine("[plan] autoplay OFF — waiting for manual input");
            continue;
        }
        if (decision.Action == DiceAction.NoOp || decision.ResponseCode == 0)
        {
            Console.WriteLine("[plan] no actionable decision");
            continue;
        }

        // ── EXECUTE ──────────────────────────────────────────────────────────
        try
        {
            await clicker.ClickResponseCodeAsync(decision.ResponseCode, s, cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[execute] click failed: {ex.Message}");
        }

        // Pause a moment so the next iteration's analyze prints after all post-click logs.
        await Task.Delay(200, cts.Token);
    }
});

// Observer task — polls mouse button, feeds ObservationChannel
var observerTask = Task.Run(() => observer.RunAsync(cts.Token));

// Correlation task — matches clicks to state transitions and records learned positions
var correlationTask = Task.Run(async () =>
{
    (GameState State, DateTime ArrivedAt)? prev = null;
    var clickQ = new Queue<ClickEvent>();  // last 8 clicks

    try
    {
        while (!cts.Token.IsCancellationRequested)
        {
            // Drain observation channel into local queue
            while (observer.ObservationChannel.Reader.TryRead(out var click))
            {
                clickQ.Enqueue(click);
                while (clickQ.Count > 8) clickQ.Dequeue();
            }

            // Process all pending state transitions
            while (stateChannel.Reader.TryRead(out var entry))
            {
                var (newState, arrivedAt) = entry;

                if (learnMode && prev.HasValue)
                {
                    // Most recent click that arrived between the previous and current state
                    var match = clickQ
                        .Where(c => c.TimestampUtc > prev.Value.ArrivedAt && c.TimestampUtc <= arrivedAt)
                        .OrderByDescending(c => c.TimestampUtc)
                        .FirstOrDefault();

                    if (match is not null)
                    {
                        int? code = InferResponseCode(prev.Value.State, newState);
                        if (code.HasValue)
                        {
                            var preState = prev.Value.State;
                            if (preState.AvailableResponseCodes.Contains(code.Value))
                            {
                                var layoutKey = LayoutKey.For(preState);
                                var pt = new System.Drawing.Point(match.X, match.Y);
                                learnedPositions.Record(layoutKey, code.Value, pt);
                                int age = (int)(arrivedAt - match.TimestampUtc).TotalMilliseconds;
                                Console.WriteLine($"[learn] {layoutKey} code={code.Value} @ ({pt.X},{pt.Y}) from click {age}ms before transition");
                                learnedPositions.Save();
                            }
                            else
                            {
                                Console.WriteLine($"[learn] WARN: inferred code {code.Value} not in preState.AvailableResponseCodes [{string.Join(",", preState.AvailableResponseCodes)}], skipping");
                            }
                        }
                    }
                }

                prev = (newState, arrivedAt);
            }

            await Task.Delay(50, cts.Token);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex) { Console.WriteLine($"[learn] correlation error: {ex.Message}"); }
});

// Periodic save every 60 s
var periodicSaveTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try { await Task.Delay(60_000, cts.Token); }
        catch (OperationCanceledException) { break; }
        learnedPositions.Save();
        Console.WriteLine("[learn] periodic save");
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
                if (autoPlay) forceRedecide = true;
                ctx.Response.Redirect("/");
                ctx.Response.Close();
                continue;
            }
            if (ctx.Request.HttpMethod == "POST" && ctx.Request.Url?.AbsolutePath == "/learn")
            {
                learnMode = !learnMode;
                observer.Enabled = learnMode;
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
            var html  = BuildDashboard(lastState, lastDecision, clicker, gamesPlayed, gamesWon, sessionProfit, autoPlay, learnMode, learnedPositions, recentLines);
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

await Task.WhenAny(tailTask, parserTask, decisionTask, httpTask, observerTask, correlationTask, periodicSaveTask);
learnedPositions.Save();
Console.WriteLine("[main] saved learned positions on shutdown");

// ── Transition → response-code inference ────────────────────────────────────
static int? InferResponseCode(GameState pre, GameState next) => (pre.Phase, next.Phase) switch
{
    (GamePhase.Intro,   GamePhase.Playing)                                      => GameState.CodePlay,
    (GamePhase.Playing, GamePhase.Playing) when !pre.Raised && next.Raised      => GameState.CodeRaise,
    (GamePhase.Playing, GamePhase.Result)  when next.BluesRolled.Length == 0    => GameState.CodeStandPat,
    (GamePhase.Playing, GamePhase.Result)  when next.BluesRolled.Length == 1    => GameState.CodeRollOne,
    (GamePhase.Playing, GamePhase.Result)  when next.BluesRolled.Length == 2    => GameState.CodeRollTwo,
    (GamePhase.Result,  GamePhase.Playing) when pre.Won                         => GameState.CodePlayAgainWin,
    (GamePhase.Result,  GamePhase.CashOut) when pre.Won                         => GameState.CodeCashOut,
    (GamePhase.Result,  GamePhase.Playing) when !pre.Won                        => GameState.CodePlay,
    (GamePhase.CashOut, GamePhase.Playing)                                      => GameState.CodePlay,
    (GamePhase.CashOut, GamePhase.Intro) or (GamePhase.CashOut, GamePhase.Inactive) => GameState.CodeClose,
    _ => null,
};

// ── Dashboard ────────────────────────────────────────────────────────────────
static string BuildDashboard(
    GameState? s, Decision? d, ClickExecutor clicker,
    int gamesPlayed, int gamesWon, int sessionProfit,
    bool autoPlay, bool learnMode, LearnedPositions learnedPositions,
    ConcurrentQueue<string> recentLines)
{
    var sb = new StringBuilder();
    double winRate = gamesPlayed > 0 ? (double)gamesWon / gamesPlayed * 100.0 : 0.0;
    string autoLabel = autoPlay ? "<span style='color:#4caf50'>ON</span>"  : "<span style='color:#f44336'>OFF</span>";
    string autoBtn   = autoPlay ? "DISABLE" : "ENABLE";
    string autoCls   = autoPlay ? "btn-on"  : "btn-off";
    string learnLabel = learnMode ? "<span style='color:#4caf50'>ON</span>" : "<span style='color:#f44336'>OFF</span>";
    string learnBtn   = learnMode ? "DISABLE" : "ENABLE";
    string learnCls   = learnMode ? "btn-on"  : "btn-off";

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
        table.coverage { border-collapse:collapse; margin-top:8px; font-size:12px; }
        table.coverage th { padding:3px 8px; color:#888; border-bottom:1px solid #0f3460; text-align:center; }
        table.coverage th.layout-col { text-align:left; }
        table.coverage td { padding:3px 8px; text-align:center; }
        table.coverage td.layout-col { text-align:left; color:#a0c4ff; }
        </style></head><body>
        <h1>Dice Game Solver <span style='color:#666'>(:9883)</span></h1>
        """);

    // Auto-play toggle + learn-mode toggle + click-timing knobs
    sb.Append($"<div class='panel'><b>Auto-Play:</b> {autoLabel} &nbsp;");
    sb.Append($"<form method='POST' action='/toggle'><button class='{autoCls}'>{autoBtn}</button></form>");
    sb.Append($"&nbsp;&nbsp;&nbsp;<b>Learn-Mode:</b> {learnLabel} &nbsp;");
    sb.Append($"<form method='POST' action='/learn'><button class='{learnCls}'>{learnBtn}</button></form>");
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
        if (clicker.LastClickUsedLearned)
            sb.Append("<span class='good'>&#10003; learned position used</span><br>");
    }
    sb.Append("</div>");

    // Learn coverage matrix
    var coverage = learnedPositions.CoverageCounts();
    var layoutKeys = coverage.Keys.Select(k => k.Item1).Distinct().OrderBy(k => k).ToList();
    int[] relevantCodes = [-1, 1, 101, 111, 112, 121, 122, 123];
    int totalObs      = coverage.Values.Sum();
    int distinctCodes = coverage.Keys.Select(k => k.Item2).Distinct().Count();

    sb.Append("<div class='panel'><b>Learn Coverage</b>&nbsp;");
    sb.Append($"<span style='color:#888;font-size:12px'>{layoutKeys.Count} layout(s) &times; {distinctCodes} code(s) observed, {totalObs} total clicks recorded</span><br>");

    if (layoutKeys.Count > 0)
    {
        sb.Append("<table class='coverage'><tr><th class='layout-col'>Layout</th>");
        foreach (int code in relevantCodes)
            sb.Append($"<th>{code}</th>");
        sb.Append("</tr>");

        foreach (var lk in layoutKeys)
        {
            sb.Append($"<tr><td class='layout-col'>{WebUtility.HtmlEncode(lk)}</td>");
            foreach (int code in relevantCodes)
            {
                coverage.TryGetValue((lk, code), out int cnt);
                string style = cnt >= 2 ? "color:#4caf50" : cnt == 1 ? "color:#ffb300" : "color:#333";
                string cell  = cnt > 0 ? cnt.ToString() : "&middot;";
                sb.Append($"<td style='{style}'>{cell}</td>");
            }
            sb.Append("</tr>");
        }
        sb.Append("</table>");
    }
    else
    {
        sb.Append("<span style='color:#888;font-size:12px'>No observations yet — play a round to start learning button positions.</span>");
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

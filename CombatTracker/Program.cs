using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// ── Shared State ──────────────────────────────────────────────────────────────

var _lock = new object();
var currentFightEvents = new List<CombatEvent>();
var completedFights = new List<FightSummary>();
DateTime lastEventTime = DateTime.MinValue;
var liveEvents = new Queue<CombatEvent>();
int liveWindowMs = 60000;
string playerName = "";
var rawLines = new Queue<string>();

// ── Regex ─────────────────────────────────────────────────────────────────────

// Format: Source: Ability on Target [(CRIT)] Dmg: N health[, N armor]
var directRx = new Regex(
    @"^(.+?):\s+(.+?)\s+on\s+(.+?)(\s+\(CRIT\))?\s+Dmg:\s+(\d+)\s+health(?:,\s+(\d+)\s+armor)?",
    RegexOptions.Compiled);

// Format: Target: Suffered indirect dmg: -N health
var indirectRx = new Regex(
    @"^(.+?):\s+Suffered indirect dmg:\s+-(\d+)\s+health",
    RegexOptions.Compiled);

// ── Fight Management ──────────────────────────────────────────────────────────

// Must be called under _lock
void FinalizeFight()
{
    if (currentFightEvents.Count == 0) return;

    var start = currentFightEvents[0].Timestamp;
    var end = currentFightEvents[^1].Timestamp;
    int totalOut = string.IsNullOrEmpty(playerName)
        ? currentFightEvents.Where(e => !e.IsIndirect).Sum(e => e.Damage)
        : currentFightEvents.Where(e => !e.IsIndirect && e.Source == playerName).Sum(e => e.Damage);
    int totalIn = string.IsNullOrEmpty(playerName)
        ? currentFightEvents.Where(e => e.IsIndirect).Sum(e => e.Damage)
        : currentFightEvents.Where(e => e.Target == playerName).Sum(e => e.Damage);

    var summary = new FightSummary(
        StartTime: start,
        EndTime: end,
        TotalDamage: totalOut,
        TotalIncoming: totalIn,
        EventCount: currentFightEvents.Count,
        Label: $"Fight at {start:HH:mm:ss}"
    );

    completedFights.Add(summary);
    if (completedFights.Count > 50)
        completedFights.RemoveAt(0);

    currentFightEvents.Clear();
}

// ── Log Processing ────────────────────────────────────────────────────────────

string? FindLatestChatLog()
{
    var chatLogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
        "Elder Game", "Project Gorgon", "ChatLogs");

    if (!Directory.Exists(chatLogDir)) return null;

    return Directory.GetFiles(chatLogDir, "Chat-*.log")
        .Select(f => new FileInfo(f))
        .OrderByDescending(fi => fi.LastWriteTime)
        .FirstOrDefault()?.FullName;
}

void ProcessCombatLine(string rawLine)
{
    // Line format: timestamp\t[Combat] content
    string content;
    int tabIdx = rawLine.IndexOf('\t');
    if (tabIdx < 0)
    {
        if (!rawLine.Contains("[Combat]")) return;
        int idx = rawLine.IndexOf("[Combat]");
        content = rawLine.Substring(idx + "[Combat]".Length).TrimStart();
    }
    else
    {
        string rest = rawLine.Substring(tabIdx + 1).TrimStart();
        if (!rest.StartsWith("[Combat]")) return;
        content = rest.Substring("[Combat]".Length).TrimStart();
    }

    lock (_lock)
    {
        rawLines.Enqueue(rawLine);
        while (rawLines.Count > 200) rawLines.Dequeue();

        var now = DateTime.Now;

        // Check fight timeout
        if (currentFightEvents.Count > 0 &&
            lastEventTime != DateTime.MinValue &&
            (now - lastEventTime).TotalMilliseconds > 25000)
        {
            FinalizeFight();
        }

        CombatEvent? evt = null;

        // Try indirect damage
        var im = indirectRx.Match(content);
        if (im.Success)
        {
            string target = im.Groups[1].Value.Trim();
            int dmg = int.Parse(im.Groups[2].Value);
            evt = new CombatEvent("", "indirect", target, dmg, false, true, now);
        }
        else
        {
            // Try direct hit
            var dm = directRx.Match(content);
            if (dm.Success)
            {
                string source = dm.Groups[1].Value.Trim();
                string ability = dm.Groups[2].Value.Trim();
                string target = dm.Groups[3].Value.Trim();
                bool isCrit = dm.Groups[4].Success;
                int health = int.Parse(dm.Groups[5].Value);
                int armor = dm.Groups[6].Success ? int.Parse(dm.Groups[6].Value) : 0;
                int total = health + armor;

                evt = new CombatEvent(source, ability, target, total, isCrit, false, now);

                // Auto-detect player name from "You" source
                if (string.IsNullOrEmpty(playerName) && source == "You")
                    playerName = "You";
            }
        }

        if (evt == null) return;

        currentFightEvents.Add(evt);
        lastEventTime = now;

        // Maintain live window (5-minute max buffer)
        liveEvents.Enqueue(evt);
        while (liveEvents.Count > 0 && (now - liveEvents.Peek().Timestamp).TotalMilliseconds > 5 * 60 * 1000)
            liveEvents.Dequeue();
    }
}

// ── Aggregation (called under lock) ──────────────────────────────────────────

static List<object> AggregateByAbility(IEnumerable<CombatEvent> events, string player, double durationMs)
{
    var src = string.IsNullOrEmpty(player)
        ? events.Where(e => !e.IsIndirect)
        : events.Where(e => !e.IsIndirect && e.Source == player);

    double durationSec = Math.Max(durationMs / 1000.0, 1.0);

    return src
        .GroupBy(e => e.Ability)
        .Select(g => (object)new
        {
            ability = g.Key,
            damage = g.Sum(e => e.Damage),
            hits = g.Count(),
            crits = g.Count(e => e.IsCrit),
            dps = Math.Round(g.Sum(e => e.Damage) / durationSec, 1)
        })
        .OrderByDescending(x => ((dynamic)x).damage)
        .ToList();
}

static List<object> AggregateByTarget(IEnumerable<CombatEvent> events, string player, double durationMs)
{
    var src = string.IsNullOrEmpty(player)
        ? events.Where(e => !e.IsIndirect)
        : events.Where(e => !e.IsIndirect && e.Source == player);

    double durationSec = Math.Max(durationMs / 1000.0, 1.0);

    return src
        .GroupBy(e => e.Target)
        .Select(g => (object)new
        {
            target = g.Key,
            damage = g.Sum(e => e.Damage),
            hits = g.Count(),
            dps = Math.Round(g.Sum(e => e.Damage) / durationSec, 1)
        })
        .OrderByDescending(x => ((dynamic)x).damage)
        .ToList();
}

static List<object> AggregateBySource(IEnumerable<CombatEvent> events, string player)
{
    var incoming = string.IsNullOrEmpty(player)
        ? events.Where(e => e.IsIndirect)
        : events.Where(e => e.Target == player);

    return incoming
        .GroupBy(e => string.IsNullOrEmpty(e.Source) ? "Environmental" : e.Source)
        .Select(g => (object)new
        {
            source = g.Key,
            damage = g.Sum(e => e.Damage),
            hits = g.Count()
        })
        .OrderByDescending(x => ((dynamic)x).damage)
        .ToList();
}

// ── Query String Helper ───────────────────────────────────────────────────────

static string? GetQueryParam(HttpListenerRequest req, string key)
{
    var query = req.Url?.Query ?? "";
    if (string.IsNullOrEmpty(query)) return null;
    foreach (var part in query.TrimStart('?').Split('&'))
    {
        int eq = part.IndexOf('=');
        if (eq < 0) continue;
        if (Uri.UnescapeDataString(part.Substring(0, eq)) == key)
            return Uri.UnescapeDataString(part.Substring(eq + 1));
    }
    return null;
}

// ── Log Tail Loop ─────────────────────────────────────────────────────────────

void LogTailLoop(CancellationToken ct)
{
    string? currentLogPath = null;
    long lastPos = 0;

    Console.WriteLine("Waiting for Chat-*.log in ChatLogs directory...");

    while (!ct.IsCancellationRequested)
    {
        try
        {
            string? latestLog = FindLatestChatLog();

            if (latestLog == null)
            {
                ct.WaitHandle.WaitOne(2000);
                continue;
            }

            if (latestLog != currentLogPath)
            {
                Console.WriteLine($"Tailing: {latestLog}");
                currentLogPath = latestLog;
                lastPos = 0; // read from beginning for session backfill
            }

            // Periodic fight timeout check
            lock (_lock)
            {
                if (currentFightEvents.Count > 0 &&
                    lastEventTime != DateTime.MinValue &&
                    (DateTime.Now - lastEventTime).TotalMilliseconds > 25000)
                {
                    FinalizeFight();
                }
            }

            var fi = new FileInfo(currentLogPath);
            if (!fi.Exists) { currentLogPath = null; ct.WaitHandle.WaitOne(500); continue; }

            if (fi.Length > lastPos)
            {
                using var fs = new FileStream(currentLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastPos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    ProcessCombatLine(line);
                lastPos = fi.Length;
            }
            else if (fi.Length < lastPos)
            {
                lastPos = 0; // log rotated
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Log tail error: {ex.Message}");
        }

        ct.WaitHandle.WaitOne(500);
    }
}

// ── HTTP Server ───────────────────────────────────────────────────────────────

async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9880/");
    listener.Start();
    Console.WriteLine("HTTP server: http://localhost:9880/");

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var ctx = await listener.GetContextAsync().WaitAsync(ct);
            _ = Task.Run(() => HandleRequest(ctx));
        }
        catch (OperationCanceledException) { break; }
        catch { }
    }

    listener.Stop();
}

void HandleRequest(HttpListenerContext ctx)
{
    try
    {
        var req = ctx.Request;
        var resp = ctx.Response;
        string path = req.Url?.AbsolutePath ?? "/";
        byte[] buffer;

        if (path == "/api/data" && req.HttpMethod == "GET")
        {
            resp.ContentType = "application/json";
            string json;
            lock (_lock)
            {
                var now = DateTime.Now;

                // Current fight duration
                double currentDurationMs = currentFightEvents.Count > 0
                    ? (now - currentFightEvents[0].Timestamp).TotalMilliseconds
                    : 0;

                // Live window events
                var cutoff = now.AddMilliseconds(-liveWindowMs);
                var liveList = liveEvents.Where(e => e.Timestamp >= cutoff).ToList();

                // Last 40 incoming events from liveEvents (newest first)
                var incomingEvts = string.IsNullOrEmpty(playerName)
                    ? liveList.Where(e => e.IsIndirect)
                    : liveList.Where(e => e.Target == playerName);
                var incomingEvents = incomingEvts.TakeLast(40).Reverse()
                    .Select(e => new
                    {
                        time = e.Timestamp.ToString("HH:mm:ss"),
                        source = string.IsNullOrEmpty(e.Source) ? "Environmental" : e.Source,
                        ability = e.Ability,
                        damage = e.Damage
                    }).ToList();

                // Last 10 completed fights (newest first)
                var recentFights = completedFights.TakeLast(10).Reverse().Select(f => new
                {
                    startTime = f.StartTime.ToString("HH:mm:ss"),
                    endTime = f.EndTime.ToString("HH:mm:ss"),
                    totalDamage = f.TotalDamage,
                    totalIncoming = f.TotalIncoming,
                    eventCount = f.EventCount,
                    label = f.Label,
                    durationSec = (int)(f.EndTime - f.StartTime).TotalSeconds,
                    dps = (f.EndTime - f.StartTime).TotalSeconds > 0
                        ? Math.Round(f.TotalDamage / (f.EndTime - f.StartTime).TotalSeconds, 1)
                        : 0.0
                }).ToList();

                // Current fight total damage/incoming
                int cfTotalDmg = string.IsNullOrEmpty(playerName)
                    ? currentFightEvents.Where(e => !e.IsIndirect).Sum(e => e.Damage)
                    : currentFightEvents.Where(e => !e.IsIndirect && e.Source == playerName).Sum(e => e.Damage);
                int cfTotalInc = string.IsNullOrEmpty(playerName)
                    ? currentFightEvents.Where(e => e.IsIndirect).Sum(e => e.Damage)
                    : currentFightEvents.Where(e => e.Target == playerName).Sum(e => e.Damage);

                // Live window total damage
                int lwTotalDmg = string.IsNullOrEmpty(playerName)
                    ? liveList.Where(e => !e.IsIndirect).Sum(e => e.Damage)
                    : liveList.Where(e => !e.IsIndirect && e.Source == playerName).Sum(e => e.Damage);
                double lwDps = liveWindowMs > 0
                    ? Math.Round(lwTotalDmg / (liveWindowMs / 1000.0), 1)
                    : 0.0;

                var apiData = new
                {
                    playerName,
                    liveWindowMs,
                    currentFight = new
                    {
                        events = currentFightEvents.TakeLast(200).Select(e => new
                        {
                            time = e.Timestamp.ToString("HH:mm:ss"),
                            source = e.Source,
                            ability = e.Ability,
                            target = e.Target,
                            damage = e.Damage,
                            isCrit = e.IsCrit,
                            isIndirect = e.IsIndirect
                        }).ToList(),
                        durationMs = (long)currentDurationMs,
                        byAbility = AggregateByAbility(currentFightEvents, playerName, currentDurationMs),
                        byTarget = AggregateByTarget(currentFightEvents, playerName, currentDurationMs),
                        incomingBySource = AggregateBySource(currentFightEvents, playerName),
                        totalDamage = cfTotalDmg,
                        totalIncoming = cfTotalInc,
                        incomingEvents
                    },
                    liveWindow = new
                    {
                        durationMs = (long)liveWindowMs,
                        byAbility = AggregateByAbility(liveList, playerName, liveWindowMs),
                        byTarget = AggregateByTarget(liveList, playerName, liveWindowMs),
                        incomingBySource = AggregateBySource(liveList, playerName),
                        totalDamage = lwTotalDmg,
                        dps = lwDps
                    },
                    recentFights,
                    rawLines = rawLines.TakeLast(200).Reverse().ToList()
                };

                json = JsonSerializer.Serialize(apiData, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                });
            }

            buffer = Encoding.UTF8.GetBytes(json);
        }
        else if (path == "/api/window" && req.HttpMethod == "POST")
        {
            resp.ContentType = "application/json";
            int newWindowMs = liveWindowMs;
            string? secsStr = GetQueryParam(req, "secs");
            if (int.TryParse(secsStr, out int s) && new[] { 10, 30, 60, 120 }.Contains(s))
                lock (_lock) { newWindowMs = s * 1000; liveWindowMs = newWindowMs; }
            buffer = Encoding.UTF8.GetBytes($"{{\"liveWindowMs\":{newWindowMs}}}");
        }
        else if (path == "/api/player" && req.HttpMethod == "POST")
        {
            resp.ContentType = "application/json";
            string name = GetQueryParam(req, "name") ?? "";
            lock (_lock) { playerName = name; }
            buffer = Encoding.UTF8.GetBytes($"{{\"playerName\":\"{name}\"}}");
        }
        else
        {
            resp.ContentType = "text/html; charset=utf-8";
            buffer = Encoding.UTF8.GetBytes(HtmlContent.PAGE);
        }

        resp.ContentLength64 = buffer.Length;
        resp.OutputStream.Write(buffer, 0, buffer.Length);
        resp.Close();
    }
    catch { }
}

// ── Startup ───────────────────────────────────────────────────────────────────

Console.WriteLine("=== Project Gorgon Combat Tracker ===");
Console.WriteLine("Listening for [Combat] channel in Chat-*.log");

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

var logTask = Task.Run(() => LogTailLoop(cts.Token));
var httpTask = Task.Run(() => RunHttpServer(cts.Token));

Console.WriteLine("Dashboard: http://localhost:9880/");
Console.WriteLine("Press Ctrl+C to stop.\n");

try { await Task.WhenAll(logTask, httpTask); }
catch (OperationCanceledException) { }

Console.WriteLine("Combat Tracker stopped.");

// ── HTML Content ──────────────────────────────────────────────────────────────

static partial class HtmlContent
{
    public const string PAGE = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>Combat Tracker — Project Gorgon</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { background: #1a1a2e; color: #e0e0e0; font-family: 'Segoe UI', sans-serif; font-size: 13px; }
.header { display: flex; align-items: center; justify-content: space-between; padding: 8px 14px; background: #12122a; border-bottom: 1px solid #4a4a6a; }
.header-title { color: #ffd700; font-weight: bold; font-size: 15px; }
.status { display: flex; align-items: center; gap: 8px; font-size: 12px; }
.dot { width: 9px; height: 9px; border-radius: 50%; background: #4a4a6a; flex-shrink: 0; }
.dot.red { background: #f44336; animation: pulse 1s infinite; }
@keyframes pulse { 0%,100% { opacity:1; } 50% { opacity:0.3; } }
#combatStatus { font-size: 12px; }
#lastUpdate { color: #444; font-size: 11px; }
.player-bar { display: flex; align-items: center; gap: 8px; padding: 6px 14px; background: #0e0e22; border-bottom: 1px solid #2a2a4a; }
.player-bar label { color: #888; font-size: 12px; }
.player-input { background: #0d1117; border: 1px solid #4a4a6a; color: #e0e0e0; padding: 3px 8px; border-radius: 4px; font-size: 12px; width: 160px; }
.player-input:focus { outline: none; border-color: #ffd700; }
.small-btn { padding: 3px 10px; background: #1e1e3e; border: 1px solid #4a4a6a; border-radius: 4px; cursor: pointer; color: #ccc; font-size: 12px; }
.small-btn:hover { border-color: #ffd700; color: #ffd700; }
#playerLabel { color: #ffd700; font-size: 12px; }
.tabs { display: flex; border-bottom: 2px solid #2a2a4a; padding: 0 10px; background: #10102a; }
.tab { padding: 8px 18px; cursor: pointer; color: #777; border-bottom: 2px solid transparent; margin-bottom: -2px; font-size: 13px; }
.tab:hover { color: #bbb; }
.tab.active { color: #ffd700; border-bottom-color: #ffd700; }
.tab-pane { display: none; padding: 12px; gap: 12px; flex-wrap: wrap; }
.tab-pane.active { display: flex; }
.tab-pane.raw { display: none; padding: 12px; }
.tab-pane.raw.active { display: block; }
.panel { background: #16213e; border: 1px solid #2e2e5e; border-radius: 6px; padding: 12px; flex: 1; min-width: 300px; }
.panel h2 { color: #ffd700; font-size: 14px; margin-bottom: 10px; }
.window-row { display: flex; justify-content: space-between; align-items: center; margin-bottom: 10px; }
.window-btns { display: flex; gap: 5px; }
.wbtn { padding: 3px 9px; background: #1e1e3e; border: 1px solid #3a3a6a; border-radius: 4px; cursor: pointer; color: #999; font-size: 11px; }
.wbtn:hover { border-color: #ffd700; color: #ffd700; }
.wbtn.active { background: #ffd70022; border-color: #ffd700; color: #ffd700; }
.summary-bar { display: flex; gap: 16px; padding: 6px 8px; background: #0d1117; border-radius: 4px; margin-bottom: 10px; font-size: 12px; }
.summary-bar .label { color: #666; }
.summary-bar .val { color: #ffd700; font-weight: bold; }
.summary-bar .dps { color: #00c853; font-weight: bold; }
.summary-bar .incval { color: #f44336; font-weight: bold; }
table { width: 100%; border-collapse: collapse; }
th { text-align: left; color: #666; font-weight: 400; padding: 4px 6px; border-bottom: 1px solid #1e1e3e; font-size: 11px; white-space: nowrap; }
td { padding: 4px 6px; border-bottom: 1px solid #0e0e20; vertical-align: middle; }
.bar-cell { position: relative; min-width: 60px; }
.bar { position: absolute; left: 0; top: 0; height: 100%; opacity: 0.22; border-radius: 2px; pointer-events: none; }
.bar-green { background: #00c853; }
.bar-red { background: #f44336; }
.bar-val { position: relative; z-index: 1; }
.crit-count { color: #ff6b6b; font-size: 11px; }
.no-data { color: #444; text-align: center; padding: 16px; }
.evt-list { max-height: 380px; overflow-y: auto; font-size: 12px; }
.evt-row { padding: 3px 0; border-bottom: 1px solid #1a1a30; display: grid; grid-template-columns: 60px 1fr 1fr 55px; gap: 4px; align-items: center; }
.evt-time { color: #555; }
.evt-src { color: #f44336; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.evt-abl { color: #aaa; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.evt-dmg { color: #ffd700; text-align: right; }
.fight-list { font-size: 12px; }
.fight-row { display: flex; gap: 10px; padding: 5px 4px; border-bottom: 1px solid #1a1a30; align-items: center; }
.fight-lbl { flex: 1; color: #bbb; }
.fight-dmg { color: #ffd700; min-width: 55px; text-align: right; }
.fight-dps { color: #00c853; min-width: 55px; text-align: right; }
.fight-inc { color: #f44336; min-width: 55px; text-align: right; }
.fight-dur { color: #555; min-width: 40px; text-align: right; }
.raw-list { font-family: 'Consolas', monospace; font-size: 11px; line-height: 1.7; }
.raw-row { padding: 1px 4px; border-bottom: 1px solid #1a2020; color: #7a9a8a; word-break: break-all; }
.raw-row:hover { background: #1a2a2a; color: #adc; }
</style>
</head>
<body>

<div class="header">
  <span class="header-title">Combat Tracker — Project Gorgon</span>
  <div class="status">
    <div class="dot" id="combatDot"></div>
    <span id="combatStatus" style="color:#666">Idle</span>
    <span id="lastUpdate"></span>
  </div>
</div>

<div class="player-bar">
  <label>Player:</label>
  <input class="player-input" id="playerInput" placeholder="type name or auto-detect">
  <button class="small-btn" onclick="setPlayer()">Set</button>
  <span id="playerLabel"></span>
</div>

<div class="tabs">
  <div class="tab active" onclick="switchTab(0)">Your DPS</div>
  <div class="tab" onclick="switchTab(1)">Incoming</div>
  <div class="tab" onclick="switchTab(2)">Raw Lines</div>
</div>

<!-- Tab 0: Your DPS -->
<div class="tab-pane active" id="tab0">
  <div class="panel">
    <div class="window-row">
      <h2>Your Abilities</h2>
      <div class="window-btns">
        <button class="wbtn" data-secs="10"  onclick="setWindow(10)">10s</button>
        <button class="wbtn" data-secs="30"  onclick="setWindow(30)">30s</button>
        <button class="wbtn active" data-secs="60"  onclick="setWindow(60)">1 min</button>
        <button class="wbtn" data-secs="120" onclick="setWindow(120)">2 min</button>
      </div>
    </div>
    <div class="summary-bar">
      <span><span class="label">Damage: </span><span class="val" id="lwDmg">0</span></span>
      <span><span class="label">DPS: </span><span class="dps" id="lwDps">0.0</span></span>
      <span id="inCombatBadge"></span>
    </div>
    <table>
      <thead><tr><th>Ability</th><th>Damage</th><th>Hits</th><th>Crits</th><th>DPS</th></tr></thead>
      <tbody id="abilityTbody"></tbody>
    </table>
  </div>
  <div class="panel">
    <h2 style="margin-bottom:10px">By Target</h2>
    <table>
      <thead><tr><th>Target</th><th>Damage</th><th>Hits</th><th>DPS</th></tr></thead>
      <tbody id="targetTbody"></tbody>
    </table>
    <h2 style="margin-top:16px;margin-bottom:8px">Recent Fights</h2>
    <div class="fight-list" id="fightList"></div>
  </div>
</div>

<!-- Tab 1: Incoming -->
<div class="tab-pane" id="tab1">
  <div class="panel">
    <h2>Incoming Sources</h2>
    <div class="summary-bar">
      <span><span class="label">Total: </span><span class="incval" id="cfIncTotal">0</span></span>
    </div>
    <table>
      <thead><tr><th>Source</th><th>Damage</th><th>Hits</th></tr></thead>
      <tbody id="sourceTbody"></tbody>
    </table>
  </div>
  <div class="panel">
    <h2>Recent Hits</h2>
    <div class="evt-list" id="incomingList"></div>
  </div>
</div>

<!-- Tab 2: Raw Lines -->
<div class="tab-pane raw" id="tab2">
  <div class="panel" style="min-width:100%">
    <h2>[Combat] Lines — newest first (last 200)</h2>
    <div class="raw-list" id="rawList"></div>
  </div>
</div>

<script>
let activeTab = 0;
let liveWindowSecs = 60;

function switchTab(n) {
  activeTab = n;
  document.querySelectorAll('.tab').forEach((t, i) => {
    t.className = 'tab' + (i === n ? ' active' : '');
  });
  document.querySelectorAll('.tab-pane').forEach((p, i) => {
    if (i === n) p.classList.add('active');
    else p.classList.remove('active');
  });
}

function setWindow(secs) {
  liveWindowSecs = secs;
  document.querySelectorAll('.wbtn').forEach(b => {
    b.className = 'wbtn' + (parseInt(b.dataset.secs) === secs ? ' active' : '');
  });
  fetch('/api/window?secs=' + secs, { method: 'POST' }).catch(() => {});
}

function setPlayer() {
  const name = document.getElementById('playerInput').value.trim();
  fetch('/api/player?name=' + encodeURIComponent(name), { method: 'POST' })
    .then(r => r.json())
    .then(d => {
      const lbl = document.getElementById('playerLabel');
      lbl.textContent = d.playerName ? '✓ ' + d.playerName : '(auto-detect)';
    })
    .catch(() => {});
}

document.getElementById('playerInput').addEventListener('keydown', e => {
  if (e.key === 'Enter') setPlayer();
});

function fmt(n) {
  return Number(n).toLocaleString();
}

function escHtml(s) {
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function bar(val, max, cls) {
  const pct = max > 0 ? Math.min(100, (val / max) * 100) : 0;
  return '<div class="bar ' + cls + '" style="width:' + pct.toFixed(1) + '%"></div>';
}

function fetchData() {
  fetch('/api/data')
    .then(r => r.json())
    .then(data => {
      render(data);
      document.getElementById('lastUpdate').textContent = new Date().toLocaleTimeString();
      const lbl = document.getElementById('playerLabel');
      if (data.playerName && !lbl.textContent) {
        lbl.textContent = '✓ ' + data.playerName;
        document.getElementById('playerInput').placeholder = data.playerName;
      }
    })
    .catch(() => {});
}

function render(data) {
  const lw = data.liveWindow || {};
  const cf = data.currentFight || {};

  // Combat status
  const inCombat = (cf.durationMs || 0) > 0 && (cf.durationMs || 0) < 30000;
  const dot = document.getElementById('combatDot');
  const statusEl = document.getElementById('combatStatus');
  if (inCombat) {
    dot.className = 'dot red';
    statusEl.textContent = 'In Combat';
    statusEl.style.color = '#f44336';
  } else {
    dot.className = 'dot';
    statusEl.textContent = 'Idle';
    statusEl.style.color = '#666';
  }

  // ── Tab 0: Your DPS ──
  const byAbility = lw.byAbility || [];
  const byTarget  = lw.byTarget  || [];
  const maxAbilDps   = byAbility.length ? Math.max(...byAbility.map(x => x.dps))    : 1;
  const maxTargetDps = byTarget.length  ? Math.max(...byTarget.map(x => x.dps))     : 1;

  document.getElementById('lwDmg').textContent = fmt(lw.totalDamage || 0);
  document.getElementById('lwDps').textContent = ((lw.dps) || 0).toFixed(1);

  const badge = document.getElementById('inCombatBadge');
  badge.innerHTML = inCombat ? '<span style="color:#f44336;font-weight:bold">● In Combat</span>' : '';

  let aHtml = '';
  for (const r of byAbility) {
    aHtml += `<tr>
      <td>${escHtml(r.ability)}</td>
      <td>${fmt(r.damage)}</td>
      <td>${r.hits}</td>
      <td class="crit-count">${r.crits > 0 ? r.crits : ''}</td>
      <td class="bar-cell">${bar(r.dps, maxAbilDps, 'bar-green')}<span class="bar-val">${r.dps.toFixed(1)}</span></td>
    </tr>`;
  }
  document.getElementById('abilityTbody').innerHTML =
    aHtml || '<tr><td colspan="5" class="no-data">No data in window</td></tr>';

  let tHtml = '';
  for (const r of byTarget) {
    tHtml += `<tr>
      <td>${escHtml(r.target)}</td>
      <td>${fmt(r.damage)}</td>
      <td>${r.hits}</td>
      <td class="bar-cell">${bar(r.dps, maxTargetDps, 'bar-green')}<span class="bar-val">${r.dps.toFixed(1)}</span></td>
    </tr>`;
  }
  document.getElementById('targetTbody').innerHTML =
    tHtml || '<tr><td colspan="4" class="no-data">No data in window</td></tr>';

  // Recent fights
  const fights = data.recentFights || [];
  let fHtml = '';
  for (const f of fights) {
    fHtml += `<div class="fight-row">
      <span class="fight-lbl">${escHtml(f.label)}</span>
      <span class="fight-dmg">${fmt(f.totalDamage)}</span>
      <span class="fight-dps">${f.dps.toFixed(1)} dps</span>
      <span class="fight-inc">-${fmt(f.totalIncoming)}</span>
      <span class="fight-dur">${f.durationSec}s</span>
    </div>`;
  }
  document.getElementById('fightList').innerHTML =
    fHtml || '<div class="no-data">No completed fights yet</div>';

  // ── Tab 1: Incoming ──
  const bySource = cf.incomingBySource || [];
  const maxSrcDmg = bySource.length ? Math.max(...bySource.map(x => x.damage)) : 1;

  document.getElementById('cfIncTotal').textContent = fmt(cf.totalIncoming || 0);

  let sHtml = '';
  for (const r of bySource) {
    sHtml += `<tr>
      <td>${escHtml(r.source)}</td>
      <td class="bar-cell">${bar(r.damage, maxSrcDmg, 'bar-red')}<span class="bar-val">${fmt(r.damage)}</span></td>
      <td>${r.hits}</td>
    </tr>`;
  }
  document.getElementById('sourceTbody').innerHTML =
    sHtml || '<tr><td colspan="3" class="no-data">No incoming damage</td></tr>';

  const incEvts = cf.incomingEvents || [];
  let iHtml = '';
  for (const e of incEvts) {
    iHtml += `<div class="evt-row">
      <span class="evt-time">${escHtml(e.time)}</span>
      <span class="evt-src">${escHtml(e.source)}</span>
      <span class="evt-abl">${escHtml(e.ability)}</span>
      <span class="evt-dmg">${fmt(e.damage)}</span>
    </div>`;
  }
  document.getElementById('incomingList').innerHTML =
    iHtml || '<div class="no-data">No recent hits</div>';

  // ── Tab 2: Raw Lines ──
  if (activeTab === 2) {
    const raw = data.rawLines || [];
    let rHtml = '';
    for (const line of raw) {
      rHtml += `<div class="raw-row">${escHtml(line)}</div>`;
    }
    document.getElementById('rawList').innerHTML =
      rHtml || '<div class="no-data">No [Combat] lines captured yet</div>';
  }
}

setInterval(fetchData, 2000);
fetchData();
</script>
</body>
</html>
""";
}

// ── Records ───────────────────────────────────────────────────────────────────

record CombatEvent(string Source, string Ability, string Target, int Damage, bool IsCrit, bool IsIndirect, DateTime Timestamp);
record FightSummary(DateTime StartTime, DateTime EndTime, int TotalDamage, int TotalIncoming, int EventCount, string Label);

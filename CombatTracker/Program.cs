using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// --- Configuration ---
string chatLogsDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon\ChatLogs";

// --- State ---
var _lock = new object();
var currentFightEvents = new List<CombatEvent>();
var completedFights = new List<FightSummary>();
var liveEvents = new Queue<CombatEvent>();
var rawLines = new Queue<string>();
DateTime lastEventTime = DateTime.MinValue;
int liveWindowMs = 60000;
string playerName = "";

// --- Regexes ---
// Format: timestamp\t[Combat] content
// Direct:   Source: AbilityName on TargetName [(CRIT)] Dmg: N health[, N armor]
// Indirect: TargetName: Suffered indirect dmg: -N health
var rxLineChannel = new Regex(@"^[^\t]*\t\[(\w+)\] (.*)$");
var rxDirect = new Regex(@"^(.+?):\s+(.+?)\s+on\s+(.+?)(?:\s+\(CRIT\))?\s+Dmg:\s+(\d+)\s+health(?:,\s*(\d+)\s+armor)?", RegexOptions.IgnoreCase);
var rxDirectCrit = new Regex(@"^(.+?):\s+(.+?)\s+on\s+(.+?)\s+\(CRIT\)\s+Dmg:\s+(\d+)\s+health(?:,\s*(\d+)\s+armor)?", RegexOptions.IgnoreCase);
var rxIndirect = new Regex(@"^(.+?):\s+Suffered indirect dmg:\s+-(\d+)\s+health", RegexOptions.IgnoreCase);

// --- Combat line parser ---
void ProcessCombatLine(string content, DateTime timestamp)
{
    // Try indirect first
    var mi = rxIndirect.Match(content);
    if (mi.Success)
    {
        var ev = new CombatEvent
        {
            Source = "indirect",
            Ability = "indirect",
            Target = mi.Groups[1].Value.Trim(),
            Damage = int.Parse(mi.Groups[2].Value),
            IsCrit = false,
            IsIndirect = true,
            Timestamp = timestamp
        };
        AddEvent(ev);
        return;
    }

    // Try direct (check CRIT variant first for correct parsing)
    bool isCrit = content.Contains("(CRIT)", StringComparison.OrdinalIgnoreCase);
    var md = rxDirect.Match(content);
    if (md.Success)
    {
        int health = int.Parse(md.Groups[4].Value);
        int armor = md.Groups[5].Success ? int.Parse(md.Groups[5].Value) : 0;
        var ev = new CombatEvent
        {
            Source = md.Groups[1].Value.Trim(),
            Ability = md.Groups[2].Value.Trim(),
            Target = md.Groups[3].Value.Trim().Replace(" (CRIT)", "").Replace("(CRIT)", "").Trim(),
            Damage = health + armor,
            IsCrit = isCrit,
            IsIndirect = false,
            Timestamp = timestamp
        };
        // Auto-detect player name: if we see a consistent source
        if (!string.IsNullOrEmpty(ev.Source) && string.IsNullOrEmpty(playerName))
            TryAutoDetectPlayer(ev.Source);
        AddEvent(ev);
    }
}

void TryAutoDetectPlayer(string source)
{
    // Heuristic: player names don't contain spaces in most cases, or match logged-in name
    // Set tentatively — user can override via /api/player
    if (source.Length > 1 && !source.Contains(' '))
        playerName = source;
}

void AddEvent(CombatEvent ev)
{
    lock (_lock)
    {
        var now = ev.Timestamp;

        // Fight gap detection
        if (lastEventTime != DateTime.MinValue && (now - lastEventTime).TotalMilliseconds > 25000 && currentFightEvents.Count > 0)
            FinalizeFight();

        currentFightEvents.Add(ev);
        lastEventTime = now;

        // Live window: evict old events
        liveEvents.Enqueue(ev);
        var cutoff = now.AddMilliseconds(-liveWindowMs);
        while (liveEvents.Count > 0 && liveEvents.Peek().Timestamp < cutoff)
            liveEvents.Dequeue();
    }
}

void FinalizeFight()
{
    // called under lock
    if (currentFightEvents.Count == 0) return;
    var start = currentFightEvents[0].Timestamp;
    var end = currentFightEvents[^1].Timestamp;
    int totalDmg = 0, totalInc = 0;
    string pName = playerName;
    foreach (var e in currentFightEvents)
    {
        if (e.Source == pName && !e.IsIndirect) totalDmg += e.Damage;
        if (e.Target == pName) totalInc += e.Damage;
    }
    var summary = new FightSummary
    {
        StartTime = start,
        EndTime = end,
        TotalDamage = totalDmg,
        TotalIncoming = totalInc,
        EventCount = currentFightEvents.Count,
        Label = $"Fight {completedFights.Count + 1} ({start:HH:mm:ss})"
    };
    completedFights.Add(summary);
    if (completedFights.Count > 50) completedFights.RemoveAt(0);
    currentFightEvents.Clear();
}

// --- Aggregation helpers ---
static List<AbilityRow> AggregateByAbility(IEnumerable<CombatEvent> events, string pName, double durationMs)
{
    double durationSec = Math.Max(durationMs / 1000.0, 1.0);
    var groups = new Dictionary<string, (int dmg, int hits, int crits)>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in events)
    {
        if (e.IsIndirect || e.Source != pName) continue;
        if (!groups.TryGetValue(e.Ability, out var g)) g = (0, 0, 0);
        groups[e.Ability] = (g.dmg + e.Damage, g.hits + 1, g.crits + (e.IsCrit ? 1 : 0));
    }
    return groups.Select(kv => new AbilityRow
    {
        ability = kv.Key,
        damage = kv.Value.dmg,
        hits = kv.Value.hits,
        crits = kv.Value.crits,
        dps = Math.Round(kv.Value.dmg / durationSec, 1)
    }).OrderByDescending(r => r.damage).ToList();
}

static List<TargetRow> AggregateByTarget(IEnumerable<CombatEvent> events, string pName, double durationMs)
{
    double durationSec = Math.Max(durationMs / 1000.0, 1.0);
    var groups = new Dictionary<string, (int dmg, int hits)>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in events)
    {
        if (e.IsIndirect || e.Source != pName) continue;
        if (!groups.TryGetValue(e.Target, out var g)) g = (0, 0);
        groups[e.Target] = (g.dmg + e.Damage, g.hits + 1);
    }
    return groups.Select(kv => new TargetRow
    {
        target = kv.Key,
        damage = kv.Value.dmg,
        hits = kv.Value.hits,
        dps = Math.Round(kv.Value.dmg / durationSec, 1)
    }).OrderByDescending(r => r.damage).ToList();
}

static List<SourceRow> AggregateBySource(IEnumerable<CombatEvent> events, string pName)
{
    var groups = new Dictionary<string, (int dmg, int hits)>(StringComparer.OrdinalIgnoreCase);
    foreach (var e in events)
    {
        if (e.Target != pName) continue;
        string src = e.IsIndirect ? $"{e.Target} (indirect)" : e.Source;
        if (!groups.TryGetValue(src, out var g)) g = (0, 0);
        groups[src] = (g.dmg + e.Damage, g.hits + 1);
    }
    return groups.Select(kv => new SourceRow
    {
        source = kv.Key,
        damage = kv.Value.dmg,
        hits = kv.Value.hits
    }).OrderByDescending(r => r.damage).ToList();
}

// --- Build API response ---
object GetApiData()
{
    lock (_lock)
    {
        var now = DateTime.Now;

        // Auto-finalize stale fights
        if (lastEventTime != DateTime.MinValue && (now - lastEventTime).TotalMilliseconds > 25000 && currentFightEvents.Count > 0)
            FinalizeFight();

        // Current fight
        var cfEvents = currentFightEvents.ToList();
        double cfDurationMs = cfEvents.Count > 0 ? (now - cfEvents[0].Timestamp).TotalMilliseconds : 0;
        bool inCombat = cfEvents.Count > 0 && (now - lastEventTime).TotalMilliseconds < 25000;
        int cfTotalDmg = 0, cfTotalInc = 0;
        string pName = playerName;
        foreach (var e in cfEvents)
        {
            if (e.Source == pName && !e.IsIndirect) cfTotalDmg += e.Damage;
            if (e.Target == pName) cfTotalInc += e.Damage;
        }

        // Live window
        var lwCutoff = now.AddMilliseconds(-liveWindowMs);
        var lwEvents = liveEvents.Where(e => e.Timestamp >= lwCutoff).ToList();
        double lwDurationMs = liveWindowMs;
        int lwTotalDmg = 0;
        foreach (var e in lwEvents) if (e.Source == pName && !e.IsIndirect) lwTotalDmg += e.Damage;
        double lwDps = Math.Round(lwTotalDmg / Math.Max(lwDurationMs / 1000.0, 1.0), 1);

        // Recent fights (last 10, newest first)
        var recentFights = completedFights.AsEnumerable().Reverse().Take(10)
            .Select(f => new
            {
                f.Label,
                startTime = f.StartTime.ToString("HH:mm:ss"),
                endTime = f.EndTime.ToString("HH:mm:ss"),
                durationSec = Math.Round((f.EndTime - f.StartTime).TotalSeconds, 1),
                f.TotalDamage,
                f.TotalIncoming,
                f.EventCount
            }).ToList();

        // Incoming recent hits (last 40)
        var allEvents = cfEvents.Concat(liveEvents).Distinct().ToList();
        var incomingHits = allEvents
            .Where(e => e.Target == pName)
            .OrderByDescending(e => e.Timestamp)
            .Take(40)
            .Select(e => new
            {
                time = e.Timestamp.ToString("HH:mm:ss"),
                source = e.Source,
                ability = e.Ability,
                damage = e.Damage,
                isCrit = e.IsCrit,
                isIndirect = e.IsIndirect
            }).ToList();

        return new
        {
            playerName = pName,
            liveWindowMs,
            inCombat,
            currentFight = new
            {
                events = cfEvents.TakeLast(200).Select(e => new
                {
                    time = e.Timestamp.ToString("HH:mm:ss"),
                    e.Source,
                    e.Ability,
                    e.Target,
                    e.Damage,
                    e.IsCrit,
                    e.IsIndirect
                }).ToList(),
                durationMs = (int)cfDurationMs,
                byAbility = AggregateByAbility(cfEvents, pName, cfDurationMs),
                byTarget = AggregateByTarget(cfEvents, pName, cfDurationMs),
                incomingBySource = AggregateBySource(cfEvents, pName),
                totalDamage = cfTotalDmg,
                totalIncoming = cfTotalInc
            },
            liveWindow = new
            {
                durationMs = liveWindowMs,
                byAbility = AggregateByAbility(lwEvents, pName, lwDurationMs),
                byTarget = AggregateByTarget(lwEvents, pName, lwDurationMs),
                incomingBySource = AggregateBySource(lwEvents, pName),
                totalDamage = lwTotalDmg,
                dps = lwDps
            },
            recentFights,
            incomingHits,
            rawLines = rawLines.TakeLast(200).Reverse().ToList()
        };
    }
}

// --- Find latest chat log ---
string? FindLatestChatLog()
{
    if (!Directory.Exists(chatLogsDir)) return null;
    var files = Directory.GetFiles(chatLogsDir, "Chat-*.log");
    if (files.Length == 0) return null;
    return files.OrderByDescending(f => new FileInfo(f).LastWriteTime).First();
}

// --- Log tailing ---
async Task TailChatLog(CancellationToken ct)
{
    string? logPath = FindLatestChatLog();

    if (logPath == null)
    {
        Console.WriteLine($"No Chat-*.log found in {chatLogsDir}. Waiting...");
        while (logPath == null && !ct.IsCancellationRequested)
        {
            await Task.Delay(2000, ct);
            logPath = FindLatestChatLog();
        }
        if (logPath == null) return;
    }

    Console.WriteLine($"Tailing: {logPath}");

    // Read existing content first
    using (var sr = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
    {
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
            ParseLine(line);
    }

    // Tail new content
    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);

    while (!ct.IsCancellationRequested)
    {
        string? newLine = await reader.ReadLineAsync(ct);
        if (newLine != null)
            ParseLine(newLine);
        else
            await Task.Delay(500, ct);
    }
}

void ParseLine(string line)
{
    var m = rxLineChannel.Match(line);
    if (!m.Success) return;

    string channel = m.Groups[1].Value;
    if (!channel.Equals("Combat", StringComparison.OrdinalIgnoreCase)) return;

    string content = m.Groups[2].Value;
    lock (_lock)
    {
        rawLines.Enqueue(content);
        if (rawLines.Count > 200) rawLines.Dequeue();
    }

    // Parse timestamp from start of line (format: HH:mm:ss or similar)
    DateTime timestamp = DateTime.Now;
    var tsMatch = Regex.Match(line, @"^(\d{1,2}:\d{2}:\d{2})");
    if (tsMatch.Success && TimeSpan.TryParse(tsMatch.Value, out var ts))
        timestamp = DateTime.Today + ts;

    ProcessCombatLine(content, timestamp);
}

// --- HTTP Server ---
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9880/");
    try { listener.Start(); Console.WriteLine("HTTP server listening on http://localhost:9880/"); }
    catch (Exception ex) { Console.WriteLine($"Failed to start HTTP server: {ex.Message}"); return; }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await listener.GetContextAsync();
            var response = context.Response;
            string path = context.Request.Url?.AbsolutePath ?? "/";
            string method = context.Request.HttpMethod;

            if (path == "/api/data" && method == "GET")
            {
                string json = JsonSerializer.Serialize(GetApiData());
                byte[] buf = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }
            else if (path == "/api/window" && method == "POST")
            {
                string? secsStr = context.Request.QueryString["secs"];
                if (int.TryParse(secsStr, out int secs) && secs is 10 or 30 or 60 or 120)
                    lock (_lock) { liveWindowMs = secs * 1000; }
                byte[] buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, liveWindowMs }));
                response.ContentType = "application/json";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }
            else if (path == "/api/player" && method == "POST")
            {
                string? name = context.Request.QueryString["name"];
                if (!string.IsNullOrWhiteSpace(name))
                    lock (_lock) { playerName = name.Trim(); }
                byte[] buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, playerName }));
                response.ContentType = "application/json";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }
            else
            {
                byte[] buf = Encoding.UTF8.GetBytes(HtmlContent.DASHBOARD);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }
            response.OutputStream.Close();
        }
        catch (Exception) when (ct.IsCancellationRequested) { break; }
        catch { }
    }
    listener.Stop();
}

// --- Main ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var httpTask = RunHttpServer(cts.Token);
var tailTask = TailChatLog(cts.Token);
Console.WriteLine("Press Ctrl+C to stop.");
try { await Task.WhenAll(httpTask, tailTask); } catch (OperationCanceledException) { }
Console.WriteLine("Shutting down.");

// --- Models ---
record CombatEvent
{
    public string Source { get; init; } = "";
    public string Ability { get; init; } = "";
    public string Target { get; init; } = "";
    public int Damage { get; init; }
    public bool IsCrit { get; init; }
    public bool IsIndirect { get; init; }
    public DateTime Timestamp { get; init; }
}

record FightSummary
{
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public int TotalDamage { get; init; }
    public int TotalIncoming { get; init; }
    public int EventCount { get; init; }
    public string Label { get; init; } = "";
}

record AbilityRow
{
    public string ability { get; init; } = "";
    public int damage { get; init; }
    public int hits { get; init; }
    public int crits { get; init; }
    public double dps { get; init; }
}

record TargetRow
{
    public string target { get; init; } = "";
    public int damage { get; init; }
    public int hits { get; init; }
    public double dps { get; init; }
}

record SourceRow
{
    public string source { get; init; } = "";
    public int damage { get; init; }
    public int hits { get; init; }
}

// --- HTML ---
static partial class HtmlContent
{
    public const string DASHBOARD = """
<!DOCTYPE html>
<html><head>
<meta charset="utf-8"><title>PG CombatTracker</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#1a1a2e;color:#e0e0e0;font-size:14px}
.container{max-width:1200px;margin:0 auto;padding:12px}
.header{background:linear-gradient(180deg,#2a2a4a,#1e1e3a);border:1px solid #4a4a6a;border-radius:6px;padding:10px 16px;margin-bottom:10px;display:flex;align-items:center;gap:12px;flex-wrap:wrap}
.header h1{color:#ffd700;font-size:18px;font-weight:bold;text-shadow:0 0 8px rgba(255,215,0,0.3)}
.stat{background:#12122a;padding:4px 10px;border-radius:4px;font-size:13px;border:1px solid #333358}
.stat-label{color:#888;font-size:11px}
.stat-value{color:#fff;font-weight:bold}
.gold{color:#ffd700}
.combat-dot{width:10px;height:10px;border-radius:50%;background:#ff4444;display:inline-block;margin-right:5px;box-shadow:0 0 6px #ff4444;animation:pulse 1s infinite}
@keyframes pulse{0%,100%{opacity:1}50%{opacity:0.4}}
.tabs{display:flex;gap:0;border-bottom:2px solid #4a4a6a;margin-bottom:12px}
.tab{padding:8px 20px;cursor:pointer;color:#888;font-size:13px;font-weight:bold;border-bottom:2px solid transparent;margin-bottom:-2px;transition:all 0.15s}
.tab:hover{color:#ccc}
.tab.active{color:#ffd700;border-bottom-color:#ffd700;background:linear-gradient(180deg,#2a2a4a 0%,transparent 100%)}
.tab-content{display:none}
.tab-content.active{display:block}
.two-col{display:grid;grid-template-columns:1fr 1fr;gap:12px}
.panel{background:#16162e;border:1px solid #4a4a6a;border-radius:6px;padding:10px}
.panel-title{color:#ffd700;font-size:13px;font-weight:bold;margin-bottom:8px;padding-bottom:6px;border-bottom:1px solid #333358}
.window-btns{display:flex;gap:4px;margin-bottom:10px;align-items:center}
.window-btn{background:#2a2a4a;border:1px solid #4a4a6a;color:#aaa;padding:4px 10px;border-radius:4px;cursor:pointer;font-size:12px;transition:all 0.15s}
.window-btn:hover{border-color:#ffd700;color:#ffd700}
.window-btn.active{background:#4a3a0a;border-color:#ffd700;color:#ffd700}
.window-label{color:#888;font-size:12px;margin-right:4px}
table{width:100%;border-collapse:collapse;font-size:12px}
th{color:#888;font-weight:normal;text-align:left;padding:4px 6px;border-bottom:1px solid #2a2a4a}
td{padding:3px 6px;border-bottom:1px solid #1e1e32}
tr:hover td{background:#1e1e36}
.bar-cell{position:relative;width:80px}
.bar{height:10px;border-radius:2px;min-width:2px;transition:width 0.3s}
.bar-green{background:linear-gradient(90deg,#22aa44,#44dd66)}
.bar-red{background:linear-gradient(90deg,#aa2222,#dd4444)}
.dmg{color:#ff9944}
.dps{color:#44ddaa}
.hits{color:#aaaaff}
.crits{color:#ffcc44}
.crit-badge{background:#6a3a00;color:#ffcc44;font-size:10px;padding:1px 5px;border-radius:3px;border:1px solid #aa6600}
.ind-badge{background:#1a2a3a;color:#6699cc;font-size:10px;padding:1px 5px;border-radius:3px;border:1px solid #335577}
.raw-list{font-family:monospace;font-size:11px;max-height:500px;overflow-y:auto;background:#0e0e1e;border-radius:4px;padding:6px}
.raw-line{padding:2px 0;border-bottom:1px solid #1a1a2a;color:#99aacc;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.raw-line:first-child{color:#ccddff}
.player-input{display:flex;gap:6px;align-items:center;margin-bottom:10px}
.player-input input{background:#12122a;border:1px solid #4a4a6a;border-radius:4px;color:#e0e0e0;padding:4px 8px;font-size:12px;width:160px}
.player-input input:focus{outline:none;border-color:#ffd700}
.player-input button{background:#2a2a4a;border:1px solid #4a4a6a;color:#ffd700;padding:4px 10px;border-radius:4px;cursor:pointer;font-size:12px}
.player-input button:hover{background:#3a3a5a}
.no-data{color:#555;font-size:12px;padding:20px;text-align:center}
</style>
</head><body>
<div class="container">
  <div class="header">
    <h1>&#x2694;&#xFE0F; CombatTracker</h1>
    <div class="stat"><span class="stat-label">Player</span><br><span class="stat-value" id="hdr-player">—</span></div>
    <div class="stat"><span class="stat-label">Live DPS</span><br><span class="stat-value dps" id="hdr-dps">0</span></div>
    <div class="stat"><span class="stat-label">Live Dmg</span><br><span class="stat-value dmg" id="hdr-dmg">0</span></div>
    <div id="hdr-combat"></div>
  </div>
  <div class="tabs">
    <div class="tab active" onclick="switchTab(0)">Your DPS</div>
    <div class="tab" onclick="switchTab(1)">Incoming</div>
    <div class="tab" onclick="switchTab(2)">Raw Lines</div>
  </div>

  <!-- Tab 0: Your DPS -->
  <div class="tab-content active" id="tab0">
    <div class="window-btns">
      <span class="window-label">Window:</span>
      <button class="window-btn" id="wbtn-10" onclick="setWindow(10)">10s</button>
      <button class="window-btn" id="wbtn-30" onclick="setWindow(30)">30s</button>
      <button class="window-btn active" id="wbtn-60" onclick="setWindow(60)">1min</button>
      <button class="window-btn" id="wbtn-120" onclick="setWindow(120)">2min</button>
      <span style="margin-left:12px;color:#888;font-size:12px" id="window-info"></span>
    </div>
    <div class="two-col">
      <div class="panel">
        <div class="panel-title">Your Abilities</div>
        <div id="tbl-abilities"><div class="no-data">No data</div></div>
      </div>
      <div class="panel">
        <div class="panel-title">By Target</div>
        <div id="tbl-targets"><div class="no-data">No data</div></div>
      </div>
    </div>
    <div style="margin-top:12px">
      <div class="panel">
        <div class="panel-title">Recent Fights</div>
        <div id="tbl-fights"><div class="no-data">No fights yet</div></div>
      </div>
    </div>
  </div>

  <!-- Tab 1: Incoming -->
  <div class="tab-content" id="tab1">
    <div class="two-col">
      <div class="panel">
        <div class="panel-title">Sources</div>
        <div id="tbl-sources"><div class="no-data">No data</div></div>
      </div>
      <div class="panel">
        <div class="panel-title">Recent Hits (incoming)</div>
        <div id="tbl-incoming-hits"><div class="no-data">No data</div></div>
      </div>
    </div>
  </div>

  <!-- Tab 2: Raw Lines -->
  <div class="tab-content" id="tab2">
    <div class="panel">
      <div class="panel-title" style="display:flex;justify-content:space-between">
        <span>Raw [Combat] Lines</span>
        <span style="color:#555;font-size:11px">newest first</span>
      </div>
      <div class="player-input">
        <span style="color:#888;font-size:12px">Player name:</span>
        <input type="text" id="player-name-input" placeholder="auto-detect">
        <button onclick="setPlayer()">Set</button>
      </div>
      <div class="raw-list" id="raw-lines"><div class="no-data">No combat lines yet</div></div>
    </div>
  </div>
</div>

<script>
let data = null;
let activeTab = 0;
let currentWindowSecs = 60;

function switchTab(i) {
  activeTab = i;
  document.querySelectorAll('.tab').forEach((t,j)=>t.classList.toggle('active',i===j));
  document.querySelectorAll('.tab-content').forEach((t,j)=>t.classList.toggle('active',i===j));
}

async function fetchData() {
  try {
    const r = await fetch('/api/data');
    data = await r.json();
    render();
  } catch(e) { console.error(e); }
}

async function setWindow(secs) {
  currentWindowSecs = secs;
  await fetch('/api/window?secs='+secs, {method:'POST'});
  fetchData();
}

async function setPlayer() {
  const name = document.getElementById('player-name-input').value.trim();
  if (!name) return;
  await fetch('/api/player?name='+encodeURIComponent(name), {method:'POST'});
  fetchData();
}

function bar(val, maxVal, cls) {
  const pct = maxVal > 0 ? Math.round(val/maxVal*100) : 0;
  return '<div class="bar-cell"><div class="bar '+cls+'" style="width:'+pct+'%"></div></div>';
}

function render() {
  if (!data) return;

  // Header
  document.getElementById('hdr-player').textContent = data.playerName || '(auto)';
  document.getElementById('hdr-dps').textContent = data.liveWindow.dps;
  document.getElementById('hdr-dmg').textContent = data.liveWindow.totalDamage.toLocaleString();

  const combatEl = document.getElementById('hdr-combat');
  combatEl.innerHTML = data.inCombat
    ? '<span><span class="combat-dot"></span><span style="color:#ff6666;font-size:13px">In Combat</span></span>'
    : '';

  // Window buttons
  [10,30,60,120].forEach(s=>{
    document.getElementById('wbtn-'+s).classList.toggle('active', data.liveWindowMs===s*1000);
  });
  document.getElementById('window-info').textContent =
    'Total: '+data.liveWindow.totalDamage.toLocaleString()+' dmg | '+data.liveWindow.dps+' DPS';

  // Abilities table
  renderAbilities(data.liveWindow.byAbility);
  renderTargets(data.liveWindow.byTarget);
  renderFights(data.recentFights);
  renderSources(data.liveWindow.incomingBySource);
  renderIncomingHits(data.incomingHits);
  renderRaw(data.rawLines);

  // Update player name input placeholder
  if (data.playerName) document.getElementById('player-name-input').placeholder = data.playerName;
}

function renderAbilities(rows) {
  const el = document.getElementById('tbl-abilities');
  if (!rows || rows.length === 0) { el.innerHTML='<div class="no-data">No data</div>'; return; }
  const maxDps = Math.max(...rows.map(r=>r.dps), 0.01);
  let h = '<table><tr><th>Ability</th><th class="dmg">Dmg</th><th class="hits">Hits</th><th class="crits">Crits</th><th class="dps">DPS</th><th></th></tr>';
  for (const r of rows) {
    h += '<tr><td>'+esc(r.ability)+'</td><td class="dmg">'+r.damage.toLocaleString()+'</td>'
      +'<td class="hits">'+r.hits+'</td><td class="crits">'+r.crits+'</td>'
      +'<td class="dps">'+r.dps+'</td>'+bar(r.dps,maxDps,'bar-green')+'</tr>';
  }
  h += '</table>';
  el.innerHTML = h;
}

function renderTargets(rows) {
  const el = document.getElementById('tbl-targets');
  if (!rows || rows.length === 0) { el.innerHTML='<div class="no-data">No data</div>'; return; }
  const maxDps = Math.max(...rows.map(r=>r.dps), 0.01);
  let h = '<table><tr><th>Target</th><th class="dmg">Dmg</th><th class="hits">Hits</th><th class="dps">DPS</th><th></th></tr>';
  for (const r of rows) {
    h += '<tr><td>'+esc(r.target)+'</td><td class="dmg">'+r.damage.toLocaleString()+'</td>'
      +'<td class="hits">'+r.hits+'</td><td class="dps">'+r.dps+'</td>'+bar(r.dps,maxDps,'bar-green')+'</tr>';
  }
  h += '</table>';
  el.innerHTML = h;
}

function renderFights(fights) {
  const el = document.getElementById('tbl-fights');
  if (!fights || fights.length === 0) { el.innerHTML='<div class="no-data">No fights yet</div>'; return; }
  let h = '<table><tr><th>Fight</th><th>Duration</th><th class="dmg">Dmg Out</th><th style="color:#dd6666">Dmg In</th><th class="hits">Events</th></tr>';
  for (const f of fights) {
    h += '<tr><td>'+esc(f.Label)+'</td><td>'+f.durationSec+'s</td>'
      +'<td class="dmg">'+f.TotalDamage.toLocaleString()+'</td>'
      +'<td style="color:#dd6666">'+f.TotalIncoming.toLocaleString()+'</td>'
      +'<td class="hits">'+f.EventCount+'</td></tr>';
  }
  h += '</table>';
  el.innerHTML = h;
}

function renderSources(rows) {
  const el = document.getElementById('tbl-sources');
  if (!rows || rows.length === 0) { el.innerHTML='<div class="no-data">No incoming damage</div>'; return; }
  const maxDmg = Math.max(...rows.map(r=>r.damage), 1);
  let h = '<table><tr><th>Source</th><th style="color:#dd6666">Dmg</th><th class="hits">Hits</th><th></th></tr>';
  for (const r of rows) {
    h += '<tr><td>'+esc(r.source)+'</td><td style="color:#dd6666">'+r.damage.toLocaleString()+'</td>'
      +'<td class="hits">'+r.hits+'</td>'+bar(r.damage,maxDmg,'bar-red')+'</tr>';
  }
  h += '</table>';
  el.innerHTML = h;
}

function renderIncomingHits(hits) {
  const el = document.getElementById('tbl-incoming-hits');
  if (!hits || hits.length === 0) { el.innerHTML='<div class="no-data">No incoming hits</div>'; return; }
  let h = '<table><tr><th>Time</th><th>Source</th><th>Ability</th><th style="color:#dd6666">Dmg</th></tr>';
  for (const h2 of hits) {
    const badge = h2.isCrit ? ' <span class="crit-badge">CRIT</span>' : '';
    const ibadge = h2.isIndirect ? ' <span class="ind-badge">indirect</span>' : '';
    h += '<tr><td style="color:#666">'+h2.time+'</td><td>'+esc(h2.source)+'</td>'
      +'<td>'+esc(h2.ability)+badge+ibadge+'</td>'
      +'<td style="color:#dd6666">'+h2.damage+'</td></tr>';
  }
  h += '</table>';
  el.innerHTML = h;
}

function renderRaw(lines) {
  const el = document.getElementById('raw-lines');
  if (!lines || lines.length === 0) { el.innerHTML='<div class="no-data">No combat lines yet</div>'; return; }
  el.innerHTML = lines.map(l=>'<div class="raw-line">'+esc(l)+'</div>').join('');
}

function esc(s) { return (s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;'); }

fetchData();
setInterval(fetchData, 2000);
</script>
</body></html>
""";
}

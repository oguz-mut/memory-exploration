using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

// --- Configuration ---
string chatLogsDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon\ChatLogs";
string playerLogFallback = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon\Player.log";

// --- State ---
var lines = new ConcurrentQueue<ChatLine>();
var alerts = new ConcurrentDictionary<string, ChatAlert>(StringComparer.Ordinal);
var seenChannels = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
int statsTotal = 0;
int statsMatched = 0;
string? activeLogPath = null;

// --- Find latest chat log ---
string? FindLatestChatLog()
{
    if (!Directory.Exists(chatLogsDir)) return null;
    var files = Directory.GetFiles(chatLogsDir, "Chat-*.log");
    if (files.Length == 0) return null;
    return files.OrderByDescending(f => new FileInfo(f).LastWriteTime).First();
}

string ResolveLogPath()
{
    string? chat = FindLatestChatLog();
    return chat ?? playerLogFallback;
}

// --- Line parsers ---
// ChatLogs format: HH:MM:SS\t[Channel] Message
bool TryParseChatLogLine(string line, out string time, out string channel, out string content)
{
    time = channel = content = "";
    int tabIdx = line.IndexOf('\t');
    if (tabIdx <= 0 || tabIdx >= line.Length - 1) return false;
    time = line[..tabIdx].Trim();
    string rest = line[(tabIdx + 1)..];
    if (!rest.StartsWith('[')) return false;
    int closeIdx = rest.IndexOf(']');
    if (closeIdx < 2) return false;
    channel = rest[1..closeIdx];
    content = rest[(closeIdx + 1)..].TrimStart();
    return true;
}

// Player.log formats:
//   [HH:MM:SS] [Channel] Message
//   [HH:MM:SS] Channel: Message  (for known channel names)
var knownChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "General", "Trade", "Help", "Combat", "NPCSays", "Party",
    "Guild", "Nearby", "System", "Private", "Tell", "Global"
};

bool TryParsePlayerLogLine(string line, out string time, out string channel, out string content)
{
    time = channel = content = "";
    if (!line.StartsWith('[')) return false;
    int closeTs = line.IndexOf(']');
    if (closeTs < 2) return false;
    time = line[1..closeTs];
    // Validate time-like: 2 digits : 2 digits : 2 digits
    if (time.Length != 8 || time[2] != ':' || time[5] != ':') return false;
    string rest = line[(closeTs + 1)..].TrimStart();

    // Bracketed channel: [Channel] Message
    if (rest.StartsWith('['))
    {
        int chanClose = rest.IndexOf(']');
        if (chanClose >= 2)
        {
            channel = rest[1..chanClose];
            content = rest[(chanClose + 1)..].TrimStart();
            return !string.IsNullOrEmpty(content);
        }
    }

    // First-word channel: General Message text
    int spaceIdx = rest.IndexOf(' ');
    if (spaceIdx > 0)
    {
        string firstWord = rest[..spaceIdx];
        if (knownChannels.Contains(firstWord))
        {
            channel = firstWord;
            content = rest[(spaceIdx + 1)..].TrimStart();
            return !string.IsNullOrEmpty(content);
        }
    }
    return false;
}

bool TryParseLine(string raw, bool isChatLogFmt, out string time, out string channel, out string content)
{
    if (isChatLogFmt)
        return TryParseChatLogLine(raw, out time, out channel, out content);
    return TryParsePlayerLogLine(raw, out time, out channel, out content);
}

// --- Apply alerts and enqueue line ---
void HandleParsedLine(string time, string channel, string content)
{
    seenChannels.AddOrUpdate(channel, 1, (_, old) => old + 1);
    Interlocked.Increment(ref statsTotal);

    bool isMatched = false;
    string alertId = "";
    foreach (var (id, alert) in alerts)
    {
        if (!alert.Enabled) continue;
        bool chanOk = string.IsNullOrEmpty(alert.ChannelFilter) ||
            channel.IndexOf(alert.ChannelFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        bool patOk = content.IndexOf(alert.Pattern, StringComparison.OrdinalIgnoreCase) >= 0;
        if (!chanOk || !patOk) continue;

        isMatched = true;
        alertId = id;
        Interlocked.Increment(ref statsMatched);
        string snippet = content[..Math.Min(80, content.Length)];
        while (true)
        {
            var old = alert;
            var updated = old with { MatchCount = old.MatchCount + 1, LastMatch = snippet };
            if (alerts.TryUpdate(id, updated, old)) break;
            if (!alerts.TryGetValue(id, out var reread)) break;
            // retry with re-read value
            if (!alerts.TryUpdate(id, reread with { MatchCount = reread.MatchCount + 1, LastMatch = snippet }, reread)) break;
            break;
        }
        break;
    }

    lines.Enqueue(new ChatLine(time, channel, content, isMatched, alertId));
    while (lines.Count > 500) lines.TryDequeue(out _);
}

// --- HTTP server ---
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9879/");
    try { listener.Start(); Console.WriteLine("HTTP server listening on http://localhost:9879/"); }
    catch (Exception ex) { Console.WriteLine($"Failed to start HTTP: {ex.Message}"); return; }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var ctx = await listener.GetContextAsync();
            var req = ctx.Request;
            var resp = ctx.Response;
            string path = req.Url?.AbsolutePath ?? "/";

            byte[] buf;
            if (path == "/api/data")
            {
                var payload = new
                {
                    lines = lines.ToArray().Select(l => new
                    {
                        time = l.Time, channel = l.Channel,
                        content = l.Content, isMatched = l.IsMatched, alertId = l.AlertId
                    }),
                    alerts = alerts.Values.Select(a => new
                    {
                        id = a.Id, name = a.Name, pattern = a.Pattern,
                        channelFilter = a.ChannelFilter, enabled = a.Enabled,
                        matchCount = a.MatchCount, lastMatch = a.LastMatch
                    }),
                    channels = seenChannels
                        .Select(kvp => new { name = kvp.Key, count = kvp.Value })
                        .OrderByDescending(c => c.count),
                    stats = new { total = statsTotal, matched = statsMatched }
                };
                buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
                resp.ContentType = "application/json";
            }
            else if (path == "/api/alerts/add" && req.HttpMethod == "POST")
            {
                string name = req.QueryString["name"] ?? "Alert";
                string pattern = req.QueryString["pattern"] ?? "";
                string chanFilter = req.QueryString["channel"] ?? "";
                if (!string.IsNullOrWhiteSpace(pattern))
                {
                    var a = new ChatAlert(Guid.NewGuid().ToString(), name, pattern, chanFilter, true, 0, "");
                    alerts[a.Id] = a;
                    buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, alert = new
                    {
                        id = a.Id, name = a.Name, pattern = a.Pattern,
                        channelFilter = a.ChannelFilter, enabled = a.Enabled,
                        matchCount = a.MatchCount, lastMatch = a.LastMatch
                    }}));
                }
                else
                {
                    buf = Encoding.UTF8.GetBytes("""{"ok":false,"error":"pattern required"}""");
                }
                resp.ContentType = "application/json";
            }
            else if (path == "/api/alerts/toggle" && req.HttpMethod == "POST")
            {
                string? id = req.QueryString["id"];
                bool newEnabled = false;
                if (id != null && alerts.TryGetValue(id, out var a))
                {
                    var updated = a with { Enabled = !a.Enabled };
                    alerts.TryUpdate(id, updated, a);
                    newEnabled = updated.Enabled;
                }
                buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = true, enabled = newEnabled }));
                resp.ContentType = "application/json";
            }
            else if (path == "/api/alerts/delete" && req.HttpMethod == "POST")
            {
                string? id = req.QueryString["id"];
                if (id != null) alerts.TryRemove(id, out _);
                buf = Encoding.UTF8.GetBytes("""{"ok":true}""");
                resp.ContentType = "application/json";
            }
            else
            {
                buf = Encoding.UTF8.GetBytes(HtmlContent.DASHBOARD);
                resp.ContentType = "text/html; charset=utf-8";
            }

            resp.ContentLength64 = buf.Length;
            await resp.OutputStream.WriteAsync(buf, ct);
            resp.OutputStream.Close();
        }
        catch (Exception) when (ct.IsCancellationRequested) { break; }
        catch { }
    }
    listener.Stop();
}

// --- Log tailer ---
async Task TailLog(string logPath, bool isChatLogFmt, CancellationToken ct)
{
    Console.WriteLine($"Tailing: {logPath} (chatLogFmt={isChatLogFmt})");

    // Read existing content
    using (var sr = new StreamReader(new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
    {
        string? prevChannel = null;
        string? raw;
        while ((raw = await sr.ReadLineAsync(ct)) != null)
        {
            if (TryParseLine(raw, isChatLogFmt, out var t, out var ch, out var cont))
            {
                prevChannel = ch;
                HandleParsedLine(t, ch, cont);
            }
            else if (prevChannel != null && !string.IsNullOrWhiteSpace(raw) && isChatLogFmt)
            {
                // continuation line belongs to previous channel
                HandleParsedLine("", prevChannel, raw.TrimStart());
            }
        }
    }

    // Tail new bytes with rotation check
    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);
    string? lastChannel = null;
    int pollCount = 0;

    while (!ct.IsCancellationRequested)
    {
        if (++pollCount >= 30)
        {
            pollCount = 0;
            string newPath = ResolveLogPath();
            if (newPath != logPath)
            {
                Console.WriteLine($"Log rotated → {newPath}");
                return; // caller restarts
            }
        }

        string? raw = await reader.ReadLineAsync(ct);
        if (raw != null)
        {
            if (TryParseLine(raw, isChatLogFmt, out var t, out var ch, out var cont))
            {
                lastChannel = ch;
                HandleParsedLine(t, ch, cont);
            }
            else if (lastChannel != null && !string.IsNullOrWhiteSpace(raw) && isChatLogFmt)
            {
                HandleParsedLine("", lastChannel, raw.TrimStart());
            }
        }
        else
        {
            await Task.Delay(500, ct);
        }
    }
}

async Task TailLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        string path = ResolveLogPath();
        activeLogPath = path;
        bool isChatFmt = (FindLatestChatLog() != null);
        try
        {
            await TailLog(path, isChatFmt, ct);
        }
        catch (OperationCanceledException) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"Tail error: {ex.Message}");
            await Task.Delay(2000, ct);
        }
    }
}

// --- Main ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var httpTask = RunHttpServer(cts.Token);
var tailTask = TailLoop(cts.Token);
Console.WriteLine("ChatWatcher running on http://localhost:9879/. Press Ctrl+C to stop.");
try { await Task.WhenAll(httpTask, tailTask); } catch (OperationCanceledException) { }
Console.WriteLine("Shutting down.");

// --- HTML ---
static partial class HtmlContent
{
    public const string DASHBOARD = """
<!DOCTYPE html>
<html><head>
<meta charset="utf-8"><title>PG ChatWatcher</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#1a1a2e;color:#e0e0e0;font-size:13px;height:100vh;overflow:hidden}
.layout{display:grid;grid-template-columns:60fr 40fr;height:100vh;gap:0}

/* Left: chat panel */
.chat-panel{display:flex;flex-direction:column;border-right:1px solid #4a4a6a;min-height:0}
.panel-header{background:linear-gradient(180deg,#2a2a4a 0%,#1e1e3a 100%);border-bottom:1px solid #4a4a6a;padding:8px 12px;flex-shrink:0}
.panel-header h1{color:#ffd700;font-size:16px;font-weight:bold;margin-bottom:6px;text-shadow:0 0 8px rgba(255,215,0,0.3)}
.toolbar{display:flex;align-items:center;gap:6px;flex-wrap:wrap}
.toolbar select,.toolbar input[type=text]{background:#12122a;border:1px solid #4a4a6a;border-radius:4px;color:#e0e0e0;padding:4px 8px;font-size:12px}
.toolbar select:focus,.toolbar input[type=text]:focus{outline:none;border-color:#ffd700}
.toolbar input[type=text]{width:160px}
.toolbar input[type=text]::placeholder{color:#555}
.autoscroll-label{color:#aaa;font-size:12px;display:flex;align-items:center;gap:4px;cursor:pointer;user-select:none}
.autoscroll-label input{cursor:pointer}
.stats-bar{color:#666;font-size:11px;margin-left:auto}
.stats-bar span{color:#aaa}

.chat-list{flex:1;overflow-y:auto;padding:4px 8px;min-height:0}
.chat-list::-webkit-scrollbar{width:6px}
.chat-list::-webkit-scrollbar-track{background:#12122a}
.chat-list::-webkit-scrollbar-thumb{background:#4a4a6a;border-radius:3px}

.chat-line{display:flex;align-items:baseline;gap:5px;padding:2px 4px;border-radius:2px;border-left:3px solid transparent;margin-bottom:1px;line-height:1.45}
.chat-line:hover{background:rgba(255,255,255,0.04)}
.chat-line.matched{border-left-color:#ffd700;background:rgba(255,215,0,0.06)}
.chat-line.hidden{display:none}

.ts{color:#555;font-size:11px;white-space:nowrap;flex-shrink:0}
.badge{font-size:10px;font-weight:bold;padding:1px 5px;border-radius:3px;white-space:nowrap;flex-shrink:0}
.ch-combat{background:#3d0a0a;color:#ff6b6b}
.ch-npcsays{background:#0a1a3d;color:#6b9fff}
.ch-trade{background:#3d2e00;color:#ffd700}
.ch-party{background:#0a2a0a;color:#6bff6b}
.ch-general{background:#1a1a2e;color:#cccccc;border:1px solid #333}
.ch-guild{background:#1a1a3d;color:#9b9bff}
.ch-help{background:#2a0a2a;color:#ff9bff}
.ch-other{background:#1a1a1a;color:#888}
.msg{color:#ddd;word-break:break-word;flex:1}
.msg.dim{color:#999}

/* Right: alerts panel */
.alerts-panel{display:flex;flex-direction:column;min-height:0}
.alerts-header{background:linear-gradient(180deg,#2a2a4a 0%,#1e1e3a 100%);border-bottom:1px solid #4a4a6a;padding:8px 12px;flex-shrink:0}
.alerts-header h2{color:#ffd700;font-size:14px;font-weight:bold;margin-bottom:6px}

.add-form{display:grid;grid-template-columns:1fr 1fr;gap:4px;margin-bottom:4px}
.add-form input{background:#12122a;border:1px solid #4a4a6a;border-radius:4px;color:#e0e0e0;padding:4px 8px;font-size:12px}
.add-form input::placeholder{color:#555}
.add-form input:focus{outline:none;border-color:#ffd700}
.add-btn{grid-column:1/-1;background:linear-gradient(180deg,#4a4a1a 0%,#2e2e0a 100%);color:#ffd700;border:1px solid #6a6a2a;border-radius:4px;padding:5px 12px;cursor:pointer;font-size:12px;font-weight:bold;transition:all 0.15s}
.add-btn:hover{background:linear-gradient(180deg,#5a5a2a 0%,#3e3e1a 100%)}

.alerts-list{flex:1;overflow-y:auto;padding:6px 8px;min-height:0}
.alerts-list::-webkit-scrollbar{width:6px}
.alerts-list::-webkit-scrollbar-track{background:#12122a}
.alerts-list::-webkit-scrollbar-thumb{background:#4a4a6a;border-radius:3px}

.alert-card{background:#12122a;border:1px solid #333358;border-radius:5px;padding:8px 10px;margin-bottom:6px}
.alert-card.disabled{opacity:0.5}
.alert-name{color:#ffd700;font-weight:bold;font-size:13px;margin-bottom:3px}
.alert-meta{color:#888;font-size:11px;margin-bottom:5px}
.alert-meta .pattern{color:#aaccff}
.alert-meta .chan-filter{color:#88cc88}
.alert-stats{display:flex;align-items:center;gap:6px;font-size:11px;margin-bottom:5px}
.match-count{background:#2a2a0a;color:#ffd700;padding:1px 6px;border-radius:10px;border:1px solid #4a4a1a}
.last-match{color:#aaa;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;max-width:200px}
.alert-actions{display:flex;gap:5px}
.toggle-btn{background:#1a3a1a;color:#6bff6b;border:1px solid #2a5a2a;border-radius:3px;padding:2px 8px;cursor:pointer;font-size:11px;font-weight:bold;transition:all 0.15s}
.toggle-btn.disabled{background:#2a1a1a;color:#888;border-color:#3a2a2a}
.toggle-btn:hover{opacity:0.8}
.del-btn{background:#3a0a0a;color:#ff6b6b;border:1px solid #5a1a1a;border-radius:3px;padding:2px 8px;cursor:pointer;font-size:11px;font-weight:bold;transition:all 0.15s}
.del-btn:hover{opacity:0.8}

.empty-alerts{color:#555;font-size:12px;text-align:center;padding:20px 0}
</style>
</head><body>
<div class="layout">
  <!-- Left: Chat -->
  <div class="chat-panel">
    <div class="panel-header">
      <h1>&#x1f4ac; ChatWatcher</h1>
      <div class="toolbar">
        <select id="chanFilter" onchange="applyFilter()"><option value="">All Channels</option></select>
        <input type="text" id="searchBox" placeholder="Search messages..." oninput="applyFilter()">
        <label class="autoscroll-label"><input type="checkbox" id="autoScroll" checked> Auto-scroll</label>
        <span class="stats-bar">Lines: <span id="statTotal">0</span> | Matched: <span id="statMatched">0</span></span>
      </div>
    </div>
    <div class="chat-list" id="chatList"></div>
  </div>

  <!-- Right: Alerts -->
  <div class="alerts-panel">
    <div class="alerts-header">
      <h2>&#x1f514; Alerts</h2>
      <div class="add-form">
        <input type="text" id="aName" placeholder="Alert name">
        <input type="text" id="aPattern" placeholder="Pattern (required)">
        <input type="text" id="aChannel" placeholder="Channel filter (optional)" style="grid-column:1/-1">
        <button class="add-btn" onclick="addAlert()">+ Add Alert</button>
      </div>
    </div>
    <div class="alerts-list" id="alertsList"></div>
  </div>
</div>

<script>
// Web Audio beep
function playBeep() {
    try {
        const ac = new AudioContext();
        const osc = ac.createOscillator();
        const gain = ac.createGain();
        osc.connect(gain);
        gain.connect(ac.destination);
        osc.frequency.value = 880;
        osc.type = 'sine';
        gain.gain.setValueAtTime(0.3, ac.currentTime);
        gain.gain.exponentialRampToValueAtTime(0.001, ac.currentTime + 0.3);
        osc.start(ac.currentTime);
        osc.stop(ac.currentTime + 0.3);
    } catch(e) {}
}

let data = null;
let prevMatchCounts = {};
let chanFilter = '';
let searchText = '';

const CHANNEL_CSS = {
    'Combat': 'ch-combat',
    'NPCSays': 'ch-npcsays',
    'NPC': 'ch-npcsays',
    'Trade': 'ch-trade',
    'Party': 'ch-party',
    'General': 'ch-general',
    'Guild': 'ch-guild',
    'Help': 'ch-help'
};

function chanCss(ch) {
    return CHANNEL_CSS[ch] || 'ch-other';
}

function esc(s) {
    return (s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

async function fetchData() {
    try {
        const r = await fetch('/api/data');
        data = await r.json();
        renderChat();
        renderAlerts();
        updateChannelDropdown();
        document.getElementById('statTotal').textContent = data.stats.total;
        document.getElementById('statMatched').textContent = data.stats.matched;
    } catch(e) { console.error(e); }
}

function renderChat() {
    if (!data) return;
    const list = document.getElementById('chatList');
    const wasAtBottom = list.scrollHeight - list.scrollTop - list.clientHeight < 40;
    const autoScroll = document.getElementById('autoScroll').checked;

    let html = '';
    for (const line of data.lines) {
        const hidden = shouldHide(line);
        const matchedCls = line.isMatched ? ' matched' : '';
        const hiddenCls = hidden ? ' hidden' : '';
        const msgDim = !line.isMatched ? ' dim' : '';
        html += `<div class="chat-line${matchedCls}${hiddenCls}">`;
        if (line.time) html += `<span class="ts">${esc(line.time)}</span>`;
        if (line.channel) html += `<span class="badge ${chanCss(line.channel)}">${esc(line.channel)}</span>`;
        html += `<span class="msg${msgDim}">${esc(line.content)}</span>`;
        html += '</div>';
    }
    list.innerHTML = html;

    if (autoScroll && (wasAtBottom || data.lines.length < 20)) {
        list.scrollTop = list.scrollHeight;
    }
}

function shouldHide(line) {
    if (chanFilter && line.channel !== chanFilter) return true;
    if (searchText && line.content.toLowerCase().indexOf(searchText) < 0) return true;
    return false;
}

function applyFilter() {
    chanFilter = document.getElementById('chanFilter').value;
    searchText = document.getElementById('searchBox').value.toLowerCase();
    if (!data) return;
    const items = document.querySelectorAll('#chatList .chat-line');
    const lineArr = data.lines;
    for (let i = 0; i < items.length && i < lineArr.length; i++) {
        items[i].classList.toggle('hidden', shouldHide(lineArr[i]));
    }
}

function updateChannelDropdown() {
    if (!data) return;
    const sel = document.getElementById('chanFilter');
    const current = sel.value;
    const channels = data.channels.map(c => c.name);
    let html = '<option value="">All Channels</option>';
    for (const ch of channels) {
        const sel2 = ch === current ? ' selected' : '';
        html += `<option value="${esc(ch)}"${sel2}>${esc(ch)}</option>`;
    }
    sel.innerHTML = html;
}

function renderAlerts() {
    if (!data) return;

    // Detect new matches → beep
    let beep = false;
    for (const a of data.alerts) {
        const prev = prevMatchCounts[a.id] ?? 0;
        if (a.matchCount > prev) beep = true;
        prevMatchCounts[a.id] = a.matchCount;
    }
    if (beep) playBeep();

    const container = document.getElementById('alertsList');
    if (data.alerts.length === 0) {
        container.innerHTML = '<div class="empty-alerts">No alerts configured.<br>Add a keyword to watch for.</div>';
        return;
    }

    let html = '';
    for (const a of data.alerts) {
        const disabledCls = a.enabled ? '' : ' disabled';
        const toggleLabel = a.enabled ? 'ON' : 'OFF';
        const toggleCls = a.enabled ? 'toggle-btn' : 'toggle-btn disabled';
        const chanDisplay = a.channelFilter ? `<span class="chan-filter">#${esc(a.channelFilter)}</span>` : '<span style="color:#555">Any channel</span>';
        const lastMatchHtml = a.lastMatch ? `<span class="last-match" title="${esc(a.lastMatch)}">${esc(a.lastMatch)}</span>` : '';
        html += `<div class="alert-card${disabledCls}">
  <div class="alert-name">${esc(a.name)}</div>
  <div class="alert-meta">Pattern: <span class="pattern">${esc(a.pattern)}</span> &nbsp; Channel: ${chanDisplay}</div>
  <div class="alert-stats">
    <span class="match-count">${a.matchCount} match${a.matchCount !== 1 ? 'es' : ''}</span>
    ${lastMatchHtml}
  </div>
  <div class="alert-actions">
    <button class="${toggleCls}" onclick="toggleAlert('${esc(a.id)}')">${toggleLabel}</button>
    <button class="del-btn" onclick="deleteAlert('${esc(a.id)}')">&#x2715; Delete</button>
  </div>
</div>`;
    }
    container.innerHTML = html;
}

async function addAlert() {
    const name = document.getElementById('aName').value.trim() || 'Alert';
    const pattern = document.getElementById('aPattern').value.trim();
    const channel = document.getElementById('aChannel').value.trim();
    if (!pattern) { document.getElementById('aPattern').focus(); return; }
    const params = new URLSearchParams({ name, pattern, channel });
    await fetch('/api/alerts/add?' + params.toString(), { method: 'POST' });
    document.getElementById('aName').value = '';
    document.getElementById('aPattern').value = '';
    document.getElementById('aChannel').value = '';
    await fetchData();
}

async function toggleAlert(id) {
    await fetch('/api/alerts/toggle?id=' + encodeURIComponent(id), { method: 'POST' });
    await fetchData();
}

async function deleteAlert(id) {
    delete prevMatchCounts[id];
    await fetch('/api/alerts/delete?id=' + encodeURIComponent(id), { method: 'POST' });
    await fetchData();
}

fetchData();
setInterval(fetchData, 2000);
</script>
</body></html>
""";
}

record ChatLine(string Time, string Channel, string Content, bool IsMatched, string AlertId);
record ChatAlert(string Id, string Name, string Pattern, string ChannelFilter, bool Enabled, int MatchCount, string LastMatch);

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

// ─── Config ───────────────────────────────────────────────────────────────────
const string ChatLogDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon\ChatLogs";
const string PlayerLogPath = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon\Player.log";

// ─── Shared state ─────────────────────────────────────────────────────────────
var lines = new ConcurrentQueue<ChatLine>();
var alerts = new ConcurrentDictionary<string, ChatAlert>();
var seenChannels = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
int statsTotal = 0;
int statsMatched = 0;

// ─── Helpers ──────────────────────────────────────────────────────────────────
string FindLatestLogFile()
{
    try
    {
        if (Directory.Exists(ChatLogDir))
        {
            var files = Directory.GetFiles(ChatLogDir, "Chat-*.log");
            if (files.Length > 0)
                return files.OrderByDescending(f => new FileInfo(f).LastWriteTime).First();
        }
    }
    catch { }
    return PlayerLogPath;
}

ChatLine? ParseLine(string raw, bool isPlayerLog)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;

    if (!isPlayerLog)
    {
        // Chat-*.log format: "HH:MM:SS\t[Channel] content"
        int tabIdx = raw.IndexOf('\t');
        if (tabIdx < 1 || tabIdx >= raw.Length - 1) return null;
        string time = raw[..tabIdx];
        string rest = raw[(tabIdx + 1)..];
        if (rest.Length > 2 && rest[0] == '[')
        {
            int closeIdx = rest.IndexOf(']');
            if (closeIdx > 1)
            {
                string channel = rest[1..closeIdx];
                string content = closeIdx + 1 < rest.Length ? rest[(closeIdx + 1)..].TrimStart() : "";
                return new ChatLine(time, channel, content, false, "");
            }
        }
        return new ChatLine(time, "", rest, false, "");
    }
    else
    {
        // Player.log format: "[HH:MM:SS] content"
        if (raw.Length > 11 && raw[0] == '[')
        {
            int close = raw.IndexOf(']');
            if (close > 0 && close < raw.Length - 1)
            {
                string time = raw[1..close];
                // Validate timestamp shape: HH:MM:SS (8 chars, colons at pos 2 and 5)
                if (time.Length == 8 && time[2] == ':' && time[5] == ':')
                {
                    string content = raw[(close + 1)..].TrimStart();
                    if (!string.IsNullOrWhiteSpace(content))
                        return new ChatLine(time, "System", content, false, "");
                }
            }
        }
        return null;
    }
}

void ProcessLine(string raw, bool isPlayerLog)
{
    var line = ParseLine(raw, isPlayerLog);
    if (line == null) return;

    statsTotal++;
    if (!string.IsNullOrEmpty(line.Channel))
        seenChannels.AddOrUpdate(line.Channel, 1, (_, c) => c + 1);

    // Match against enabled alerts (first match wins)
    string matchedId = "";
    bool matched = false;
    foreach (var kv in alerts)
    {
        var alert = kv.Value;
        if (!alert.Enabled) continue;
        if (!string.IsNullOrEmpty(alert.ChannelFilter) &&
            line.Channel.IndexOf(alert.ChannelFilter, StringComparison.OrdinalIgnoreCase) < 0)
            continue;
        if (!string.IsNullOrEmpty(alert.Pattern) &&
            line.Content.IndexOf(alert.Pattern, StringComparison.OrdinalIgnoreCase) < 0)
            continue;
        alert.MatchCount++;
        alert.LastMatch = line.Content[..Math.Min(80, line.Content.Length)];
        matchedId = alert.Id;
        matched = true;
        statsMatched++;
        break;
    }

    if (matched)
        line = line with { IsMatched = true, AlertId = matchedId };

    lines.Enqueue(line);
    while (lines.Count > 500)
        lines.TryDequeue(out _);
}

object GetData()
{
    var lArr = lines.ToArray().Select(l => new
    {
        time = l.Time, channel = l.Channel, content = l.Content,
        isMatched = l.IsMatched, alertId = l.AlertId
    });
    var aArr = alerts.Values.OrderBy(a => a.Name).Select(a => new
    {
        id = a.Id, name = a.Name, pattern = a.Pattern, channelFilter = a.ChannelFilter,
        enabled = a.Enabled, matchCount = a.MatchCount, lastMatch = a.LastMatch
    });
    var cArr = seenChannels.OrderByDescending(kv => kv.Value)
        .Select(kv => new { name = kv.Key, count = kv.Value });
    return new { lines = lArr, alerts = aArr, channels = cArr,
                 stats = new { total = statsTotal, matched = statsMatched } };
}

// ─── HTTP server ──────────────────────────────────────────────────────────────
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9879/");
    try { listener.Start(); Console.WriteLine("Listening: http://localhost:9879/"); }
    catch (Exception ex) { Console.WriteLine($"HTTP start failed: {ex.Message}"); return; }

    ct.Register(() => { try { listener.Stop(); } catch { } });

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var ctx = await listener.GetContextAsync();
            var req = ctx.Request;
            var resp = ctx.Response;
            string path = req.Url?.AbsolutePath ?? "/";

            byte[] buf;
            if (req.HttpMethod == "GET" && path == "/api/data")
            {
                buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(GetData()));
                resp.ContentType = "application/json";
            }
            else if (req.HttpMethod == "POST" && path == "/api/alerts/add")
            {
                string name = req.QueryString["name"] ?? "";
                string pattern = req.QueryString["pattern"] ?? "";
                string channel = req.QueryString["channel"] ?? "";
                var a = new ChatAlert
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = name, Pattern = pattern, ChannelFilter = channel
                };
                alerts[a.Id] = a;
                buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    ok = true,
                    alert = new { a.Id, a.Name, a.Pattern, a.ChannelFilter,
                                  a.Enabled, a.MatchCount, a.LastMatch }
                }));
                resp.ContentType = "application/json";
            }
            else if (req.HttpMethod == "POST" && path == "/api/alerts/toggle")
            {
                string? id = req.QueryString["id"];
                bool enabled = false;
                if (id != null && alerts.TryGetValue(id, out var a))
                { a.Enabled = !a.Enabled; enabled = a.Enabled; }
                buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = id != null, enabled }));
                resp.ContentType = "application/json";
            }
            else if (req.HttpMethod == "POST" && path == "/api/alerts/delete")
            {
                string? id = req.QueryString["id"];
                bool ok = id != null && alerts.TryRemove(id, out _);
                buf = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok }));
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

// ─── Log tailer ───────────────────────────────────────────────────────────────
async Task TailLog(CancellationToken ct)
{
    string logPath = FindLatestLogFile();
    bool isPlayerLog = string.Equals(logPath, PlayerLogPath, StringComparison.OrdinalIgnoreCase);

    while (!ct.IsCancellationRequested)
    {
        Console.WriteLine($"Watching: {logPath}");

        // Read all existing content
        try
        {
            using var sr = new StreamReader(
                new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                Encoding.UTF8);
            string? line;
            while ((line = await sr.ReadLineAsync(ct)) != null)
                ProcessLine(line, isPlayerLog);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Console.WriteLine($"Initial read error: {ex.Message}"); }

        // Tail new content from end, poll every 500ms
        int pollCount = 0;
        bool rotated = false;
        try
        {
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(0, SeekOrigin.End);
            using var reader = new StreamReader(fs, Encoding.UTF8, false, 4096, leaveOpen: true);

            while (!ct.IsCancellationRequested && !rotated)
            {
                string? newLine = await reader.ReadLineAsync(ct);
                if (newLine != null)
                {
                    ProcessLine(newLine, isPlayerLog);
                }
                else
                {
                    await Task.Delay(500, ct);
                    pollCount++;
                    if (pollCount % 30 == 0)
                    {
                        string newPath = FindLatestLogFile();
                        if (!string.Equals(newPath, logPath, StringComparison.OrdinalIgnoreCase))
                        {
                            logPath = newPath;
                            isPlayerLog = string.Equals(logPath, PlayerLogPath,
                                StringComparison.OrdinalIgnoreCase);
                            rotated = true;
                            Console.WriteLine($"Rotated to: {logPath}");
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Console.WriteLine($"Tail error: {ex.Message}"); }
    }
}

// ─── Entry point ──────────────────────────────────────────────────────────────
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.WriteLine("ChatWatcher starting...");
var httpTask = RunHttpServer(cts.Token);
var tailTask = TailLog(cts.Token);
Console.WriteLine("Press Ctrl+C to stop.");
try { await Task.WhenAll(httpTask, tailTask); }
catch (OperationCanceledException) { }
Console.WriteLine("Shutting down.");

// ─── Data types ───────────────────────────────────────────────────────────────
record ChatLine(string Time, string Channel, string Content, bool IsMatched, string AlertId);

class ChatAlert
{
    public string Id { get; init; } = "";
    public string Name { get; set; } = "";
    public string Pattern { get; set; } = "";
    public string ChannelFilter { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int MatchCount { get; set; }
    public string LastMatch { get; set; } = "";
}

// ─── HTML ─────────────────────────────────────────────────────────────────────
static partial class HtmlContent
{
    public const string DASHBOARD = """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>PG ChatWatcher</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#1a1a2e;color:#e0e0e0;font-size:13px;height:100vh;overflow:hidden}
.app{display:flex;flex-direction:column;height:100vh;padding:8px;gap:8px}
.hdr{background:#22223a;border:1px solid #4a4a6a;border-radius:6px;padding:8px 14px;display:flex;align-items:center;gap:14px;flex-shrink:0}
.hdr h1{color:#ffd700;font-size:17px;font-weight:bold;text-shadow:0 0 8px rgba(255,215,0,0.3)}
.stat{font-size:12px;color:#888}.stat b{color:#fff}
.grid{display:grid;grid-template-columns:60fr 40fr;gap:8px;flex:1;min-height:0}
.panel{display:flex;flex-direction:column;background:#12122a;border:1px solid #4a4a6a;border-radius:6px;min-height:0;overflow:hidden}
.panel-hdr{padding:7px 10px;border-bottom:1px solid #2a2a4a;flex-shrink:0;font-weight:bold;color:#ffd700;font-size:13px}
.toolbar{padding:5px 8px;display:flex;align-items:center;gap:6px;border-bottom:1px solid #1e1e38;flex-shrink:0;flex-wrap:wrap}
.chat-list{flex:1;overflow-y:auto;min-height:0}
input[type=text],select{background:#0a0a1e;border:1px solid #3a3a5e;border-radius:3px;color:#e0e0e0;padding:3px 7px;font-size:12px}
input[type=text]:focus,select:focus{outline:none;border-color:#ffd700}
input[type=text]::placeholder{color:#444}
label{font-size:11px;color:#888;display:inline-flex;align-items:center;gap:3px;white-space:nowrap}
.btn{border:1px solid;border-radius:3px;padding:3px 9px;cursor:pointer;font-size:11px;font-weight:bold;transition:filter 0.1s}
.btn:hover{filter:brightness(1.2)}
.btn-add{background:#1a3020;color:#88dd88;border-color:#3a6040}
.btn-on{background:#143014;color:#55cc55;border-color:#336633}
.btn-off{background:#1e1e1e;color:#666;border-color:#333}
.btn-del{background:#301414;color:#cc5555;border-color:#603333;padding:3px 7px}
.cl{display:flex;gap:5px;padding:2px 8px;align-items:baseline;border-left:3px solid transparent;min-height:20px}
.cl:hover{background:rgba(255,255,255,0.03)}
.cl.hit{border-left-color:#ffd700;background:rgba(255,215,0,0.05)}
.ts{color:#555;font-size:11px;flex-shrink:0;width:56px}
.ch{flex-shrink:0;padding:0 5px;border-radius:3px;font-size:10px;font-weight:bold;min-width:50px;text-align:center;border:1px solid;line-height:18px}
.msg{word-break:break-word;flex:1;line-height:1.5}
.cc-Combat{background:#1e0a0a;color:#ff7777;border-color:#4a1a1a}
.cc-NPCSays{background:#0a0e20;color:#7799ff;border-color:#1a2244}
.cc-General{background:#151526;color:#ccccee;border-color:#2a2a4a}
.cc-Trade{background:#1e1600;color:#ffcc33;border-color:#4a3800}
.cc-Party{background:#0a180a;color:#77dd77;border-color:#1a441a}
.cc-System{background:#0e0e18;color:#888;border-color:#222230}
.cc-x{background:#111118;color:#666;border-color:#222}
.add-form{padding:7px 8px;border-bottom:1px solid #1e1e38;flex-shrink:0}
.fr{display:flex;align-items:center;gap:5px;margin-bottom:4px}
.fr label{width:58px;text-align:right;flex-shrink:0;font-size:12px}
.fr input{flex:1;min-width:0}
.alert-list{flex:1;overflow-y:auto;padding:5px;min-height:0}
.ac{background:#181828;border:1px solid #3a3a5a;border-radius:4px;padding:6px 8px;margin-bottom:4px}
.ac.off{opacity:0.5}
.ac-top{display:flex;align-items:center;gap:5px;margin-bottom:3px}
.ac-name{font-weight:bold;color:#ffd700;flex:1;font-size:13px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.ac-info{font-size:11px;color:#666;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.ac-info span{color:#aaa}
.badge{background:#222;color:#888;font-size:10px;padding:1px 5px;border-radius:8px;border:1px solid #333;flex-shrink:0}
.badge.hit{background:#2a2000;color:#ffcc44;border-color:#554400}
.ac-last{font-size:11px;color:#66aa66;margin-top:3px;font-style:italic;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
::-webkit-scrollbar{width:6px}
::-webkit-scrollbar-track{background:#0e0e1e}
::-webkit-scrollbar-thumb{background:#3a3a5a;border-radius:3px}
::-webkit-scrollbar-thumb:hover{background:#5a5a7a}
</style>
</head>
<body>
<div class="app">
  <div class="hdr">
    <h1>&#128172; ChatWatcher</h1>
    <div class="stat">Lines: <b id="sTotal">0</b></div>
    <div class="stat">Matched: <b id="sMatched">0</b></div>
    <div class="stat" style="margin-left:auto;color:#4a4a6a;font-size:11px" id="sStatus">connecting...</div>
  </div>
  <div class="grid">
    <div class="panel">
      <div class="toolbar">
        <label>Channel</label>
        <select id="chanSel" onchange="applyFilter()" style="min-width:80px">
          <option value="">All</option>
        </select>
        <input type="text" id="searchBox" placeholder="Search chat..." style="flex:1" oninput="applyFilter()">
        <label><input type="checkbox" id="autoScroll" checked> Auto-scroll</label>
      </div>
      <div class="chat-list" id="chatList"></div>
    </div>
    <div class="panel">
      <div class="panel-hdr">Keyword Alerts</div>
      <div class="add-form">
        <div class="fr"><label>Name:</label><input type="text" id="aName" placeholder="My Alert"></div>
        <div class="fr"><label>Pattern:</label><input type="text" id="aPat" placeholder="text to match"></div>
        <div class="fr"><label>Channel:</label><input type="text" id="aChan" placeholder="(optional)"></div>
        <div style="text-align:right;margin-top:5px">
          <button class="btn btn-add" onclick="addAlert()">+ Add Alert</button>
        </div>
      </div>
      <div class="alert-list" id="alertList"></div>
    </div>
  </div>
</div>
<script>
function playBeep(){
  try{
    const ctx=new AudioContext();
    const osc=ctx.createOscillator();
    const gain=ctx.createGain();
    osc.connect(gain);gain.connect(ctx.destination);
    osc.frequency.value=880;osc.type='sine';
    gain.gain.setValueAtTime(0.3,ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.0001,ctx.currentTime+0.3);
    osc.start(ctx.currentTime);osc.stop(ctx.currentTime+0.3);
  }catch(e){}
}

let prevMatchCounts={};
let prevData=null;

function chClass(ch){
  const k=['Combat','NPCSays','General','Trade','Party','System'];
  return k.includes(ch)?'cc-'+ch:'cc-x';
}

function esc(s){
  return(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

function applyFilter(){
  const chanVal=document.getElementById('chanSel').value;
  const q=document.getElementById('searchBox').value.toLowerCase();
  document.querySelectorAll('#chatList .cl').forEach(d=>{
    const ch=d.dataset.ch||'';
    const msg=d.dataset.msg||'';
    d.style.display=((!chanVal||ch===chanVal)&&(!q||msg.toLowerCase().includes(q)||ch.toLowerCase().includes(q)))?'':'none';
  });
}

function renderLines(lines){
  const list=document.getElementById('chatList');
  const atBottom=list.scrollHeight-list.scrollTop-list.clientHeight<60;
  const chanVal=document.getElementById('chanSel').value;
  const q=document.getElementById('searchBox').value.toLowerCase();
  let h='';
  for(const l of lines){
    const cls=chClass(l.channel);
    const hitCls=l.isMatched?' hit':'';
    const hidden=((!chanVal||l.channel===chanVal)&&(!q||(l.content||'').toLowerCase().includes(q)||(l.channel||'').toLowerCase().includes(q)))?'':' style="display:none"';
    h+=`<div class="cl${hitCls}" data-ch="${esc(l.channel)}" data-msg="${esc(l.content)}"${hidden}>`
      +`<span class="ts">${esc(l.time)}</span>`
      +`<span class="ch ${cls}">${esc(l.channel||'?')}</span>`
      +`<span class="msg">${esc(l.content)}</span>`
      +`</div>`;
  }
  list.innerHTML=h;
  if(document.getElementById('autoScroll').checked&&(atBottom||!prevData))
    list.scrollTop=list.scrollHeight;
}

function renderAlerts(alerts){
  const list=document.getElementById('alertList');
  if(!alerts.length){
    list.innerHTML='<div style="color:#444;font-size:12px;padding:12px;text-align:center">No alerts. Add one above.</div>';
    return;
  }
  let h='';
  for(const a of alerts){
    const offCls=a.enabled?'':'off';
    const tCls=a.enabled?'btn-on':'btn-off';
    const bHit=a.matchCount>0?' hit':'';
    h+=`<div class="ac ${offCls}">`
      +`<div class="ac-top">`
      +`<span class="ac-name">${esc(a.name)}</span>`
      +`<span class="badge${bHit}">${a.matchCount}</span>`
      +`<button class="btn ${tCls}" onclick="toggleAlert('${esc(a.id)}')">${a.enabled?'ON':'OFF'}</button>`
      +`<button class="btn btn-del" onclick="delAlert('${esc(a.id)}')">&#10005;</button>`
      +`</div>`
      +`<div class="ac-info">Pattern: <span>${esc(a.pattern)}</span>`
      +(a.channelFilter?` &nbsp;Channel: <span>${esc(a.channelFilter)}</span>`
                       :' &nbsp;Channel: <span>Any</span>')
      +`</div>`;
    if(a.lastMatch) h+=`<div class="ac-last">&#8627; ${esc(a.lastMatch)}</div>`;
    h+=`</div>`;
  }
  list.innerHTML=h;
}

function renderChannels(channels){
  const sel=document.getElementById('chanSel');
  const cur=sel.value;
  let h='<option value="">All</option>';
  for(const c of channels)
    h+=`<option value="${esc(c.name)}">${esc(c.name)} (${c.count})</option>`;
  sel.innerHTML=h;
  if(channels.some(c=>c.name===cur)) sel.value=cur;
}

async function fetchData(){
  try{
    const r=await fetch('/api/data');
    const d=await r.json();
    let beep=false;
    for(const a of(d.alerts||[])){
      if(prevMatchCounts[a.id]!==undefined&&a.matchCount>prevMatchCounts[a.id]) beep=true;
      prevMatchCounts[a.id]=a.matchCount;
    }
    if(beep&&prevData!==null) playBeep();
    document.getElementById('sTotal').textContent=d.stats.total;
    document.getElementById('sMatched').textContent=d.stats.matched;
    const st=document.getElementById('sStatus');
    st.textContent='live';st.style.color='#44aa44';
    renderLines(d.lines||[]);
    renderAlerts(d.alerts||[]);
    renderChannels(d.channels||[]);
    prevData=d;
  }catch(e){
    const st=document.getElementById('sStatus');
    st.textContent='offline';st.style.color='#aa4444';
  }
}

async function addAlert(){
  const name=document.getElementById('aName').value.trim();
  const pat=document.getElementById('aPat').value.trim();
  const chan=document.getElementById('aChan').value.trim();
  if(!name||!pat){alert('Name and Pattern are required.');return;}
  await fetch(`/api/alerts/add?name=${encodeURIComponent(name)}&pattern=${encodeURIComponent(pat)}&channel=${encodeURIComponent(chan)}`,{method:'POST'});
  document.getElementById('aName').value='';
  document.getElementById('aPat').value='';
  document.getElementById('aChan').value='';
  fetchData();
}

async function toggleAlert(id){
  await fetch(`/api/alerts/toggle?id=${encodeURIComponent(id)}`,{method:'POST'});
  fetchData();
}

async function delAlert(id){
  await fetch(`/api/alerts/delete?id=${encodeURIComponent(id)}`,{method:'POST'});
  fetchData();
}

fetchData();
setInterval(fetchData,2000);
</script>
</body>
</html>
""";
}

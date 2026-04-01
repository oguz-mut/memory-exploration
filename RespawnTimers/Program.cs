using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// --- Persistence ---
string storeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectGorgonTools");
string storePath = Path.Combine(storeDir, "respawn-timers.json");
Directory.CreateDirectory(storeDir);

// --- State ---
var _lock = new object();
var timers = new List<RespawnTimer>();

void LoadTimers()
{
    if (!File.Exists(storePath)) return;
    try
    {
        var json = File.ReadAllText(storePath);
        var loaded = JsonSerializer.Deserialize<List<RespawnTimer>>(json);
        if (loaded != null) timers = loaded;
    }
    catch { }
}

void SaveTimers()
{
    var json = JsonSerializer.Serialize(timers, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(storePath, json);
}

LoadTimers();
Console.WriteLine($"Loaded {timers.Count} timers from {storePath}");

// --- Status computation ---
static long? GetRemainingMs(RespawnTimer t)
{
    if (t.LastTriggeredAt == null) return null;
    return (long)(t.LastTriggeredAt.Value.AddMilliseconds(t.CooldownMs) - DateTime.UtcNow).TotalMilliseconds;
}

static string GetStatus(RespawnTimer t, long? remainingMs) =>
    remainingMs == null || remainingMs <= 0 ? "ready"
    : remainingMs <= t.CooldownMs * 0.10 ? "soon"
    : "waiting";

static double? GetPercentComplete(RespawnTimer t)
{
    if (t.LastTriggeredAt == null) return null;
    return Math.Clamp((DateTime.UtcNow - t.LastTriggeredAt.Value).TotalMilliseconds / t.CooldownMs, 0, 1);
}

object TimerToResponse(RespawnTimer t)
{
    var remaining = GetRemainingMs(t);
    return new
    {
        id = t.Id,
        name = t.Name,
        type = t.Type,
        cooldownMs = t.CooldownMs,
        notes = t.Notes,
        lastTriggeredAt = t.LastTriggeredAt,
        remainingMs = remaining,
        status = GetStatus(t, remaining),
        percentComplete = GetPercentComplete(t)
    };
}

// Sort: ready/soon first, then by remainingMs ascending
int SortKey(string status) => status switch { "ready" => 0, "soon" => 1, _ => 2 };

object BuildDataResponse()
{
    lock (_lock)
    {
        var list = timers
            .Select(t => { var rem = GetRemainingMs(t); return (timer: t, rem, status: GetStatus(t, rem)); })
            .OrderBy(x => SortKey(x.status))
            .ThenBy(x => x.rem ?? long.MinValue)
            .Select(x => TimerToResponse(x.timer))
            .ToList();
        return new { timers = list, serverTimeUtc = DateTime.UtcNow };
    }
}

// --- HTTP Server ---
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9883/");
    try { listener.Start(); Console.WriteLine("HTTP server listening on http://localhost:9883/"); }
    catch (Exception ex) { Console.WriteLine($"Failed to start HTTP server: {ex.Message}"); return; }

    var jsonOpts = new JsonSerializerOptions { PropertyNamingPolicy = null };

    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync(); }
        catch (Exception) when (ct.IsCancellationRequested) { break; }
        catch { continue; }

        _ = Task.Run(async () =>
        {
            var req = context.Request;
            var resp = context.Response;
            string path = req.Url?.AbsolutePath ?? "/";

            try
            {
                if (path == "/api/data" && req.HttpMethod == "GET")
                {
                    var data = BuildDataResponse();
                    await WriteJson(resp, data);
                }
                else if (path == "/api/trigger" && req.HttpMethod == "POST")
                {
                    string? id = req.QueryString["id"];
                    lock (_lock)
                    {
                        var t = timers.FirstOrDefault(x => x.Id == id);
                        if (t == null) { resp.StatusCode = 404; resp.OutputStream.Close(); return; }
                        t.LastTriggeredAt = DateTime.UtcNow;
                        SaveTimers();
                        _ = WriteJson(resp, TimerToResponse(t));
                    }
                }
                else if (path == "/api/reset" && req.HttpMethod == "POST")
                {
                    string? id = req.QueryString["id"];
                    lock (_lock)
                    {
                        var t = timers.FirstOrDefault(x => x.Id == id);
                        if (t == null) { resp.StatusCode = 404; resp.OutputStream.Close(); return; }
                        t.LastTriggeredAt = null;
                        SaveTimers();
                        _ = WriteJson(resp, TimerToResponse(t));
                    }
                }
                else if (path == "/api/add" && req.HttpMethod == "POST")
                {
                    using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
                    string body = await sr.ReadToEndAsync(ct);
                    var dto = JsonSerializer.Deserialize<AddTimerDto>(body);
                    if (dto == null || string.IsNullOrWhiteSpace(dto.Name))
                    {
                        resp.StatusCode = 400; resp.OutputStream.Close(); return;
                    }
                    long defaultCooldown = dto.Type switch { "boss" => 3600000, "chest" => 1800000, _ => 600000 };
                    var timer = new RespawnTimer
                    {
                        Id = Guid.NewGuid().ToString(),
                        Name = dto.Name,
                        Type = dto.Type ?? "other",
                        CooldownMs = dto.CooldownMs ?? defaultCooldown,
                        Notes = dto.Notes,
                        LastTriggeredAt = null
                    };
                    lock (_lock)
                    {
                        timers.Add(timer);
                        SaveTimers();
                    }
                    await WriteJson(resp, TimerToResponse(timer));
                }
                else if (path == "/api/delete" && req.HttpMethod == "POST")
                {
                    string? id = req.QueryString["id"];
                    lock (_lock)
                    {
                        int removed = timers.RemoveAll(x => x.Id == id);
                        if (removed == 0) { resp.StatusCode = 404; resp.OutputStream.Close(); return; }
                        SaveTimers();
                    }
                    await WriteJson(resp, new { ok = true });
                }
                else
                {
                    byte[] buf = Encoding.UTF8.GetBytes(HtmlContent.DASHBOARD);
                    resp.ContentType = "text/html; charset=utf-8";
                    resp.ContentLength64 = buf.Length;
                    await resp.OutputStream.WriteAsync(buf, ct);
                    resp.OutputStream.Close();
                }
            }
            catch { try { resp.OutputStream.Close(); } catch { } }
        }, ct);
    }
    listener.Stop();
}

async Task WriteJson(HttpListenerResponse resp, object data)
{
    string json = JsonSerializer.Serialize(data);
    byte[] buf = Encoding.UTF8.GetBytes(json);
    resp.ContentType = "application/json";
    resp.ContentLength64 = buf.Length;
    await resp.OutputStream.WriteAsync(buf);
    resp.OutputStream.Close();
}

// --- Main ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
Console.WriteLine("RespawnTimers starting on http://localhost:9883/");
Console.WriteLine("Press Ctrl+C to stop.");
try { await RunHttpServer(cts.Token); } catch (OperationCanceledException) { }
Console.WriteLine("Shutting down.");

// --- Models ---
class RespawnTimer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "other";
    public long CooldownMs { get; set; } = 600000;
    public string? Notes { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
}

class AddTimerDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("cooldownMs")] public long? CooldownMs { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
}

// --- HTML ---
static partial class HtmlContent
{
    public const string DASHBOARD = """
<!DOCTYPE html>
<html><head>
<meta charset="utf-8"><title>PG Respawn Timers</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#1a1a2e;color:#e0e0e0;font-size:14px}
.container{max-width:900px;margin:0 auto;padding:12px}

.header{background:linear-gradient(180deg,#2a2a4a 0%,#1e1e3a 100%);border:1px solid #4a4a6a;border-radius:6px;padding:10px 16px;margin-bottom:12px}
.header h1{color:#ffd700;font-size:18px;font-weight:bold;text-shadow:0 0 8px rgba(255,215,0,0.3);margin-bottom:10px}

.add-form{display:flex;gap:8px;flex-wrap:wrap;align-items:center}
.add-form input,.add-form select{background:#12122a;border:1px solid #4a4a6a;border-radius:4px;color:#e0e0e0;padding:5px 10px;font-size:13px}
.add-form input::placeholder{color:#555}
.add-form input:focus,.add-form select:focus{outline:none;border-color:#ffd700}
#inputName{width:200px}
#inputCooldown{width:80px}
#inputNotes{width:180px}
.btn{background:linear-gradient(180deg,#4a4a6a 0%,#2e2e4e 100%);color:#ffd700;border:1px solid #6a6a8a;border-radius:4px;padding:6px 14px;cursor:pointer;font-size:13px;font-weight:bold;transition:all 0.15s}
.btn:hover{background:linear-gradient(180deg,#5a5a7a 0%,#3e3e5e 100%);border-color:#8a8aaa}
.btn:active{transform:scale(0.97)}
.btn-red{background:linear-gradient(180deg,#6a1a1a 0%,#4e1a1a 100%);border-color:#8a4a4a;color:#ff8888}
.btn-red:hover{border-color:#aa4444}
.btn-trigger{background:linear-gradient(180deg,#1a4a1a 0%,#0e2e0e 100%);border-color:#4a8a4a;color:#88dd88}
.btn-trigger:hover{border-color:#66aa66}

.section-header{color:#aaa;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:1px;margin:14px 0 6px;padding-bottom:4px;border-bottom:1px solid #333}
.section-ready .section-header{color:#66cc66}
.section-soon .section-header{color:#ffaa44}
.section-waiting .section-header{color:#6688aa}

.timer-card{background:linear-gradient(180deg,#1e1e38 0%,#16162c 100%);border:1px solid #4a4a6a;border-radius:6px;padding:10px 14px;margin-bottom:8px;position:relative}
.timer-card.status-ready{border-color:#3a6a3a;background:linear-gradient(180deg,#1a2e1a 0%,#121e12 100%)}
.timer-card.status-soon{border-color:#6a4a1a;background:linear-gradient(180deg,#2a1e0e 0%,#1a1206 100%)}

.card-top{display:flex;align-items:center;gap:8px;margin-bottom:6px}
.timer-name{font-size:16px;font-weight:bold;color:#e8e8e8;flex:1}
.type-badge{font-size:11px;font-weight:bold;padding:2px 7px;border-radius:3px;text-transform:uppercase}
.type-boss{background:#4a1010;color:#ff6666;border:1px solid #6a2020}
.type-chest{background:#0a1a3a;color:#6699ff;border:1px solid #1a2a5a}
.type-other{background:#2a2a2a;color:#aaaaaa;border:1px solid #3a3a3a}
.status-badge{font-size:11px;font-weight:bold;padding:2px 7px;border-radius:3px}
.badge-ready{background:#1a3a1a;color:#66cc66;border:1px solid #2a5a2a}
.badge-soon{background:#3a2a0a;color:#ffaa44;border:1px solid #5a3a0a}
.badge-waiting{background:#1a1a2a;color:#6688aa;border:1px solid #2a2a4a}

.countdown{font-size:22px;font-weight:bold;color:#ffd700;margin:4px 0}
.countdown.ready{color:#66cc66}
.countdown.soon{color:#ffaa44}

.progress-wrap{background:#0a0a1a;border-radius:3px;height:6px;margin:6px 0;overflow:hidden}
.progress-bar{height:100%;border-radius:3px;transition:width 0.5s}
.progress-ready{background:#66cc66}
.progress-soon{background:#ffaa44}
.progress-waiting{background:#4466aa}

.card-meta{display:flex;align-items:center;gap:10px;margin-top:6px;flex-wrap:wrap}
.cooldown-info{font-size:12px;color:#666}
.notes-text{font-size:12px;color:#888;font-style:italic}
.last-triggered{font-size:11px;color:#555;margin-left:auto}

.card-buttons{display:flex;gap:6px;margin-top:8px;align-items:center}
</style>
</head><body>
<div class="container">
  <div class="header">
    <h1>&#x23F1;&#xFE0F; Respawn Timers</h1>
    <div class="add-form">
      <input type="text" id="inputName" placeholder="Name (e.g. Riger MacBride)">
      <select id="inputType" onchange="onTypeChange()">
        <option value="boss">Boss</option>
        <option value="chest">Chest</option>
        <option value="other">Other</option>
      </select>
      <input type="number" id="inputCooldown" placeholder="min" value="60" min="1">
      <input type="text" id="inputNotes" placeholder="Notes (optional)">
      <button class="btn" onclick="addTimer()">Add Timer</button>
    </div>
  </div>
  <div id="sections"></div>
</div>
<script>
let prevStatuses = {};
let audioCtx = null;

function getAudioCtx(){
  if(!audioCtx) audioCtx=new(window.AudioContext||window.webkitAudioContext)();
  return audioCtx;
}

function playBeep(){
  try{
    const ctx=getAudioCtx();
    const osc=ctx.createOscillator();
    const gain=ctx.createGain();
    osc.connect(gain);gain.connect(ctx.destination);
    osc.type='sine';osc.frequency.value=880;
    gain.gain.setValueAtTime(0.4,ctx.currentTime);
    gain.gain.linearRampToValueAtTime(0,ctx.currentTime+0.5);
    osc.start(ctx.currentTime);osc.stop(ctx.currentTime+0.5);
  }catch(e){}
}

function onTypeChange(){
  const t=document.getElementById('inputType').value;
  const defaults={boss:60,chest:30,other:10};
  document.getElementById('inputCooldown').value=defaults[t]||10;
}

async function addTimer(){
  const name=document.getElementById('inputName').value.trim();
  if(!name){document.getElementById('inputName').focus();return;}
  const type=document.getElementById('inputType').value;
  const mins=parseFloat(document.getElementById('inputCooldown').value)||10;
  const notes=document.getElementById('inputNotes').value.trim();
  const body={name,type,cooldownMs:Math.round(mins*60000)};
  if(notes) body.notes=notes;
  await fetch('/api/add',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)});
  document.getElementById('inputName').value='';
  document.getElementById('inputNotes').value='';
  await fetchData();
}

async function trigger(id){
  await fetch('/api/trigger?id='+encodeURIComponent(id),{method:'POST'});
  await fetchData();
}
async function reset(id){
  await fetch('/api/reset?id='+encodeURIComponent(id),{method:'POST'});
  await fetchData();
}
async function del(id){
  await fetch('/api/delete?id='+encodeURIComponent(id),{method:'POST'});
  await fetchData();
}

function formatMs(ms){
  if(ms<=0)return'READY';
  const h=Math.floor(ms/3600000),m=Math.floor((ms%3600000)/60000),s=Math.floor((ms%60000)/1000);
  if(h>0)return h+'h '+m+'m';
  if(m>0)return m+'m '+s+'s';
  return s+'s';
}

function formatCooldown(ms){
  const h=Math.floor(ms/3600000),m=Math.floor((ms%3600000)/60000);
  if(h>0&&m>0)return h+'h '+m+'m cooldown';
  if(h>0)return h+'h cooldown';
  return m+'m cooldown';
}

function formatTime(iso){
  if(!iso)return'';
  const d=new Date(iso);
  return d.toLocaleTimeString([],{hour:'2-digit',minute:'2-digit'});
}

function esc(s){return(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;')}

async function fetchData(){
  try{
    const r=await fetch('/api/data');
    const data=await r.json();
    render(data);
  }catch(e){console.error(e)}
}

function render(data){
  const newStatuses={};
  for(const t of data.timers) newStatuses[t.id]=t.status;

  // Beep on transition to ready
  for(const id in newStatuses){
    const prev=prevStatuses[id];
    if((prev==='soon'||prev==='waiting')&&newStatuses[id]==='ready') playBeep();
  }
  prevStatuses=newStatuses;

  const groups={ready:[],soon:[],waiting:[]};
  for(const t of data.timers) groups[t.status].push(t);

  let html='';
  if(groups.ready.length){
    html+='<div class="section-ready"><div class="section-header">Ready ('+groups.ready.length+')</div>';
    for(const t of groups.ready) html+=renderCard(t);
    html+='</div>';
  }
  if(groups.soon.length){
    html+='<div class="section-soon"><div class="section-header">Soon ('+groups.soon.length+')</div>';
    for(const t of groups.soon) html+=renderCard(t);
    html+='</div>';
  }
  if(groups.waiting.length){
    html+='<div class="section-waiting"><div class="section-header">Waiting ('+groups.waiting.length+')</div>';
    for(const t of groups.waiting) html+=renderCard(t);
    html+='</div>';
  }
  if(!data.timers.length) html='<div style="color:#555;text-align:center;padding:40px">No timers yet. Add one above.</div>';
  document.getElementById('sections').innerHTML=html;
}

function renderCard(t){
  const typeCss='type-'+t.type;
  const statusCss='status-'+t.status;
  const badgeCss='badge-'+t.status;
  const countdownCss=t.status==='ready'?'ready':t.status==='soon'?'soon':'';
  const cdText=t.status==='ready'?'READY':formatMs(t.remainingMs||0);
  const pct=t.percentComplete!=null?t.percentComplete*100:0;
  const progCss='progress-'+t.status;
  const lastText=t.lastTriggeredAt?'Triggered: '+formatTime(t.lastTriggeredAt):'Never triggered';

  return '<div class="timer-card '+statusCss+'">'
    +'<div class="card-top">'
    +'<span class="timer-name">'+esc(t.name)+'</span>'
    +'<span class="type-badge '+typeCss+'">'+esc(t.type)+'</span>'
    +'<span class="status-badge '+badgeCss+'">'+t.status+'</span>'
    +'</div>'
    +'<div class="countdown '+countdownCss+'">'+cdText+'</div>'
    +'<div class="progress-wrap"><div class="progress-bar '+progCss+'" style="width:'+pct.toFixed(1)+'%"></div></div>'
    +'<div class="card-meta">'
    +'<span class="cooldown-info">('+formatCooldown(t.cooldownMs)+')</span>'
    +(t.notes?'<span class="notes-text">'+esc(t.notes)+'</span>':'')
    +'<span class="last-triggered">'+lastText+'</span>'
    +'</div>'
    +'<div class="card-buttons">'
    +'<button class="btn btn-trigger" onclick="trigger(\''+esc(t.id)+'\')">Triggered Now</button>'
    +'<button class="btn" onclick="reset(\''+esc(t.id)+'\')">Reset</button>'
    +'<button class="btn btn-red" onclick="del(\''+esc(t.id)+'\')" title="Delete">&#x2715;</button>'
    +'</div>'
    +'</div>';
}

fetchData();
setInterval(fetchData,2000);
</script>
</body></html>
""";
}

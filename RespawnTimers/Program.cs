using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

// ── Persistence ───────────────────────────────────────────────────────────────

var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectGorgonTools");
var dataFile = Path.Combine(dataDir, "respawn-timers.json");
Directory.CreateDirectory(dataDir);

var _lock = new object();
var timers = new List<RespawnTimer>();

if (File.Exists(dataFile))
{
    try
    {
        var json = File.ReadAllText(dataFile);
        var loaded = JsonSerializer.Deserialize<List<RespawnTimer>>(json);
        if (loaded != null) timers = loaded;
    }
    catch { }
}

void SaveTimers()
{
    var json = JsonSerializer.Serialize(timers, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(dataFile, json);
}

// ── HTTP Server ───────────────────────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:9883/");
listener.Start();
Console.WriteLine("RespawnTimers running at http://localhost:9883/");

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
};

while (!cts.IsCancellationRequested)
{
    HttpListenerContext ctx;
    try { ctx = await listener.GetContextAsync().WaitAsync(cts.Token); }
    catch (OperationCanceledException) { break; }

    _ = Task.Run(() => HandleRequest(ctx));
}

listener.Stop();

// ── Request Handler ───────────────────────────────────────────────────────────

void HandleRequest(HttpListenerContext ctx)
{
    var req = ctx.Request;
    var res = ctx.Response;

    try
    {
        var path = req.Url?.AbsolutePath ?? "/";
        var query = req.Url?.Query ?? "";
        string? GetQuery(string key)
        {
            var prefix = $"?{key}=";
            var altPrefix = $"&{key}=";
            int idx = query.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) idx = query.IndexOf(altPrefix, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;
            int start = idx + (idx == 0 || query[idx] == '?' ? prefix.Length : altPrefix.Length);
            int end = query.IndexOf('&', start);
            return Uri.UnescapeDataString(end < 0 ? query[start..] : query[start..end]);
        }

        if (path == "/" && req.HttpMethod == "GET")
        {
            Respond(res, 200, "text/html; charset=utf-8", HtmlContent.Page);
            return;
        }

        if (path == "/api/data" && req.HttpMethod == "GET")
        {
            List<TimerDto> dtos;
            lock (_lock)
                dtos = timers.Select(ToDto).OrderBy(t => t.Status == "ready" || t.Status == "soon" ? 0 : 1)
                             .ThenBy(t => t.RemainingMs ?? long.MaxValue).ToList();
            var payload = JsonSerializer.Serialize(new { timers = dtos, serverTimeUtc = DateTime.UtcNow }, jsonOpts);
            Respond(res, 200, "application/json", payload);
            return;
        }

        if (path == "/api/trigger" && req.HttpMethod == "POST")
        {
            var id = GetQuery("id");
            TimerDto? dto = null;
            lock (_lock)
            {
                var t = timers.FirstOrDefault(x => x.Id == id);
                if (t != null) { t.LastTriggeredAt = DateTime.UtcNow; SaveTimers(); dto = ToDto(t); }
            }
            if (dto == null) { Respond(res, 404, "application/json", "{\"error\":\"not found\"}"); return; }
            Respond(res, 200, "application/json", JsonSerializer.Serialize(dto, jsonOpts));
            return;
        }

        if (path == "/api/reset" && req.HttpMethod == "POST")
        {
            var id = GetQuery("id");
            TimerDto? dto = null;
            lock (_lock)
            {
                var t = timers.FirstOrDefault(x => x.Id == id);
                if (t != null) { t.LastTriggeredAt = null; SaveTimers(); dto = ToDto(t); }
            }
            if (dto == null) { Respond(res, 404, "application/json", "{\"error\":\"not found\"}"); return; }
            Respond(res, 200, "application/json", JsonSerializer.Serialize(dto, jsonOpts));
            return;
        }

        if (path == "/api/add" && req.HttpMethod == "POST")
        {
            string body;
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
                body = sr.ReadToEnd();

            AddRequest? addReq = null;
            try { addReq = JsonSerializer.Deserialize<AddRequest>(body, jsonOpts); } catch { }
            if (addReq == null || string.IsNullOrWhiteSpace(addReq.Name))
            {
                Respond(res, 400, "application/json", "{\"error\":\"name required\"}");
                return;
            }

            long defaultCooldown = addReq.Type switch { "boss" => 3600000, "chest" => 1800000, _ => 600000 };
            var timer = new RespawnTimer
            {
                Id = Guid.NewGuid().ToString(),
                Name = addReq.Name,
                Type = addReq.Type ?? "other",
                CooldownMs = addReq.CooldownMs ?? defaultCooldown,
                Notes = addReq.Notes
            };

            TimerDto dto;
            lock (_lock) { timers.Add(timer); SaveTimers(); dto = ToDto(timer); }
            Respond(res, 200, "application/json", JsonSerializer.Serialize(dto, jsonOpts));
            return;
        }

        if (path == "/api/delete" && req.HttpMethod == "POST")
        {
            var id = GetQuery("id");
            bool removed;
            lock (_lock) { removed = timers.RemoveAll(x => x.Id == id) > 0; if (removed) SaveTimers(); }
            if (!removed) { Respond(res, 404, "application/json", "{\"error\":\"not found\"}"); return; }
            Respond(res, 200, "application/json", "{\"ok\":true}");
            return;
        }

        Respond(res, 404, "text/plain", "Not Found");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        try { Respond(ctx.Response, 500, "text/plain", "Internal Server Error"); } catch { }
    }
}

void Respond(HttpListenerResponse res, int status, string contentType, string body)
{
    var bytes = Encoding.UTF8.GetBytes(body);
    res.StatusCode = status;
    res.ContentType = contentType;
    res.ContentLength64 = bytes.Length;
    res.OutputStream.Write(bytes);
    res.OutputStream.Close();
}

TimerDto ToDto(RespawnTimer t)
{
    long? remainingMs = t.LastTriggeredAt == null
        ? null
        : (long)(t.LastTriggeredAt.Value.AddMilliseconds(t.CooldownMs) - DateTime.UtcNow).TotalMilliseconds;

    string status = remainingMs == null || remainingMs <= 0
        ? "ready"
        : remainingMs <= t.CooldownMs * 0.10
            ? "soon"
            : "waiting";

    double? pct = t.LastTriggeredAt == null
        ? null
        : Math.Clamp((DateTime.UtcNow - t.LastTriggeredAt.Value).TotalMilliseconds / t.CooldownMs, 0, 1);

    return new TimerDto(t.Id, t.Name, t.Type, t.CooldownMs, t.Notes, t.LastTriggeredAt, remainingMs, status, pct);
}

// ── Models ────────────────────────────────────────────────────────────────────

class RespawnTimer
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "other";
    public long CooldownMs { get; set; } = 600000;
    public string? Notes { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
}

record TimerDto(
    string Id,
    string Name,
    string Type,
    long CooldownMs,
    string? Notes,
    DateTime? LastTriggeredAt,
    long? RemainingMs,
    string Status,
    double? PercentComplete
);

class AddRequest
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public long? CooldownMs { get; set; }
    public string? Notes { get; set; }
}

// ── HTML ──────────────────────────────────────────────────────────────────────

static partial class HtmlContent
{
    public static readonly string Page = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Respawn Timers</title>
<style>
  *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
  body { background: #1a1a2e; color: #e0e0e0; font-family: 'Segoe UI', sans-serif; min-height: 100vh; }
  h1 { color: #ffd700; font-size: 1.6rem; margin-bottom: 1rem; }
  h2 { color: #aaa; font-size: 1rem; text-transform: uppercase; letter-spacing: 2px; margin: 1.2rem 0 0.6rem; border-bottom: 1px solid #4a4a6a; padding-bottom: 4px; }
  .container { max-width: 900px; margin: 0 auto; padding: 1.5rem; }
  .add-form { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; background: #16213e; border: 1px solid #4a4a6a; border-radius: 8px; padding: 12px; margin-bottom: 1rem; }
  .add-form input, .add-form select { background: #0f3460; color: #e0e0e0; border: 1px solid #4a4a6a; border-radius: 4px; padding: 6px 10px; font-size: 0.9rem; }
  .add-form input::placeholder { color: #888; }
  .add-form input[name=name] { flex: 2; min-width: 140px; }
  .add-form input[name=cooldown] { width: 90px; }
  .add-form input[name=notes] { flex: 2; min-width: 140px; }
  .btn { padding: 6px 14px; border-radius: 4px; border: none; cursor: pointer; font-size: 0.88rem; font-weight: 600; transition: opacity 0.15s; }
  .btn:hover { opacity: 0.85; }
  .btn-add { background: #ffd700; color: #1a1a2e; }
  .btn-trigger { background: #2ecc71; color: #111; }
  .btn-reset { background: #4a4a6a; color: #e0e0e0; }
  .btn-delete { background: #c0392b; color: #fff; padding: 4px 10px; }
  .cards { display: flex; flex-direction: column; gap: 10px; }
  .card { background: #16213e; border: 1px solid #4a4a6a; border-radius: 8px; padding: 12px 14px; }
  .card-header { display: flex; align-items: center; gap: 8px; margin-bottom: 6px; }
  .card-name { font-size: 1.1rem; font-weight: 700; color: #ffd700; }
  .badge { border-radius: 4px; padding: 2px 7px; font-size: 0.75rem; font-weight: 700; text-transform: uppercase; }
  .badge-boss { background: #8b0000; color: #ffcccc; }
  .badge-chest { background: #003580; color: #aaccff; }
  .badge-other { background: #3a3a3a; color: #ccc; }
  .badge-ready { background: #1a6b2a; color: #7fff7f; }
  .badge-soon { background: #7b4e00; color: #ffd080; }
  .badge-waiting { background: #2a2a3a; color: #999; }
  .card-notes { font-size: 0.8rem; color: #888; margin-bottom: 6px; }
  .countdown { font-size: 1.3rem; font-weight: 700; margin-bottom: 6px; }
  .countdown.ready { color: #2ecc71; }
  .countdown.soon { color: #f39c12; }
  .countdown.waiting { color: #888; }
  .progress-wrap { background: #0f3460; border-radius: 4px; height: 6px; margin-bottom: 8px; overflow: hidden; }
  .progress-bar { height: 100%; border-radius: 4px; transition: width 1s linear; }
  .progress-bar.ready { background: #2ecc71; }
  .progress-bar.soon { background: #f39c12; }
  .progress-bar.waiting { background: #4a4a6a; }
  .card-footer { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .cooldown-info { font-size: 0.78rem; color: #666; margin-left: auto; }
  .last-triggered { font-size: 0.75rem; color: #666; margin-top: 4px; }
  label { font-size: 0.85rem; color: #aaa; white-space: nowrap; }
</style>
</head>
<body>
<div class="container">
  <h1>Respawn Timers</h1>

  <form class="add-form" id="addForm" onsubmit="addTimer(event)">
    <input name="name" placeholder="Name (e.g. Riger MacBride)" required>
    <select name="type" onchange="onTypeChange(this)">
      <option value="boss">Boss</option>
      <option value="chest">Chest</option>
      <option value="other">Other</option>
    </select>
    <label>Cooldown (min):</label>
    <input name="cooldown" type="number" min="1" value="60">
    <input name="notes" placeholder="Notes (optional)">
    <button class="btn btn-add" type="submit">Add</button>
  </form>

  <div id="sections"></div>
</div>

<script>
const DEFAULTS = { boss: 60, chest: 30, other: 10 };
let prevStatuses = {};

function onTypeChange(sel) {
  const form = sel.closest('form');
  form.querySelector('[name=cooldown]').value = DEFAULTS[sel.value] ?? 10;
}

function formatMs(ms) {
  if (ms <= 0) return 'READY';
  const h = Math.floor(ms / 3600000), m = Math.floor((ms % 3600000) / 60000), s = Math.floor((ms % 60000) / 1000);
  if (h > 0) return h + 'h ' + m + 'm';
  if (m > 0) return m + 'm ' + s + 's';
  return s + 's';
}

function fmtTime(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

function playBeep() {
  try {
    const ctx = new (window.AudioContext || window.webkitAudioContext)();
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.connect(gain); gain.connect(ctx.destination);
    osc.type = 'sine'; osc.frequency.value = 880;
    gain.gain.setValueAtTime(0.3, ctx.currentTime);
    gain.gain.linearRampToValueAtTime(0, ctx.currentTime + 0.5);
    osc.start(ctx.currentTime); osc.stop(ctx.currentTime + 0.5);
  } catch(e) {}
}

function renderCard(t) {
  const pct = t.percentComplete == null ? 1 : t.percentComplete;
  const barWidth = Math.round(pct * 100);
  const cd = Math.round(t.cooldownMs / 60000);
  const cdLabel = cd >= 60 ? (cd / 60) + 'h' : cd + 'm';
  return `<div class="card" data-id="${t.id}">
    <div class="card-header">
      <span class="card-name">${escHtml(t.name)}</span>
      <span class="badge badge-${t.type}">${t.type}</span>
      <span class="badge badge-${t.status}">${t.status}</span>
    </div>
    ${t.notes ? `<div class="card-notes">${escHtml(t.notes)}</div>` : ''}
    <div class="countdown ${t.status}">${t.status === 'ready' ? 'READY' : formatMs(t.remainingMs)}</div>
    <div class="progress-wrap"><div class="progress-bar ${t.status}" style="width:${barWidth}%"></div></div>
    <div class="card-footer">
      <button class="btn btn-trigger" onclick="trigger('${t.id}')">Triggered Now</button>
      <button class="btn btn-reset" onclick="reset('${t.id}')">Reset</button>
      <button class="btn btn-delete" onclick="del('${t.id}')">&times;</button>
      <span class="cooldown-info">(${cdLabel} cooldown)</span>
    </div>
    ${t.lastTriggeredAt ? `<div class="last-triggered">Triggered: ${fmtTime(t.lastTriggeredAt)}</div>` : ''}
  </div>`;
}

function escHtml(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}

async function fetchData() {
  try {
    const r = await fetch('/api/data');
    const data = await r.json();
    const { timers } = data;

    // Beep detection
    for (const t of timers) {
      const prev = prevStatuses[t.id];
      if (prev && prev !== 'ready' && t.status === 'ready') playBeep();
    }
    prevStatuses = Object.fromEntries(timers.map(t => [t.id, t.status]));

    const ready = timers.filter(t => t.status === 'ready');
    const soon  = timers.filter(t => t.status === 'soon');
    const waiting = timers.filter(t => t.status === 'waiting');

    let html = '';
    if (ready.length)   html += `<h2>Ready (${ready.length})</h2><div class="cards">${ready.map(renderCard).join('')}</div>`;
    if (soon.length)    html += `<h2>Soon (${soon.length})</h2><div class="cards">${soon.map(renderCard).join('')}</div>`;
    if (waiting.length) html += `<h2>Waiting (${waiting.length})</h2><div class="cards">${waiting.map(renderCard).join('')}</div>`;
    if (!timers.length) html = '<p style="color:#666;margin-top:1rem">No timers yet. Add one above.</p>';

    document.getElementById('sections').innerHTML = html;
  } catch(e) { console.error(e); }
}

async function trigger(id) {
  await fetch('/api/trigger?id=' + id, { method: 'POST' });
  fetchData();
}

async function reset(id) {
  await fetch('/api/reset?id=' + id, { method: 'POST' });
  fetchData();
}

async function del(id) {
  await fetch('/api/delete?id=' + id, { method: 'POST' });
  fetchData();
}

async function addTimer(e) {
  e.preventDefault();
  const f = e.target;
  const name = f.querySelector('[name=name]').value.trim();
  const type = f.querySelector('[name=type]').value;
  const cooldownMin = parseFloat(f.querySelector('[name=cooldown]').value) || DEFAULTS[type];
  const notes = f.querySelector('[name=notes]').value.trim();
  await fetch('/api/add', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, type, cooldownMs: Math.round(cooldownMin * 60000), notes: notes || undefined })
  });
  f.querySelector('[name=name]').value = '';
  f.querySelector('[name=notes]').value = '';
  fetchData();
}

fetchData();
setInterval(fetchData, 2000);
</script>
</body>
</html>
""";
}

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// --- Configuration ---
string dataDir = @"C:\Users\oguzb\source\memory-exploration";
string logDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon";
string npcsPath = Path.Combine(dataDir, "npcs.json");
string logPath = Path.Combine(logDir, "Player.log");
string logPathPrev = Path.Combine(logDir, "Player-prev.log");

if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
    logPath = logPathPrev;
if (!File.Exists(logPath))
{
    Console.WriteLine("No log file found. Exiting.");
    return;
}

// --- Load npcs.json ---
Console.WriteLine("Loading npcs.json...");
var npcsDoc = JsonDocument.Parse(File.ReadAllText(npcsPath));
var allVendors = new List<NpcVendorDef>();

foreach (var npcProp in npcsDoc.RootElement.EnumerateObject())
{
    string key = npcProp.Name;
    var npcVal = npcProp.Value;

    if (!npcVal.TryGetProperty("Services", out var servicesEl)) continue;

    List<CapTier>? caps = null;
    foreach (var svc in servicesEl.EnumerateArray())
    {
        if (!svc.TryGetProperty("Type", out var typeEl) || typeEl.GetString() != "Store") continue;
        caps = new List<CapTier>();
        if (svc.TryGetProperty("CapIncreases", out var capsEl))
        {
            foreach (var capEl in capsEl.EnumerateArray())
            {
                string capStr = capEl.GetString()!;
                var parts = capStr.Split(':');
                if (parts.Length < 2) continue;
                string favorLevel = parts[0];
                if (!int.TryParse(parts[1], out int cap)) continue;
                string[] buyTypes = parts.Length > 2
                    ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    : Array.Empty<string>();
                caps.Add(new CapTier(favorLevel, cap, buyTypes));
            }
        }
        break;
    }

    if (caps == null) continue;

    string displayName = npcVal.TryGetProperty("Name", out var nameEl)
        ? nameEl.GetString()!
        : key.StartsWith("NPC_") ? key[4..] : key;
    string area = npcVal.TryGetProperty("AreaName", out var areaEl)
        ? areaEl.GetString()!
        : "";

    allVendors.Add(new NpcVendorDef(key, displayName, area, caps));
}

Console.WriteLine($"Loaded {allVendors.Count} vendor NPCs with store services.");

// --- Shared state ---
var entityToNpc = new ConcurrentDictionary<int, string>();
var vendorStates = new ConcurrentDictionary<string, VendorState>(StringComparer.OrdinalIgnoreCase);
string? currentNpcKey = null;

// --- Regex patterns ---
var startInteractionRx = new Regex(@"ProcessStartInteraction\((\d+),\s*7,\s*0,\s*False,\s*""([^""]+)""\)", RegexOptions.Compiled);
var vendorGoldRx = new Regex(@"ProcessVendorUpdateAvailableGold\((\d+),\s*(\d+)\)", RegexOptions.Compiled);
var favorDeltaRx = new Regex(@"ProcessDeltaFavor\(0,\s*""([^""]+)"",\s*(-?[\d.]+),\s*True\)", RegexOptions.Compiled);
var vendorScreenRx = new Regex(@"ProcessVendorScreen\(", RegexOptions.Compiled);

void ProcessLine(string line)
{
    var m = startInteractionRx.Match(line);
    if (m.Success)
    {
        int entityId = int.Parse(m.Groups[1].Value);
        string npcName = m.Groups[2].Value;
        entityToNpc[entityId] = npcName;
        currentNpcKey = npcName;
        return;
    }

    m = vendorGoldRx.Match(line);
    if (m.Success)
    {
        int entityId = int.Parse(m.Groups[1].Value);
        int gold = int.Parse(m.Groups[2].Value);
        if (entityToNpc.TryGetValue(entityId, out string? npcKey))
        {
            var state = vendorStates.GetOrAdd(npcKey, k => new VendorState(k));
            state.AvailableGold = gold;
        }
        return;
    }

    m = favorDeltaRx.Match(line);
    if (m.Success)
    {
        string npcName = m.Groups[1].Value;
        if (double.TryParse(m.Groups[2].Value, out double delta))
        {
            var state = vendorStates.GetOrAdd(npcName, k => new VendorState(k));
            state.FavorDelta += delta;
        }
        return;
    }

    m = vendorScreenRx.Match(line);
    if (m.Success && currentNpcKey != null)
    {
        var state = vendorStates.GetOrAdd(currentNpcKey, k => new VendorState(k));
        state.LastSeen = DateTime.UtcNow;
        state.IsOpen = true;
    }
}

// --- Build API response ---
object GetApiData()
{
    var vendorDefMap = allVendors.ToDictionary(v => v.Key, StringComparer.OrdinalIgnoreCase);

    var result = new List<object>();

    foreach (var def in allVendors)
    {
        vendorStates.TryGetValue(def.Key, out var state);
        CapTier? activeCap = null;
        if (state?.AvailableGold is int gold && def.Caps.Count > 0)
        {
            // smallest cap tier where cap >= availableGold (lowest plausible active tier)
            activeCap = def.Caps
                .Where(c => c.Cap >= gold)
                .OrderBy(c => c.Cap)
                .FirstOrDefault() ?? def.Caps[^1];
        }

        result.Add(new
        {
            key = def.Key,
            name = def.DisplayName,
            area = def.Area,
            availableGold = state?.AvailableGold,
            favorDelta = state?.FavorDelta ?? 0.0,
            lastSeen = state?.LastSeen,
            isOpen = state?.IsOpen ?? false,
            caps = def.Caps.Select(c => new { favorLevel = c.FavorLevel, cap = c.Cap, buyTypes = c.BuyTypes }),
            activeCap = activeCap == null ? null : (object)new { favorLevel = activeCap.FavorLevel, cap = activeCap.Cap }
        });
    }

    // Include state-only entries not in npcs.json
    foreach (var kvp in vendorStates)
    {
        if (vendorDefMap.ContainsKey(kvp.Key)) continue;
        var state = kvp.Value;
        result.Add(new
        {
            key = kvp.Key,
            name = kvp.Key.StartsWith("NPC_") ? kvp.Key[4..] : kvp.Key,
            area = "",
            availableGold = state.AvailableGold,
            favorDelta = state.FavorDelta,
            lastSeen = state.LastSeen,
            isOpen = state.IsOpen,
            caps = Array.Empty<object>(),
            activeCap = (object?)null
        });
    }

    // Sort: recently seen first (nulls last), then alphabetical
    result.Sort((a, b) =>
    {
        dynamic da = a, db = b;
        DateTime? la = da.lastSeen, lb = db.lastSeen;
        if (la.HasValue && lb.HasValue) return lb.Value.CompareTo(la.Value);
        if (la.HasValue) return -1;
        if (lb.HasValue) return 1;
        return string.Compare((string)da.name, (string)db.name, StringComparison.OrdinalIgnoreCase);
    });

    return new { vendors = result };
}

// --- HTTP Server ---
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9882/");
    try { listener.Start(); Console.WriteLine("HTTP server listening on http://localhost:9882/"); }
    catch (Exception ex) { Console.WriteLine($"Failed to start HTTP server: {ex.Message}"); return; }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await listener.GetContextAsync();
            var response = context.Response;
            string path = context.Request.Url?.AbsolutePath ?? "/";

            if (path == "/api/data")
            {
                string json = JsonSerializer.Serialize(GetApiData());
                byte[] buf = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }
            else if (path == "/api/favor" && context.Request.HttpMethod == "POST")
            {
                string? npc = context.Request.QueryString["npc"];
                string? deltaStr = context.Request.QueryString["delta"];
                if (npc != null && double.TryParse(deltaStr, out double delta))
                {
                    var state = vendorStates.GetOrAdd(npc, k => new VendorState(k));
                    state.FavorDelta = delta;
                    string json = JsonSerializer.Serialize(new { ok = true, npc, favorDelta = state.FavorDelta });
                    byte[] buf = Encoding.UTF8.GetBytes(json);
                    response.ContentType = "application/json";
                    response.ContentLength64 = buf.Length;
                    await response.OutputStream.WriteAsync(buf, ct);
                }
                else
                {
                    response.StatusCode = 400;
                }
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

// --- Log tailing ---
async Task TailLog(string path, CancellationToken ct)
{
    Console.WriteLine($"Tailing log: {path}");
    using (var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
    {
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
            ProcessLine(line);
    }

    Console.WriteLine("Initial state loaded. Watching for new log entries...");

    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);

    while (!ct.IsCancellationRequested)
    {
        string? newLine = await reader.ReadLineAsync(ct);
        if (newLine != null)
            ProcessLine(newLine);
        else
            await Task.Delay(500, ct);
    }
}

// --- Main ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var httpTask = RunHttpServer(cts.Token);
var tailTask = TailLog(logPath, cts.Token);
Console.WriteLine("Press Ctrl+C to stop.");
try { await Task.WhenAll(httpTask, tailTask); } catch (OperationCanceledException) { }
Console.WriteLine("Shutting down.");

// --- Data classes ---
record CapTier(string FavorLevel, int Cap, string[] BuyTypes);
record NpcVendorDef(string Key, string DisplayName, string Area, List<CapTier> Caps);

class VendorState(string npcKey)
{
    public string NpcKey { get; } = npcKey;
    public int? AvailableGold { get; set; }
    public double FavorDelta { get; set; }
    public DateTime? LastSeen { get; set; }
    public bool IsOpen { get; set; }
}

// --- HTML ---
static partial class HtmlContent
{
    public const string DASHBOARD = """
<!DOCTYPE html>
<html><head>
<meta charset="utf-8"><title>PG VendorTracker</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#1a1a2e;color:#e0e0e0;font-size:14px}
.container{max-width:1200px;margin:0 auto;padding:12px}
.header{background:linear-gradient(180deg,#2a2a4a 0%,#1e1e3a 100%);border:1px solid #4a4a6a;border-radius:6px;padding:10px 16px;margin-bottom:12px;display:flex;align-items:center;gap:14px;flex-wrap:wrap}
.header h1{color:#ffd700;font-size:18px;font-weight:bold;text-shadow:0 0 8px rgba(255,215,0,0.3)}
.stat{background:#12122a;padding:4px 10px;border-radius:4px;font-size:13px;border:1px solid #333358}
.stat-label{color:#888;font-size:11px}
.stat-value{color:#fff;font-weight:bold}
.toolbar{background:#22223a;border:1px solid #4a4a6a;border-radius:6px;padding:8px 12px;margin-bottom:12px;display:flex;align-items:center;gap:8px}
.search{background:#12122a;border:1px solid #4a4a6a;border-radius:4px;color:#e0e0e0;padding:6px 12px;font-size:13px;width:260px}
.search:focus{outline:none;border-color:#ffd700}
.search::placeholder{color:#555}
.section-label{color:#ffd700;font-size:13px;font-weight:bold;margin:0 0 8px 2px;padding-bottom:4px;border-bottom:1px solid #2a2a4a}
.section{margin-bottom:18px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(310px,1fr));gap:10px;margin-top:8px}
.card{background:#1e1e38;border:1px solid #3a3a5a;border-radius:6px;padding:12px;transition:border-color 0.15s}
.card:hover{border-color:#5a5a7a}
.card.recent{border-color:#3a5a3a}
.card.open{border-color:#ffd700;box-shadow:0 0 8px rgba(255,215,0,0.12)}
.card-header{display:flex;align-items:baseline;gap:8px;margin-bottom:8px;flex-wrap:wrap}
.card-name{color:#ffd700;font-size:15px;font-weight:bold;flex:0 0 auto}
.area-badge{background:#12122a;color:#888;font-size:11px;padding:2px 7px;border-radius:10px;border:1px solid #2a2a4a;flex:0 0 auto}
.last-seen{color:#666;font-size:11px;margin-left:auto;flex:0 0 auto}
.open-dot{display:inline-block;width:7px;height:7px;border-radius:50%;background:#ffd700;margin-right:4px;vertical-align:middle;box-shadow:0 0 4px #ffd700}
.gold-section{margin:7px 0 4px}
.gold-label{color:#aaa;font-size:12px;margin-bottom:3px;display:flex;justify-content:space-between}
.gold-val{color:#ffd700;font-weight:bold}
.progress-bar{height:7px;background:#0a0a1a;border-radius:4px;overflow:hidden;border:1px solid #1a1a2e}
.progress-fill{height:100%;border-radius:3px;transition:width 0.4s}
.prog-green{background:linear-gradient(90deg,#2d8a2d,#44bb44)}
.prog-yellow{background:linear-gradient(90deg,#8a7000,#ddaa00)}
.prog-red{background:linear-gradient(90deg,#8a1a1a,#dd4444)}
.spent-label{color:#555;font-size:11px;text-align:right;margin-top:2px}
.caps-table{width:100%;border-collapse:collapse;font-size:11px;margin-top:8px}
.caps-table th{color:#666;text-align:left;padding:2px 5px;border-bottom:1px solid #2a2a4a;font-weight:normal}
.caps-table td{padding:3px 5px;color:#bbb;border-bottom:1px solid #1a1a2e}
.caps-table tr:last-child td{border-bottom:none}
.caps-table tr.active-row td{color:#ffd700;background:rgba(255,215,0,0.06)}
.buytypes{color:#777;font-size:10px}
.favor-row{display:flex;align-items:center;gap:6px;margin-top:7px;font-size:12px}
.favor-label{color:#888}
.favor-pos{color:#44bb44;font-weight:bold}
.favor-neg{color:#dd4444;font-weight:bold}
.no-caps{color:#444;font-size:11px;font-style:italic;margin-top:4px}
</style>
</head><body>
<div class="container">
  <div class="header">
    <h1>&#x1f4b0; VendorTracker</h1>
    <div class="stat"><span class="stat-label">Vendors</span><br><span class="stat-value" id="vendorCount">0</span></div>
    <div class="stat"><span class="stat-label">Recently Seen</span><br><span class="stat-value" id="recentCount">0</span></div>
  </div>
  <div class="toolbar">
    <input type="text" class="search" id="searchBox" placeholder="Search vendors by name..." oninput="render()">
  </div>
  <div id="recentSection"></div>
  <div id="allSection"></div>
</div>
<script>
let allData = null;

async function fetchData() {
    try {
        const r = await fetch('/api/data');
        allData = await r.json();
        render();
    } catch(e) { console.error(e); }
}

function timeAgo(iso) {
    if (!iso) return '';
    const diff = (Date.now() - new Date(iso).getTime()) / 1000;
    if (diff < 60) return Math.floor(diff) + 's ago';
    if (diff < 3600) return Math.floor(diff / 60) + 'm ago';
    return Math.floor(diff / 3600) + 'h ago';
}

function esc(s) {
    return (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function buildCard(v) {
    const areaShort = (v.area || '').replace(/^Area/, '');
    const lastSeenStr = v.lastSeen ? timeAgo(v.lastSeen) : '';
    const isRecent = v.lastSeen && (Date.now() - new Date(v.lastSeen).getTime()) < 600000;
    const cardClass = 'card' + (v.isOpen ? ' open' : (isRecent ? ' recent' : ''));

    let h = '<div class="' + cardClass + '">';

    h += '<div class="card-header">';
    if (v.isOpen) h += '<span class="open-dot"></span>';
    h += '<span class="card-name">' + esc(v.name) + '</span>';
    if (areaShort) h += '<span class="area-badge">' + esc(areaShort) + '</span>';
    if (lastSeenStr) h += '<span class="last-seen">' + lastSeenStr + '</span>';
    h += '</div>';

    if (v.availableGold != null && v.caps && v.caps.length > 0) {
        const refCap = v.activeCap ? v.activeCap.cap : v.caps[v.caps.length - 1].cap;
        const pct = Math.max(0, Math.min(100, (v.availableGold / refCap) * 100));
        const spent = refCap - v.availableGold;
        const progClass = pct > 50 ? 'prog-green' : pct > 20 ? 'prog-yellow' : 'prog-red';
        h += '<div class="gold-section">';
        h += '<div class="gold-label"><span>Available Gold</span><span class="gold-val">' + v.availableGold.toLocaleString() + ' / ' + refCap.toLocaleString() + '</span></div>';
        h += '<div class="progress-bar"><div class="progress-fill ' + progClass + '" style="width:' + pct.toFixed(1) + '%"></div></div>';
        if (spent > 0) h += '<div class="spent-label">spent: ' + spent.toLocaleString() + '</div>';
        h += '</div>';
    } else if (v.availableGold != null) {
        h += '<div class="gold-section"><div class="gold-label"><span>Available Gold</span><span class="gold-val">' + v.availableGold.toLocaleString() + '</span></div></div>';
    }

    if (v.caps && v.caps.length > 0) {
        h += '<table class="caps-table"><thead><tr><th>Favor</th><th>Cap</th><th>Buys</th></tr></thead><tbody>';
        for (const c of v.caps) {
            const isActive = v.activeCap && v.activeCap.favorLevel === c.favorLevel;
            h += '<tr' + (isActive ? ' class="active-row"' : '') + '>';
            h += '<td>' + esc(c.favorLevel) + '</td>';
            h += '<td>' + c.cap.toLocaleString() + '</td>';
            h += '<td class="buytypes">' + (c.buyTypes && c.buyTypes.length > 0 ? esc(c.buyTypes.join(', ')) : '<span style="color:#555">All</span>') + '</td>';
            h += '</tr>';
        }
        h += '</tbody></table>';
    } else {
        h += '<div class="no-caps">No cap data</div>';
    }

    if (v.favorDelta !== 0) {
        const cls = v.favorDelta > 0 ? 'favor-pos' : 'favor-neg';
        const sign = v.favorDelta > 0 ? '+' : '';
        h += '<div class="favor-row"><span class="favor-label">Favor delta:</span><span class="' + cls + '">' + sign + Math.round(v.favorDelta) + '</span></div>';
    }

    h += '</div>';
    return h;
}

function render() {
    if (!allData) return;
    const search = document.getElementById('searchBox').value.toLowerCase();
    const vendors = (allData.vendors || []).filter(v =>
        !search || v.name.toLowerCase().includes(search) || (v.area || '').toLowerCase().includes(search)
    );
    const now = Date.now();
    const recent = vendors.filter(v => v.lastSeen && (now - new Date(v.lastSeen).getTime()) < 600000);
    const rest = vendors.filter(v => !v.lastSeen || (now - new Date(v.lastSeen).getTime()) >= 600000);

    document.getElementById('vendorCount').textContent = vendors.length;
    document.getElementById('recentCount').textContent = recent.length;

    let recentHtml = '';
    if (recent.length > 0) {
        recentHtml += '<div class="section"><div class="section-label">Recently Visited (last 10 min)</div><div class="grid">';
        for (const v of recent) recentHtml += buildCard(v);
        recentHtml += '</div></div>';
    }
    document.getElementById('recentSection').innerHTML = recentHtml;

    let allHtml = '';
    if (rest.length > 0) {
        allHtml += '<div class="section"><div class="section-label">All Vendors</div><div class="grid">';
        for (const v of rest) allHtml += buildCard(v);
        allHtml += '</div></div>';
    }
    document.getElementById('allSection').innerHTML = allHtml;
}

fetchData();
setInterval(fetchData, 2000);
</script>
</body></html>
""";
}

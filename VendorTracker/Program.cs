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
var allVendors = new List<NpcVendorDef>();
{
    using var npcsDoc = JsonDocument.Parse(File.ReadAllText(npcsPath));
    foreach (var prop in npcsDoc.RootElement.EnumerateObject())
    {
        string npcKey = prop.Name;
        var npcVal = prop.Value;
        if (!npcVal.TryGetProperty("Services", out var services)) continue;

        List<CapTier>? caps = null;
        foreach (var svc in services.EnumerateArray())
        {
            if (!svc.TryGetProperty("Type", out var typeEl) || typeEl.GetString() != "Store") continue;
            if (!svc.TryGetProperty("CapIncreases", out var capArr)) continue;
            caps = [];
            foreach (var capEl in capArr.EnumerateArray())
            {
                var s = capEl.GetString() ?? "";
                var parts = s.Split(':');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int cap)) continue;
                string[] buyTypes = parts.Length >= 3
                    ? parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries)
                    : [];
                caps.Add(new CapTier(parts[0], cap, buyTypes));
            }
            break;
        }

        if (caps is not { Count: > 0 }) continue;

        string displayName = npcVal.TryGetProperty("Name", out var nameEl)
            ? nameEl.GetString() ?? npcKey : npcKey;
        string area = npcVal.TryGetProperty("AreaFriendlyName", out var areaEl)
            ? areaEl.GetString() ?? "" : "";
        allVendors.Add(new NpcVendorDef(npcKey, displayName, area, caps));
    }
}
Console.WriteLine($"Loaded {allVendors.Count} vendor NPCs with cap data.");

// --- State ---
var entityToNpc = new ConcurrentDictionary<int, string>();
var vendorStates = new ConcurrentDictionary<string, VendorState>(StringComparer.OrdinalIgnoreCase);
string? currentNpcKey = null;

// --- Regex ---
var startInteractionRx = new Regex(@"ProcessStartInteraction\((\d+),\s*7,\s*0,\s*False,\s*""([^""]+)""\)");
var vendorGoldRx = new Regex(@"ProcessVendorUpdateAvailableGold\((\d+),\s*(\d+)\)");
var deltaFavorRx = new Regex(@"ProcessDeltaFavor\(0,\s*""([^""]+)"",\s*(-?[\d.]+),\s*True\)");
var vendorScreenRx = new Regex(@"ProcessVendorScreen\(");

void ProcessLine(string line)
{
    var m = startInteractionRx.Match(line);
    if (m.Success)
    {
        if (int.TryParse(m.Groups[1].Value, out int entityId))
        {
            string npcName = m.Groups[2].Value;
            entityToNpc[entityId] = npcName;
            currentNpcKey = npcName;
        }
        return;
    }

    m = vendorGoldRx.Match(line);
    if (m.Success)
    {
        if (int.TryParse(m.Groups[1].Value, out int entityId) &&
            int.TryParse(m.Groups[2].Value, out int gold) &&
            entityToNpc.TryGetValue(entityId, out string? npcKey))
        {
            var state = vendorStates.GetOrAdd(npcKey, k => new VendorState { NpcKey = k });
            state.AvailableGold = gold;
            state.LastSeen = DateTime.UtcNow;
        }
        return;
    }

    m = deltaFavorRx.Match(line);
    if (m.Success)
    {
        if (double.TryParse(m.Groups[2].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double delta))
        {
            string npcKey = m.Groups[1].Value;
            var state = vendorStates.GetOrAdd(npcKey, k => new VendorState { NpcKey = k });
            state.FavorDelta += delta;
        }
        return;
    }

    if (vendorScreenRx.IsMatch(line) && currentNpcKey != null)
    {
        var state = vendorStates.GetOrAdd(currentNpcKey, k => new VendorState { NpcKey = k });
        state.IsOpen = true;
        state.LastSeen ??= DateTime.UtcNow;
    }
}

// --- Helpers ---
object? ComputeActiveCap(List<CapTier> caps, int? availableGold)
{
    if (availableGold is not int gold || caps.Count == 0) return null;
    CapTier? best = null, maxTier = null;
    foreach (var t in caps)
    {
        if (maxTier == null || t.Cap > maxTier.Cap) maxTier = t;
        if (t.Cap >= gold && (best == null || t.Cap < best.Cap)) best = t;
    }
    best ??= maxTier;
    return best is null ? null : (object?)new { favorLevel = best.FavorLevel, cap = best.Cap };
}

object BuildVendorDto(string key, string name, string area, List<CapTier> caps, VendorState? state, object? activeCap)
    => new
    {
        key,
        name,
        area,
        availableGold = state?.AvailableGold,
        favorDelta = state?.FavorDelta ?? 0.0,
        lastSeen = state?.LastSeen?.ToString("o"),
        isOpen = state?.IsOpen ?? false,
        caps = caps.Select(c => new { favorLevel = c.FavorLevel, cap = c.Cap, buyTypes = c.BuyTypes }).ToArray(),
        activeCap
    };

// --- API ---
object GetApiData()
{
    var defMap = allVendors.ToDictionary(v => v.Key, v => v, StringComparer.OrdinalIgnoreCase);
    var result = new List<object>(allVendors.Count + vendorStates.Count);

    foreach (var def in allVendors)
    {
        vendorStates.TryGetValue(def.Key, out var state);
        result.Add(BuildVendorDto(def.Key, def.DisplayName, def.Area, def.Caps, state,
            ComputeActiveCap(def.Caps, state?.AvailableGold)));
    }

    foreach (var kvp in vendorStates)
    {
        if (defMap.ContainsKey(kvp.Key)) continue;
        string name = kvp.Key.StartsWith("NPC_", StringComparison.Ordinal) ? kvp.Key[4..] : kvp.Key;
        result.Add(BuildVendorDto(kvp.Key, name, "", new List<CapTier>(), kvp.Value, null));
    }

    result.Sort((a, b) =>
    {
        dynamic da = a, db = b;
        string? la = da.lastSeen, lb = db.lastSeen;
        if (la != null && lb != null) return string.Compare(lb, la, StringComparison.Ordinal);
        if (la != null) return -1;
        if (lb != null) return 1;
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
                string? npcKey = context.Request.QueryString["npc"];
                string? deltaStr = context.Request.QueryString["delta"];
                if (npcKey != null && double.TryParse(deltaStr,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double delta))
                {
                    vendorStates.GetOrAdd(npcKey, k => new VendorState { NpcKey = k }).FavorDelta = delta;
                }
                byte[] buf = Encoding.UTF8.GetBytes("{\"ok\":true}");
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

// --- Log tailing ---
async Task TailLog(string path, CancellationToken ct)
{
    Console.WriteLine($"Tailing log: {path}");
    using (var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
    {
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null) ProcessLine(line);
    }
    Console.WriteLine($"Initial parse done. Vendors with state: {vendorStates.Count}");

    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);
    while (!ct.IsCancellationRequested)
    {
        string? newLine = await reader.ReadLineAsync(ct);
        if (newLine != null) ProcessLine(newLine);
        else await Task.Delay(500, ct);
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

// --- Types ---
record CapTier(string FavorLevel, int Cap, string[] BuyTypes);
record NpcVendorDef(string Key, string DisplayName, string Area, List<CapTier> Caps);

class VendorState
{
    public string NpcKey { get; init; } = "";
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
.container{max-width:1300px;margin:0 auto;padding:12px}
.header{background:linear-gradient(180deg,#2a2a4a 0%,#1e1e3a 100%);border:1px solid #4a4a6a;border-radius:6px;padding:10px 16px;margin-bottom:12px;display:flex;align-items:center;gap:14px;flex-wrap:wrap}
.header h1{color:#ffd700;font-size:18px;font-weight:bold;text-shadow:0 0 8px rgba(255,215,0,0.3)}
.stat{background:#12122a;padding:4px 10px;border-radius:4px;font-size:13px;border:1px solid #333358}
.stat-label{color:#888;font-size:11px}
.stat-value{color:#fff;font-weight:bold}
.toolbar{background:#22223a;border:1px solid #4a4a6a;border-radius:6px;padding:8px 12px;margin-bottom:12px;display:flex;align-items:center;gap:12px;flex-wrap:wrap}
.filter-input{background:#12122a;border:1px solid #4a4a6a;border-radius:4px;color:#e0e0e0;padding:5px 10px;font-size:13px;width:260px}
.filter-input::placeholder{color:#555}
.filter-input:focus{outline:none;border-color:#ffd700}
.toggle-label{color:#aaa;font-size:13px;cursor:pointer;display:flex;align-items:center;gap:5px;user-select:none}
.toggle-label input{accent-color:#ffd700}
.section-title{color:#ffd700;font-size:13px;font-weight:bold;margin:0 0 8px;padding-bottom:4px;border-bottom:1px solid #333360;display:flex;align-items:center;gap:8px}
.section-title span{color:#888;font-weight:normal;font-size:12px}
.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(310px,1fr));gap:10px;margin-bottom:16px}
.card{background:linear-gradient(180deg,#16162e 0%,#0e0e22 100%);border:1px solid #2a2a4a;border-radius:6px;padding:12px}
.card.active{border-color:#ffd700;box-shadow:0 0 10px rgba(255,215,0,0.12)}
.card-header{display:flex;justify-content:space-between;align-items:flex-start;margin-bottom:8px;gap:8px}
.card-name-row{display:flex;align-items:center;gap:6px;flex-wrap:wrap}
.card-name{color:#ffd700;font-weight:bold;font-size:15px}
.open-badge{background:#0e2a0e;color:#44cc44;font-size:10px;padding:1px 6px;border-radius:4px;border:1px solid #1a4a1a;white-space:nowrap}
.area-badge{background:#1a1a38;color:#8888bb;font-size:11px;padding:2px 8px;border-radius:10px;border:1px solid #2a2a4a;white-space:nowrap;flex-shrink:0}
.last-seen{color:#555;font-size:11px;margin-top:3px}
.gold-section{margin:8px 0 4px}
.gold-label{font-size:12px;color:#aaa;margin-bottom:4px}
.gold-label strong{color:#e0e0e0}
.progress-bg{background:#0a0a1a;border-radius:3px;height:8px;overflow:hidden;border:1px solid #2a2a3a}
.progress-fill{height:100%;border-radius:3px;transition:width 0.4s ease}
.fill-green{background:linear-gradient(90deg,#1a6a1a,#33bb33)}
.fill-yellow{background:linear-gradient(90deg,#6a5500,#ccaa00)}
.fill-red{background:linear-gradient(90deg,#6a1010,#cc3333)}
.favor-row{font-size:12px;color:#aaa;margin:6px 0 4px}
.favor-pos{color:#44cc44;font-weight:bold}
.favor-neg{color:#cc4444;font-weight:bold}
.favor-zero{color:#555}
.caps-table{width:100%;border-collapse:collapse;margin-top:6px;font-size:11px}
.caps-table th{color:#666;text-align:left;padding:2px 5px;border-bottom:1px solid #1e1e38;font-weight:normal}
.caps-table td{padding:2px 5px;color:#aaa;border-bottom:1px solid #14142a}
.caps-table tr.active-row td{color:#ffd700}
.caps-table tr.active-row td:first-child::before{content:"\25B6 ";font-size:9px}
.buy-types{color:#5577aa;font-size:10px}
.no-vendors{color:#555;text-align:center;padding:24px;font-size:13px}
</style>
</head><body>
<div class="container">
  <div class="header">
    <h1>&#x1f4b0; VendorTracker</h1>
    <div class="stat"><div class="stat-label">Total Vendors</div><div class="stat-value" id="totalCount">0</div></div>
    <div class="stat"><div class="stat-label">Active (10m)</div><div class="stat-value" id="recentCount">0</div></div>
    <div class="stat"><div class="stat-label">Gold Known</div><div class="stat-value" id="goldCount">0</div></div>
  </div>
  <div class="toolbar">
    <input type="text" class="filter-input" placeholder="Search by name or area..." id="searchBox" oninput="render()">
    <label class="toggle-label"><input type="checkbox" id="recentOnly" onchange="render()"> Recently visited only</label>
  </div>
  <div id="recentSection" style="display:none">
    <div class="section-title">Recently Visited <span>(last 10 minutes)</span></div>
    <div class="grid" id="recentGrid"></div>
  </div>
  <div id="allTitle" class="section-title" style="display:none">All Vendors <span id="allTitleCount"></span></div>
  <div class="grid" id="allGrid"></div>
</div>
<script>
let data = null;

async function fetchData(){
  try{const r=await fetch('/api/data');data=await r.json();render();}catch(e){console.error(e);}
}

function timeAgo(iso){
  if(!iso)return'';
  const s=Math.round((Date.now()-new Date(iso))/1000);
  if(s<60)return s+'s ago';
  if(s<3600)return Math.floor(s/60)+'m ago';
  return Math.floor(s/3600)+'h ago';
}
function isRecent(iso){return iso&&(Date.now()-new Date(iso))<600000;}
function esc(s){return(s||'').replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/"/g,'&quot;');}
function fmtG(n){return n!=null?n.toLocaleString()+'g':'-';}

function buildCard(v){
  const recent=isRecent(v.lastSeen);
  const ac=v.activeCap;

  let bar='';
  if(v.availableGold!=null&&ac){
    const pct=v.availableGold/ac.cap;
    const cls=pct>0.5?'fill-green':pct>0.2?'fill-yellow':'fill-red';
    const w=Math.min(100,Math.round(pct*100));
    const spent=(ac.cap-v.availableGold).toLocaleString();
    bar=`<div class="gold-section">
      <div class="gold-label">Gold: <strong>${fmtG(v.availableGold)}</strong> / ${fmtG(ac.cap)} &nbsp;<span style="color:#666;font-size:11px">(${spent}g spent)</span></div>
      <div class="progress-bg"><div class="progress-fill ${cls}" style="width:${w}%"></div></div>
    </div>`;
  } else if(v.availableGold!=null){
    bar=`<div class="gold-section"><div class="gold-label">Gold available: <strong>${fmtG(v.availableGold)}</strong></div></div>`;
  }

  const fd=v.favorDelta||0;
  const fdCls=fd>0?'favor-pos':fd<0?'favor-neg':'favor-zero';
  const fdStr=(fd>0?'+':'')+fd.toFixed(2);
  const favorRow=`<div class="favor-row">Favor delta: <span class="${fdCls}">${fdStr}</span></div>`;

  let capsHtml='';
  if(v.caps&&v.caps.length>0){
    capsHtml='<table class="caps-table"><tr><th>Favor Level</th><th>Cap</th><th>Buy Types</th></tr>';
    for(const c of v.caps){
      const isAct=ac&&c.favorLevel===ac.favorLevel&&c.cap===ac.cap;
      const rowCls=isAct?' class="active-row"':'';
      const types=c.buyTypes&&c.buyTypes.length>0
        ?`<span class="buy-types">${c.buyTypes.map(esc).join(', ')}</span>`
        :'<span class="buy-types" style="color:#444">All</span>';
      capsHtml+=`<tr${rowCls}><td>${esc(c.favorLevel)}</td><td>${c.cap.toLocaleString()}g</td><td>${types}</td></tr>`;
    }
    capsHtml+='</table>';
  }

  const openBadge=v.isOpen?'<span class="open-badge">OPEN</span>':'';
  const ago=v.lastSeen?`<div class="last-seen">${timeAgo(v.lastSeen)}</div>`:'';

  return `<div class="card${recent?' active':''}">
    <div class="card-header">
      <div>
        <div class="card-name-row"><span class="card-name">${esc(v.name)}</span>${openBadge}</div>
        ${ago}
      </div>
      ${v.area?`<span class="area-badge">${esc(v.area)}</span>`:''}
    </div>
    ${bar}
    ${favorRow}
    ${capsHtml}
  </div>`;
}

function render(){
  if(!data)return;
  const search=document.getElementById('searchBox').value.toLowerCase();
  const recentOnly=document.getElementById('recentOnly').checked;

  let vendors=data.vendors;
  if(search) vendors=vendors.filter(v=>
    v.name.toLowerCase().includes(search)||v.area.toLowerCase().includes(search));

  const recent=vendors.filter(v=>isRecent(v.lastSeen));
  const allVendors=recentOnly?recent:vendors;

  document.getElementById('totalCount').textContent=data.vendors.length;
  document.getElementById('recentCount').textContent=data.vendors.filter(v=>isRecent(v.lastSeen)).length;
  document.getElementById('goldCount').textContent=data.vendors.filter(v=>v.availableGold!=null).length;

  const recentSection=document.getElementById('recentSection');
  const allTitle=document.getElementById('allTitle');
  if(recent.length>0&&!recentOnly){
    recentSection.style.display='';
    document.getElementById('recentGrid').innerHTML=recent.map(buildCard).join('');
    allTitle.style.display='';
    document.getElementById('allTitleCount').textContent=`(${allVendors.length})`;
  } else {
    recentSection.style.display='none';
    allTitle.style.display='none';
  }

  const allGrid=document.getElementById('allGrid');
  if(allVendors.length===0){
    allGrid.innerHTML='<div class="no-vendors">No vendors match your search.</div>';
  } else {
    allGrid.innerHTML=allVendors.map(buildCard).join('');
  }
}

fetchData();
setInterval(fetchData,2000);
</script>
</body></html>
""";
}

using MemoryLib;
using MemoryLib.Models;
using MemoryLib.Readers;
using System.Net;
using System.Text;
using System.Text.Json;

// ── Configuration ─────────────────────────────────────────────────────────────
string dataDir    = @"C:\Users\oguzb\source\memory-exploration";
string questsPath = Path.Combine(dataDir, "quests.json");
string npcsPath   = Path.Combine(dataDir, "npcs.json");
string areasPath  = Path.Combine(dataDir, "areas.json");
string itemsPath  = Path.Combine(dataDir, "items.json");

// ── Load CDN quest definitions ────────────────────────────────────────────────
// internalName -> enrichment (Name, GiverNpc, Area, Description)
var questCdn = new Dictionary<string, QuestCdnInfo>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(questsPath))
{
    try
    {
        Console.WriteLine("Loading quests.json...");
        using var doc = JsonDocument.Parse(File.ReadAllText(questsPath));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string key          = prop.Name;
            string internalName = key.StartsWith("quest_", StringComparison.OrdinalIgnoreCase) ? key[6..] : key;
            var val             = prop.Value;
            string cdnName  = val.TryGetProperty("Name",        out var np) ? np.GetString() ?? "" : "";
            string giverNpc = val.TryGetProperty("GiverNpc",    out var gp) ? gp.GetString() ?? "" : "";
            string area     = val.TryGetProperty("Area",        out var ap) ? ap.GetString() ?? "" : "";
            string cdnDesc  = val.TryGetProperty("Description", out var dp) ? dp.GetString() ?? "" : "";
            int    xpReward = val.TryGetProperty("Rewards_XP",  out var xp) ? xp.GetInt32()        : 0;
            questCdn[internalName] = new QuestCdnInfo(cdnName, giverNpc, area, cdnDesc, xpReward);
        }
        Console.WriteLine($"Loaded {questCdn.Count} quest definitions.");
    }
    catch (Exception ex) { Console.WriteLine($"Warning: quests.json load failed: {ex.Message}"); }
}

// ── Load NPC display names ────────────────────────────────────────────────────
var npcNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(npcsPath))
{
    try
    {
        Console.WriteLine("Loading npcs.json...");
        using var doc = JsonDocument.Parse(File.ReadAllText(npcsPath));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("Name", out var n))
            {
                string? name = n.GetString();
                if (name != null) npcNames[prop.Name] = name;
            }
        }
        Console.WriteLine($"Loaded {npcNames.Count} NPC names.");
    }
    catch (Exception ex) { Console.WriteLine($"Warning: npcs.json load failed: {ex.Message}"); }
}

// ── Load area display names ───────────────────────────────────────────────────
var areaNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(areasPath))
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(areasPath));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("FriendlyName", out var fn))
            {
                string? name = fn.GetString();
                if (name != null) areaNames[prop.Name] = name;
            }
            else if (prop.Value.TryGetProperty("Name", out var n))
            {
                string? name = n.GetString();
                if (name != null) areaNames[prop.Name] = name;
            }
        }
    }
    catch { }
}

// ── Load item display names (for objective matching) ──────────────────────────
var itemCodeToName = new Dictionary<int, string>();
if (File.Exists(itemsPath))
{
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(itemsPath));
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            string key = prop.Name;
            string numPart = key.StartsWith("item_", StringComparison.OrdinalIgnoreCase) ? key[5..] : key;
            if (int.TryParse(numPart, out int code))
            {
                string name = prop.Value.TryGetProperty("Name", out var n) ? n.GetString() ?? key : key;
                itemCodeToName[code] = name;
            }
        }
    }
    catch { }
}

// ── Shared state ──────────────────────────────────────────────────────────────
var _lock          = new object();
var _quests        = new List<QuestSnapshot>();
var _items         = new List<InventoryItemSnapshot>();
DateTime _lastRead = DateTime.MinValue;
string _status     = "Initializing...";
bool _discovered   = false;

// ── MemoryLib readers ─────────────────────────────────────────────────────────
ProcessMemory?       _memory          = null;
MemoryRegionScanner? _scanner         = null;
QuestReader?         _questReader     = null;
InventoryReader?     _inventoryReader = null;

bool TryConnect()
{
    try
    {
        int? pid = ProcessMemory.FindGameProcess();
        if (pid == null) { lock (_lock) _status = "Game not running"; return false; }

        _memory?.Dispose();
        _memory          = ProcessMemory.Open(pid.Value);
        _scanner         = new MemoryRegionScanner(_memory);
        _questReader     = new QuestReader(_memory, _scanner);
        _inventoryReader = new InventoryReader(_memory, _scanner);
        _inventoryReader.LoadItemData(itemsPath);
        Console.WriteLine($"Opened game process PID {pid.Value}");
        return true;
    }
    catch (Exception ex)
    {
        lock (_lock) _status = $"Connect failed: {ex.Message}";
        return false;
    }
}

bool TryDiscover()
{
    if (_questReader == null || _inventoryReader == null) return false;
    try
    {
        lock (_lock) _status = "Discovering quest controller (may take a moment)...";
        Console.WriteLine("AutoDiscover: QuestReader...");
        bool questOk = _questReader.AutoDiscover();
        if (!questOk)
        {
            lock (_lock) _status = "Quest controller not found — is the game in-world?";
            return false;
        }
        Console.WriteLine("QuestReader discovered.");

        Console.WriteLine("AutoDiscover: InventoryReader...");
        bool itemOk = _inventoryReader.AutoDiscover();
        if (!itemOk)
            Console.WriteLine("Warning: item vtable not found — inventory counts will be unavailable.");
        else
            Console.WriteLine("InventoryReader discovered.");

        return true;
    }
    catch (Exception ex)
    {
        lock (_lock) _status = $"Discovery failed: {ex.Message}";
        return false;
    }
}

void RefreshMemory()
{
    if (_questReader == null) return;
    try
    {
        List<QuestSnapshot>?        quests = _questReader.ReadActiveQuests();
        List<InventoryItemSnapshot>? items  = _inventoryReader?.ReadAllItems();

        lock (_lock)
        {
            if (quests != null) _quests = quests;
            if (items  != null) _items  = items;
            _lastRead = DateTime.Now;
            _status   = $"OK — {_quests.Count} active quests, {_items.Count} items";
        }
    }
    catch (Exception ex)
    {
        lock (_lock) _status = $"Read error: {ex.Message}";
    }
}

// ── Scan loop ─────────────────────────────────────────────────────────────────
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

var scanTask = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        if (_memory == null)
        {
            if (!TryConnect())
            {
                try { await Task.Delay(5000, cts.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
        }

        if (!_discovered)
        {
            _discovered = TryDiscover();
            if (!_discovered)
            {
                try { await Task.Delay(10000, cts.Token); } catch (OperationCanceledException) { break; }
                continue;
            }
        }

        RefreshMemory();
        try { await Task.Delay(5000, cts.Token); } catch (OperationCanceledException) { break; }
    }
});

// ── HTTP server ───────────────────────────────────────────────────────────────
var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:9884/");
listener.Start();
Console.WriteLine("QuestTracker running at http://localhost:9884/");
Console.WriteLine("Press Ctrl+C to stop.");

while (!cts.IsCancellationRequested)
{
    HttpListenerContext ctx;
    try { ctx = await listener.GetContextAsync().WaitAsync(cts.Token); }
    catch (OperationCanceledException) { break; }
    _ = Task.Run(() => HandleRequest(ctx));
}

listener.Stop();
await scanTask;
_memory?.Dispose();
Console.WriteLine("QuestTracker stopped.");

// ── Request handler ───────────────────────────────────────────────────────────

void HandleRequest(HttpListenerContext ctx)
{
    var req = ctx.Request;
    var res = ctx.Response;
    try
    {
        string path   = req.Url?.AbsolutePath ?? "/";
        string method = req.HttpMethod;

        if (path == "/" && method == "GET")
        {
            Respond(res, 200, "text/html; charset=utf-8", BuildHtmlPage());
            return;
        }

        if (path == "/api/data" && method == "GET")
        {
            List<QuestSnapshot>        quests;
            List<InventoryItemSnapshot> items;
            string   status;
            DateTime lastRead;

            lock (_lock)
            {
                quests   = new List<QuestSnapshot>(_quests);
                items    = new List<InventoryItemSnapshot>(_items);
                status   = _status;
                lastRead = _lastRead;
            }

            // Build item-count lookup: internalName -> stack total
            var itemCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in items)
            {
                itemCounts.TryGetValue(it.InternalName, out int prev);
                itemCounts[it.InternalName] = prev + it.StackCount;
            }

            var payload = new
            {
                status,
                lastRefresh = lastRead == DateTime.MinValue ? null : (string?)lastRead.ToString("HH:mm:ss"),
                itemCount   = items.Count,
                quests      = quests.Select(q => BuildQuestDto(q, itemCounts)).ToList(),
            };

            var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            Respond(res, 200, "application/json", JsonSerializer.Serialize(payload, opts));
            return;
        }

        Respond(res, 404, "text/plain", "Not Found");
    }
    catch { }
    finally
    {
        try { res.Close(); } catch { }
    }
}

void Respond(HttpListenerResponse res, int code, string contentType, string body)
{
    res.StatusCode  = code;
    res.ContentType = contentType;
    byte[] bytes    = Encoding.UTF8.GetBytes(body);
    res.ContentLength64 = bytes.Length;
    res.OutputStream.Write(bytes);
}

object BuildQuestDto(QuestSnapshot q, Dictionary<string, int> itemCounts)
{
    questCdn.TryGetValue(q.InternalName, out QuestCdnInfo? cdn);

    string giverDisplay = "";
    if (cdn?.GiverNpc is { Length: > 0 } giverKey)
        giverDisplay = npcNames.TryGetValue(giverKey, out string? dn) ? dn : giverKey;

    string areaDisplay = q.DisplayedLocation.Length > 0 ? q.DisplayedLocation : "";
    if (areaDisplay.Length == 0 && cdn?.Area is { Length: > 0 } areaKey)
        areaDisplay = areaNames.TryGetValue(areaKey, out string? an) ? an : areaKey;

    string description = q.Description.Length > 0 ? q.Description : cdn?.Description ?? "";

    return new
    {
        id              = q.QuestId,
        name            = q.Name.Length > 0 ? q.Name : cdn?.CdnName ?? $"Quest_{q.QuestId}",
        internalName    = q.InternalName,
        description,
        location        = areaDisplay,
        giver           = giverDisplay,
        xpReward        = cdn?.XpReward ?? 0,
        isTracked       = q.IsTracked,
        isReadyForTurnIn = q.IsReadyForTurnIn,
        isWorkOrder     = q.IsWorkOrder,
        objectives      = q.Objectives.Select(o => BuildObjectiveDto(o, itemCounts)).ToList(),
    };
}

object BuildObjectiveDto(ObjectiveSnapshot o, Dictionary<string, int> itemCounts)
{
    // Try to match an item requirement from the description (format: "Collect N ItemName")
    int inInventory = 0;
    if (o.Type == "Collect" || o.Type == "Have")
    {
        foreach (var (name, count) in itemCounts)
        {
            if (o.Description.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                inInventory += count;
                break;
            }
        }
    }

    return new
    {
        type        = o.Type,
        description = o.Description,
        current     = o.CurrentState,
        target      = o.TargetCount,
        isComplete  = o.IsComplete,
        inInventory = inInventory > 0 ? (int?)inInventory : null,
    };
}

// ── HTML dashboard ─────────────────────────────────────────────────────────────

string BuildHtmlPage() => """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Quest Tracker — Project Gorgon</title>
<style>
* { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0f0f1a; color: #ddd; min-height: 100vh; }
header { background: #16213e; padding: 14px 20px; display: flex; justify-content: space-between; align-items: center; border-bottom: 2px solid #e94560; }
h1 { font-size: 1.3em; color: #e94560; letter-spacing: 1px; }
#status-bar { font-size: 0.78em; color: #aaa; }
main { padding: 20px; max-width: 1300px; margin: 0 auto; }
.section-header { font-size: 0.85em; font-weight: 700; color: #e94560; text-transform: uppercase; letter-spacing: 2px; margin: 24px 0 10px; border-bottom: 1px solid #1e2d50; padding-bottom: 4px; }
.quest-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 10px; }
.card { background: #16213e; border: 1px solid #1e2d50; border-radius: 6px; padding: 12px 14px; }
.card.tracked { border-color: #e94560; }
.card.ready { border-color: #2ecc71; background: #162e1e; }
.card-title { font-weight: 600; font-size: 0.95em; display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.card-meta { font-size: 0.72em; color: #888; margin: 5px 0 8px; display: flex; gap: 12px; flex-wrap: wrap; }
.badge { font-size: 0.65em; font-weight: 700; padding: 2px 7px; border-radius: 10px; text-transform: uppercase; }
.b-ready { background: #2ecc71; color: #111; }
.b-track { background: #e94560; color: #fff; }
.b-work  { background: #f39c12; color: #111; }
.b-xp    { background: #2c3e50; color: #aaa; }
.obj-list { list-style: none; margin-top: 2px; }
.obj { display: flex; gap: 7px; padding: 3px 0; font-size: 0.8em; border-top: 1px solid #1e2d50; }
.obj:first-child { border-top: none; }
.circle { width: 15px; min-width: 15px; height: 15px; border-radius: 50%; border: 2px solid #555; display: flex; align-items: center; justify-content: center; font-size: 9px; margin-top: 1px; }
.circle.done { background: #2ecc71; border-color: #2ecc71; color: #111; }
.obj-text { flex: 1; line-height: 1.35; }
.obj-count { color: #888; font-size: 0.9em; }
.obj-inv { color: #3498db; font-size: 0.85em; margin-left: 4px; }
.empty { text-align: center; color: #555; padding: 60px 20px; font-size: 1em; }
</style>
</head>
<body>
<header>
  <h1>&#x1F4DC; Quest Tracker</h1>
  <span id="status-bar">Connecting...</span>
</header>
<main id="main">
  <div class="empty">Waiting for data...</div>
</main>
<script>
async function refresh() {
  let d;
  try { d = await (await fetch('/api/data')).json(); } catch { return; }
  document.getElementById('status-bar').textContent =
    d.status + (d.lastRefresh ? '  \u2014  last read ' + d.lastRefresh : '');
  const main = document.getElementById('main');
  const qs = d.quests || [];
  if (!qs.length) { main.innerHTML = '<div class="empty">' + esc(d.status) + '</div>'; return; }
  const tracked    = qs.filter(q => q.isTracked);
  const workOrders = qs.filter(q => q.isWorkOrder && !q.isTracked);
  const regular    = qs.filter(q => !q.isWorkOrder && !q.isTracked);
  let html = '';
  if (tracked.length)    html += section('Tracked', tracked);
  if (regular.length)    html += section('Active Quests', regular);
  if (workOrders.length) html += section('Work Orders', workOrders);
  main.innerHTML = html;
}
function section(title, quests) {
  return '<div class="section-header">' + esc(title) + ' &mdash; ' + quests.length + '</div>'
       + '<div class="quest-grid">' + quests.map(card).join('') + '</div>';
}
function card(q) {
  const cls = 'card' + (q.isTracked ? ' tracked' : '') + (q.isReadyForTurnIn ? ' ready' : '');
  const badges = [
    q.isReadyForTurnIn ? '<span class="badge b-ready">Turn In</span>' : '',
    q.isTracked        ? '<span class="badge b-track">Tracked</span>' : '',
    q.isWorkOrder      ? '<span class="badge b-work">Work Order</span>' : '',
    q.xpReward > 0     ? '<span class="badge b-xp">' + q.xpReward.toLocaleString() + ' XP</span>' : '',
  ].filter(Boolean).join('');
  const meta = [
    q.giver    ? '\u{1F464} ' + esc(q.giver)    : '',
    q.location ? '\u{1F4CD} ' + esc(q.location) : '',
  ].filter(Boolean).map(s => '<span>' + s + '</span>').join('');
  const objs = (q.objectives || []).map(o => {
    const cnt = o.target > 0
      ? '<span class="obj-count"> ' + o.current + '\u2009/\u2009' + o.target + '</span>' : '';
    const inv = o.inInventory != null
      ? '<span class="obj-inv">(have ' + o.inInventory + ')</span>' : '';
    return '<li class="obj">'
      + '<div class="circle' + (o.isComplete ? ' done' : '') + '">' + (o.isComplete ? '\u2713' : '') + '</div>'
      + '<span class="obj-text">' + esc(o.description) + cnt + inv + '</span></li>';
  }).join('');
  return '<div class="' + cls + '">'
    + '<div class="card-title">' + esc(q.name) + badges + '</div>'
    + (meta ? '<div class="card-meta">' + meta + '</div>' : '')
    + (objs ? '<ul class="obj-list">' + objs + '</ul>' : '')
    + '</div>';
}
function esc(s) {
  if (!s) return '';
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
}
refresh();
setInterval(refresh, 5000);
</script>
</body>
</html>
""";

// ── Data types ────────────────────────────────────────────────────────────────
record QuestCdnInfo(string CdnName, string GiverNpc, string Area, string Description, int XpReward);

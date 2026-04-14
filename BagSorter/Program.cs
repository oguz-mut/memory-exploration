using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using MemoryLib;
using MemoryLib.Models;
using MemoryLib.Readers;

// --- Configuration ---
string dataDir = @"C:\Users\oguzb\source\memory-exploration";
string itemsPath = Path.Combine(dataDir, "items.json");

// --- Load items.json ---
Console.WriteLine("Loading items.json...");
var itemsDoc = JsonDocument.Parse(File.ReadAllText(itemsPath));

// --- Build item data ---
var itemCodeToInternal = new Dictionary<int, string>();
var internalToItemCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var itemCodeToName = new Dictionary<int, string>();
var itemCodeToValue = new Dictionary<int, int>();
var itemCodeToMaxStack = new Dictionary<int, int>();
var itemCodeToKeywords = new Dictionary<int, List<string>>();
var itemCodeToIconId = new Dictionary<int, int>();
var itemCodeToDescription = new Dictionary<int, string>();

foreach (var prop in itemsDoc.RootElement.EnumerateObject())
{
    int code = int.Parse(prop.Name.Replace("item_", ""));
    string displayName = prop.Value.TryGetProperty("Name", out var n) ? n.GetString()! : prop.Name;
    itemCodeToName[code] = displayName;

    if (prop.Value.TryGetProperty("InternalName", out var intName))
    {
        string iname = intName.GetString()!;
        itemCodeToInternal[code] = iname;
        internalToItemCode[iname] = code;
    }
    if (prop.Value.TryGetProperty("Value", out var val) && val.TryGetInt32(out int valInt)) itemCodeToValue[code] = valInt;
    if (prop.Value.TryGetProperty("MaxStackSize", out var ms)) itemCodeToMaxStack[code] = ms.GetInt32();
    if (prop.Value.TryGetProperty("IconId", out var icon)) itemCodeToIconId[code] = icon.GetInt32();
    if (prop.Value.TryGetProperty("Description", out var desc)) itemCodeToDescription[code] = desc.GetString()!;

    if (prop.Value.TryGetProperty("Keywords", out var kws))
    {
        var kwList = new List<string>();
        foreach (var kw in kws.EnumerateArray()) kwList.Add(kw.GetString()!);
        itemCodeToKeywords[code] = kwList;
    }
}
Console.WriteLine($"Loaded {itemCodeToName.Count} items.");

// --- Category assignment based on keywords ---
string GetItemCategory(int code)
{
    if (!itemCodeToKeywords.TryGetValue(code, out var keywords))
        return "Misc";

    foreach (var kw in keywords)
    {
        if (kw.IndexOf("Equipment", StringComparison.OrdinalIgnoreCase) >= 0) return "Equipment";
        if (kw.IndexOf("Armor", StringComparison.OrdinalIgnoreCase) >= 0) return "Equipment";
        if (kw.IndexOf("Weapon", StringComparison.OrdinalIgnoreCase) >= 0) return "Equipment";
        if (kw.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0) return "Equipment";
    }
    foreach (var kw in keywords)
    {
        if (kw.IndexOf("CookingIngredient", StringComparison.OrdinalIgnoreCase) >= 0) return "Cooking";
        if (kw.IndexOf("AlchemyIngredient", StringComparison.OrdinalIgnoreCase) >= 0) return "Alchemy";
    }
    foreach (var kw in keywords)
    {
        if (kw.IndexOf("Seed", StringComparison.OrdinalIgnoreCase) >= 0) return "Gardening";
        if (kw.IndexOf("GardeningRelated", StringComparison.OrdinalIgnoreCase) >= 0) return "Gardening";
    }
    foreach (var kw in keywords)
    {
        if (kw.IndexOf("Consumable", StringComparison.OrdinalIgnoreCase) >= 0) return "Consumables";
        if (kw.IndexOf("Food", StringComparison.OrdinalIgnoreCase) >= 0) return "Consumables";
        if (kw.IndexOf("Drink", StringComparison.OrdinalIgnoreCase) >= 0) return "Consumables";
        if (kw.IndexOf("Potion", StringComparison.OrdinalIgnoreCase) >= 0) return "Consumables";
    }
    foreach (var kw in keywords)
    {
        if (kw.IndexOf("CorpseTrophy", StringComparison.OrdinalIgnoreCase) >= 0) return "Loot";
        if (kw.IndexOf("Bone", StringComparison.OrdinalIgnoreCase) >= 0) return "Loot";
        if (kw.IndexOf("AnimalSkin", StringComparison.OrdinalIgnoreCase) >= 0) return "Loot";
    }
    foreach (var kw in keywords)
    {
        if (kw.Equals("Vendor Trash", StringComparison.OrdinalIgnoreCase)) return "Junk";
    }

    return "Misc";
}

// --- Inventory state ---
var inventory = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
bool sorted = false;
string sortMode = "category";
string? memoryError = null;
string scanStatus = "Initializing...";

// --- MemoryLib setup ---
InventoryReader? inventoryReader = null;
List<InventoryItemSnapshot>? cachedItemInfos = null;
bool fullScanDone = false;

int? pid = ProcessMemory.FindGameProcess();
if (pid == null)
{
    memoryError = "Game process not found. Start Project Gorgon and restart BagSorter.";
    Console.WriteLine(memoryError);
}
else
{
    try
    {
        var memory = ProcessMemory.Open(pid.Value);
        var scanner = new MemoryRegionScanner(memory);
        inventoryReader = new InventoryReader(memory, scanner);
        inventoryReader.LoadItemData(itemsPath);
        Console.WriteLine("Running AutoDiscover...");
        bool discovered = inventoryReader.AutoDiscover();
        if (!discovered)
        {
            memoryError = "InventoryReader could not discover item vtable. Make sure items are in your inventory.";
            Console.WriteLine(memoryError);
        }
        else
        {
            Console.WriteLine("AutoDiscover succeeded.");
            scanStatus = "Running initial inventory scan (~15s)...";
        }
    }
    catch (Exception ex)
    {
        memoryError = $"Failed to open game process: {ex.Message}";
        Console.WriteLine(memoryError);
    }
}

// --- Memory poll loop ---
async Task MemoryPollLoop(CancellationToken ct)
{
    while (!ct.IsCancellationRequested)
    {
        if (inventoryReader != null)
        {
            try
            {
                List<InventoryItemSnapshot>? bagItems = null;

                if (fullScanDone)
                {
                    // Fast path: re-read the cached controller array directly
                    bagItems = inventoryReader.ReadInventoryFast();
                    if (bagItems == null)
                    {
                        Console.WriteLine("[BagSorter] Fast read failed — re-running full scan.");
                        fullScanDone = false;
                        scanStatus = "Re-scanning inventory...";
                    }
                }

                if (!fullScanDone)
                {
                    // Full scan: Option A (structural) → Option B (controller list)
                    cachedItemInfos ??= inventoryReader.ReadAllItems();
                    if (cachedItemInfos != null)
                    {
                        var allItems = inventoryReader.ReadAllInventoryItems(cachedItemInfos);
                        if (allItems != null)
                        {
                            bagItems = inventoryReader.ReadInventoryViaControllerList(allItems);
                            fullScanDone = bagItems != null && bagItems != allItems;
                            scanStatus = fullScanDone ? "Live" : "Scan complete (controller list not found)";
                        }
                    }
                }

                if (bagItems != null)
                {
                    var newInv = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in bagItems)
                        newInv.AddOrUpdate(item.InternalName, item.StackCount, (_, old) => old + item.StackCount);
                    inventory = newInv;
                    memoryError = null;
                }
            }
            catch (Exception ex)
            {
                memoryError = $"Memory read error: {ex.Message}";
            }
        }
        await Task.Delay(fullScanDone ? 5000 : 1000, ct);
    }
}

// --- Build API response ---
object GetBagState()
{
    var items = new List<object>();
    int totalValue = 0;
    int totalItems = 0;
    var categoryCounts = new Dictionary<string, int>();

    var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kvp in inventory) if (kvp.Value > 0) allNames.Add(kvp.Key);

    foreach (var iname in allNames)
    {
        if (!internalToItemCode.TryGetValue(iname, out int code)) continue;
        int count = inventory.GetValueOrDefault(iname, 0);
        if (count <= 0) continue;

        string displayName = itemCodeToName.GetValueOrDefault(code, iname);
        string category = GetItemCategory(code);
        int value = itemCodeToValue.GetValueOrDefault(code, 0);
        int maxStack = itemCodeToMaxStack.GetValueOrDefault(code, 1);

        totalValue += value * count;
        totalItems += count;
        categoryCounts[category] = categoryCounts.GetValueOrDefault(category, 0) + count;

        items.Add(new
        {
            name = displayName,
            internalName = iname,
            code,
            count,
            storageCount = 0,
            category,
            value,
            maxStack,
            iconId = itemCodeToIconId.GetValueOrDefault(code, 0),
            description = itemCodeToDescription.GetValueOrDefault(code, "")
        });
    }

    if (sorted)
    {
        items.Sort((a, b) =>
        {
            dynamic da = a, db = b;
            return sortMode switch
            {
                "category" => string.Compare((string)da.category, (string)db.category, StringComparison.OrdinalIgnoreCase) is int c && c != 0
                    ? c : string.Compare((string)da.name, (string)db.name, StringComparison.OrdinalIgnoreCase),
                "name" => string.Compare((string)da.name, (string)db.name, StringComparison.OrdinalIgnoreCase),
                "value" => ((int)db.value * (int)db.count).CompareTo((int)da.value * (int)da.count),
                _ => 0
            };
        });
    }

    return new
    {
        items,
        totalValue,
        totalItems,
        totalSlots = items.Count,
        sorted,
        sortMode,
        categories = categoryCounts,
        scanStatus,
        error = memoryError
    };
}

// --- HTTP Server ---
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9875/");
    try { listener.Start(); Console.WriteLine("HTTP server listening on http://localhost:9875/"); }
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
                string json = JsonSerializer.Serialize(GetBagState());
                byte[] buf = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }
            else if (path == "/api/sort")
            {
                string? mode = context.Request.QueryString["mode"];
                if (mode is "category" or "name" or "value") sortMode = mode;
                sorted = true;
                string json = JsonSerializer.Serialize(new { ok = true, sortMode });
                byte[] buf = Encoding.UTF8.GetBytes(json);
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
var pollTask = MemoryPollLoop(cts.Token);
Console.WriteLine("Press Ctrl+C to stop.");
try { await Task.WhenAll(httpTask, pollTask); } catch (OperationCanceledException) { }
Console.WriteLine("Shutting down.");

// --- HTML ---
static partial class HtmlContent
{
    public const string DASHBOARD = """
<!DOCTYPE html>
<html><head>
<meta charset="utf-8"><title>PG BagSorter</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#1a1a2e;color:#e0e0e0;font-size:14px}
.container{max-width:1100px;margin:0 auto;padding:12px}

/* Header bar */
.header{background:linear-gradient(180deg,#2a2a4a 0%,#1e1e3a 100%);border:1px solid #4a4a6a;border-radius:6px;padding:10px 16px;margin-bottom:12px;display:flex;align-items:center;gap:14px;flex-wrap:wrap}
.header h1{color:#ffd700;font-size:18px;font-weight:bold;text-shadow:0 0 8px rgba(255,215,0,0.3)}
.stat{background:#12122a;padding:4px 10px;border-radius:4px;font-size:13px;border:1px solid #333358}
.stat-label{color:#888;font-size:11px}
.stat-value{color:#fff;font-weight:bold}
.gold{color:#ffd700}

/* Error banner */
.error-banner{background:#2a0a0a;border:1px solid #aa3333;border-radius:6px;padding:10px 16px;margin-bottom:12px;color:#ff6666;font-size:13px}

/* Sort toolbar */
.toolbar{background:#22223a;border:1px solid #4a4a6a;border-radius:6px;padding:8px 12px;margin-bottom:12px;display:flex;align-items:center;gap:8px;flex-wrap:wrap}
.sort-btn{background:linear-gradient(180deg,#4a4a6a 0%,#2e2e4e 100%);color:#ffd700;border:1px solid #6a6a8a;border-radius:4px;padding:6px 14px;cursor:pointer;font-size:13px;font-weight:bold;transition:all 0.15s}
.sort-btn:hover{background:linear-gradient(180deg,#5a5a7a 0%,#3e3e5e 100%);border-color:#8a8aaa}
.sort-btn:active{transform:scale(0.97)}
.sort-btn.active{background:linear-gradient(180deg,#6a5a1a 0%,#4a3a0a 100%);border-color:#ffd700;box-shadow:0 0 6px rgba(255,215,0,0.3)}
.sort-label{color:#aaa;font-size:12px;margin-right:4px}
.filter-input{background:#12122a;border:1px solid #4a4a6a;border-radius:4px;color:#e0e0e0;padding:5px 10px;font-size:13px;width:180px}
.filter-input::placeholder{color:#555}
.filter-input:focus{outline:none;border-color:#ffd700}

/* Category tabs */
.cat-tabs{display:flex;gap:4px;flex-wrap:wrap}
.cat-tab{background:#1a1a30;border:1px solid #333;border-radius:4px 4px 0 0;padding:4px 10px;cursor:pointer;font-size:12px;color:#aaa;transition:all 0.15s}
.cat-tab:hover{color:#fff;background:#2a2a4a}
.cat-tab.active{color:#ffd700;border-color:#ffd700;background:#2a2a4a;border-bottom-color:#2a2a4a}
.cat-tab .count{color:#666;margin-left:3px}
.cat-tab.active .count{color:#aa8800}

/* Bag container — the main grid */
.bag{background:linear-gradient(180deg,#16162e 0%,#0e0e22 100%);border:2px solid #4a4a6a;border-radius:6px;padding:10px;margin-bottom:12px}
.bag-title{color:#aaa;font-size:12px;padding:0 4px 8px;border-bottom:1px solid #333;margin-bottom:10px;display:flex;justify-content:space-between}

/* Item slot grid */
.slots{display:grid;grid-template-columns:repeat(auto-fill,52px);gap:4px;justify-content:start}
.slot{width:50px;height:50px;background:#0a0a1a;border:1px solid #333358;border-radius:3px;position:relative;cursor:pointer;transition:border-color 0.15s,box-shadow 0.15s;display:flex;align-items:center;justify-content:center}
.slot:hover{border-color:#ffd700;box-shadow:0 0 6px rgba(255,215,0,0.25);z-index:10}
.slot.empty{opacity:0.3;cursor:default}
.slot .icon{font-size:22px;line-height:1}
.slot .qty{position:absolute;bottom:1px;right:3px;font-size:10px;font-weight:bold;color:#fff;text-shadow:0 0 3px #000,0 0 3px #000}
.slot .cat-dot{position:absolute;top:2px;left:2px;width:6px;height:6px;border-radius:50%}

/* Category colors */
.cat-equipment{border-color:#a335ee;background:#1a0a2e}.cat-equipment .cat-dot{background:#a335ee}
.cat-cooking{border-color:#ff8c00;background:#1a1000}.cat-cooking .cat-dot{background:#ff8c00}
.cat-alchemy{border-color:#00cc88;background:#001a10}.cat-alchemy .cat-dot{background:#00cc88}
.cat-gardening{border-color:#44bb44;background:#0a1a0a}.cat-gardening .cat-dot{background:#44bb44}
.cat-consumables{border-color:#ff4444;background:#1a0a0a}.cat-consumables .cat-dot{background:#ff4444}
.cat-loot{border-color:#6699cc;background:#0a0e1a}.cat-loot .cat-dot{background:#6699cc}
.cat-junk{border-color:#888;background:#111}.cat-junk .cat-dot{background:#888}
.cat-misc{border-color:#aaaadd;background:#10101e}.cat-misc .cat-dot{background:#aaaadd}

/* Tooltip */
.tooltip{display:none;position:fixed;background:#1a1a2e;border:1px solid #ffd700;border-radius:6px;padding:10px 14px;max-width:320px;z-index:1000;pointer-events:none;box-shadow:0 4px 16px rgba(0,0,0,0.6)}
.tooltip .tt-name{color:#ffd700;font-size:15px;font-weight:bold;margin-bottom:4px}
.tooltip .tt-cat{font-size:11px;color:#aaa;margin-bottom:6px}
.tooltip .tt-desc{font-size:12px;color:#ccc;margin-bottom:6px;line-height:1.4}
.tooltip .tt-stats{font-size:12px;color:#88aacc}
.tooltip .tt-stats span{color:#fff}
.tooltip .tt-value{color:#ffd700;font-size:12px;margin-top:4px}

/* Sorted indicator */
.sorted-badge{background:#4a3a0a;color:#ffd700;font-size:11px;padding:2px 8px;border-radius:10px;border:1px solid #6a5a1a}
.unsorted-badge{background:#2a1a1a;color:#aa4444;font-size:11px;padding:2px 8px;border-radius:10px;border:1px solid #4a2a2a}
</style>
</head><body>
<div class="container">
    <div id="errorBanner" class="error-banner" style="display:none"></div>
    <div class="header">
        <h1>&#x1f392; BagSorter</h1>
        <div class="stat"><span class="stat-label">Items</span><br><span class="stat-value" id="totalItems">0</span></div>
        <div class="stat"><span class="stat-label">Slots</span><br><span class="stat-value" id="totalSlots">0</span></div>
        <div class="stat"><span class="stat-label">Total Value</span><br><span class="stat-value gold" id="totalValue">0g</span></div>
        <div id="sortedStatus"></div>
        <div class="stat" style="margin-left:auto"><span class="stat-label">Source</span><br><span class="stat-value" id="scanStatus" style="font-size:11px;color:#888">...</span></div>
    </div>
    <div class="toolbar">
        <span class="sort-label">Sort Bags:</span>
        <button class="sort-btn" onclick="sortBags('category')" id="btn-category">By Category</button>
        <button class="sort-btn" onclick="sortBags('name')" id="btn-name">By Name</button>
        <button class="sort-btn" onclick="sortBags('value')" id="btn-value">By Value</button>
        <div style="flex:1"></div>
        <input type="text" class="filter-input" placeholder="Search items..." id="searchBox" oninput="applyFilter()">
    </div>
    <div class="cat-tabs" id="catTabs"></div>
    <div class="bag">
        <div class="bag-title">
            <span id="bagLabel">Inventory</span>
            <span id="bagCount"></span>
        </div>
        <div class="slots" id="slotsGrid"></div>
    </div>
</div>
<div class="tooltip" id="tooltip"></div>
<script>
let data = null;
let activeCategory = 'All';
let searchText = '';

const CATEGORY_ICONS = {
    'Equipment':'\u2694\uFE0F','Cooking':'\uD83C\uDF73','Alchemy':'\u2697\uFE0F',
    'Gardening':'\uD83C\uDF31','Consumables':'\uD83C\uDF7A','Loot':'\uD83D\uDC80',
    'Junk':'\uD83D\uDDD1\uFE0F','Misc':'\uD83D\uDCE6'
};
const CATEGORY_CSS = {
    'Equipment':'equipment','Cooking':'cooking','Alchemy':'alchemy',
    'Gardening':'gardening','Consumables':'consumables','Loot':'loot',
    'Junk':'junk','Misc':'misc'
};

async function fetchData(){
    try{
        const r=await fetch('/api/data');
        data=await r.json();
        render();
    }catch(e){console.error(e)}
}

function sortBags(mode){
    fetch('/api/sort?mode='+mode).then(()=>fetchData());
}

function applyFilter(){
    searchText=document.getElementById('searchBox').value.toLowerCase();
    render();
}

function selectCategory(cat){
    activeCategory=cat;
    render();
}

function getFilteredItems(){
    if(!data)return[];
    let items=data.items;
    if(activeCategory!=='All') items=items.filter(i=>i.category===activeCategory);
    if(searchText) items=items.filter(i=>i.name.toLowerCase().includes(searchText));
    return items;
}

function render(){
    if(!data)return;

    const banner=document.getElementById('errorBanner');
    if(data.error){banner.textContent=data.error;banner.style.display='block';}
    else{banner.style.display='none';}

    document.getElementById('totalItems').textContent=data.totalItems;
    document.getElementById('totalSlots').textContent=data.totalSlots;
    document.getElementById('totalValue').textContent=data.totalValue.toLocaleString()+'g';
    document.getElementById('scanStatus').textContent=data.scanStatus||'';
    document.getElementById('sortedStatus').innerHTML=data.sorted
        ?'<span class="sorted-badge">Sorted: '+data.sortMode+'</span>'
        :'<span class="unsorted-badge">Unsorted</span>';

    // Highlight active sort button
    ['category','name','value'].forEach(m=>{
        const btn=document.getElementById('btn-'+m);
        btn.classList.toggle('active',data.sorted&&data.sortMode===m);
    });

    // Category tabs
    const cats=data.categories||{};
    let tabsHtml='<div class="cat-tab'+(activeCategory==='All'?' active':'')+'" onclick="selectCategory(\'All\')">All <span class="count">'+data.totalSlots+'</span></div>';
    for(const[cat,count]of Object.entries(cats).sort((a,b)=>b[1]-a[1])){
        tabsHtml+='<div class="cat-tab'+(activeCategory===cat?' active':'')+'" onclick="selectCategory(\''+cat+'\')">'+(CATEGORY_ICONS[cat]||'')+' '+cat+' <span class="count">'+count+'</span></div>';
    }
    document.getElementById('catTabs').innerHTML=tabsHtml;

    // Slots
    const items=getFilteredItems();
    document.getElementById('bagLabel').textContent=activeCategory==='All'?'Inventory':activeCategory;
    document.getElementById('bagCount').textContent=items.length+' items';

    let html='';
    for(const item of items){
        const css=CATEGORY_CSS[item.category]||'misc';
        html+='<div class="slot cat-'+css+'" onmouseenter="showTip(event,this)" onmouseleave="hideTip()" '
            +'data-name="'+esc(item.name)+'" data-cat="'+esc(item.category)+'" data-desc="'+esc(item.description)+'" '
            +'data-value="'+item.value+'" data-count="'+item.count+'" data-maxstack="'+item.maxStack+'">'
            +'<div class="cat-dot"></div>'
            +'<div class="icon">'+(CATEGORY_ICONS[item.category]||'\uD83D\uDCE6')+'</div>'
            +(item.count>1?'<div class="qty">'+item.count+'</div>':'')
            +'</div>';
    }
    // Pad with empty slots to fill row
    const pad=(Math.ceil(Math.max(items.length,1)/16)*16)-items.length;
    for(let i=0;i<pad;i++) html+='<div class="slot empty"></div>';

    document.getElementById('slotsGrid').innerHTML=html;
}

function esc(s){return(s||'').replace(/"/g,'&quot;').replace(/</g,'&lt;')}

function showTip(ev,el){
    const tip=document.getElementById('tooltip');
    const name=el.dataset.name;
    const cat=el.dataset.cat;
    const desc=el.dataset.desc;
    const val=parseInt(el.dataset.value);
    const count=parseInt(el.dataset.count);
    const maxStack=parseInt(el.dataset.maxstack);
    let h='<div class="tt-name">'+name+'</div>';
    h+='<div class="tt-cat">'+(CATEGORY_ICONS[cat]||'')+' '+cat+'</div>';
    if(desc) h+='<div class="tt-desc">'+desc+'</div>';
    h+='<div class="tt-stats">Bag: <span>'+count+'</span>';
    if(maxStack>1) h+=' / Stack: <span>'+maxStack+'</span>';
    h+='</div>';
    if(val>0) h+='<div class="tt-value">Value: '+val+'g'+(count>1?' ('+val*count+'g total)':'')+'</div>';
    tip.innerHTML=h;
    tip.style.display='block';
    positionTip(ev);
}

function positionTip(ev){
    const tip=document.getElementById('tooltip');
    let x=ev.clientX+14, y=ev.clientY+14;
    const r=tip.getBoundingClientRect();
    if(x+r.width>window.innerWidth) x=ev.clientX-r.width-10;
    if(y+r.height>window.innerHeight) y=ev.clientY-r.height-10;
    tip.style.left=x+'px';tip.style.top=y+'px';
}
document.addEventListener('mousemove',e=>{
    if(document.getElementById('tooltip').style.display==='block') positionTip(e);
});

function hideTip(){document.getElementById('tooltip').style.display='none'}

fetchData();
setInterval(fetchData,2000);
</script>
</body></html>
""";
}

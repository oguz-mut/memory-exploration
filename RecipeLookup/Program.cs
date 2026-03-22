using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// --- Configuration ---
string dataDir = @"C:\Users\oguzb\source\memory-exploration";
string logDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon";
string recipesPath = Path.Combine(dataDir, "recipes.json");
string itemsPath = Path.Combine(dataDir, "items.json");
string logPath = Path.Combine(logDir, "Player.log");
string logPathPrev = Path.Combine(logDir, "Player-prev.log");

if (!File.Exists(logPath))
{
    Console.WriteLine($"Player.log not found, falling back to Player-prev.log");
    logPath = logPathPrev;
}
if (!File.Exists(logPath))
{
    Console.WriteLine("No log file found. Exiting.");
    return;
}

// --- Load JSON data ---
Console.WriteLine("Loading items.json...");
var itemsDoc = JsonDocument.Parse(File.ReadAllText(itemsPath));
Console.WriteLine("Loading recipes.json...");
var recipesDoc = JsonDocument.Parse(File.ReadAllText(recipesPath));

// --- Build item mappings ---
// itemCode -> InternalName, InternalName -> itemCode, itemCode -> display Name
var itemCodeToInternal = new Dictionary<int, string>();
var internalToItemCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var itemCodeToName = new Dictionary<int, string>();

foreach (var prop in itemsDoc.RootElement.EnumerateObject())
{
    string key = prop.Name; // "item_5020"
    var item = prop.Value;
    int code = int.Parse(key.Replace("item_", ""));

    string displayName = item.TryGetProperty("Name", out var n) ? n.GetString()! : key;
    itemCodeToName[code] = displayName;

    if (item.TryGetProperty("InternalName", out var intName))
    {
        string iname = intName.GetString()!;
        itemCodeToInternal[code] = iname;
        internalToItemCode[iname] = code;
    }
}

// Build keyword -> set of internal names (for keyword-based recipe ingredients)
var keywordToInternalNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var prop in itemsDoc.RootElement.EnumerateObject())
{
    var item = prop.Value;
    string? iname = item.TryGetProperty("InternalName", out var intN) ? intN.GetString() : null;
    if (iname == null) continue;
    if (item.TryGetProperty("Keywords", out var kws))
    {
        foreach (var kw in kws.EnumerateArray())
        {
            string kwStr = kw.GetString()!;
            if (!keywordToInternalNames.TryGetValue(kwStr, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                keywordToInternalNames[kwStr] = set;
            }
            set.Add(iname);
        }
    }
}

Console.WriteLine($"Loaded {itemCodeToName.Count} items, {keywordToInternalNames.Count} keywords.");

// --- Load cooking recipes ---
var cookingRecipes = new List<CookingRecipe>();
foreach (var prop in recipesDoc.RootElement.EnumerateObject())
{
    var r = prop.Value;
    if (!r.TryGetProperty("Skill", out var skill) || skill.GetString() != "Cooking")
        continue;

    string name = r.TryGetProperty("Name", out var nm) ? nm.GetString()! : prop.Name;
    int levelReq = r.TryGetProperty("SkillLevelReq", out var lr) ? lr.GetInt32() : 0;

    var ingredients = new List<RecipeIngredient>();
    if (r.TryGetProperty("Ingredients", out var ings))
    {
        foreach (var ing in ings.EnumerateArray())
        {
            if (!ing.TryGetProperty("ItemCode", out var icProp))
            {
                // Keyword-based ingredient (e.g. "CheapMeat") — use Desc and ItemKeys
                string desc = ing.TryGetProperty("Desc", out var d) ? d.GetString()! : "Unknown";
                int ss = ing.TryGetProperty("StackSize", out var ssProp) ? ssProp.GetInt32() : 1;
                // Use first ItemKey as pseudo-internal-name for matching
                string itemKey = "";
                if (ing.TryGetProperty("ItemKeys", out var keys) && keys.GetArrayLength() > 0)
                    itemKey = keys[0].GetString()!;
                ingredients.Add(new RecipeIngredient(-1, ss, desc, itemKey, isKeyword: true));
                continue;
            }
            int itemCode = icProp.GetInt32();
            int stackSize = ing.TryGetProperty("StackSize", out var szProp) ? szProp.GetInt32() : 1;
            string ingName = itemCodeToName.GetValueOrDefault(itemCode, $"Item#{itemCode}");
            string ingInternal = itemCodeToInternal.GetValueOrDefault(itemCode, $"Unknown_{itemCode}");
            ingredients.Add(new RecipeIngredient(itemCode, stackSize, ingName, ingInternal));
        }
    }

    cookingRecipes.Add(new CookingRecipe(prop.Name, name, levelReq, ingredients));
}
cookingRecipes.Sort((a, b) => a.SkillLevelReq.CompareTo(b.SkillLevelReq));
Console.WriteLine($"Loaded {cookingRecipes.Count} cooking recipes.");

// --- Build set of item codes that are cooking ingredients ---
var cookingIngredientCodes = new HashSet<int>();
foreach (var recipe in cookingRecipes)
    foreach (var ing in recipe.Ingredients)
        cookingIngredientCodes.Add(ing.ItemCode);

// --- Shared state ---
var inventory = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase); // InternalName -> count
int cookingLevel = 0;
int cookingXp = 0;
int cookingTnl = 0;
int cookingMax = 0;
int cookingBonus = 0;
object stateLock = new();

// Helper: get how many of an ingredient the player has
int GetIngredientCount(RecipeIngredient ing)
{
    if (ing.isKeyword)
    {
        // Sum all inventory items matching this keyword
        if (keywordToInternalNames.TryGetValue(ing.InternalName, out var names))
        {
            int total = 0;
            foreach (var name in names)
                total += inventory.GetValueOrDefault(name, 0);
            return total;
        }
        return 0;
    }
    return inventory.GetValueOrDefault(ing.InternalName, 0);
}

// --- Regex patterns ---
var addItemRx = new Regex(@"ProcessAddItem\((\w+)\((\d+)\),\s*-1,\s*False\)");
var vendorAddRx = new Regex(@"ProcessVendorAddItem\(\d+,\s*(\w+)\((\d+)\),\s*False\)");
var showRecipesRx = new Regex(@"ProcessShowRecipes\(Cooking\)");
var updateSkillRx = new Regex(@"ProcessUpdateSkill\(\{type=Cooking,raw=(\d+),bonus=(\d+),xp=(\d+),tnl=(\d+),max=(\d+)\}");
var loadSkillsRx = new Regex(@"\{type=Cooking,raw=(\d+),bonus=(\d+),xp=(\d+),tnl=(\d+),max=(\d+)\}");

void ProcessLine(string line)
{
    // ProcessAddItem
    var m = addItemRx.Match(line);
    if (m.Success)
    {
        string internalName = m.Groups[1].Value;
        inventory.AddOrUpdate(internalName, 1, (_, old) => old + 1);
        return;
    }

    // ProcessVendorAddItem (sold = removed)
    m = vendorAddRx.Match(line);
    if (m.Success)
    {
        string internalName = m.Groups[1].Value;
        inventory.AddOrUpdate(internalName, 0, (_, old) => Math.Max(0, old - 1));
        return;
    }

    // ProcessUpdateSkill for Cooking
    m = updateSkillRx.Match(line);
    if (m.Success)
    {
        lock (stateLock)
        {
            cookingLevel = int.Parse(m.Groups[1].Value);
            cookingBonus = int.Parse(m.Groups[2].Value);
            cookingXp = int.Parse(m.Groups[3].Value);
            cookingTnl = int.Parse(m.Groups[4].Value);
            cookingMax = int.Parse(m.Groups[5].Value);
        }
        return;
    }

    // ProcessLoadSkills — extract cooking
    if (line.Contains("ProcessLoadSkills"))
    {
        m = loadSkillsRx.Match(line);
        if (m.Success)
        {
            lock (stateLock)
            {
                cookingLevel = int.Parse(m.Groups[1].Value);
                cookingBonus = int.Parse(m.Groups[2].Value);
                cookingXp = int.Parse(m.Groups[3].Value);
                cookingTnl = int.Parse(m.Groups[4].Value);
                cookingMax = int.Parse(m.Groups[5].Value);
            }
        }
        return;
    }

    // ProcessShowRecipes(Cooking)
    if (showRecipesRx.IsMatch(line))
    {
        PrintRecipeReport();
    }
}

void PrintRecipeReport()
{
    int level;
    lock (stateLock) { level = cookingLevel + cookingBonus; }

    Console.WriteLine();
    Console.WriteLine("===================================================================");
    Console.WriteLine($"  COOKING RECIPES  (Cooking Level: {cookingLevel}+{cookingBonus})");
    Console.WriteLine("===================================================================");

    var canMake = new List<string>();
    var closeTo = new List<string>();

    foreach (var recipe in cookingRecipes)
    {
        if (recipe.SkillLevelReq > level) continue;

        var missing = new List<string>();
        bool canCraft = true;

        foreach (var ing in recipe.Ingredients)
        {
            int have = GetIngredientCount(ing);
            if (have < ing.StackSize)
            {
                canCraft = false;
                int need = ing.StackSize - have;
                missing.Add($"{ing.DisplayName} x{need}");
            }
        }

        if (canCraft)
        {
            canMake.Add($"  [OK] {recipe.Name} (Lv{recipe.SkillLevelReq})");
        }
        else if (missing.Count <= 2)
        {
            closeTo.Add($"  [~]  {recipe.Name} (Lv{recipe.SkillLevelReq}) -- Missing: {string.Join(", ", missing)}");
        }
    }

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n CAN MAKE ({canMake.Count}):");
    foreach (var r in canMake) Console.WriteLine(r);

    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\n CLOSE (missing 1-2 ingredients) ({closeTo.Count}):");
    foreach (var r in closeTo) Console.WriteLine(r);

    Console.ResetColor();
    Console.WriteLine("===================================================================\n");
}

(List<CookingRecipe> canMake, List<(CookingRecipe recipe, List<string> missing)> closeTo) GetRecipeStatus()
{
    int level;
    lock (stateLock) { level = cookingLevel + cookingBonus; }

    var can = new List<CookingRecipe>();
    var close = new List<(CookingRecipe, List<string>)>();

    foreach (var recipe in cookingRecipes)
    {
        if (recipe.SkillLevelReq > level) continue;

        var missing = new List<string>();
        bool canCraft = true;

        foreach (var ing in recipe.Ingredients)
        {
            int have = GetIngredientCount(ing);
            if (have < ing.StackSize)
            {
                canCraft = false;
                int need = ing.StackSize - have;
                missing.Add($"{ing.DisplayName} x{need}");
            }
        }

        if (canCraft) can.Add(recipe);
        else if (missing.Count <= 2) close.Add((recipe, missing));
    }

    return (can, close);
}

Dictionary<string, int> GetIngredientInventory()
{
    var result = new Dictionary<string, int>();
    foreach (var kvp in inventory)
    {
        if (kvp.Value <= 0) continue;
        // Check if this internal name corresponds to a cooking ingredient
        if (internalToItemCode.TryGetValue(kvp.Key, out int code) && cookingIngredientCodes.Contains(code))
        {
            string displayName = itemCodeToName.GetValueOrDefault(code, kvp.Key);
            result[displayName] = kvp.Value;
        }
    }
    return result;
}

// --- HTTP Server ---
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9877/");
    try
    {
        listener.Start();
        Console.WriteLine("HTTP server listening on http://localhost:9877/");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to start HTTP server: {ex.Message}");
        return;
    }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await listener.GetContextAsync();
            var request = context.Request;
            var response = context.Response;

            if (request.Url?.AbsolutePath == "/api/state")
            {
                var (can, close) = GetRecipeStatus();
                var ingredientInv = GetIngredientInventory();
                int lvl, xp, tnl, max, bonus;
                lock (stateLock) { lvl = cookingLevel; xp = cookingXp; tnl = cookingTnl; max = cookingMax; bonus = cookingBonus; }

                var state = new
                {
                    cookingLevel = lvl,
                    cookingBonus = bonus,
                    cookingXp = xp,
                    cookingTnl = tnl,
                    cookingMax = max,
                    inventory = ingredientInv.OrderBy(k => k.Key).Select(k => new { name = k.Key, count = k.Value }),
                    canMake = can.Select(r => new { name = r.Name, level = r.SkillLevelReq }),
                    closeTo = close.Select(c => new { name = c.recipe.Name, level = c.recipe.SkillLevelReq, missing = c.missing })
                };

                string json = JsonSerializer.Serialize(state);
                byte[] buf = Encoding.UTF8.GetBytes(json);
                response.ContentType = "application/json";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }
            else
            {
                string html = BuildHtmlPage();
                byte[] buf = Encoding.UTF8.GetBytes(html);
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buf.Length;
                await response.OutputStream.WriteAsync(buf, ct);
            }

            response.OutputStream.Close();
        }
        catch (Exception) when (ct.IsCancellationRequested) { break; }
        catch (Exception ex)
        {
            Console.WriteLine($"HTTP error: {ex.Message}");
        }
    }

    listener.Stop();
}

string BuildHtmlPage()
{
    return """
<!DOCTYPE html>
<html>
<head>
<meta charset="utf-8">
<title>PG Cooking Assistant</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { font-family: 'Segoe UI', Tahoma, sans-serif; background: #1a1a2e; color: #e0e0e0; padding: 20px; }
  h1 { color: #e94560; margin-bottom: 10px; font-size: 1.6em; }
  h2 { margin: 16px 0 8px; font-size: 1.2em; }
  .stats { background: #16213e; padding: 12px 18px; border-radius: 8px; margin-bottom: 16px; display: inline-block; }
  .xp-bar { background: #333; border-radius: 4px; height: 18px; width: 300px; display: inline-block; vertical-align: middle; margin-left: 10px; }
  .xp-fill { background: #e94560; height: 100%; border-radius: 4px; transition: width 0.3s; }
  .section { margin-bottom: 20px; }
  .green { color: #4ecca3; }
  .yellow { color: #f0c040; }
  .inventory-grid { display: flex; flex-wrap: wrap; gap: 6px; }
  .inv-item { background: #16213e; padding: 4px 10px; border-radius: 4px; font-size: 0.9em; }
  .inv-count { color: #e94560; font-weight: bold; }
  table { border-collapse: collapse; width: 100%; max-width: 800px; }
  th, td { padding: 6px 12px; text-align: left; border-bottom: 1px solid #333; }
  th { color: #aaa; font-size: 0.85em; text-transform: uppercase; }
  .missing { color: #e94560; font-size: 0.85em; }
  .badge { display: inline-block; padding: 2px 8px; border-radius: 4px; font-size: 0.8em; margin-right: 4px; }
  .badge-lv { background: #0f3460; }
  #status { font-size: 0.8em; color: #666; margin-top: 20px; }
</style>
</head>
<body>
<h1>Project Gorgon -- Cooking Assistant</h1>
<div class="stats" id="stats">Loading...</div>

<div class="section">
  <h2>Ingredient Inventory</h2>
  <div class="inventory-grid" id="inventory"></div>
</div>

<div class="section">
  <h2 class="green">Recipes You Can Cook NOW</h2>
  <table id="canMake"><thead><tr><th>Recipe</th><th>Level</th></tr></thead><tbody></tbody></table>
</div>

<div class="section">
  <h2 class="yellow">Almost There (Missing 1-2 Ingredients)</h2>
  <table id="closeTo"><thead><tr><th>Recipe</th><th>Level</th><th>Missing</th></tr></thead><tbody></tbody></table>
</div>

<div id="status"></div>

<script>
async function refresh() {
  try {
    const res = await fetch('/api/state');
    const d = await res.json();

    const pct = d.cookingTnl > 0 ? Math.round(d.cookingXp / d.cookingTnl * 100) : 0;
    document.getElementById('stats').innerHTML =
      `<b>Cooking Level:</b> ${d.cookingLevel}` +
      (d.cookingBonus > 0 ? ` (+${d.cookingBonus} bonus)` : '') +
      ` &nbsp;|&nbsp; <b>XP:</b> ${d.cookingXp} / ${d.cookingTnl}` +
      ` <div class="xp-bar"><div class="xp-fill" style="width:${pct}%"></div></div>` +
      ` &nbsp;|&nbsp; <b>Max:</b> ${d.cookingMax}`;

    const inv = document.getElementById('inventory');
    inv.innerHTML = d.inventory.length === 0 ? '<span style="color:#666">No cooking ingredients detected yet</span>' :
      d.inventory.map(i => `<div class="inv-item">${i.name} <span class="inv-count">x${i.count}</span></div>`).join('');

    const canBody = document.querySelector('#canMake tbody');
    canBody.innerHTML = d.canMake.length === 0 ? '<tr><td colspan="2" style="color:#666">None yet</td></tr>' :
      d.canMake.map(r => `<tr><td style="color:#4ecca3">${r.name}</td><td><span class="badge badge-lv">Lv ${r.level}</span></td></tr>`).join('');

    const closeBody = document.querySelector('#closeTo tbody');
    closeBody.innerHTML = d.closeTo.length === 0 ? '<tr><td colspan="3" style="color:#666">None</td></tr>' :
      d.closeTo.map(r => `<tr><td style="color:#f0c040">${r.name}</td><td><span class="badge badge-lv">Lv ${r.level}</span></td><td class="missing">${r.missing.join(', ')}</td></tr>`).join('');

    document.getElementById('status').textContent = 'Last updated: ' + new Date().toLocaleTimeString();
  } catch (e) {
    document.getElementById('status').textContent = 'Error: ' + e.message;
  }
}
refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}

// --- Log tailing ---
async Task TailLog(string path, CancellationToken ct)
{
    Console.WriteLine($"Tailing log: {path}");

    // First, read entire existing file to build initial state
    using (var sr = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)))
    {
        string? line;
        while ((line = await sr.ReadLineAsync(ct)) != null)
        {
            ProcessLine(line);
        }
    }

    int lvl, xp, tnl;
    lock (stateLock) { lvl = cookingLevel; xp = cookingXp; tnl = cookingTnl; }
    Console.WriteLine($"Initial state loaded. Cooking Lv{lvl}, XP {xp}/{tnl}, Inventory items: {inventory.Count(kv => kv.Value > 0)}");

    // Now tail for new lines
    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);

    while (!ct.IsCancellationRequested)
    {
        string? newLine = await reader.ReadLineAsync(ct);
        if (newLine != null)
        {
            ProcessLine(newLine);
        }
        else
        {
            await Task.Delay(250, ct);
        }
    }
}

// --- Main execution ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var httpTask = RunHttpServer(cts.Token);
var tailTask = TailLog(logPath, cts.Token);

Console.WriteLine("Press Ctrl+C to stop.");

try
{
    await Task.WhenAll(httpTask, tailTask);
}
catch (OperationCanceledException) { }

Console.WriteLine("Shutting down.");

// --- Records ---
record RecipeIngredient(int ItemCode, int StackSize, string DisplayName, string InternalName, bool isKeyword = false);
record CookingRecipe(string Key, string Name, int SkillLevelReq, List<RecipeIngredient> Ingredients);

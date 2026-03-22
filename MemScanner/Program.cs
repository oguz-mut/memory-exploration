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
string knownRecipesPath = Path.Combine(dataDir, "known_recipes.json");
string logPath = Path.Combine(logDir, "Player.log");
string logPathPrev = Path.Combine(logDir, "Player-prev.log");

if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
    logPath = logPathPrev;
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
var itemCodeToInternal = new Dictionary<int, string>();
var internalToItemCode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var itemCodeToName = new Dictionary<int, string>();

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
}

// Keyword -> internal names
var keywordToInternalNames = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var prop in itemsDoc.RootElement.EnumerateObject())
{
    string? iname = prop.Value.TryGetProperty("InternalName", out var intN) ? intN.GetString() : null;
    if (iname == null) continue;
    if (prop.Value.TryGetProperty("Keywords", out var kws))
    {
        foreach (var kw in kws.EnumerateArray())
        {
            string kwStr = kw.GetString()!;
            if (!keywordToInternalNames.TryGetValue(kwStr, out var set))
                keywordToInternalNames[kwStr] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(iname);
        }
    }
}
Console.WriteLine($"Loaded {itemCodeToName.Count} items, {keywordToInternalNames.Count} keywords.");

// --- Helper: look up result item details from items.json ---
RecipeResultItem LookupResultItem(int itemCode, int stackSize, float pctChance)
{
    string displayName = itemCodeToName.GetValueOrDefault(itemCode, $"Item#{itemCode}");
    string? foodDesc = null;
    var effectDescs = new List<string>();
    var skillReqs = new Dictionary<string, int>();
    int goldValue = 0;

    string itemKey = $"item_{itemCode}";
    if (itemsDoc.RootElement.TryGetProperty(itemKey, out var item))
    {
        if (item.TryGetProperty("FoodDesc", out var fd)) foodDesc = fd.GetString();
        if (item.TryGetProperty("EffectDescs", out var eds))
            foreach (var ed in eds.EnumerateArray())
                effectDescs.Add(ed.GetString()!);
        if (item.TryGetProperty("SkillReqs", out var sr))
            foreach (var s in sr.EnumerateObject())
                if (s.Value.TryGetInt32(out int v)) skillReqs[s.Name] = v;
        if (item.TryGetProperty("Value", out var val)) goldValue = val.GetInt32();
    }
    return new RecipeResultItem(itemCode, displayName, stackSize, pctChance, foodDesc, effectDescs, skillReqs, goldValue);
}

// --- Load cooking recipes (expanded) ---
var cookingRecipes = new List<CookingRecipe>();
foreach (var prop in recipesDoc.RootElement.EnumerateObject())
{
    var r = prop.Value;
    if (!r.TryGetProperty("Skill", out var skill) || skill.GetString() != "Cooking") continue;

    string name = r.TryGetProperty("Name", out var nm) ? nm.GetString()! : prop.Name;
    int levelReq = r.TryGetProperty("SkillLevelReq", out var lr) ? lr.GetInt32() : 0;
    string desc = r.TryGetProperty("Description", out var ds) ? ds.GetString()! : "";

    // Ingredients
    var ingredients = new List<RecipeIngredient>();
    if (r.TryGetProperty("Ingredients", out var ings))
    {
        foreach (var ing in ings.EnumerateArray())
        {
            float chanceToConsume = ing.TryGetProperty("ChanceToConsume", out var ctc) ? ctc.GetSingle() : 1f;
            if (!ing.TryGetProperty("ItemCode", out var icProp))
            {
                string idesc = ing.TryGetProperty("Desc", out var d) ? d.GetString()! : "Unknown";
                int ss = ing.TryGetProperty("StackSize", out var ssProp) ? ssProp.GetInt32() : 1;
                string itemKey = "";
                if (ing.TryGetProperty("ItemKeys", out var keys) && keys.GetArrayLength() > 0)
                    itemKey = keys[0].GetString()!;
                ingredients.Add(new RecipeIngredient(-1, ss, idesc, itemKey, chanceToConsume, isKeyword: true));
                continue;
            }
            int itemCode = icProp.GetInt32();
            int stackSize = ing.TryGetProperty("StackSize", out var szProp) ? szProp.GetInt32() : 1;
            string ingName = itemCodeToName.GetValueOrDefault(itemCode, $"Item#{itemCode}");
            string ingInternal = itemCodeToInternal.GetValueOrDefault(itemCode, $"Unknown_{itemCode}");
            ingredients.Add(new RecipeIngredient(itemCode, stackSize, ingName, ingInternal, chanceToConsume));
        }
    }

    // Result items
    var resultItems = new List<RecipeResultItem>();
    if (r.TryGetProperty("ResultItems", out var ris))
    {
        if (ris.ValueKind == JsonValueKind.Array)
        {
            foreach (var ri in ris.EnumerateArray())
            {
                int riCode = ri.TryGetProperty("ItemCode", out var ric) ? ric.GetInt32() : 0;
                int riStack = ri.TryGetProperty("StackSize", out var ris2) ? ris2.GetInt32() : 1;
                float riPct = ri.TryGetProperty("PercentChance", out var rip) ? rip.GetSingle() : 1f;
                resultItems.Add(LookupResultItem(riCode, riStack, riPct));
            }
        }
        else if (ris.ValueKind == JsonValueKind.Object)
        {
            int riCode = ris.TryGetProperty("ItemCode", out var ric) ? ric.GetInt32() : 0;
            int riStack = ris.TryGetProperty("StackSize", out var ris2) ? ris2.GetInt32() : 1;
            float riPct = ris.TryGetProperty("PercentChance", out var rip) ? rip.GetSingle() : 1f;
            resultItems.Add(LookupResultItem(riCode, riStack, riPct));
        }
    }

    // Category from Keywords
    string category = "Other";
    if (r.TryGetProperty("Keywords", out var kws2))
    {
        foreach (var kw in kws2.EnumerateArray())
        {
            string k = kw.GetString()!;
            if (k == "MealRecipe") { category = "Meal"; break; }
            if (k == "SnackRecipe") { category = "Snack"; break; }
            if (k == "InstantSnackRecipe") { category = "Instant"; break; }
        }
    }

    // Station required
    bool requiresStation = Regex.IsMatch(desc, @"stove|fire pit|oven", RegexOptions.IgnoreCase);

    // XP fields
    int rewardXp = r.TryGetProperty("RewardSkillXp", out var rxp) ? rxp.GetInt32() : 0;
    int rewardXpFirst = r.TryGetProperty("RewardSkillXpFirstTime", out var rxpf) ? rxpf.GetInt32() : 0;
    int dropOffLevel = r.TryGetProperty("RewardSkillXpDropOffLevel", out var dol) ? dol.GetInt32() : 999;
    float dropOffPct = r.TryGetProperty("RewardSkillXpDropOffPct", out var dop) ? dop.GetSingle() : 0.1f;
    float dropOffRate = r.TryGetProperty("RewardSkillXpDropOffRate", out var dor) ? dor.GetSingle() : 5f;

    // Prereq
    string? prereq = r.TryGetProperty("PrereqRecipe", out var pr) ? $"recipe_{pr.GetInt32()}" : null;

    cookingRecipes.Add(new CookingRecipe(prop.Name, name, levelReq, ingredients, resultItems,
        category, requiresStation, rewardXp, rewardXpFirst, dropOffLevel, dropOffPct, dropOffRate, prereq, desc));
}
cookingRecipes.Sort((a, b) => a.SkillLevelReq.CompareTo(b.SkillLevelReq));
Console.WriteLine($"Loaded {cookingRecipes.Count} cooking recipes.");

// --- Cooking ingredient codes ---
var cookingIngredientCodes = new HashSet<int>();
foreach (var recipe in cookingRecipes)
    foreach (var ing in recipe.Ingredients)
        cookingIngredientCodes.Add(ing.ItemCode);

// --- Known recipes (persisted) ---
var knownRecipes = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
if (File.Exists(knownRecipesPath))
{
    try
    {
        var knownData = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(knownRecipesPath));
        if (knownData != null) foreach (var kv in knownData) knownRecipes[kv.Key] = kv.Value;
        Console.WriteLine($"Loaded {knownRecipes.Count} known recipes from file.");
    }
    catch { }
}
bool knownRecipesDirty = false;

void SaveKnownRecipes()
{
    try
    {
        File.WriteAllText(knownRecipesPath, JsonSerializer.Serialize(knownRecipes.ToDictionary(kv => kv.Key, kv => kv.Value)));
        knownRecipesDirty = false;
    }
    catch { }
}

// --- Shared state ---
var inventory = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
var storageItems = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase); // InternalName -> count in storage
var objectIdToInternal = new ConcurrentDictionary<string, string>(); // objectInstanceId -> InternalName (for remove tracking)
int cookingLevel = 0, cookingXp = 0, cookingTnl = 0, cookingMax = 0, cookingBonus = 0;
object stateLock = new();

int GetIngredientCount(RecipeIngredient ing)
{
    if (ing.isKeyword)
    {
        if (keywordToInternalNames.TryGetValue(ing.InternalName, out var names))
            return names.Sum(n => inventory.GetValueOrDefault(n, 0) + storageItems.GetValueOrDefault(n, 0));
        return 0;
    }
    return inventory.GetValueOrDefault(ing.InternalName, 0) + storageItems.GetValueOrDefault(ing.InternalName, 0);
}

(int bag, int storage) GetIngredientCountSplit(RecipeIngredient ing)
{
    if (ing.isKeyword)
    {
        if (keywordToInternalNames.TryGetValue(ing.InternalName, out var names))
        {
            int bag = names.Sum(n => inventory.GetValueOrDefault(n, 0));
            int stor = names.Sum(n => storageItems.GetValueOrDefault(n, 0));
            return (bag, stor);
        }
        return (0, 0);
    }
    return (inventory.GetValueOrDefault(ing.InternalName, 0), storageItems.GetValueOrDefault(ing.InternalName, 0));
}

int CalcEffectiveXp(CookingRecipe recipe, int playerLevel)
{
    if (playerLevel < recipe.DropOffLevel) return recipe.RewardXp;
    int levelsOver = playerLevel - recipe.DropOffLevel;
    int dropSteps = (int)(levelsOver / Math.Max(1, recipe.DropOffRate));
    float mult = (float)Math.Pow(1.0 - recipe.DropOffPct, dropSteps);
    return Math.Max(1, (int)(recipe.RewardXp * mult));
}

// --- Regex patterns ---
var addItemRx = new Regex(@"ProcessAddItem\((\w+)\((\d+)\),\s*-1,\s*False\)");
var vendorAddRx = new Regex(@"ProcessVendorAddItem\(\d+,\s*(\w+)\((\d+)\),\s*False\)");
var showRecipesRx = new Regex(@"ProcessShowRecipes\(Cooking\)");
var updateSkillRx = new Regex(@"ProcessUpdateSkill\(\{type=Cooking,raw=(\d+),bonus=(\d+),xp=(\d+),tnl=(\d+),max=(\d+)\}");
var loadSkillsRx = new Regex(@"\{type=Cooking,raw=(\d+),bonus=(\d+),xp=(\d+),tnl=(\d+),max=(\d+)\}");
var updateRecipeRx = new Regex(@"ProcessUpdateRecipe\((\d+),\s*(\w+)\)");
var addToStorageRx = new Regex(@"ProcessAddToStorageVault\(\d+,\s*-?\d+,\s*\d+,\s*(\w+)\((\d+)\)\)");
var removeFromStorageRx = new Regex(@"ProcessRemoveFromStorageVault\(\d+,\s*-?\d+,\s*(\d+),\s*(\d+)\)");

void ProcessLine(string line)
{
    var m = addItemRx.Match(line);
    if (m.Success) { inventory.AddOrUpdate(m.Groups[1].Value, 1, (_, old) => old + 1); return; }

    m = vendorAddRx.Match(line);
    if (m.Success) { inventory.AddOrUpdate(m.Groups[1].Value, 0, (_, old) => Math.Max(0, old - 1)); return; }

    m = addToStorageRx.Match(line);
    if (m.Success)
    {
        string internalName = m.Groups[1].Value;
        string objectId = m.Groups[2].Value;
        objectIdToInternal[objectId] = internalName;
        storageItems.AddOrUpdate(internalName, 1, (_, old) => old + 1);
        return;
    }

    m = removeFromStorageRx.Match(line);
    if (m.Success)
    {
        string objectId = m.Groups[2].Value;
        if (objectIdToInternal.TryGetValue(objectId, out string? internalName))
        {
            storageItems.AddOrUpdate(internalName, 0, (_, old) => Math.Max(0, old - 1));
        }
        return;
    }

    m = updateRecipeRx.Match(line);
    if (m.Success)
    {
        string key = $"recipe_{m.Groups[1].Value}";
        knownRecipes.AddOrUpdate(key, 1, (_, old) => old + 1);
        knownRecipesDirty = true;
        return;
    }

    m = updateSkillRx.Match(line);
    if (m.Success)
    {
        lock (stateLock) { cookingLevel = int.Parse(m.Groups[1].Value); cookingBonus = int.Parse(m.Groups[2].Value); cookingXp = int.Parse(m.Groups[3].Value); cookingTnl = int.Parse(m.Groups[4].Value); cookingMax = int.Parse(m.Groups[5].Value); }
        return;
    }

    if (line.Contains("ProcessLoadSkills"))
    {
        m = loadSkillsRx.Match(line);
        if (m.Success) lock (stateLock) { cookingLevel = int.Parse(m.Groups[1].Value); cookingBonus = int.Parse(m.Groups[2].Value); cookingXp = int.Parse(m.Groups[3].Value); cookingTnl = int.Parse(m.Groups[4].Value); cookingMax = int.Parse(m.Groups[5].Value); }
        return;
    }

    if (showRecipesRx.IsMatch(line)) PrintRecipeReport();
}

void PrintRecipeReport()
{
    int level; lock (stateLock) { level = cookingLevel + cookingBonus; }
    Console.WriteLine($"\n=== COOKING (Lv{cookingLevel}+{cookingBonus}) | Known: {knownRecipes.Count} ===");
    var (can, close, _) = GetRecipeStatus();
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($" CAN MAKE ({can.Count}):");
    foreach (var r in can) Console.WriteLine($"  [OK] {r.Name} (Lv{r.SkillLevelReq}) {(knownRecipes.ContainsKey(r.Key) ? "" : "[NEW?]")} +{CalcEffectiveXp(r, level)}xp");
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($" CLOSE ({close.Count}):");
    foreach (var (r, missing) in close) Console.WriteLine($"  [~]  {r.Name} (Lv{r.SkillLevelReq}) -- Missing: {string.Join(", ", missing.Select(m => $"{m.name} x{m.need}"))}");
    Console.ResetColor();
}

(List<CookingRecipe> canMake, List<(CookingRecipe recipe, List<(string name, int need)> missing)> closeTo, Dictionary<string, (int count, List<string> recipes)> shoppingList) GetRecipeStatus()
{
    int level; lock (stateLock) { level = cookingLevel + cookingBonus; }
    var can = new List<CookingRecipe>();
    var close = new List<(CookingRecipe, List<(string, int)>)>();
    var shopAgg = new Dictionary<string, (int count, List<string> recipes)>(StringComparer.OrdinalIgnoreCase);

    foreach (var recipe in cookingRecipes)
    {
        if (recipe.SkillLevelReq > level) continue;
        var missing = new List<(string name, int need)>();
        bool canCraft = true;

        foreach (var ing in recipe.Ingredients)
        {
            int have = GetIngredientCount(ing);
            if (have < ing.StackSize)
            {
                canCraft = false;
                int need = ing.StackSize - have;
                missing.Add((ing.DisplayName, need));
            }
        }

        if (canCraft) can.Add(recipe);
        else if (missing.Count <= 2)
        {
            close.Add((recipe, missing));
            foreach (var (mName, mNeed) in missing)
            {
                if (!shopAgg.ContainsKey(mName)) shopAgg[mName] = (0, new List<string>());
                var entry = shopAgg[mName];
                entry.count += mNeed;
                entry.recipes.Add(recipe.Name);
                shopAgg[mName] = entry;
            }
        }
    }
    return (can, close, shopAgg);
}

List<object> GetIngredientInventory()
{
    var result = new List<object>();
    // Collect all internal names from both inventory and storage
    var allNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kvp in inventory) if (kvp.Value > 0) allNames.Add(kvp.Key);
    foreach (var kvp in storageItems) if (kvp.Value > 0) allNames.Add(kvp.Key);

    foreach (var iname in allNames)
    {
        if (internalToItemCode.TryGetValue(iname, out int code) && cookingIngredientCodes.Contains(code))
        {
            int bagCount = inventory.GetValueOrDefault(iname, 0);
            int storageCount = storageItems.GetValueOrDefault(iname, 0);
            if (bagCount > 0 || storageCount > 0)
                result.Add(new { name = itemCodeToName.GetValueOrDefault(code, iname), bag = bagCount, storage = storageCount });
        }
    }
    result.Sort((a, b) => string.Compare(((dynamic)a).name, ((dynamic)b).name, StringComparison.OrdinalIgnoreCase));
    return result;
}

// --- HTTP Server ---
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9878/");
    try { listener.Start(); Console.WriteLine("HTTP server listening on http://localhost:9878/"); }
    catch (Exception ex) { Console.WriteLine($"Failed to start HTTP server: {ex.Message}"); return; }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var context = await listener.GetContextAsync();
            var response = context.Response;

            if (context.Request.Url?.AbsolutePath == "/api/state")
            {
                var (can, close, shopList) = GetRecipeStatus();
                var ingredientInv = GetIngredientInventory();
                int lvl, xp, tnl, max, bonus;
                lock (stateLock) { lvl = cookingLevel; xp = cookingXp; tnl = cookingTnl; max = cookingMax; bonus = cookingBonus; }
                int effectiveLvl = lvl + bonus;
                int totalStorageCount = storageItems.Values.Where(v => v > 0).Sum();

                var state = new
                {
                    cookingLevel = lvl, cookingBonus = bonus, cookingXp = xp, cookingTnl = tnl, cookingMax = max,
                    knownCount = knownRecipes.Count,
                    totalAtLevel = cookingRecipes.Count(r => r.SkillLevelReq <= effectiveLvl),
                    totalStorageItems = totalStorageCount,
                    inventory = ingredientInv,
                    canMake = can.Select(r => MapRecipe(r, effectiveLvl)),
                    closeTo = close.Select(c => new { recipe = MapRecipe(c.recipe, effectiveLvl), missing = c.missing.Select(m => new { name = m.name, need = m.need }) }),
                    shoppingList = shopList.OrderByDescending(k => k.Value.recipes.Count).Select(k => new { name = k.Key, totalNeed = k.Value.count, recipesUnlocked = k.Value.recipes.Count, recipes = k.Value.recipes }),
                    bestForLeveling = cookingRecipes.Where(r => r.SkillLevelReq <= effectiveLvl && r.RewardXp > 0)
                        .OrderByDescending(r => {
                            int xpVal = !knownRecipes.ContainsKey(r.Key) && r.RewardXpFirstTime > 0 ? r.RewardXpFirstTime : CalcEffectiveXp(r, effectiveLvl);
                            int ingCount = r.Ingredients.Sum(i => i.StackSize);
                            return ingCount > 0 ? (float)xpVal / ingCount : 0;
                        }).Take(20).Select(r => MapRecipe(r, effectiveLvl)),
                    firstTimeBonuses = cookingRecipes.Where(r => r.SkillLevelReq <= effectiveLvl && !knownRecipes.ContainsKey(r.Key) && r.RewardXpFirstTime > 0)
                        .OrderByDescending(r => r.RewardXpFirstTime).Take(20).Select(r => MapRecipe(r, effectiveLvl))
                };

                string json = JsonSerializer.Serialize(state);
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

object MapRecipe(CookingRecipe r, int playerLevel)
{
    bool isKnown = knownRecipes.ContainsKey(r.Key);
    int xpEff = CalcEffectiveXp(r, playerLevel);
    int xpActual = !isKnown && r.RewardXpFirstTime > 0 ? r.RewardXpFirstTime : xpEff;
    int ingCount = r.Ingredients.Sum(i => i.StackSize);
    bool canMakeNow = r.Ingredients.All(i => GetIngredientCount(i) >= i.StackSize);

    return new
    {
        key = r.Key, name = r.Name, level = r.SkillLevelReq, category = r.Category,
        requiresStation = r.RequiresStation, known = isKnown,
        craftCount = knownRecipes.GetValueOrDefault(r.Key, 0),
        xp = xpActual, xpBase = xpEff, xpFirstTime = r.RewardXpFirstTime,
        xpPerIngredient = ingCount > 0 ? Math.Round((float)xpActual / ingCount, 1) : 0,
        dropOffLevel = r.DropOffLevel, canMakeNow,
        ingredients = r.Ingredients.Select(i => {
            var (bag, storage) = GetIngredientCountSplit(i);
            return new { name = i.DisplayName, need = i.StackSize, have = bag + storage, haveBag = bag, haveStorage = storage, optional = i.ChanceToConsume < 1f };
        }),
        results = r.ResultItems.Select(ri => new { name = ri.DisplayName, count = ri.StackSize, chance = ri.PercentChance, foodDesc = ri.FoodDesc, effects = ri.EffectDescs, skillReqs = ri.SkillReqs, value = ri.GoldValue }),
        description = r.Description
    };
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
    if (knownRecipesDirty) SaveKnownRecipes();

    int lvl, xp, tnl;
    lock (stateLock) { lvl = cookingLevel; xp = cookingXp; tnl = cookingTnl; }
    Console.WriteLine($"Initial state: Cooking Lv{lvl}, XP {xp}/{tnl}, Inventory: {inventory.Count(kv => kv.Value > 0)}, Known recipes: {knownRecipes.Count}");

    // Also backfill from prev log for known recipes if we tailed Player.log
    if (path != logPathPrev && File.Exists(logPathPrev))
    {
        using var sr2 = new StreamReader(new FileStream(logPathPrev, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        string? line2;
        while ((line2 = await sr2.ReadLineAsync(ct)) != null)
        {
            var m = updateRecipeRx.Match(line2);
            if (m.Success) knownRecipes.AddOrUpdate($"recipe_{m.Groups[1].Value}", 1, (_, old) => old + 1);
        }
        if (knownRecipesDirty) SaveKnownRecipes();
    }

    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);
    DateTime lastSave = DateTime.Now;

    while (!ct.IsCancellationRequested)
    {
        string? newLine = await reader.ReadLineAsync(ct);
        if (newLine != null)
        {
            ProcessLine(newLine);
            if (knownRecipesDirty && (DateTime.Now - lastSave).TotalSeconds > 5)
            {
                SaveKnownRecipes();
                lastSave = DateTime.Now;
            }
        }
        else await Task.Delay(250, ct);
    }
}

// --- Main ---
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
PrintRecipeReport();
var httpTask = RunHttpServer(cts.Token);
var tailTask = TailLog(logPath, cts.Token);
Console.WriteLine("Press Ctrl+C to stop.");
try { await Task.WhenAll(httpTask, tailTask); } catch (OperationCanceledException) { }
if (knownRecipesDirty) SaveKnownRecipes();
Console.WriteLine("Shutting down.");

// --- Records ---
record RecipeIngredient(int ItemCode, int StackSize, string DisplayName, string InternalName, float ChanceToConsume = 1f, bool isKeyword = false);
record RecipeResultItem(int ItemCode, string DisplayName, int StackSize, float PercentChance, string? FoodDesc, List<string> EffectDescs, Dictionary<string, int> SkillReqs, int GoldValue);
record CookingRecipe(string Key, string Name, int SkillLevelReq, List<RecipeIngredient> Ingredients,
    List<RecipeResultItem> ResultItems, string Category, bool RequiresStation,
    int RewardXp, int RewardXpFirstTime, int DropOffLevel, float DropOffPct, float DropOffRate,
    string? PrereqRecipeKey, string Description);

// --- HTML (must be in a partial class to work with top-level statements) ---
static partial class HtmlContent
{
    public const string DASHBOARD = """
<!DOCTYPE html>
<html><head>
<meta charset="utf-8"><title>PG Cooking Helper</title>
<style>
*{box-sizing:border-box;margin:0;padding:0}
body{font-family:'Segoe UI',sans-serif;background:#0a0e17;color:#e0e6f0;font-size:14px;line-height:1.5}
.container{max-width:960px;margin:0 auto;padding:16px}
.header{position:sticky;top:0;z-index:100;background:#141b2d;border:1px solid #1e2a42;border-radius:8px;padding:14px 18px;margin-bottom:14px;display:flex;align-items:center;gap:16px;flex-wrap:wrap}
.header h1{color:#60a5fa;font-size:18px;white-space:nowrap}
.stat-box{background:#0a0e17;padding:6px 12px;border-radius:6px;font-size:13px;text-align:center}
.stat-label{color:#6b7280;font-size:11px;display:block}
.stat-val{font-weight:700;font-size:15px}
.xp-bar{background:#1a2235;border-radius:4px;height:18px;width:300px;position:relative;overflow:hidden;margin-top:4px}
.xp-fill{background:linear-gradient(90deg,#7c3aed,#a78bfa);height:100%;border-radius:4px;transition:width .3s}
.xp-label{position:absolute;top:0;left:0;right:0;text-align:center;font-size:10px;line-height:18px;color:#fff;font-weight:600}
.tabs{display:flex;gap:4px;margin-bottom:0}
.tab{padding:10px 20px;background:#141b2d;border:1px solid #1e2a42;border-bottom:none;border-radius:8px 8px 0 0;cursor:pointer;font-size:14px;color:#6b7280;transition:all .2s;user-select:none}
.tab:hover{color:#e0e6f0;background:#1a2540}
.tab.active{background:#141b2d;color:#60a5fa;font-weight:600;border-bottom:1px solid #141b2d;position:relative;z-index:1}
.panel{display:none;background:#141b2d;border:1px solid #1e2a42;border-radius:0 8px 8px 8px;padding:18px;margin-top:-1px}
.panel.active{display:block}
.section-title{font-size:16px;font-weight:700;margin-bottom:10px;padding-bottom:6px;border-bottom:1px solid #1e2a42}
.section-title.green{color:#4ade80}
.section-title.yellow{color:#fbbf24}
.section-title.blue{color:#60a5fa}
.section-title.purple{color:#a78bfa}
.section-title.pink{color:#f472b6}

/* Ingredient grid */
.ing-grid{display:flex;flex-wrap:wrap;gap:6px;margin-bottom:18px}
.ing-card{background:#0a0e17;border:1px solid #1e2a42;border-radius:6px;padding:6px 10px;font-size:12px;display:flex;align-items:center;gap:6px;transition:border-color .2s}
.ing-card.used{border-color:#4ade8050}
.ing-card.dim{opacity:.5}
.ing-name{font-weight:600}
.ing-bag{color:#fbbf24;font-size:11px}
.ing-storage{color:#60a5fa;font-size:11px}

/* Recipe cards */
.recipe-card{background:#0a0e17;border:1px solid #1e2a42;border-left:3px solid #4ade80;border-radius:8px;padding:14px;margin-bottom:8px;transition:background .2s;cursor:pointer}
.recipe-card:hover{background:#111827}
.recipe-card.yellow-border{border-left-color:#fbbf24}
.recipe-card.red-border{border-left-color:#f87171}
.recipe-card .rc-top{display:flex;align-items:flex-start;gap:12px}
.recipe-card .rc-left{display:flex;align-items:center;gap:6px;flex-shrink:0}
.recipe-card .rc-center{flex:1;min-width:0}
.recipe-card .rc-right{text-align:right;flex-shrink:0}
.rc-name{font-weight:700;font-size:15px}
.rc-pills{display:flex;flex-wrap:wrap;gap:4px;margin-top:6px}
.pill{display:inline-block;padding:2px 8px;border-radius:10px;font-size:11px;font-weight:600}
.pill.green{background:#14532d;color:#4ade80}
.pill.red{background:#450a0a;color:#f87171}
.pill.yellow{background:#422006;color:#fbbf24}
.cat-badge{display:inline-block;padding:2px 8px;border-radius:4px;font-size:10px;font-weight:700;text-transform:uppercase}
.cat-Meal{background:#14532d;color:#86efac}
.cat-Snack{background:#1e3a5f;color:#93c5fd}
.cat-Instant{background:#78350f;color:#fed7aa}
.cat-Other{background:#374151;color:#9ca3af}
.xp-badge{background:#2e1065;color:#a78bfa;padding:3px 10px;border-radius:6px;font-weight:700;font-size:14px;display:inline-block}
.xp-first-badge{color:#f472b6;font-size:11px;display:block;margin-top:2px}
.station-icon{color:#f59e0b;font-size:14px}
.known-icon{color:#4ade80;font-size:14px}
.unknown-icon{color:#6b7280;font-size:12px}
.food-desc{color:#60a5fa;font-size:12px;font-weight:600;margin-top:2px}
.rc-detail{display:none;margin-top:10px;padding-top:10px;border-top:1px solid #1e2a42;font-size:12px;color:#9ca3af}
.rc-detail.open{display:block}
.rc-detail .effect{padding:2px 0}

/* Leveling */
.callout{background:#1e3a5f;border:1px solid #2563eb40;border-radius:8px;padding:14px;margin-bottom:16px;font-size:14px}
.xp-eff-bar-wrap{display:flex;align-items:center;gap:8px;margin-bottom:6px;font-size:13px}
.xp-eff-bar{height:14px;background:linear-gradient(90deg,#7c3aed,#a78bfa);border-radius:3px;min-width:4px;transition:width .3s}
.new-badge{background:#f472b6;color:#0a0e17;font-size:10px;font-weight:800;padding:1px 6px;border-radius:4px;margin-right:4px}

/* All Recipes */
.filter-bar{display:flex;align-items:center;gap:10px;margin-bottom:14px;flex-wrap:wrap}
.filter-bar input[type=text]{background:#0a0e17;border:1px solid #1e2a42;border-radius:6px;padding:8px 12px;color:#e0e6f0;font-size:14px;width:250px;outline:none}
.filter-bar input[type=text]:focus{border-color:#60a5fa}
.filter-btn{padding:6px 12px;border-radius:6px;border:1px solid #1e2a42;background:#0a0e17;color:#6b7280;cursor:pointer;font-size:12px;transition:all .2s}
.filter-btn:hover,.filter-btn.active{background:#1e3a5f;color:#60a5fa;border-color:#60a5fa}
.toggle-label{font-size:12px;color:#6b7280;display:flex;align-items:center;gap:4px;cursor:pointer}
.toggle-label input{accent-color:#60a5fa}

/* Shopping */
.shop-card{background:#0a0e17;border:1px solid #1e2a42;border-radius:8px;padding:14px;margin-bottom:8px}
.shop-name{font-weight:700;color:#fbbf24;font-size:15px}
.shop-need{color:#6b7280;font-size:13px}
.progress-bar-wrap{background:#1a2235;border-radius:4px;height:10px;margin:8px 0;overflow:hidden}
.progress-bar-fill{height:100%;border-radius:4px;transition:width .3s}
.progress-bar-fill.green{background:#4ade80}
.progress-bar-fill.yellow{background:#fbbf24}
.progress-bar-fill.red{background:#f87171}
.shop-unlocks{font-size:12px;color:#9ca3af;margin-top:4px}
.shop-unlocks b{color:#e0e6f0}

.dim{color:#4b5563}
.mt{margin-top:16px}
.footer{text-align:center;color:#374151;font-size:11px;margin-top:16px;padding:8px}
</style></head><body>
<div class="container">

<div class="header" id="header">Loading...</div>

<div class="tabs">
  <div class="tab active" onclick="switchTab('craftable')">Craftable</div>
  <div class="tab" onclick="switchTab('leveling')">Leveling Guide</div>
  <div class="tab" onclick="switchTab('all')">All Recipes</div>
  <div class="tab" onclick="switchTab('shopping')">Shopping List</div>
</div>

<div class="panel active" id="p-craftable"></div>
<div class="panel" id="p-leveling"></div>
<div class="panel" id="p-all"></div>
<div class="panel" id="p-shopping"></div>

<div class="footer">Auto-refreshes every 2.5s | Data from official CDN recipes.json</div>
</div>

<script>
var D=null;
var expandedCards={};
var allRecipeFilter='All';
var allRecipeSearch='';
var allRecipeCanOnly=false;

function switchTab(id){
  var tabs=['craftable','leveling','all','shopping'];
  document.querySelectorAll('.tab').forEach(function(t,i){t.classList.toggle('active',tabs[i]===id)});
  document.querySelectorAll('.panel').forEach(function(p,i){p.classList.toggle('active',['p-craftable','p-leveling','p-all','p-shopping'][i]==='p-'+id)});
}
function esc(s){if(!s)return'';var d=document.createElement('div');d.textContent=s;return d.innerHTML}
function toggleExpand(key){expandedCards[key]=!expandedCards[key];var el=document.getElementById('detail-'+key);if(el)el.classList.toggle('open',!!expandedCards[key])}

function catBadge(c){return '<span class="cat-badge cat-'+c+'">'+c+'</span>'}
function stationIcon(b){return b?'<span class="station-icon" title="Requires cooking station">&#128293;</span>':''}
function knownIcon(k,cnt){return k?'<span class="known-icon" title="Crafted '+cnt+'x">&#10003;</span>':'<span class="unknown-icon" title="Possibly new">?</span>'}

function recipeCard(r,borderClass,miss){
  var bc=borderClass||'';
  var xpFirst=(!r.known&&r.xpFirstTime>0)?'<span class="xp-first-badge">First: +'+r.xpFirstTime+'</span>':'';
  var pills='';
  if(r.ingredients){
    r.ingredients.forEach(function(i){
      var cls=i.have>=i.need?'green':(i.have>0?'yellow':'red');
      var bagStr=i.haveBag!==undefined?' [B:'+i.haveBag+' S:'+i.haveStorage+']':'';
      pills+='<span class="pill '+cls+'">'+esc(i.name)+' '+i.have+'/'+i.need+'</span>';
    });
  }
  var missHtml='';
  if(miss&&miss.length){
    miss.forEach(function(m){missHtml+='<span class="pill red">Need: '+esc(m.name)+' x'+m.need+'</span>'});
  }
  var foodDesc='';
  if(r.results&&r.results.length&&r.results[0].foodDesc){foodDesc='<div class="food-desc">'+esc(r.results[0].foodDesc)+'</div>'}
  var detailHtml='';
  if(r.results&&r.results.length){
    var res=r.results[0];
    if(res.effects&&res.effects.length){detailHtml+=res.effects.map(function(e){return'<div class="effect">'+esc(e)+'</div>'}).join('')}
  }
  if(r.description){detailHtml+='<div class="effect" style="margin-top:4px;color:#6b7280"><i>'+esc(r.description)+'</i></div>'}
  var isOpen=expandedCards[r.key]?'open':'';

  return '<div class="recipe-card '+bc+'" onclick="toggleExpand(\''+r.key+'\')">'+
    '<div class="rc-top">'+
      '<div class="rc-left">'+catBadge(r.category)+stationIcon(r.requiresStation)+knownIcon(r.known,r.craftCount)+'</div>'+
      '<div class="rc-center">'+
        '<div class="rc-name">'+esc(r.name)+' <span style="color:#6b7280;font-size:12px;font-weight:400">Lv'+r.level+'</span></div>'+
        '<div class="rc-pills">'+pills+missHtml+'</div>'+
        foodDesc+
      '</div>'+
      '<div class="rc-right"><span class="xp-badge">'+r.xp+' XP</span>'+xpFirst+'</div>'+
    '</div>'+
    '<div class="rc-detail '+isOpen+'" id="detail-'+r.key+'">'+detailHtml+'</div>'+
  '</div>';
}

function renderCraftable(){
  if(!D)return;
  // Determine which ingredient names are used by makeable recipes
  var usedNames={};
  D.canMake.forEach(function(r){if(r.ingredients)r.ingredients.forEach(function(i){usedNames[i.name]=true})});
  D.closeTo.forEach(function(c){if(c.recipe.ingredients)c.recipe.ingredients.forEach(function(i){usedNames[i.name]=true})});

  var invHtml='';
  if(D.inventory&&D.inventory.length){
    D.inventory.forEach(function(item){
      var used=usedNames[item.name]?'used':'dim';
      var bagStr=item.bag>0?'<span class="ing-bag">\uD83C\uDF92'+item.bag+'</span>':'';
      var storStr=item.storage>0?'<span class="ing-storage">\uD83D\uDCE6'+item.storage+'</span>':'';
      invHtml+='<div class="ing-card '+used+'"><span class="ing-name">'+esc(item.name)+'</span>'+bagStr+storStr+'</div>';
    });
  } else {
    invHtml='<span class="dim">No cooking ingredients found</span>';
  }

  var canHtml='';
  D.canMake.forEach(function(r){canHtml+=recipeCard(r,'')});
  if(!D.canMake.length) canHtml='<div class="dim" style="padding:12px">None available</div>';

  var closeHtml='';
  D.closeTo.forEach(function(c){closeHtml+=recipeCard(c.recipe,'yellow-border',c.missing)});
  if(!D.closeTo.length) closeHtml='<div class="dim" style="padding:12px">None</div>';

  document.getElementById('p-craftable').innerHTML=
    '<div class="section-title blue">Ingredients ('+D.inventory.length+')</div>'+
    '<div class="ing-grid">'+invHtml+'</div>'+
    '<div class="section-title green mt">Can Cook Now ('+D.canMake.length+')</div>'+canHtml+
    '<div class="section-title yellow mt">Almost There ('+D.closeTo.length+')</div>'+closeHtml;
}

function renderLeveling(){
  if(!D)return;
  var xpNeeded=D.cookingTnl-D.cookingXp;
  var bestCanMake=D.bestForLeveling?D.bestForLeveling.filter(function(r){return r.canMakeNow}):[];
  var suggestion='';
  if(bestCanMake.length>0){
    var best=bestCanMake[0];
    var craftsNeeded=Math.ceil(xpNeeded/best.xp);
    suggestion='<div class="callout">Fastest path: craft <b>'+esc(best.name)+'</b> x'+craftsNeeded+' for ~'+(craftsNeeded*best.xp)+' total XP ('+best.xp+' each)</div>';
  }

  var maxXpPer=1;
  if(D.bestForLeveling&&D.bestForLeveling.length){D.bestForLeveling.forEach(function(r){if(r.xpPerIngredient>maxXpPer)maxXpPer=r.xpPerIngredient})}
  var bestHtml='';
  if(D.bestForLeveling){
    D.bestForLeveling.forEach(function(r){
      var pct=Math.round(r.xpPerIngredient/maxXpPer*100);
      var canStyle=r.canMakeNow?'color:#4ade80':'color:#6b7280';
      bestHtml+='<div class="xp-eff-bar-wrap">'+
        '<div style="width:180px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;'+canStyle+'">'+esc(r.name)+'</div>'+
        '<div style="flex:1"><div class="xp-eff-bar" style="width:'+pct+'%"></div></div>'+
        '<div style="width:70px;text-align:right;color:#a78bfa;font-weight:600">'+r.xpPerIngredient+' xp/i</div>'+
      '</div>';
    });
  }
  if(!bestHtml) bestHtml='<div class="dim">No recipes available</div>';

  var firstHtml='';
  if(D.firstTimeBonuses&&D.firstTimeBonuses.length){
    D.firstTimeBonuses.forEach(function(r){
      firstHtml+=recipeCard(r,'red-border');
    });
    // Add NEW badge overlay by re-rendering with custom
    firstHtml='';
    D.firstTimeBonuses.forEach(function(r){
      var pills='';
      if(r.ingredients){r.ingredients.forEach(function(i){
        var cls=i.have>=i.need?'green':'red';
        pills+='<span class="pill '+cls+'">'+esc(i.name)+' '+i.have+'/'+i.need+'</span>';
      })}
      var isOpen=expandedCards[r.key]?'open':'';
      firstHtml+='<div class="recipe-card" style="border-left-color:#f472b6" onclick="toggleExpand(\''+r.key+'\')">'+
        '<div class="rc-top">'+
          '<div class="rc-left"><span class="new-badge">NEW</span>'+catBadge(r.category)+'</div>'+
          '<div class="rc-center"><div class="rc-name">'+esc(r.name)+' <span style="color:#6b7280;font-size:12px;font-weight:400">Lv'+r.level+'</span></div><div class="rc-pills">'+pills+'</div></div>'+
          '<div class="rc-right"><span class="xp-badge" style="background:#831843">'+r.xpFirstTime+' XP</span><span class="xp-first-badge">first-time bonus</span></div>'+
        '</div>'+
        '<div class="rc-detail '+isOpen+'" id="detail-'+r.key+'"></div>'+
      '</div>';
    });
  } else {
    firstHtml='<div class="dim" style="padding:12px">All available recipes have been crafted at least once!</div>';
  }

  document.getElementById('p-leveling').innerHTML=
    suggestion+
    '<div class="section-title purple">Best XP per Ingredient</div>'+bestHtml+
    '<div class="section-title pink mt">First-Time Bonuses ('+((D.firstTimeBonuses&&D.firstTimeBonuses.length)||0)+')</div>'+firstHtml;
}

function getAllRecipes(){
  if(!D)return[];
  var seen={};
  var all=[];
  var sources=[D.canMake,D.closeTo.map(function(c){return c.recipe}),D.bestForLeveling,D.firstTimeBonuses];
  sources.forEach(function(list){
    if(!list)return;
    list.forEach(function(r){
      if(!seen[r.key]){seen[r.key]=true;all.push(r)}
    });
  });
  all.sort(function(a,b){return a.level-b.level});
  return all;
}

function filterRecipes(){
  allRecipeSearch=document.getElementById('recipe-search')?document.getElementById('recipe-search').value.toLowerCase():'';
  allRecipeCanOnly=document.getElementById('can-only-toggle')?document.getElementById('can-only-toggle').checked:false;
  renderAllRecipes();
}
function setCategory(cat){
  allRecipeFilter=cat;
  document.querySelectorAll('.cat-filter-btn').forEach(function(b){b.classList.toggle('active',b.dataset.cat===cat)});
  renderAllRecipes();
}

function renderAllRecipes(){
  if(!D)return;
  var all=getAllRecipes();
  var filtered=all.filter(function(r){
    if(allRecipeSearch&&r.name.toLowerCase().indexOf(allRecipeSearch)===-1)return false;
    if(allRecipeFilter!=='All'&&r.category!==allRecipeFilter)return false;
    if(allRecipeCanOnly&&!r.canMakeNow)return false;
    return true;
  });

  var filterBar='<div class="filter-bar">'+
    '<input type="text" id="recipe-search" placeholder="Search recipes..." oninput="filterRecipes()" value="'+esc(allRecipeSearch)+'">'+
    '<button class="filter-btn cat-filter-btn'+(allRecipeFilter==='All'?' active':'')+'" data-cat="All" onclick="setCategory(\'All\')">All</button>'+
    '<button class="filter-btn cat-filter-btn'+(allRecipeFilter==='Meal'?' active':'')+'" data-cat="Meal" onclick="setCategory(\'Meal\')">Meal</button>'+
    '<button class="filter-btn cat-filter-btn'+(allRecipeFilter==='Snack'?' active':'')+'" data-cat="Snack" onclick="setCategory(\'Snack\')">Snack</button>'+
    '<button class="filter-btn cat-filter-btn'+(allRecipeFilter==='Instant'?' active':'')+'" data-cat="Instant" onclick="setCategory(\'Instant\')">Instant</button>'+
    '<button class="filter-btn cat-filter-btn'+(allRecipeFilter==='Other'?' active':'')+'" data-cat="Other" onclick="setCategory(\'Other\')">Other</button>'+
    '<label class="toggle-label"><input type="checkbox" id="can-only-toggle" onchange="filterRecipes()"'+(allRecipeCanOnly?' checked':'')+'>Can make only</label>'+
  '</div>';

  var cardsHtml='';
  filtered.forEach(function(r){
    var bc=r.canMakeNow?'':'red-border';
    cardsHtml+=recipeCard(r,bc);
  });
  if(!filtered.length) cardsHtml='<div class="dim" style="padding:12px">No matching recipes</div>';

  document.getElementById('p-all').innerHTML=filterBar+
    '<div style="color:#6b7280;font-size:12px;margin-bottom:8px">Showing '+filtered.length+' of '+all.length+' recipes</div>'+
    cardsHtml;
}

function renderShopping(){
  if(!D)return;
  var shopHtml='';
  if(D.shoppingList&&D.shoppingList.length){
    D.shoppingList.forEach(function(s){
      // Calculate progress: we have (totalNeed - totalNeed) items... we need totalNeed more
      // For shopping, totalNeed is the deficit. We don't have a "have" count directly.
      // We show need and how many recipes it unlocks.
      var pct=0; // We don't have have-count for shopping items in the API, so just show need
      var barClass='red';
      shopHtml+='<div class="shop-card">'+
        '<div style="display:flex;justify-content:space-between;align-items:center">'+
          '<span class="shop-name">'+esc(s.name)+'</span>'+
          '<span class="shop-need">need x'+s.totalNeed+'</span>'+
        '</div>'+
        '<div class="progress-bar-wrap"><div class="progress-bar-fill '+barClass+'" style="width:'+Math.max(5,pct)+'%"></div></div>'+
        '<div class="shop-unlocks">Unlocks <b>'+s.recipesUnlocked+'</b> recipe'+(s.recipesUnlocked>1?'s':'')+': '+s.recipes.map(function(r){return esc(r)}).join(', ')+'</div>'+
      '</div>';
    });
  } else {
    shopHtml='<div class="dim" style="padding:16px;text-align:center">You can make everything available! Nothing to shop for.</div>';
  }
  document.getElementById('p-shopping').innerHTML=
    '<div class="section-title yellow">Missing Ingredients (sorted by impact)</div>'+shopHtml;
}

async function refresh(){
  try{
    var res=await fetch('/api/state');
    D=await res.json();
    var pct=D.cookingTnl>0?Math.round(D.cookingXp/D.cookingTnl*100):0;
    var xpNeeded=D.cookingTnl-D.cookingXp;

    document.getElementById('header').innerHTML=
      '<h1>Project Gorgon Cooking Helper</h1>'+
      '<div class="stat-box"><span class="stat-label">Level</span><span class="stat-val">'+D.cookingLevel+(D.cookingBonus>0?'+'+D.cookingBonus:'')+'</span></div>'+
      '<div class="stat-box"><span class="stat-label">XP Progress</span>'+
        '<div class="xp-bar"><div class="xp-fill" style="width:'+pct+'%"></div><div class="xp-label">'+D.cookingXp+' / '+D.cookingTnl+' ('+pct+'%)</div></div>'+
      '</div>'+
      '<div class="stat-box"><span class="stat-label">Known Recipes</span><span class="stat-val">'+D.knownCount+' / '+D.totalAtLevel+'</span></div>'+
      '<div class="stat-box"><span class="stat-label">XP to Next Level</span><span class="stat-val">'+xpNeeded+'</span></div>';

    renderCraftable();
    renderLeveling();
    renderAllRecipes();
    renderShopping();
  }catch(e){}
}
refresh();setInterval(refresh,2500);
</script></body></html>
""";
}

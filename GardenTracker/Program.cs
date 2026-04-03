using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// ── Main Program ─────────────────────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

// Shared state
var _lock = new object();
var plants = new Dictionary<long, PlantInfo>();
var _pendingPlants = new Dictionary<long, PlantInfo>(); // plants we've seen but not confirmed as ours
var seeds = new List<SeedInfo>();
var stats = new GardenStats();
int gardeningLevel = 0;
double gardeningXp = 0;
double gardeningTnl = 0;
int gardeningBonus = 0;
int gardeningMax = 50;
string statsFilePath = Path.Combine(AppContext.BaseDirectory, "garden_stats.json");

// Load persisted stats
if (File.Exists(statsFilePath))
{
    try
    {
        var json = File.ReadAllText(statsFilePath);
        var loaded = JsonSerializer.Deserialize<GardenStats>(json);
        if (loaded != null) stats = loaded;
    }
    catch { }
}
stats.SessionStart = DateTime.Now;

// Load seed data from items.json
string itemsPath = Path.Combine(@"c:\Users\oguzb\source\memory-exploration", "items.json");
if (File.Exists(itemsPath))
{
    try
    {
        Console.WriteLine("Loading items.json for seed data...");
        using var fs = new FileStream(itemsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var doc = JsonDocument.Parse(fs);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            var item = prop.Value;
            if (!item.TryGetProperty("Keywords", out var keywords)) continue;

            bool hasSeedKeyword = false;
            int seedValue = 0;
            bool isFlowerSeed = false;
            foreach (var kw in keywords.EnumerateArray())
            {
                var kwStr = kw.GetString() ?? "";
                if (kwStr.StartsWith("Seed=") && int.TryParse(kwStr.AsSpan(5), out var sv))
                { hasSeedKeyword = true; seedValue = sv; }
                if (kwStr.StartsWith("Seedling=") && int.TryParse(kwStr.AsSpan(9), out var slv))
                { hasSeedKeyword = true; seedValue = slv; }
                if (kwStr == "FlowerSeed") isFlowerSeed = true;
            }

            if (!hasSeedKeyword) continue;
            // Must have a Plant use verb
            bool hasPlantVerb = false;
            if (item.TryGetProperty("Behaviors", out var behaviors))
            {
                foreach (var b in behaviors.EnumerateArray())
                {
                    if (b.TryGetProperty("UseVerb", out var uv) && uv.GetString() == "Plant")
                        hasPlantVerb = true;
                }
            }
            if (!hasPlantVerb) continue;

            int reqLevel = 0;
            if (item.TryGetProperty("SkillReqs", out var skillReqs) &&
                skillReqs.TryGetProperty("Gardening", out var gl))
                reqLevel = gl.GetInt32();

            string name = item.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "";
            double value = item.TryGetProperty("Value", out var v) ? v.GetDouble() : 0;
            string internalName = item.TryGetProperty("InternalName", out var iname) ? iname.GetString() ?? "" : "";

            // Determine type
            string type = "vegetable";
            if (isFlowerSeed) type = "flower";
            else if (name.Contains("Barley") || name.Contains("Sugarcane") || name.Contains("Oat") ||
                     name.Contains("Flax") || name.Contains("Wheat") || name.Contains("Rye") ||
                     name.Contains("Moonspelt") || name.Contains("Corn"))
                type = "grain";

            seeds.Add(new SeedInfo
            {
                Name = name,
                GardeningLevel = reqLevel,
                Value = value,
                Type = type,
                InternalName = internalName
            });
        }
        seeds = seeds.OrderBy(s => s.GardeningLevel).ThenBy(s => s.Name).ToList();
        Console.WriteLine($"Loaded {seeds.Count} seeds from items.json");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error loading items.json: {ex.Message}");
    }
}

// Find log file
var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
    "Elder Game", "Project Gorgon");
var logPath = Path.Combine(logDir, "Player.log");
var prevLogPath = Path.Combine(logDir, "Player-prev.log");

bool playerLogActive = File.Exists(logPath) && new FileInfo(logPath).Length > 0;
if (!playerLogActive)
    logPath = prevLogPath;

Console.WriteLine($"Active log: {logPath}");
Console.WriteLine($"Backfill from: {prevLogPath}");

// ── Log Processing ───────────────────────────────────────────────────────────

void ProcessLogLine(string line)
{
    lock (_lock)
    {
        // ProcessUpdateDescription for garden plants
        if (line.Contains("ProcessUpdateDescription"))
        {
            // Format: ProcessUpdateDescription(entityId, "State PlantName", "Description", "Action", UseItem, "Model(Scale=X)", 0)
            var m = Regex.Match(line, @"ProcessUpdateDescription\((\d+),\s*""([^""]+)"",\s*""([^""]*)"",\s*""([^""]*)"",\s*\w+,\s*""[^""]*Scale=([\d.]+)");
            if (m.Success)
            {
                long entityId = long.Parse(m.Groups[1].Value);
                string stateAndName = m.Groups[2].Value;
                string action = m.Groups[4].Value;
                double scale = double.TryParse(m.Groups[5].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var sc) ? sc : 0;

                // Parse state and plant name
                string state = "";
                string plantName = stateAndName;
                var stateMatch = Regex.Match(stateAndName, @"^(Thirsty|Growing|Hungry|Blooming|Ripe)\s+(.+)$");
                if (stateMatch.Success)
                {
                    state = stateMatch.Groups[1].Value;
                    plantName = stateMatch.Groups[2].Value;
                }

                if (plants.ContainsKey(entityId))
                {
                    // Already tracking this plant — update it
                    var existingPlant = plants[entityId];
                    string prevState = existingPlant.CurrentState;
                    existingPlant.CurrentState = state;
                    existingPlant.Action = action;
                    existingPlant.Scale = scale;
                    existingPlant.LastUpdateTime = DateTime.Now;
                }
                else
                {
                    // Only start tracking a plant if we can tell it's ours.
                    // Strategy: remember "pending" plants when we see Thirsty/Hungry.
                    // If the same entity transitions to Growing within ~3 seconds,
                    // that means WE watered/fertilized it (our action caused the transition).
                    // Also claim plants we see at Scale=0.5 with Thirsty (freshly planted by us).
                    if (state == "Thirsty" || state == "Hungry")
                    {
                        // Store as pending — not yet confirmed as ours
                        _pendingPlants[entityId] = new PlantInfo
                        {
                            EntityId = entityId,
                            PlantName = plantName,
                            CurrentState = state,
                            Action = action,
                            Scale = scale,
                            LastUpdateTime = DateTime.Now,
                            FirstSeenTime = DateTime.Now
                        };
                    }
                    else if (state == "Growing" && _pendingPlants.TryGetValue(entityId, out var pending))
                    {
                        // Pending plant just transitioned to Growing — this is ours!
                        // (We watered/fertilized it, causing the rapid state change)
                        double elapsed = (DateTime.Now - pending.LastUpdateTime).TotalSeconds;
                        if (elapsed < 5) // within 5 seconds = our interaction
                        {
                            pending.CurrentState = state;
                            pending.Action = action;
                            pending.Scale = scale;
                            pending.LastUpdateTime = DateTime.Now;
                            plants[entityId] = pending;
                        }
                        _pendingPlants.Remove(entityId);
                    }
                    else if ((state == "Blooming" || state == "Ripe") && _pendingPlants.ContainsKey(entityId))
                    {
                        // Plant we were tracking went straight to harvestable
                        var pending2 = _pendingPlants[entityId];
                        pending2.CurrentState = state;
                        pending2.Action = action;
                        pending2.Scale = scale;
                        pending2.LastUpdateTime = DateTime.Now;
                        plants[entityId] = pending2;
                        _pendingPlants.Remove(entityId);
                    }
                    // Otherwise: not our plant, ignore it
                }
            }
        }
        // Handle harvested items
        else if (line.Contains("ProcessAddItem"))
        {
            var m = Regex.Match(line, @"ProcessAddItem\((\d+),\s*""([^""]+)""");
            if (m.Success)
            {
                string itemName = m.Groups[2].Value;
                bool isGardenProduct = IsGardenProduct(itemName);
                if (isGardenProduct)
                {
                    if (!stats.HarvestsByType.ContainsKey(itemName))
                        stats.HarvestsByType[itemName] = 0;
                    stats.HarvestsByType[itemName]++;

                    stats.RecentHarvests.Add(new HarvestRecord
                    {
                        PlantName = itemName,
                        ItemName = itemName,
                        Timestamp = DateTime.Now
                    });
                    if (stats.RecentHarvests.Count > 100)
                        stats.RecentHarvests.RemoveAt(0);

                    // Try to find matching plant and record growth cycle
                    var matchingPlant = plants.Values
                        .Where(p => (p.CurrentState == "Blooming" || p.CurrentState == "Ripe") &&
                                    itemName.Contains(p.PlantName.Split(' ').Last()))
                        .OrderBy(p => p.LastUpdateTime)
                        .FirstOrDefault();

                    if (matchingPlant != null)
                    {
                        double cycleSec = (DateTime.Now - matchingPlant.FirstSeenTime).TotalSeconds;
                        string plantType = matchingPlant.PlantName;
                        if (!stats.GrowthCycles.ContainsKey(plantType))
                            stats.GrowthCycles[plantType] = new List<double>();
                        stats.GrowthCycles[plantType].Add(cycleSec);
                        if (stats.GrowthCycles[plantType].Count > 50)
                            stats.GrowthCycles[plantType].RemoveAt(0);

                        plants.Remove(matchingPlant.EntityId);
                    }

                    SaveStats();
                }
            }
        }
        // Gardening skill update
        else if (line.Contains("ProcessUpdateSkill") && line.Contains("Gardening"))
        {
            var m = Regex.Match(line, @"type=Gardening,raw=(\d+),bonus=(\d+),xp=([\d.]+),tnl=([\d.]+),max=(\d+)");
            if (m.Success)
            {
                int newLevel = int.Parse(m.Groups[1].Value);
                gardeningBonus = int.Parse(m.Groups[2].Value);
                double newXp = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                gardeningTnl = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                gardeningMax = int.Parse(m.Groups[5].Value);

                // Track XP gained
                if (gardeningXp > 0 && newXp != gardeningXp)
                {
                    double xpGained;
                    if (newLevel > gardeningLevel)
                        xpGained = newXp + 50; // level up - approximate
                    else
                    {
                        xpGained = newXp - gardeningXp;
                        if (xpGained < 0) xpGained = newXp;
                    }
                    if (xpGained > 0)
                        stats.TotalXpEarned += xpGained;
                }

                gardeningLevel = newLevel;
                gardeningXp = newXp;
            }
        }
        // ProcessLoadSkills for initial gardening level
        else if (line.Contains("ProcessLoadSkills") && line.Contains("Gardening"))
        {
            var m = Regex.Match(line, @"type=Gardening,raw=(\d+),bonus=(\d+),xp=([\d.]+),tnl=([\d.]+),max=(\d+)");
            if (m.Success)
            {
                gardeningLevel = int.Parse(m.Groups[1].Value);
                gardeningBonus = int.Parse(m.Groups[2].Value);
                gardeningXp = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                gardeningTnl = double.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                gardeningMax = int.Parse(m.Groups[5].Value);
            }
        }

        // Remove plants that have been picked/harvested (no update for > 2 min while in ready state)
        var toRemove = plants.Values
            .Where(p => (p.CurrentState == "Blooming" || p.CurrentState == "Ripe") &&
                        (DateTime.Now - p.LastUpdateTime).TotalMinutes > 2)
            .Select(p => p.EntityId)
            .ToList();
        foreach (var id in toRemove)
        {
            var p = plants[id];
            double cycleSec = (p.LastUpdateTime - p.FirstSeenTime).TotalSeconds;
            if (cycleSec > 5)
            {
                if (!stats.GrowthCycles.ContainsKey(p.PlantName))
                    stats.GrowthCycles[p.PlantName] = new List<double>();
                stats.GrowthCycles[p.PlantName].Add(cycleSec);
            }
            plants.Remove(id);
        }

        // Clean up stale pending plants (seen >30s ago, never confirmed as ours)
        var stalePending = _pendingPlants
            .Where(kv => (DateTime.Now - kv.Value.LastUpdateTime).TotalSeconds > 30)
            .Select(kv => kv.Key).ToList();
        foreach (var id in stalePending)
            _pendingPlants.Remove(id);
    }
}

bool IsGardenProduct(string itemName)
{
    var gardenProducts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Potato", "Onion", "Cabbage", "Beet", "Squash", "Broccoli", "Carrot", "Pumpkin",
        "Green Pepper", "Red Pepper", "Corn", "Escarole", "Basil", "Cantaloupe", "Pea",
        "Tomato", "Red-Leaf Lettuce", "Barley", "Sugarcane", "Oat", "Flax",
        "Orcish Wheat", "Tundra Rye", "Moonspelt", "Horse Apple"
    };
    if (gardenProducts.Contains(itemName)) return true;
    if (Regex.IsMatch(itemName, @"^Flower\d+$")) return true;
    if (itemName.Contains("Bluebell") || itemName.Contains("Aster") || itemName.Contains("Violet") ||
        itemName.Contains("Dahlia") || itemName.Contains("Daisy") || itemName.Contains("Pansy") ||
        itemName.Contains("Marigold") || itemName.Contains("Poppy") || itemName.Contains("Rose") ||
        itemName.Contains("Lily") || itemName.Contains("Winterhue") || itemName.Contains("Dandelion") ||
        itemName.Contains("Mysotis") || itemName.Contains("Tulip") || itemName.Contains("Paleblood") ||
        itemName.Contains("Hyacinth") || itemName.Contains("Sunflower") || itemName.Contains("Lavender") ||
        itemName.Contains("Bouquet"))
        return true;
    return false;
}

void SaveStats()
{
    try
    {
        var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(statsFilePath, json);
    }
    catch { }
}

// ── Log Tail Loop ────────────────────────────────────────────────────────────

void LogTailLoop(CancellationToken ct)
{
    long lastPos = 0;

    // Backfill from prev log (last 2MB)
    if (File.Exists(prevLogPath))
    {
        try
        {
            var fi = new FileInfo(prevLogPath);
            long backfillStart = Math.Max(0, fi.Length - 2 * 1024 * 1024);
            using var fs = new FileStream(prevLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(backfillStart, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            if (backfillStart > 0) reader.ReadLine(); // skip partial line
            string? line;
            while ((line = reader.ReadLine()) != null)
                ProcessLogLine(line);
            Console.WriteLine($"Backfilled from Player-prev.log ({fi.Length / 1024}KB)");
        }
        catch (Exception ex) { Console.WriteLine($"Backfill error: {ex.Message}"); }
    }

    // Also backfill from active log
    if (File.Exists(logPath) && logPath != prevLogPath)
    {
        try
        {
            var fi = new FileInfo(logPath);
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
                ProcessLogLine(line);
            lastPos = fi.Length;
            Console.WriteLine($"Processed active log ({fi.Length / 1024}KB)");
        }
        catch (Exception ex) { Console.WriteLine($"Active log error: {ex.Message}"); }
    }
    else if (File.Exists(logPath))
    {
        lastPos = new FileInfo(logPath).Length;
    }

    while (!ct.IsCancellationRequested)
    {
        try
        {
            if (!File.Exists(logPath)) { ct.WaitHandle.WaitOne(2000); continue; }
            var fi = new FileInfo(logPath);
            if (fi.Length > lastPos)
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(lastPos, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    ProcessLogLine(line);
                lastPos = fi.Length;
            }
            else if (fi.Length < lastPos)
                lastPos = 0; // log rotated
        }
        catch { }
        ct.WaitHandle.WaitOne(500);
    }
}

// ── HTTP Server ──────────────────────────────────────────────────────────────

async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add("http://localhost:9879/");
    listener.Start();

    while (!ct.IsCancellationRequested)
    {
        try
        {
            var ctx = await listener.GetContextAsync().WaitAsync(ct);
            var resp = ctx.Response;
            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            byte[] buffer;

            if (path == "/api/data")
            {
                resp.ContentType = "application/json";
                string json;
                lock (_lock)
                {
                    var apiData = new
                    {
                        gardeningLevel,
                        gardeningXp,
                        gardeningTnl,
                        gardeningBonus,
                        gardeningMax,
                        plants = plants.Values.Select(p => new
                        {
                            p.EntityId,
                            p.PlantName,
                            p.CurrentState,
                            p.Action,
                            p.Scale,
                            p.StateColor,
                            p.StateEmoji,
                            lastUpdateAgo = (int)(DateTime.Now - p.LastUpdateTime).TotalSeconds,
                            growthSec = (int)(DateTime.Now - p.FirstSeenTime).TotalSeconds
                        }).ToList(),
                        seeds = seeds.Select(s => new
                        {
                            s.Name,
                            s.GardeningLevel,
                            s.Value,
                            s.Type,
                            status = s.GardeningLevel <= gardeningLevel ? "available" :
                                     s.GardeningLevel <= gardeningLevel + 5 ? "soon" : "locked"
                        }).ToList(),
                        stats = new
                        {
                            stats.HarvestsByType,
                            stats.TotalXpEarned,
                            sessionStart = stats.SessionStart.ToString("o"),
                            sessionDurationMin = (int)(DateTime.Now - stats.SessionStart).TotalMinutes,
                            xpPerHour = (DateTime.Now - stats.SessionStart).TotalHours > 0.01
                                ? Math.Round(stats.TotalXpEarned / (DateTime.Now - stats.SessionStart).TotalHours, 1) : 0,
                            totalHarvests = stats.HarvestsByType.Values.Sum(),
                            avgGrowthCycles = stats.GrowthCycles.ToDictionary(
                                kv => kv.Key,
                                kv => Math.Round(kv.Value.Average(), 1)),
                            recentHarvests = stats.RecentHarvests.TakeLast(20).Reverse().Select(h => new
                            {
                                h.PlantName,
                                h.ItemName,
                                time = h.Timestamp.ToString("HH:mm:ss"),
                                h.GrowthDurationSec
                            }).ToList()
                        }
                    };
                    json = JsonSerializer.Serialize(apiData, new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    });
                }
                buffer = Encoding.UTF8.GetBytes(json);
            }
            else
            {
                resp.ContentType = "text/html; charset=utf-8";
                buffer = Encoding.UTF8.GetBytes(GardenHtml.PAGE);
            }

            resp.ContentLength64 = buffer.Length;
            await resp.OutputStream.WriteAsync(buffer, ct);
            resp.Close();
        }
        catch (OperationCanceledException) { break; }
        catch { }
    }
    listener.Stop();
}

// ── Start ────────────────────────────────────────────────────────────────────

Console.WriteLine("=== Project Gorgon Garden Tracker ===");

var logTask = Task.Run(() => LogTailLoop(cts.Token));
var httpTask = Task.Run(() => RunHttpServer(cts.Token));

Console.WriteLine($"\nGarden Tracker running at http://localhost:9879");
Console.WriteLine($"Tracking {seeds.Count} seed types");
Console.WriteLine("Press Ctrl+C to stop.\n");

try { await Task.WhenAll(logTask, httpTask); }
catch (OperationCanceledException) { }

SaveStats();
Console.WriteLine("Garden Tracker stopped.");

// ── Data Models ──────────────────────────────────────────────────────────────

class PlantInfo
{
    public long EntityId { get; set; }
    public string PlantName { get; set; } = "";
    public string CurrentState { get; set; } = "";
    public string Action { get; set; } = "";
    public double Scale { get; set; }
    public DateTime LastUpdateTime { get; set; } = DateTime.Now;
    public DateTime FirstSeenTime { get; set; } = DateTime.Now;
    public string StateColor => CurrentState switch
    {
        "Thirsty" => "#60a5fa",
        "Growing" => "#4ade80",
        "Hungry" => "#fbbf24",
        "Blooming" => "#f472b6",
        "Ripe" => "#fb923c",
        _ => "#8899aa"
    };
    public string StateEmoji => CurrentState switch
    {
        "Thirsty" => "\U0001f535",
        "Growing" => "\U0001f7e2",
        "Hungry" => "\U0001f7e1",
        "Blooming" => "\U0001f338",
        "Ripe" => "\U0001f345",
        _ => "\u2753"
    };
}

class SeedInfo
{
    public string Name { get; set; } = "";
    public int GardeningLevel { get; set; }
    public double Value { get; set; }
    public string Type { get; set; } = "unknown"; // flower, vegetable, grain
    public string InternalName { get; set; } = "";
}

class HarvestRecord
{
    public string PlantName { get; set; } = "";
    public string ItemName { get; set; } = "";
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public double GrowthDurationSec { get; set; }
}

class GardenStats
{
    public Dictionary<string, int> HarvestsByType { get; set; } = new();
    public Dictionary<string, List<double>> GrowthCycles { get; set; } = new();
    public double TotalXpEarned { get; set; }
    public DateTime SessionStart { get; set; } = DateTime.Now;
    public List<HarvestRecord> RecentHarvests { get; set; } = new();
}

// ── HTML Dashboard ───────────────────────────────────────────────────────────

static partial class GardenHtml
{
    public const string PAGE = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<title>Project Gorgon Garden Tracker</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { background: #0a0e17; color: #e0e6f0; font-family: 'Segoe UI', system-ui, sans-serif; padding: 16px; }
  .container { max-width: 960px; margin: 0 auto; }

  /* Header */
  .header { margin-bottom: 16px; }
  .header h1 { color: #7eb8ff; font-size: 22px; display: flex; align-items: center; gap: 10px; }
  .header h1 .dot { width: 10px; height: 10px; border-radius: 50%; background: #4ade80; animation: pulse 1.5s infinite; }
  @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.3; } }
  .header-stats { display: flex; gap: 16px; margin-top: 10px; flex-wrap: wrap; align-items: center; }
  .header-stat { background: #141b2d; border: 1px solid #1e2a42; border-radius: 6px; padding: 6px 14px; font-size: 13px; }
  .header-stat .label { color: #8899aa; margin-right: 6px; }
  .header-stat .value { color: #e0e6f0; font-weight: 600; }

  /* XP Bar */
  .xp-bar-wrap { flex: 1; min-width: 200px; background: #141b2d; border: 1px solid #1e2a42; border-radius: 6px; padding: 6px 14px; }
  .xp-bar-label { font-size: 12px; color: #8899aa; margin-bottom: 3px; }
  .xp-bar-outer { height: 8px; background: #1e2a42; border-radius: 4px; overflow: hidden; }
  .xp-bar-inner { height: 100%; background: linear-gradient(90deg, #4ade80, #22c55e); border-radius: 4px; transition: width 0.5s; }

  /* Tabs */
  .tabs { display: flex; gap: 4px; margin-bottom: 12px; }
  .tab { background: #141b2d; border: 1px solid #1e2a42; border-radius: 6px 6px 0 0; padding: 8px 20px; cursor: pointer; font-size: 14px; color: #8899aa; transition: all 0.2s; }
  .tab:hover { color: #e0e6f0; background: #1a2235; }
  .tab.active { background: #1e2a42; color: #7eb8ff; border-bottom-color: #1e2a42; font-weight: 600; }
  .tab-content { display: none; }
  .tab-content.active { display: block; }

  /* Cards */
  .card { background: #141b2d; border: 1px solid #1e2a42; border-radius: 8px; padding: 14px; margin-bottom: 10px; }
  .card h2 { color: #60a5fa; font-size: 13px; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 8px; border-bottom: 1px solid #1e2a42; padding-bottom: 4px; }

  /* Plant grid */
  .plant-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 10px; }
  .plant-card { background: #141b2d; border: 1px solid #1e2a42; border-radius: 8px; padding: 12px; transition: border-color 0.3s; }
  .plant-card .plant-name { font-weight: 700; font-size: 15px; margin-bottom: 4px; }
  .plant-card .plant-state { font-size: 13px; margin-bottom: 4px; display: flex; align-items: center; gap: 6px; }
  .plant-card .plant-action { font-size: 12px; color: #8899aa; margin-bottom: 6px; }
  .plant-card .plant-time { font-size: 11px; color: #6b7280; }
  .plant-card .progress-bar { height: 6px; background: #1e2a42; border-radius: 3px; overflow: hidden; margin-top: 6px; }
  .plant-card .progress-fill { height: 100%; border-radius: 3px; transition: width 0.5s; }
  .empty-msg { color: #4b5563; font-style: italic; padding: 20px; text-align: center; }

  /* Seed catalog */
  .seed-group { margin-bottom: 16px; }
  .seed-group h3 { color: #8899aa; font-size: 14px; margin-bottom: 8px; padding-bottom: 4px; border-bottom: 1px solid #1e2a42; }
  .seed-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(200px, 1fr)); gap: 8px; }
  .seed-card { background: #141b2d; border: 2px solid #1e2a42; border-radius: 8px; padding: 10px; font-size: 13px; }
  .seed-card.available { border-color: #16a34a; }
  .seed-card.soon { border-color: #ca8a04; }
  .seed-card.locked { border-color: #374151; opacity: 0.5; }
  .seed-card .seed-name { font-weight: 600; margin-bottom: 3px; }
  .seed-card .seed-info { color: #8899aa; font-size: 12px; }
  .seed-type { display: inline-block; font-size: 10px; padding: 1px 6px; border-radius: 3px; margin-left: 4px; }
  .seed-type.flower { background: #4c1d95; color: #c4b5fd; }
  .seed-type.vegetable { background: #14532d; color: #86efac; }
  .seed-type.grain { background: #78350f; color: #fed7aa; }

  /* Yields */
  .stats-row { display: grid; grid-template-columns: repeat(4, 1fr); gap: 10px; margin-bottom: 12px; }
  .stat-card { background: #141b2d; border: 1px solid #1e2a42; border-radius: 8px; padding: 12px; text-align: center; }
  .stat-card .stat-value { font-size: 24px; font-weight: 700; color: #7eb8ff; }
  .stat-card .stat-label { font-size: 12px; color: #8899aa; margin-top: 2px; }

  .bar-chart { margin-bottom: 12px; }
  .bar-row { display: flex; align-items: center; margin-bottom: 4px; font-size: 13px; }
  .bar-label { min-width: 120px; color: #8899aa; text-align: right; padding-right: 10px; }
  .bar-outer { flex: 1; height: 18px; background: #1e2a42; border-radius: 4px; overflow: hidden; }
  .bar-inner { height: 100%; background: linear-gradient(90deg, #4ade80, #22c55e); border-radius: 4px; transition: width 0.5s; display: flex; align-items: center; padding-left: 6px; font-size: 11px; min-width: 20px; }

  .harvest-log { max-height: 300px; overflow-y: auto; }
  .harvest-item { display: flex; gap: 10px; padding: 4px 0; border-bottom: 1px solid #1a2235; font-size: 13px; }
  .harvest-item .h-time { color: #6b7280; min-width: 60px; font-size: 12px; }
  .harvest-item .h-name { color: #e0e6f0; }

  .scroll::-webkit-scrollbar { width: 4px; }
  .scroll::-webkit-scrollbar-track { background: #0a0e17; }
  .scroll::-webkit-scrollbar-thumb { background: #2d3a52; border-radius: 4px; }
  .footer { text-align: center; color: #4b5563; font-size: 11px; margin-top: 16px; }

  @media (max-width: 600px) {
    .stats-row { grid-template-columns: repeat(2, 1fr); }
    .plant-grid, .seed-grid { grid-template-columns: 1fr; }
  }
</style>
</head>
<body>
<div class="container">

<div class="header">
  <h1><span class="dot"></span> Project Gorgon Garden Tracker</h1>
  <div class="header-stats">
    <div class="header-stat">
      <span class="label">Gardening</span>
      <span class="value" id="hLevel">-</span>
    </div>
    <div class="xp-bar-wrap">
      <div class="xp-bar-label" id="xpLabel">XP: -</div>
      <div class="xp-bar-outer"><div class="xp-bar-inner" id="xpBar" style="width:0%"></div></div>
    </div>
    <div class="header-stat">
      <span class="label">Harvests</span>
      <span class="value" id="hHarvests">0</span>
    </div>
    <div class="header-stat">
      <span class="label">XP Earned</span>
      <span class="value" id="hXpEarned">0</span>
    </div>
    <div class="header-stat">
      <span class="label">XP/hr</span>
      <span class="value" id="hXpHr">0</span>
    </div>
  </div>
</div>

<div class="tabs">
  <div class="tab active" onclick="switchTab('garden')">Garden</div>
  <div class="tab" onclick="switchTab('seeds')">Seeds</div>
  <div class="tab" onclick="switchTab('yields')">Yields</div>
</div>

<!-- Garden Tab -->
<div id="tab-garden" class="tab-content active">
  <div id="plantGrid" class="plant-grid">
    <div class="empty-msg">No plants currently growing. Plant some seeds!</div>
  </div>
</div>

<!-- Seeds Tab -->
<div id="tab-seeds" class="tab-content">
  <div id="seedCatalog"></div>
</div>

<!-- Yields Tab -->
<div id="tab-yields" class="tab-content">
  <div class="stats-row">
    <div class="stat-card"><div class="stat-value" id="yTotalHarvests">0</div><div class="stat-label">Total Harvests</div></div>
    <div class="stat-card"><div class="stat-value" id="yXpEarned">0</div><div class="stat-label">XP Earned</div></div>
    <div class="stat-card"><div class="stat-value" id="yXpHr">0</div><div class="stat-label">XP / Hour</div></div>
    <div class="stat-card"><div class="stat-value" id="yAvgGrowth">-</div><div class="stat-label">Avg Growth Time</div></div>
  </div>
  <div class="card">
    <h2>Harvests by Type</h2>
    <div id="harvestChart" class="bar-chart"></div>
  </div>
  <div class="card">
    <h2>Recent Harvests</h2>
    <div id="harvestLog" class="harvest-log scroll"></div>
  </div>
</div>

<div class="footer">Garden Tracker &middot; Auto-refreshes every 2s</div>
</div>

<script>
function switchTab(name) {
  document.querySelectorAll('.tab').forEach(t => t.classList.remove('active'));
  document.querySelectorAll('.tab-content').forEach(t => t.classList.remove('active'));
  document.getElementById('tab-' + name).classList.add('active');
  document.querySelectorAll('.tab').forEach(t => {
    if (t.textContent.toLowerCase() === name) t.classList.add('active');
  });
}

function fmtTime(sec) {
  if (sec < 60) return sec + 's';
  if (sec < 3600) return Math.floor(sec/60) + 'm ' + (sec%60) + 's';
  return Math.floor(sec/3600) + 'h ' + Math.floor((sec%3600)/60) + 'm';
}

function fmtGold(val) {
  if (val >= 100) return (val/100).toFixed(0) + 'g';
  return val + 'c';
}

async function refresh() {
  try {
    const r = await fetch('/api/data');
    const d = await r.json();

    // Header
    document.getElementById('hLevel').textContent = 'Lv ' + d.gardeningLevel + (d.gardeningBonus > 0 ? ' (+' + d.gardeningBonus + ')' : '');
    const xpPct = d.gardeningTnl > 0 ? Math.round((d.gardeningXp / (d.gardeningXp + d.gardeningTnl)) * 100) : 0;
    document.getElementById('xpLabel').textContent = 'XP: ' + Math.floor(d.gardeningXp) + ' / ' + Math.floor(d.gardeningXp + d.gardeningTnl) + ' (' + xpPct + '%)';
    document.getElementById('xpBar').style.width = xpPct + '%';
    document.getElementById('hHarvests').textContent = d.stats.totalHarvests;
    document.getElementById('hXpEarned').textContent = Math.floor(d.stats.totalXpEarned);
    document.getElementById('hXpHr').textContent = d.stats.xpPerHour;

    // Garden tab
    const pg = document.getElementById('plantGrid');
    if (d.plants.length === 0) {
      pg.innerHTML = '<div class="empty-msg">No plants currently growing. Plant some seeds!</div>';
    } else {
      pg.innerHTML = d.plants.map(p => {
        const progress = Math.min(100, Math.max(5, ((p.scale - 0.5) / 0.5) * 100));
        return '<div class="plant-card" style="border-color:' + p.stateColor + '">'
          + '<div class="plant-name">' + p.plantName + '</div>'
          + '<div class="plant-state"><span>' + p.stateEmoji + '</span> <span style="color:' + p.stateColor + '">' + (p.currentState || 'Unknown') + '</span></div>'
          + '<div class="plant-action">' + p.action + '</div>'
          + '<div class="plant-time">Updated ' + fmtTime(p.lastUpdateAgo) + ' ago &middot; Growing ' + fmtTime(p.growthSec) + '</div>'
          + '<div class="progress-bar"><div class="progress-fill" style="width:' + progress + '%;background:' + p.stateColor + '"></div></div>'
          + '</div>';
      }).join('');
    }

    // Seeds tab
    const sc = document.getElementById('seedCatalog');
    const available = d.seeds.filter(s => s.status === 'available');
    const soon = d.seeds.filter(s => s.status === 'soon');
    const locked = d.seeds.filter(s => s.status === 'locked');

    let seedHtml = '';
    if (available.length > 0) {
      seedHtml += '<div class="seed-group"><h3>Available Now (Gardening ' + d.gardeningLevel + ')</h3><div class="seed-grid">';
      seedHtml += available.map(s => seedCard(s, 'available')).join('');
      seedHtml += '</div></div>';
    }
    if (soon.length > 0) {
      seedHtml += '<div class="seed-group"><h3>Coming Soon (next 5 levels)</h3><div class="seed-grid">';
      seedHtml += soon.map(s => seedCard(s, 'soon')).join('');
      seedHtml += '</div></div>';
    }
    if (locked.length > 0) {
      seedHtml += '<div class="seed-group"><h3>Locked</h3><div class="seed-grid">';
      seedHtml += locked.map(s => seedCard(s, 'locked')).join('');
      seedHtml += '</div></div>';
    }
    sc.innerHTML = seedHtml || '<div class="empty-msg">No seed data loaded</div>';

    // Yields tab
    document.getElementById('yTotalHarvests').textContent = d.stats.totalHarvests;
    document.getElementById('yXpEarned').textContent = Math.floor(d.stats.totalXpEarned);
    document.getElementById('yXpHr').textContent = d.stats.xpPerHour;

    // Avg growth time
    const cycles = d.stats.avgGrowthCycles;
    const allCycles = Object.values(cycles);
    if (allCycles.length > 0) {
      const avg = allCycles.reduce((a,b) => a+b, 0) / allCycles.length;
      document.getElementById('yAvgGrowth').textContent = fmtTime(Math.round(avg));
    }

    // Bar chart
    const hChart = document.getElementById('harvestChart');
    const harvests = d.stats.harvestsByType;
    const entries = Object.entries(harvests).sort((a,b) => b[1] - a[1]);
    const maxH = entries.length > 0 ? entries[0][1] : 1;
    if (entries.length === 0) {
      hChart.innerHTML = '<div class="empty-msg">No harvests yet</div>';
    } else {
      hChart.innerHTML = entries.map(([name, count]) => {
        const pct = Math.max(5, (count / maxH) * 100);
        return '<div class="bar-row"><span class="bar-label">' + name + '</span><div class="bar-outer"><div class="bar-inner" style="width:' + pct + '%">' + count + '</div></div></div>';
      }).join('');
    }

    // Recent harvests
    const hLog = document.getElementById('harvestLog');
    if (d.stats.recentHarvests.length === 0) {
      hLog.innerHTML = '<div class="empty-msg">No harvests recorded</div>';
    } else {
      hLog.innerHTML = d.stats.recentHarvests.map(h =>
        '<div class="harvest-item"><span class="h-time">' + h.time + '</span><span class="h-name">' + h.plantName + '</span></div>'
      ).join('');
    }

  } catch(e) { console.error('Refresh error:', e); }
}

function seedCard(s, status) {
  const typeClass = s.type || 'vegetable';
  const typeLabel = s.type ? s.type.charAt(0).toUpperCase() + s.type.slice(1) : '';
  return '<div class="seed-card ' + status + '">'
    + '<div class="seed-name">' + s.name + ' <span class="seed-type ' + typeClass + '">' + typeLabel + '</span></div>'
    + '<div class="seed-info">Gardening Lv ' + s.gardeningLevel + ' &middot; ' + fmtGold(s.value) + '</div>'
    + '</div>';
}

refresh();
setInterval(refresh, 2000);
</script>
</body>
</html>
""";
}

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MnMSolver;

// ── Configuration ──
string logDir = @"C:\Users\oguzb\AppData\LocalLow\Elder Game\Project Gorgon";
string logPath = Path.Combine(logDir, "Player.log");
string logPathPrev = Path.Combine(logDir, "Player-prev.log");
int port = 9882;
string settingsDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ProjectGorgonTools");
Directory.CreateDirectory(settingsDir);

if (!File.Exists(logPath) || new FileInfo(logPath).Length == 0)
    logPath = logPathPrev;
if (!File.Exists(logPath))
{
    Console.WriteLine("No log file found. Exiting.");
    return;
}

// ── Shared State ──
var _lock = new object();
MnMGame? _currentGame = null;
var _gameHistory = new List<MnMGame>();
var cts = new CancellationTokenSource();
int _mnmNpcId = 0;
bool _autoplay = false;
bool _autoloop = false;
var _eventLog = new List<string>();

void AddEvent(string msg)
{
    Console.WriteLine(msg);
    lock (_lock)
    {
        _eventLog.Add($"{DateTime.Now:HH:mm:ss} {msg}");
        if (_eventLog.Count > 200) _eventLog.RemoveAt(0);
    }
}

// ── Log Processing ──

// Real log format: ProcessTalkScreen(-36, "title", "bodyHTML", "", System.Int32[], System.String[], 0, Generic)
// Note: entity IDs are negative, choices are NOT expanded (show as System.Int32[]/System.String[])
var _talkScreenRx = new Regex(
    @"ProcessTalkScreen\((-?\d+),\s*""((?:[^""\\]|\\.)*)"",\s*""((?:[^""\\]|\\.)*)"",",
    RegexOptions.Compiled);

var _startInteractionRx = new Regex(
    @"ProcessStartInteraction\((-?\d+),\s*\d+,.*?""(MonstersAndMantids[^""]*)",
    RegexOptions.Compiled);

var _screenTextRx = new Regex(
    @"ProcessScreenText\([^,]+,\s*""([^""]*)""\)",
    RegexOptions.Compiled);

void ProcessLogLine(string line)
{
    // Detect M&M table interaction start
    if (line.Contains("MonstersAndMantids"))
    {
        var sm = _startInteractionRx.Match(line);
        if (sm.Success)
        {
            int entityId = int.Parse(sm.Groups[1].Value);
            _mnmNpcId = entityId;
            AddEvent($"[+] M&M table interaction started (entity {entityId})");
        }
    }

    // ProcessTalkScreen
    if (!line.Contains("ProcessTalkScreen(")) return;
    var m = _talkScreenRx.Match(line);
    if (!m.Success) return;

    int npcId = int.Parse(m.Groups[1].Value);
    string title = m.Groups[2].Value.Replace("\\\"", "\"");
    string bodyHtml = m.Groups[3].Value.Replace("\\\"", "\"").Replace("\\n", "\n");

    bool isMnM = title.Contains("Monsters", StringComparison.OrdinalIgnoreCase)
              || title.Contains("Mantids", StringComparison.OrdinalIgnoreCase);
    if (!isMnM) return;

    // Skip empty bodies (loading screens)
    if (string.IsNullOrWhiteSpace(bodyHtml)) return;

    lock (_lock)
    {
        // Don't start a new game if this is just the R.I.P. follow-up screen of a game that just ended
        bool isDeathContinuation = _currentGame?.Status == "over"
            && (bodyHtml.Contains("You have died") || bodyHtml.Contains("R.I.P."));
        if (!isDeathContinuation && (_currentGame == null || _currentGame.Status != "playing"))
        {
            _currentGame = new MnMGame();
            _mnmNpcId = npcId;
            AddEvent($"[+] New MnM game detected: {title}");
        }

        var game = _currentGame;
        game.RawLastHtml = bodyHtml;

        ParseGameState(bodyHtml, game);
        game.Phase = DetectPhase(game);
        if (game.Phase != GamePhase.GameOver && game.Phase != GamePhase.WaitingForGame && game.Phase != GamePhase.Unknown)
            UpdateRecommendation(game, bodyHtml);

        // Detect game end
        if (game.Phase == GamePhase.GameOver && game.Status == "playing")
        {
            game.Status = "over";
            game.FinalGold = game.Gold;
            game.FinalTokens = game.CulturalArtifacts;
            _gameHistory.Add(game);
            AddEvent($"[!] Game ended: {(game.CashedOut ? "CASHED OUT" : "DIED")} | Gold={game.Gold} Artifacts={game.CulturalArtifacts} Encounters={game.EncounterNumber}");
        }
    }

    // Dump raw HTML for learning
    try
    {
        var rawLogPath = Path.Combine(settingsDir, "mnm_raw_logs.txt");
        File.AppendAllText(rawLogPath,
            $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {title} | Phase={_currentGame?.Phase} ===\n{bodyHtml}\n");
    }
    catch { }

    AddEvent($"[*] {title} | Phase={_currentGame?.Phase} | HP={_currentGame?.CurrentHP}/{_currentGame?.MaxHP} Gold={_currentGame?.Gold} Enemy={_currentGame?.EnemyName}");
}

// ── State Parser ──
// Real HTML uses TextMeshPro tags: <b>, <em>, <color=#hex>, <size=+N>, <voffset=Nem>, <pos=N>, <indent=N>, <sprite=...>
void ParseGameState(string bodyHtml, MnMGame game)
{
    // Strip all tags for plain text parsing
    string plain = Regex.Replace(bodyHtml, @"<[^>]+>", " ");
    plain = Regex.Replace(plain, @"\s+", " ").Trim();

    // Your Health: N / M (from status blocks and combat screens)
    var hpM = Regex.Match(plain, @"Your Health:\s*(\d+)\s*/\s*(\d+)");
    if (hpM.Success)
    {
        game.CurrentHP = int.Parse(hpM.Groups[1].Value);
        game.MaxHP = int.Parse(hpM.Groups[2].Value);
    }

    // Gold: N (from status blocks)
    var goldM = Regex.Match(plain, @"Gold:\s*(\d+)");
    if (goldM.Success)
        game.Gold = int.Parse(goldM.Groups[1].Value);

    // Cultural Artifacts: N
    var artM = Regex.Match(plain, @"Cultural Artifacts?:\s*(\d+)");
    if (artM.Success)
        game.CulturalArtifacts = int.Parse(artM.Groups[1].Value);

    // Health Potions: N
    var potM = Regex.Match(plain, @"Health Potions?:\s*(\d+)");
    if (potM.Success)
        game.HealthPotions = int.Parse(potM.Groups[1].Value);

    // Makeshift Bombs: N
    var bombM = Regex.Match(plain, @"Makeshift Bombs?:\s*(\d+)");
    if (bombM.Success)
        game.MakeshiftBombs = int.Parse(bombM.Groups[1].Value);

    // Your Hat: HatName (from status blocks: "Your Hat: Moist Towel")
    var hatM = Regex.Match(plain, @"Your Hat:\s*(.+?)(?:\s+Boosts|\s*$)");
    if (hatM.Success)
        game.HatName = hatM.Groups[1].Value.Trim();

    // Hat description from <indent=20><i>...</i></indent> after hat name
    var hatDescM = Regex.Match(bodyHtml, @"<indent=\d+><i>(.*?)</i></indent>");
    if (hatDescM.Success)
        game.HatDescription = Regex.Replace(hatDescM.Groups[1].Value, @"<[^>]+>", "").Trim();

    // Enemy name from combat: "Borbo vs. EnemyName" pattern
    var vsM = Regex.Match(plain, @"\w+ vs\.\s+(.+?)(?:\s+Health:|$)");
    if (vsM.Success)
    {
        string enemy = vsM.Groups[1].Value.Trim();
        if (game.EnemyName != enemy)
        {
            game.EnemyName = enemy;
            game.EnemyDice = null; // will be looked up from monster DB
            // Count new encounters
            if (!plain.Contains("Dead"))
                game.EncounterNumber++;
        }
    }

    // Enemy health from: "EnemyName Health: N / M" or "Dead"
    var ehM = Regex.Match(plain, @"Health:\s*(\d+)\s*/\s*(\d+).*?Your Health:");
    if (ehM.Success)
    {
        game.EnemyCurrentHP = int.Parse(ehM.Groups[1].Value);
        game.EnemyMaxHP = int.Parse(ehM.Groups[2].Value);
    }
    if (plain.Contains("Dead") && plain.Contains("vs."))
        game.EnemyCurrentHP = 0;

    // Damage dealt: "You dealt N damage while Enemy dealt M damage."
    var dmgM = Regex.Match(plain, @"You dealt\s+(\d+)\s+damage.*?dealt\s+(\d+)\s+damage");
    if (dmgM.Success)
    {
        game.LastDamageDealt = int.Parse(dmgM.Groups[1].Value);
        game.LastDamageTaken = int.Parse(dmgM.Groups[2].Value);
    }

    // Level up: "You leveled up!"
    if (plain.Contains("leveled up"))
        game.Level++;

    // "You can now use AbilityName N" — ability upgrade
    var abilUpM = Regex.Match(plain, @"You can now use (.+?)\s+\d+");
    if (abilUpM.Success)
    {
        string upgraded = abilUpM.Groups[1].Value.Trim();
        game.EventLog.Add($"Upgraded: {upgraded}");
    }

    // Rest/eat: "You eat EnemyName. Your Max Health is increased by N and you recover M health."
    var eatM = Regex.Match(plain, @"You eat .+?\.\s*Your Max Health is increased by (\d+) and you recover (\d+) health");
    if (eatM.Success)
        game.EventLog.Add($"Ate corpse: +{eatM.Groups[1].Value} maxHP, healed {eatM.Groups[2].Value}");

    // "found N gold coins" (from victory)
    var lootGoldM = Regex.Match(plain, @"found (\d+) gold coins");
    if (lootGoldM.Success)
        game.EventLog.Add($"Loot: {lootGoldM.Groups[1].Value} gold");

    // "found a new hat: HatName"
    var lootHatM = Regex.Match(plain, @"found a new hat:\s*(.+?)!");
    if (lootHatM.Success)
        game.EventLog.Add($"Found hat: {lootHatM.Groups[1].Value}");

    // "found a healing potion" / "found a cultural artifact"
    if (plain.Contains("healing potion")) game.EventLog.Add("Found healing potion");
    if (plain.Contains("cultural artifact")) game.EventLog.Add("Found cultural artifact");

    // Detect game end: "Escaped the Caves" = successful cash out
    if (plain.Contains("Escaped the") || plain.Contains("The End"))
    {
        game.CashedOut = true;
        // Parse leaderboard score
        var scoreM = Regex.Match(plain, @"leaderboard score:\s*(\d+)");
        if (scoreM.Success) game.LeaderboardScore = int.Parse(scoreM.Groups[1].Value);
    }

    // Detect death: "You have died" appears in both the mutual-kill combat screen and the R.I.P. summary screen
    if (plain.Contains("You have died") || plain.Contains("R.I.P."))
    {
        game.Died = true;
        var scoreM2 = Regex.Match(plain, @"leaderboard score:\s*(\d+)");
        if (scoreM2.Success) game.LeaderboardScore = int.Parse(scoreM2.Groups[1].Value);
    }

    // Perk gained: "You gained +N Max Health!" / "Your X ability became X+!"
    var perkM = Regex.Match(plain, @"You gained \+(\d+ Max Health)");
    if (perkM.Success) game.Perks.Add($"Tough (+{perkM.Groups[1].Value})");

    var perkAbilM = Regex.Match(plain, @"Your (\w+) ability became (\w+\+?)!");
    if (perkAbilM.Success) game.Perks.Add($"{perkAbilM.Groups[2].Value}");
}

// ── Phase Detection ──
// Since choices are NOT in the log (System.Int32[]/System.String[]), detect phase from body text
GamePhase DetectPhase(MnMGame game)
{
    string body = game.RawLastHtml ?? "";
    string plain = Regex.Replace(body, @"<[^>]+>", " ");

    // Game over states
    if (game.CashedOut || plain.Contains("The End")) return GamePhase.GameOver;
    if (game.Died) return GamePhase.GameOver;

    // Combat: "Use which attack?" or "vs." with health bars
    if (plain.Contains("Use which attack")) return GamePhase.CombatAbilitySelect;

    // Post-victory: "Do you want to go deeper" / "retreat and end your adventure"
    if (plain.Contains("go deeper") || plain.Contains("retreat and end")) return GamePhase.PostVictoryChoice;

    // Rest/Meditate choice: "you can meditate" and "eat the corpse"
    if (plain.Contains("you can meditate") && plain.Contains("eat the corpse")) return GamePhase.MeditateChoice;

    // Perk selection: "Pick a Perk"
    if (plain.Contains("Pick a Perk", StringComparison.OrdinalIgnoreCase)) return GamePhase.PerkSelection;

    // Hat selection: "choose which hat to wear"
    if (plain.Contains("choose which hat")) return GamePhase.HatSelection;

    // Shop encounter: "Buy hat" / "Buy bomb" from goblin
    if (plain.Contains("Buy hat") || plain.Contains("Buy bomb")) return GamePhase.ShopEncounter;

    // Diplomacy: "Diplomacy Check" / "trick" / "calm"
    if (plain.Contains("Diplomacy", StringComparison.OrdinalIgnoreCase)) return GamePhase.DiplomacyCheck;

    // Saving throw
    if (plain.Contains("Saving Throw", StringComparison.OrdinalIgnoreCase)) return GamePhase.SavingThrow;

    // Adventure intro
    if (plain.Contains("Adventure M1") || plain.Contains("traveled to the")) return GamePhase.WaitingForGame;

    // Leaderboard screen
    if (plain.Contains("Skillcode") || plain.Contains("rolling high-score")) return GamePhase.WaitingForGame;

    return GamePhase.Unknown;
}

// ── HTTP Server ──
async Task RunHttpServer(CancellationToken ct)
{
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    Console.WriteLine($"[*] Dashboard at http://localhost:{port}/");

    while (!ct.IsCancellationRequested)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync().WaitAsync(ct); }
        catch (OperationCanceledException) { break; }

        try
        {
            var req = ctx.Request;
            var resp = ctx.Response;

            if (req.Url?.AbsolutePath == "/api/state")
            {
                MnMGame? game;
                List<MnMGame> history;
                List<string> evLog;
                lock (_lock)
                {
                    game = _currentGame;
                    history = _gameHistory.ToList();
                    evLog = _eventLog.ToList();
                }
                var apiObj = BuildApiState(game, history, evLog);
                var json = JsonSerializer.Serialize(apiObj, new JsonSerializerOptions { WriteIndented = false });
                var buf = Encoding.UTF8.GetBytes(json);
                resp.ContentType = "application/json";
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf, ct);
            }
            else
            {
                var buf = Encoding.UTF8.GetBytes(DashboardHtml.PAGE);
                resp.ContentType = "text/html; charset=utf-8";
                resp.ContentLength64 = buf.Length;
                await resp.OutputStream.WriteAsync(buf, ct);
            }
            resp.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] HTTP error: {ex.Message}");
        }
    }
    listener.Stop();
}

// ── Recommendation Engine ──
void UpdateRecommendation(MnMGame game, string bodyHtml)
{
    string plain = Regex.Replace(bodyHtml, @"<[^>]+>", " ");

    string action = "", rationale = "";
    double evCashOut = 0, evDelve = 0, evRest = 0, pSurvive = 0;

    switch (game.Phase)
    {
        case GamePhase.PostVictoryChoice:
        {
            var r = DecisionEngine.DecidePostVictory(
                game.CurrentHP, game.MaxHP, game.Gold, game.CulturalArtifacts,
                game.EncounterNumber, null, null, canRest: false, estimatedHealDice: 0);
            (action, rationale, evCashOut, evDelve, evRest, pSurvive) = r;
            break;
        }
        case GamePhase.MeditateChoice:
        {
            var eatM = Regex.Match(plain, @"regain\s+(\d+D[+\-]?\d*)", RegexOptions.IgnoreCase);
            double eatHeal = 0;
            if (eatM.Success) { var (c, s, b) = DecisionEngine.ParseDice(eatM.Groups[1].Value); eatHeal = DecisionEngine.AverageRoll(c, s, b); }
            double hpPct = game.MaxHP > 0 ? (double)game.CurrentHP / game.MaxHP : 1.0;
            pSurvive = DecisionEngine.ProbSurvive(game.CurrentHP, game.EnemyName, null, game.EncounterNumber + 1);
            double healedHP = Math.Min(game.MaxHP, game.CurrentHP + eatHeal);
            double pSurviveHealed = DecisionEngine.ProbSurvive((int)healedHP, game.EnemyName, null, game.EncounterNumber + 1);
            if (hpPct < 0.45 && pSurviveHealed - pSurvive > 0.08)
            { action = "Rest"; rationale = $"HP low ({game.CurrentHP}/{game.MaxHP}). Eating heals ~{eatHeal:F0} HP, survival {pSurvive:P0} → {pSurviveHealed:P0}."; }
            else
            { action = "Meditate"; rationale = $"HP ok ({game.CurrentHP}/{game.MaxHP} = {hpPct:P0}). Meditate to upgrade ability — free power gain."; }
            break;
        }
        case GamePhase.CombatAbilitySelect:
        {
            pSurvive = DecisionEngine.ProbSurvive(game.CurrentHP, game.EnemyName, null, game.EncounterNumber);
            string danger = pSurvive < 0.5 ? " ⚠ DANGER" : "";
            action = "?";
            rationale = $"P(survive hit) = {pSurvive:P0} vs {game.EnemyName ?? "enemy"}.{danger} Ability names not visible in log.";
            break;
        }
        case GamePhase.HatSelection:
        {
            var newHatM = Regex.Match(plain, @"found a new hat[:\s]+([^\n!.]+)", RegexOptions.IgnoreCase);
            string newName = newHatM.Success ? newHatM.Groups[1].Value.Trim() : "new hat";
            action = "?";
            rationale = $"New: {newName}. Current: {game.HatName} — {game.HatDescription ?? "no desc"}. Compare effects manually.";
            break;
        }
        case GamePhase.PerkSelection:
            action = "?";
            rationale = "Choices not in log. Priority: Lucky > Tough/Resilient > Diplomat > damage perks.";
            break;
        case GamePhase.DiplomacyCheck:
            pSurvive = DecisionEngine.Prob3d6GeqTarget(10, game.CulturalArtifacts);
            action = pSurvive > 0.5 && game.MaxHP > 0 && game.CurrentHP > 0.3 * game.MaxHP ? "Attempt" : "Skip";
            rationale = $"P(success with {game.CulturalArtifacts} artifacts) ≈ {pSurvive:P0}. {(game.CurrentHP <= 0.3 * game.MaxHP ? "HP too low — crit fail is fatal." : "")}";
            break;
    }

    if (action == "") return;
    game.RecAction    = action;
    game.RecRationale = rationale;
    game.RecEvCashOut = evCashOut;
    game.RecEvDelve   = evDelve;
    game.RecEvRest    = evRest;
    game.RecPSurvive  = pSurvive;
    game.LastDecision = $"[{action}] {rationale}";
    if (action != "?")
        AddEvent($"[>] {game.Phase}: {action} — {rationale}");
}

// ── BuildApiState ──
object BuildApiState(MnMGame? game, List<MnMGame> history, List<string> evLog)
{
    int wins = history.Count(g => g.CashedOut);
    int deaths = history.Count(g => g.Died);
    int totalGold = history.Sum(g => g.FinalGold);
    int totalTokens = history.Sum(g => g.FinalTokens);

    if (game == null)
    {
        return new
        {
            status = "waiting",
            message = "Waiting for Monsters & Mantids game in Player.log...",
            history = new { gamesPlayed = history.Count, wins, deaths, totalGold, totalTokens },
            eventLog = evLog.AsEnumerable().Reverse().Take(50).ToArray()
        };
    }

    return new
    {
        status = game.Status,
        phase = game.Phase.ToString(),
        startedAt = game.StartedAt.ToString("HH:mm:ss"),
        hero = new
        {
            currentHP = game.CurrentHP,
            maxHP = game.MaxHP,
            level = game.Level,
            gold = game.Gold,
            culturalArtifacts = game.CulturalArtifacts,
            healthPotions = game.HealthPotions,
            makeshiftBombs = game.MakeshiftBombs,
            hat = game.HatName,
            hatDescription = game.HatDescription,
            abilities = game.Abilities.Select(a => new { a.name, a.dice }).ToArray(),
            perks = game.Perks.ToArray()
        },
        enemy = game.EnemyName == null ? null : new
        {
            name = game.EnemyName,
            dice = game.EnemyDice,
            currentHP = game.EnemyCurrentHP,
            maxHP = game.EnemyMaxHP
        },
        leaderboardScore = game.LeaderboardScore,
        encounterNumber = game.EncounterNumber,
        lastDecision = game.LastDecision,
        recommendation = game.RecAction == null ? null : new
        {
            action    = game.RecAction,
            rationale = game.RecRationale,
            evCashOut = Math.Round(game.RecEvCashOut, 1),
            evDelve   = Math.Round(game.RecEvDelve,   1),
            evRest    = Math.Round(game.RecEvRest,    1),
            pSurvive  = Math.Round(game.RecPSurvive * 100, 0)
        },
        autoplay = _autoplay,
        autoloop = _autoloop,
        history = new { gamesPlayed = history.Count, wins, deaths, totalGold, totalTokens },
        eventLog = evLog.AsEnumerable().Reverse().Take(50).ToArray()
    };
}

// ── Log Tailer ──
async Task TailLog(CancellationToken ct)
{
    Console.WriteLine($"[*] Tailing {logPath}");

    // Scan last portion of log for recent M&M TalkScreens
    {
        using var scanFs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        // Only scan last 500KB to avoid processing entire log
        long seekBack = Math.Min(scanFs.Length, 500_000);
        scanFs.Seek(-seekBack, SeekOrigin.End);
        using var scanReader = new StreamReader(scanFs);
        scanReader.ReadLine(); // discard partial line

        var mnmLines = new List<string>();
        while (scanReader.ReadLine() is { } line)
        {
            if (line.Contains("Monsters") && line.Contains("ProcessTalkScreen"))
                mnmLines.Add(line);
            else if (line.Contains("MonstersAndMantids") && line.Contains("ProcessStartInteraction"))
                mnmLines.Add(line);
        }

        if (mnmLines.Count > 0)
        {
            Console.WriteLine($"[*] Found {mnmLines.Count} M&M events in log, replaying last game...");
            // Find the last game start and replay from there
            int lastStart = mnmLines.FindLastIndex(l => l.Contains("ProcessStartInteraction"));
            int start = lastStart >= 0 ? lastStart : Math.Max(0, mnmLines.Count - 20);
            for (int i = start; i < mnmLines.Count; i++)
                ProcessLogLine(mnmLines[i]);
        }
    }

    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    fs.Seek(0, SeekOrigin.End);
    using var reader = new StreamReader(fs);

    while (!ct.IsCancellationRequested)
    {
        string? line = await reader.ReadLineAsync(ct);
        if (line != null)
        {
            ProcessLogLine(line);
        }
        else
        {
            try { await Task.Delay(250, ct); } catch (OperationCanceledException) { break; }
        }
    }
}

// ── Startup ──
Console.WriteLine("=== MnM Solver for Project Gorgon ===");
Console.WriteLine($"Port: {port}");

foreach (var arg in args)
{
    if (arg == "--autoplay") _autoplay = true;
    if (arg == "--autoloop") _autoloop = true;
}

if (_autoplay) Console.WriteLine("[*] Autoplay enabled");
if (_autoloop) Console.WriteLine("[*] Autoloop enabled");

var logTask = Task.Run(() => TailLog(cts.Token));
var httpTask = Task.Run(() => RunHttpServer(cts.Token));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await Task.WhenAll(logTask, httpTask);

// ── Data Models ──
class MnMGame
{
    public DateTime StartedAt = DateTime.Now;
    public string Status = "playing";
    public int CurrentHP, MaxHP;
    public int Level = 1;
    public int Gold;
    public int CulturalArtifacts;
    public int HealthPotions;
    public int MakeshiftBombs;
    public int EncounterNumber;
    public string? HatName = "Basic Hat";
    public string? HatDescription;
    public List<(string name, string dice)> Abilities = new();
    public List<string> Perks = new();
    public string? EnemyName, EnemyDice;
    public int EnemyCurrentHP, EnemyMaxHP;
    public int LastDamageDealt, LastDamageTaken;
    public List<(int id, string label)> Choices = new();
    public GamePhase Phase = GamePhase.WaitingForGame;
    public string? LastDecision;
    public string? RecAction;
    public string? RecRationale;
    public double RecEvCashOut, RecEvDelve, RecEvRest, RecPSurvive;
    public List<string> EventLog = new();
    public bool CashedOut, Died;
    public int FinalGold, FinalTokens;
    public int LeaderboardScore;
    public string? RawLastHtml;
}

enum GamePhase
{
    WaitingForGame, CombatAbilitySelect, PostVictoryChoice,
    DiplomacyCheck, SavingThrow, PerkSelection, HatSelection,
    MeditateChoice, ConsumableChoice, ShopEncounter, GameOver, Unknown
}

// ── Dashboard HTML ──
static class DashboardHtml
{
    public const string PAGE = """
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>MnM Solver - Monsters &amp; Mantids</title>
<style>
  * { box-sizing: border-box; margin: 0; padding: 0; }
  body { background: #1a1a2e; color: #e0e0e0; font-family: 'Consolas', 'Courier New', monospace; font-size: 13px; padding: 12px; }
  h1 { color: #c8a0e0; font-size: 18px; margin-bottom: 12px; }
  h2 { color: #a0c8e0; font-size: 13px; margin-bottom: 6px; text-transform: uppercase; letter-spacing: 1px; }
  .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 10px; }
  .grid3 { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 10px; }
  .panel { background: #16213e; border: 1px solid #2a3a5e; border-radius: 6px; padding: 10px; }
  .full { grid-column: 1 / -1; }
  .hp-bar-outer { background: #0d0d1a; border-radius: 4px; height: 16px; margin: 6px 0; overflow: hidden; }
  .hp-bar-inner { height: 100%; border-radius: 4px; transition: width 0.3s, background 0.3s; }
  .hp-green { background: #2ea84b; }
  .hp-yellow { background: #c8a020; }
  .hp-red { background: #c82020; }
  .stat { display: flex; justify-content: space-between; margin: 3px 0; }
  .stat-label { color: #8898aa; }
  .stat-value { color: #e8e8e8; font-weight: bold; }
  .phase-badge { display: inline-block; background: #2a1a5e; color: #c8a0ff; border: 1px solid #5a3a9e; border-radius: 4px; padding: 2px 8px; font-size: 11px; margin-bottom: 6px; }
  .choice { background: #0d1a2e; border: 1px solid #2a4a6e; border-radius: 4px; padding: 5px 8px; margin: 3px 0; cursor: default; }
  .choice:hover { border-color: #5a8abe; }
  .decision { background: #0d1a1a; border-left: 3px solid #2ea84b; padding: 6px 10px; border-radius: 0 4px 4px 0; font-style: italic; color: #a0e0a0; min-height: 30px; }
  .rec-action { font-size: 20px; font-weight: bold; letter-spacing: 2px; margin-bottom: 6px; }
  .rec-cashout { color: #e8c040; }
  .rec-delve   { color: #40e880; }
  .rec-rest    { color: #40b8e8; }
  .rec-meditate { color: #a060e8; }
  .rec-attempt  { color: #40e880; }
  .rec-skip     { color: #e84040; }
  .rec-unknown  { color: #888; }
  .rec-rationale { color: #c0d0c0; font-size: 12px; margin-bottom: 8px; }
  .ev-row { display: flex; gap: 10px; flex-wrap: wrap; margin-top: 4px; }
  .ev-cell { background: #0a1a2a; border: 1px solid #1a3a4a; border-radius: 4px; padding: 4px 8px; text-align: center; }
  .ev-cell .ev-label { color: #6888a8; font-size: 10px; }
  .ev-cell .ev-val { color: #c0e0ff; font-weight: bold; font-size: 13px; }
  .event-log { height: 180px; overflow-y: auto; background: #0d0d1a; border-radius: 4px; padding: 6px; }
  .event-entry { color: #9abaaa; margin: 1px 0; font-size: 11px; white-space: pre-wrap; word-break: break-all; }
  .event-entry.new { color: #e0e0e0; }
  .history-stat { text-align: center; }
  .history-stat .big { font-size: 22px; font-weight: bold; color: #c8d0e8; }
  .history-stat .label { color: #6878a8; font-size: 11px; }
  .waiting { color: #6878a8; text-align: center; padding: 20px; font-style: italic; }
  .enemy-dice { color: #e0a040; font-weight: bold; }
  .ability-row { display: flex; justify-content: space-between; padding: 2px 0; border-bottom: 1px solid #1a2a3e; }
  .ability-name { color: #b0c8e8; }
  .ability-dice { color: #e0a040; }
  .perk-tag { display: inline-block; background: #1a2e1a; color: #80c880; border: 1px solid #2a5e2a; border-radius: 3px; padding: 1px 6px; margin: 2px; font-size: 11px; }
</style>
</head>
<body>
<h1>&#x1F3B2; MnM Solver &mdash; Monsters &amp; Mantids</h1>

<div id="content" class="waiting">Connecting to solver...</div>

<script>
let prevLog = [];

function hpColor(cur, max) {
  if (max === 0) return 'hp-green';
  const pct = cur / max;
  if (pct > 0.6) return 'hp-green';
  if (pct > 0.3) return 'hp-yellow';
  return 'hp-red';
}

function hpPct(cur, max) {
  if (max === 0) return 100;
  return Math.min(100, Math.round(cur / max * 100));
}

function esc(s) {
  return String(s).replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;');
}

function render(d) {
  if (d.status === 'waiting') {
    document.getElementById('content').innerHTML =
      '<div class="waiting">' + esc(d.message) + '</div>' + renderHistory(d.history) + renderEventLog(d.eventLog);
    return;
  }

  const hero = d.hero || {};
  const hpPctVal = hpPct(hero.currentHP, hero.maxHP);
  const hpClass = hpColor(hero.currentHP, hero.maxHP);

  let abilitiesHtml = '';
  if (hero.abilities && hero.abilities.length > 0) {
    abilitiesHtml = hero.abilities.map(a =>
      '<div class="ability-row"><span class="ability-name">' + esc(a.name) + '</span><span class="ability-dice">' + esc(a.dice) + '</span></div>'
    ).join('');
  } else {
    abilitiesHtml = '<div style="color:#444">none yet</div>';
  }

  let perksHtml = '';
  if (hero.perks && hero.perks.length > 0) {
    perksHtml = hero.perks.map(p => '<span class="perk-tag">' + esc(p) + '</span>').join('');
  } else {
    perksHtml = '<span style="color:#444">none</span>';
  }

  let enemyHtml = '<div class="waiting">No enemy</div>';
  if (d.enemy) {
    enemyHtml = '<div class="stat"><span class="stat-label">Name</span><span class="stat-value">' + esc(d.enemy.name) + '</span></div>'
              + '<div class="stat"><span class="stat-label">Dice</span><span class="enemy-dice">' + esc(d.enemy.dice || '?') + '</span></div>';
  }

  let choicesHtml = '';
  if (d.choices && d.choices.length > 0) {
    choicesHtml = d.choices.map(c =>
      '<div class="choice">[' + c.id + '] ' + esc(c.label) + '</div>'
    ).join('');
  } else {
    choicesHtml = '<div style="color:#444">No choices available</div>';
  }

  document.getElementById('content').innerHTML = `
<div class="grid">
  <div class="panel">
    <h2>Hero</h2>
    <div class="stat"><span class="stat-label">HP</span><span class="stat-value">${esc(hero.currentHP)} / ${esc(hero.maxHP)}</span></div>
    <div class="hp-bar-outer"><div class="hp-bar-inner ${hpClass}" style="width:${hpPctVal}%"></div></div>
    <div class="stat"><span class="stat-label">Level</span><span class="stat-value">${esc(hero.level)}</span></div>
    <div class="stat"><span class="stat-label">Gold</span><span class="stat-value">${esc(hero.gold)}</span></div>
    <div class="stat"><span class="stat-label">Artifacts</span><span class="stat-value">${esc(hero.culturalArtifacts)}</span></div>
    <div class="stat"><span class="stat-label">Hat</span><span class="stat-value">${esc(hero.hat || '?')}</span></div>
    <div class="stat"><span class="stat-label">Encounter</span><span class="stat-value">#${esc(d.encounterNumber)}</span></div>
  </div>
  <div class="panel">
    <h2>Enemy</h2>
    ${enemyHtml}
    <br>
    <h2>Abilities</h2>
    ${abilitiesHtml}
    <br>
    <h2>Perks</h2>
    ${perksHtml}
  </div>
  <div class="panel">
    <h2>Choices &mdash; <span class="phase-badge">${esc(d.phase)}</span></h2>
    ${choicesHtml}
  </div>
  <div class="panel">
    <h2>Recommendation &mdash; <span class="phase-badge">${esc(d.phase)}</span></h2>
    ${renderRecommendation(d.recommendation)}
    <div style="margin-top:8px;color:#6878a8;font-size:11px">Autoplay: ${d.autoplay ? '<span style="color:#2ea84b">ON</span>' : 'OFF'} | Autoloop: ${d.autoloop ? '<span style="color:#2ea84b">ON</span>' : 'OFF'}</div>
  </div>
</div>
<br>
${renderHistory(d.history)}
${renderEventLog(d.eventLog)}
`;
}

function renderRecommendation(r) {
  if (!r) return '<div style="color:#444;font-style:italic">Awaiting decision...</div>';
  const actionLower = r.action.toLowerCase();
  const cls = actionLower === 'cashout' ? 'rec-cashout'
            : actionLower === 'delve'   ? 'rec-delve'
            : actionLower === 'rest'    ? 'rec-rest'
            : actionLower === 'meditate' ? 'rec-meditate'
            : actionLower === 'attempt' ? 'rec-attempt'
            : actionLower === 'skip'    ? 'rec-skip'
            : 'rec-unknown';
  const label = r.action === '?' ? '— see rationale —' : r.action.toUpperCase();
  let evHtml = '';
  if (r.evCashOut > 0 || r.evDelve > 0) {
    evHtml = `<div class="ev-row">
      <div class="ev-cell"><div class="ev-label">EV Cash Out</div><div class="ev-val">${r.evCashOut}</div></div>
      <div class="ev-cell"><div class="ev-label">EV Delve</div><div class="ev-val">${r.evDelve}</div></div>
      ${r.evRest > 0 ? `<div class="ev-cell"><div class="ev-label">EV Rest</div><div class="ev-val">${r.evRest}</div></div>` : ''}
      ${r.pSurvive > 0 ? `<div class="ev-cell"><div class="ev-label">P(survive)</div><div class="ev-val">${r.pSurvive}%</div></div>` : ''}
    </div>`;
  } else if (r.pSurvive > 0) {
    evHtml = `<div class="ev-row"><div class="ev-cell"><div class="ev-label">P(survive hit)</div><div class="ev-val">${r.pSurvive}%</div></div></div>`;
  }
  return `<div class="rec-action ${cls}">${esc(label)}</div>
          <div class="rec-rationale">${esc(r.rationale)}</div>
          ${evHtml}`;
}

function renderHistory(h) {
  if (!h) return '';
  return `
<div class="panel" style="margin-top:10px">
  <h2>Session History</h2>
  <div class="grid3">
    <div class="history-stat"><div class="big">${esc(h.gamesPlayed)}</div><div class="label">Games</div></div>
    <div class="history-stat"><div class="big" style="color:#2ea84b">${esc(h.wins)}</div><div class="label">Wins</div></div>
    <div class="history-stat"><div class="big" style="color:#c82020">${esc(h.deaths)}</div><div class="label">Deaths</div></div>
    <div class="history-stat"><div class="big">${esc(h.totalGold)}</div><div class="label">Total Gold</div></div>
    <div class="history-stat"><div class="big">${esc(h.totalTokens)}</div><div class="label">Tokens</div></div>
  </div>
</div>`;
}

function renderEventLog(log) {
  if (!log || log.length === 0) return '';
  const entries = log.map((e, i) =>
    '<div class="event-entry' + (i < 3 ? ' new' : '') + '">' + esc(e) + '</div>'
  ).join('');
  return `
<div class="panel" style="margin-top:10px">
  <h2>Event Log</h2>
  <div class="event-log">${entries}</div>
</div>`;
}

async function poll() {
  try {
    const r = await fetch('/api/state');
    const d = await r.json();
    render(d);
  } catch(e) {
    document.getElementById('content').innerHTML = '<div class="waiting">Connection error: ' + e.message + '</div>';
  }
  setTimeout(poll, 2000);
}

poll();
</script>
</body>
</html>
""";
}

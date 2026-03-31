using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

// ProcessTalkScreen(entityId, "title", "bodyHTML", "notification", Int32[id1, id2, ...], String["choice1", "choice2", ...], curState, TypeName)
var _talkScreenRx = new Regex(
    @"ProcessTalkScreen\((\d+),\s*""((?:[^""\\]|\\.)*)"",\s*""((?:[^""\\]|\\.)*)"",\s*""(?:[^""\\]|\\.)*"",\s*Int32\[([^\]]*)\],\s*String\[([^\]]*)\],\s*\d+,\s*(\w+)\)",
    RegexOptions.Compiled);

var _preTalkScreenRx = new Regex(
    @"ProcessPreTalkScreen\((\d+),\s*""((?:[^""\\]|\\.)*)""\)",
    RegexOptions.Compiled);

var _screenTextRx = new Regex(
    @"ProcessScreenText\(""([^""]*)""\)",
    RegexOptions.Compiled);

void ProcessLogLine(string line)
{
    // ProcessTalkScreen
    var m = _talkScreenRx.Match(line);
    if (m.Success)
    {
        int entityId = int.Parse(m.Groups[1].Value);
        string title = m.Groups[2].Value.Replace("\\\"", "\"");
        string bodyHtml = m.Groups[3].Value.Replace("\\\"", "\"").Replace("\\n", "\n");
        string rawChoiceIds = m.Groups[4].Value;
        string rawChoices = m.Groups[5].Value;
        string talkType = m.Groups[6].Value;

        bool isMnM = title.Contains("Monsters", StringComparison.OrdinalIgnoreCase)
                  || title.Contains("Mantids", StringComparison.OrdinalIgnoreCase)
                  || talkType == "DiceRoll";
        if (!isMnM) return;

        // Parse choice IDs: "1, 2, 3" -> [1, 2, 3]
        var choiceIds = rawChoiceIds
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0)
            .ToList();

        // Parse choice labels: "\"Cash Out\", \"Delve Deeper\"" -> ["Cash Out", "Delve Deeper"]
        var choiceLabels = Regex.Matches(rawChoices, @"""((?:[^""\\]|\\.)*)""")
            .Select(cm => cm.Groups[1].Value.Replace("\\\"", "\""))
            .ToList();

        lock (_lock)
        {
            if (_currentGame == null || _currentGame.Status != "playing")
            {
                _currentGame = new MnMGame();
                _mnmNpcId = entityId;
                AddEvent($"[+] New MnM game started (NPC {entityId}): {title}");
            }

            var game = _currentGame;
            game.RawLastHtml = bodyHtml;

            ParseGameState(bodyHtml, game);

            game.Choices = choiceIds
                .Zip(choiceLabels, (id, label) => (id, label))
                .ToList();

            game.Phase = DetectPhase(game);
        }

        // Dump raw HTML for learning
        try
        {
            var rawLogPath = Path.Combine(settingsDir, "mnm_raw_logs.txt");
            File.AppendAllText(rawLogPath,
                $"\n=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {title} | Phase={_currentGame?.Phase} ===\n{bodyHtml}\n");
        }
        catch { }

        AddEvent($"[*] TalkScreen: {title} | Phase={_currentGame?.Phase} | Choices={_currentGame?.Choices.Count}");
        return;
    }

    // ProcessPreTalkScreen
    var pm = _preTalkScreenRx.Match(line);
    if (pm.Success)
    {
        int entityId = int.Parse(pm.Groups[1].Value);
        string info = pm.Groups[2].Value;
        AddEvent($"[~] PreTalkScreen entity={entityId}: {info}");
        return;
    }

    // ProcessScreenText — watch for gold payouts
    var sm = _screenTextRx.Match(line);
    if (sm.Success)
    {
        string text = sm.Groups[1].Value;
        if (text.Contains("Council", StringComparison.OrdinalIgnoreCase))
            AddEvent($"[*] ScreenText (gold?): {text}");
    }
}

// ── State Parser ──
void ParseGameState(string bodyHtml, MnMGame game)
{
    // Strip HTML tags for plain text parsing
    string plain = Regex.Replace(bodyHtml, "<[^>]+>", " ");
    plain = System.Net.WebUtility.HtmlDecode(plain);

    // HP: "Health: N/M", "HP: N/M", "Hit Points: N/M"
    var hpM = Regex.Match(plain, @"(?:Health|HP|Hit Points)\s*[:\-]\s*(\d+)\s*/\s*(\d+)", RegexOptions.IgnoreCase);
    if (hpM.Success)
    {
        game.CurrentHP = int.Parse(hpM.Groups[1].Value);
        game.MaxHP = int.Parse(hpM.Groups[2].Value);
    }

    // Gold: "Gold: N", "found N gold", "gained N gold"
    var goldM = Regex.Match(plain, @"(?:Gold\s*:\s*(\d+)|(?:found|gained)\s+(\d+)\s+gold)", RegexOptions.IgnoreCase);
    if (goldM.Success)
    {
        var val = goldM.Groups[1].Success ? goldM.Groups[1].Value : goldM.Groups[2].Value;
        if (int.TryParse(val, out int g)) game.Gold = g;
    }

    // Cultural Artifacts: "Cultural Artifacts: N"
    var artM = Regex.Match(plain, @"Cultural Artifacts?\s*[:\-]\s*(\d+)", RegexOptions.IgnoreCase);
    if (artM.Success && int.TryParse(artM.Groups[1].Value, out int art))
        game.CulturalArtifacts = art;

    // Level: "Level N"
    var lvlM = Regex.Match(plain, @"\bLevel\s+(\d+)\b", RegexOptions.IgnoreCase);
    if (lvlM.Success && int.TryParse(lvlM.Groups[1].Value, out int lvl))
        game.Level = lvl;

    // Enemy + dice formula: word(s) followed by "(NdM+K)" or "(ND+K)" or "(ND6+K)"
    var enemyM = Regex.Match(plain, @"([A-Z][a-zA-Z\s\-']+?)\s*\((\d+[Dd]\d*(?:[+\-]\d+)?)\)", RegexOptions.IgnoreCase);
    if (enemyM.Success)
    {
        game.EnemyName = enemyM.Groups[1].Value.Trim();
        game.EnemyDice = enemyM.Groups[2].Value.Trim();
    }

    // Hat: "Hat: ..." or "wearing ..."
    var hatM = Regex.Match(plain, @"(?:Hat\s*[:\-]\s*|wearing\s+)([A-Za-z][A-Za-z\s]+?)(?:\.|,|$)", RegexOptions.IgnoreCase);
    if (hatM.Success)
        game.HatName = hatM.Groups[1].Value.Trim();

    // Abilities: "AbilityName (NdM+K)"
    var abilityMatches = Regex.Matches(plain, @"([A-Z][a-zA-Z\s]+?)\s+\((\d+[Dd]\d+(?:[+\-]\d+)?)\)");
    foreach (Match am in abilityMatches)
    {
        var name = am.Groups[1].Value.Trim();
        var dice = am.Groups[2].Value.Trim();
        // Avoid duplicating enemy entry
        if (name != game.EnemyName && !game.Abilities.Any(a => a.name == name))
            game.Abilities.Add((name, dice));
    }

    // Encounter number
    var encM = Regex.Match(plain, @"(?:Encounter|Floor|Room)\s+#?\s*(\d+)", RegexOptions.IgnoreCase);
    if (encM.Success && int.TryParse(encM.Groups[1].Value, out int enc))
        game.EncounterNumber = enc;

    // Detect death / game over
    if (Regex.IsMatch(plain, @"\b(?:defeated|dead|died|game over)\b", RegexOptions.IgnoreCase))
    {
        game.Died = true;
        game.Status = "over";
    }

    // Detect cash-out
    if (Regex.IsMatch(plain, @"\bcashed?\s*out\b", RegexOptions.IgnoreCase))
    {
        game.CashedOut = true;
        game.Status = "over";
    }
}

// ── Phase Detection ──
GamePhase DetectPhase(MnMGame game)
{
    var labels = game.Choices.Select(c => c.label).ToList();
    var bodyText = game.RawLastHtml ?? "";

    bool anyLabel(params string[] terms) =>
        labels.Any(l => terms.Any(t => l.Contains(t, StringComparison.OrdinalIgnoreCase)));

    if (anyLabel("Delve Deeper", "Cash Out")) return GamePhase.PostVictoryChoice;
    if (anyLabel("Keep", "Wear")) return GamePhase.HatSelection;
    if (anyLabel("Diplomacy", "Walk Away")) return GamePhase.DiplomacyCheck;
    if (bodyText.Contains("perk", StringComparison.OrdinalIgnoreCase)) return GamePhase.PerkSelection;
    if (bodyText.Contains("meditat", StringComparison.OrdinalIgnoreCase)) return GamePhase.MeditateChoice;
    if (game.Died || bodyText.Contains("game over", StringComparison.OrdinalIgnoreCase)) return GamePhase.GameOver;
    if (labels.Count > 0) return GamePhase.CombatAbilitySelect;
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
            hat = game.HatName,
            abilities = game.Abilities.Select(a => new { a.name, a.dice }).ToArray(),
            perks = game.Perks.ToArray()
        },
        enemy = game.EnemyName == null ? null : new { name = game.EnemyName, dice = game.EnemyDice },
        choices = game.Choices.Select(c => new { c.id, c.label }).ToArray(),
        encounterNumber = game.EncounterNumber,
        lastDecision = game.LastDecision,
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

    // Scan existing log for the last MnM TalkScreen
    {
        string? lastMnMLine = null;
        using var scanFs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        long seekBack = scanFs.Length;
        if (seekBack > 0)
        {
            scanFs.Seek(-seekBack, SeekOrigin.End);
            using var scanReader = new StreamReader(scanFs);
            scanReader.ReadLine(); // discard partial line
            while (scanReader.ReadLine() is { } line)
            {
                if (_talkScreenRx.IsMatch(line))
                {
                    var m2 = _talkScreenRx.Match(line);
                    string t = m2.Groups[2].Value;
                    string tp = m2.Groups[6].Value;
                    if (t.Contains("Monsters", StringComparison.OrdinalIgnoreCase)
                     || t.Contains("Mantids", StringComparison.OrdinalIgnoreCase)
                     || tp == "DiceRoll")
                        lastMnMLine = line;
                }
            }
        }
        if (lastMnMLine != null)
        {
            Console.WriteLine("[*] Found existing MnM game in log, processing...");
            ProcessLogLine(lastMnMLine);
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
    public int EncounterNumber;
    public string? HatName = "Basic Hat";
    public List<(string name, string dice)> Abilities = new();
    public List<string> Perks = new();
    public string? EnemyName, EnemyDice;
    public List<(int id, string label)> Choices = new();
    public GamePhase Phase = GamePhase.WaitingForGame;
    public string? LastDecision;
    public List<string> EventLog = new();
    public bool CashedOut, Died;
    public int FinalGold, FinalTokens;
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
    <h2>Decision</h2>
    <div class="decision">${d.lastDecision ? esc(d.lastDecision) : '<span style="color:#444">Awaiting decision engine...</span>'}</div>
    <div style="margin-top:8px;color:#6878a8;font-size:11px">Autoplay: ${d.autoplay ? '<span style="color:#2ea84b">ON</span>' : 'OFF'} | Autoloop: ${d.autoloop ? '<span style="color:#2ea84b">ON</span>' : 'OFF'}</div>
  </div>
</div>
<br>
${renderHistory(d.history)}
${renderEventLog(d.eventLog)}
`;
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

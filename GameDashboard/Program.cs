using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MemoryLib;
using MemoryLib.Models;
using MemoryLib.Readers;

class GameDashboard
{
    // Shared state
    static DashboardData _data = new();
    static readonly object _lock = new();
    static string _logPath = "";
    static int _gamePid = 0;

    static async Task Main(string[] args)
    {
        Console.WriteLine("=== Project Gorgon Live Dashboard ===");

        // Find game process
        var proc = Process.GetProcessesByName("WindowsPlayer").FirstOrDefault();
        if (proc == null)
        {
            Console.WriteLine("Project Gorgon not running! Start the game first.");
            return;
        }
        _gamePid = proc.Id;
        _data.ProcessName = proc.MainWindowTitle;
        _data.GamePid = _gamePid;
        Console.WriteLine($"Found: {proc.MainWindowTitle} (PID {proc.Id})");

        // Find log file
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "Elder Game", "Project Gorgon");
        _logPath = Path.Combine(logDir, "Player.log");
        var prevLogPath = Path.Combine(logDir, "Player-prev.log");

        bool playerLogActive = File.Exists(_logPath) && new FileInfo(_logPath).Length > 0;
        string backfillPath = prevLogPath;

        if (!playerLogActive)
            _logPath = prevLogPath;

        Console.WriteLine($"Active log: {_logPath}");
        Console.WriteLine($"Backfill from: {backfillPath}");

        var cts = new CancellationTokenSource();

        // Start MemoryPoller
        MemoryPoller? poller = null;
        ProcessMemory? memory = null;
        try
        {
            memory = ProcessMemory.Open(proc.Id);
            var scanner = new MemoryRegionScanner(memory);
            poller = new MemoryPoller(memory, scanner);

            poller.OnSkillsChanged += skills =>
            {
                lock (_lock) { _data.Skills = skills; }
            };
            poller.OnInventoryChanged += items =>
            {
                lock (_lock) { _data.Inventory = items; }
            };
            poller.OnCombatantChanged += combatant =>
            {
                lock (_lock) { _data.Combatant = combatant; }
            };
            poller.OnEffectsChanged += effects =>
            {
                lock (_lock) { _data.MemoryEffects = effects; }
            };
            poller.OnDeathChanged += isDead =>
            {
                lock (_lock)
                {
                    string ts = DateTime.Now.ToString("HH:mm:ss");
                    AddEvent(isDead ? "DEATH" : "ALIVE", isDead ? "Player died" : "Player alive", ts);
                }
            };
            poller.OnError += ex =>
            {
                lock (_lock) { _data.Errors.Add($"Poller error: {ex.Message}"); }
            };

            bool discovered = poller.AutoDiscoverAll();
            if (discovered)
            {
                poller.Start();
                Console.WriteLine("[MemoryPoller] Started.");
            }
            else
            {
                Console.WriteLine("[MemoryPoller] AutoDiscover found nothing — running in log-only mode.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemoryPoller] Failed to open process memory: {ex.Message}");
            Console.WriteLine("Running in log-only mode.");
        }

        // Process stats timer (working set, threads, handles)
        var statsTimer = new System.Timers.Timer(3000);
        statsTimer.Elapsed += (s, e) =>
        {
            try
            {
                var p = Process.GetProcessById(_gamePid);
                lock (_lock)
                {
                    _data.MemoryMB = Math.Round(p.WorkingSet64 / 1048576.0, 1);
                    _data.PrivateMemoryMB = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1);
                    _data.PeakMemoryMB = Math.Round(p.PeakWorkingSet64 / 1048576.0, 1);
                    _data.ThreadCount = p.Threads.Count;
                    _data.HandleCount = p.HandleCount;
                    _data.LastMemoryScan = DateTime.Now.ToString("HH:mm:ss");

                    _data.MemoryHistory.Add(new MemorySample
                    {
                        Time = DateTime.Now.ToString("HH:mm:ss"),
                        WorkingSetMB = _data.MemoryMB,
                        PrivateMB = _data.PrivateMemoryMB
                    });
                    if (_data.MemoryHistory.Count > 60)
                        _data.MemoryHistory.RemoveAt(0);
                }
            }
            catch { }
        };
        statsTimer.Start();

        var logTask = Task.Run(() => LogTailLoop(cts.Token));
        var httpTask = Task.Run(() => RunHttpServer(cts.Token));

        Console.WriteLine("\nDashboard running at http://localhost:9876");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        try { await Task.WhenAll(logTask, httpTask); }
        catch (OperationCanceledException) { }

        statsTimer.Stop();
        statsTimer.Dispose();
        poller?.Stop();
        poller?.Dispose();
        memory?.Dispose();

        Console.WriteLine("Dashboard stopped.");
    }

    static void LogTailLoop(CancellationToken ct)
    {
        long lastPos = 0;

        var logsToBackfill = new List<string>();
        var prevPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "Elder Game", "Project Gorgon", "Player-prev.log");
        if (File.Exists(prevPath))
            logsToBackfill.Add(prevPath);
        if (File.Exists(_logPath) && _logPath != prevPath)
            logsToBackfill.Add(_logPath);

        foreach (var logFile in logsToBackfill)
        {
            try
            {
                var fi = new FileInfo(logFile);
                long backfillStart = Math.Max(0, fi.Length - 1024 * 1024);
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(backfillStart, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                if (backfillStart > 0) reader.ReadLine();
                string? line;
                while ((line = reader.ReadLine()) != null)
                    ProcessLogLine(line);
            }
            catch { }
        }

        if (File.Exists(_logPath))
            lastPos = new FileInfo(_logPath).Length;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(_logPath)) { ct.WaitHandle.WaitOne(2000); continue; }

                var fi = new FileInfo(_logPath);
                if (fi.Length > lastPos)
                {
                    using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    fs.Seek(lastPos, SeekOrigin.Begin);
                    using var reader = new StreamReader(fs);
                    string? line;
                    while ((line = reader.ReadLine()) != null)
                        ProcessLogLine(line);
                    lastPos = fi.Length;
                }
                else if (fi.Length < lastPos)
                    lastPos = 0;
            }
            catch { }
            ct.WaitHandle.WaitOne(500);
        }
    }

    static void ProcessLogLine(string line)
    {
        lock (_lock)
        {
            var tsMatch = Regex.Match(line, @"^\[(\d{2}:\d{2}:\d{2})\]");
            string ts = tsMatch.Success ? tsMatch.Groups[1].Value : DateTime.Now.ToString("HH:mm:ss");

            if (line.Contains("causeOfDeath="))
            {
                var m = Regex.Match(line, @"causeOfDeath=([^,]+)");
                if (m.Success) AddEvent("DEATH", $"Died: {m.Groups[1].Value}", ts);
            }
            else if (line.Contains("LOADING LEVEL"))
            {
                var m = Regex.Match(line, @"LOADING LEVEL (.+)$");
                if (m.Success)
                {
                    _data.CurrentZone = m.Groups[1].Value.Trim();
                    AddEvent("ZONE", $"Loading: {_data.CurrentZone}", ts);
                }
            }
            else if (line.Contains("ProcessTalkScreen"))
            {
                if (line.Contains("Favor Complete:") || line.Contains("Favor Advanced:"))
                {
                    var m = Regex.Match(line, @"(Favor (?:Complete|Advanced): [^<""\\]+)");
                    if (m.Success) AddEvent("FAVOR", m.Groups[1].Value, ts);
                }
                else if (line.Contains("currently tasked"))
                {
                    var tasks = Regex.Matches(line, @"<b>([^<]+)</b>");
                    foreach (Match m in tasks)
                    {
                        var q = m.Groups[1].Value;
                        if (!_data.ActiveQuests.Contains(q))
                            _data.ActiveQuests.Add(q);
                    }
                    if (_data.ActiveQuests.Count > 30)
                        _data.ActiveQuests = _data.ActiveQuests.TakeLast(30).ToList();
                }
                else if (line.Contains("Examine "))
                {
                    var m = Regex.Match(line, @"Examine (\w+).*?Active Combat Skills: <i>([^<]+)</i>");
                    if (m.Success)
                    {
                        _data.PlayersExamined[m.Groups[1].Value] = m.Groups[2].Value;
                        AddEvent("PLAYER", $"Examined {m.Groups[1].Value}: {m.Groups[2].Value}", ts);
                    }
                }
                else if (line.Contains("Search Corpse of"))
                {
                    var m = Regex.Match(line, @"Search Corpse of (.+?)""");
                    if (m.Success)
                    {
                        var creature = m.Groups[1].Value;
                        if (!_data.RecentKills.Contains(creature))
                        {
                            _data.RecentKills.Add(creature);
                            if (_data.RecentKills.Count > 20) _data.RecentKills.RemoveAt(0);
                            AddEvent("KILL", $"Looted: {creature}", ts);
                        }
                    }
                    if (line.Contains("obtained"))
                    {
                        var m2 = Regex.Match(line, @"obtained (.+?)""");
                        if (m2.Success) AddEvent("LOOT", $"Obtained: {m2.Groups[1].Value}", ts);
                    }
                }
            }
            else if (line.Contains("ProcessVendorScreen"))
            {
                var m = Regex.Match(line, @"ProcessVendorScreen\((\d+), (\w+), (\d+),");
                if (m.Success)
                    AddEvent("VENDOR", $"Shop (NPC {m.Groups[1].Value}, Favor: {m.Groups[2].Value}, Gold: {m.Groups[3].Value})", ts);
            }
            else if (line.Contains("ProcessVendorUpdateAvailableGold"))
            {
                var m = Regex.Match(line, @"ProcessVendorUpdateAvailableGold\((\d+),");
                if (m.Success)
                {
                    _data.LastVendorGold = int.Parse(m.Groups[1].Value);
                    AddEvent("GOLD", $"Vendor gold: {_data.LastVendorGold}", ts);
                }
            }
            else if (line.Contains("ProcessSetAttributes"))
            {
                var m = Regex.Match(line, @"ProcessSetAttributes\(\d+, ""\[([^\]]+)\], \[([^\]]+)\]""");
                if (m.Success)
                {
                    var names = m.Groups[1].Value.Split(", ");
                    var values = m.Groups[2].Value.Split(", ");
                    for (int i = 0; i < Math.Min(names.Length, values.Length); i++)
                        _data.PlayerAttributes[names[i].Trim()] = values[i].Trim();
                    AddEvent("STATS", "Attributes updated", ts);
                }
            }
            else if (line.Contains("ProcessUpdateEffectName"))
            {
                var m = Regex.Match(line, @"ProcessUpdateEffectName\(\d+, \d+, ""([^""]+)""");
                if (m.Success)
                {
                    var effect = m.Groups[1].Value;
                    if (!_data.ActiveEffects.Contains(effect))
                    {
                        _data.ActiveEffects.Add(effect);
                        AddEvent("BUFF", $"Effect: {effect}", ts);
                    }
                }
            }
            else if (line.Contains("Removing effect"))
            {
                var m = Regex.Match(line, @"Removing effect.*?: (.+?)\(");
                if (m.Success)
                {
                    _data.ActiveEffects.Remove(m.Groups[1].Value.Trim());
                    AddEvent("DEBUFF", $"Removed: {m.Groups[1].Value.Trim()}", ts);
                }
            }
            else if (line.Contains("ProcessErrorMessage"))
            {
                var m = Regex.Match(line, @"ProcessErrorMessage\(\w+, ""([^""]+)""");
                if (m.Success) AddEvent("ERROR", m.Groups[1].Value, ts);
            }
            else if (line.Contains("ProcessUpdateDescription") &&
                     (line.Contains("Marigold") || line.Contains("Cabbage") || line.Contains("Flower") ||
                      line.Contains("growing") || line.Contains("Blooming")))
            {
                var m = Regex.Match(line, @"ProcessUpdateDescription\(\d+, ""([^""]+)"", ""([^""]+)"", ""([^""]+)""");
                if (m.Success)
                    AddEvent("GARDEN", $"{m.Groups[1].Value}: {m.Groups[2].Value} [{m.Groups[3].Value}]", ts);
            }
            else if (line.Contains("ProcessBook("))
            {
                var m = Regex.Match(line, @"ProcessBook\(""([^""]+)""");
                if (m.Success) AddEvent("BOOK", m.Groups[1].Value, ts);
            }
        }
    }

    static void AddEvent(string type, string message, string time)
    {
        _data.EventLog.Add(new GameEvent { Type = type, Message = message, Time = time });
        if (_data.EventLog.Count > 100)
            _data.EventLog.RemoveAt(0);
    }

    static async Task RunHttpServer(CancellationToken ct)
    {
        var listener = new HttpListener();
        listener.Prefixes.Add("http://localhost:9876/");
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
                        json = JsonSerializer.Serialize(_data, new JsonSerializerOptions
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
                    buffer = Encoding.UTF8.GetBytes(DASHBOARD_HTML);
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

    static readonly string DASHBOARD_HTML = @"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""UTF-8"">
<title>Project Gorgon - Live Dashboard</title>
<style>
  * { margin: 0; padding: 0; box-sizing: border-box; }
  body { background: #0a0e17; color: #e0e6f0; font-family: 'Segoe UI', system-ui, sans-serif; padding: 12px; }
  h1 { color: #7eb8ff; font-size: 22px; margin-bottom: 8px; display: flex; align-items: center; gap: 10px; }
  h1 .dot { width: 10px; height: 10px; border-radius: 50%; background: #4ade80; animation: pulse 1.5s infinite; }
  @keyframes pulse { 0%,100% { opacity: 1; } 50% { opacity: 0.3; } }
  .grid { display: grid; grid-template-columns: 1fr 1fr 1fr; gap: 10px; margin-top: 10px; }
  .card { background: #141b2d; border: 1px solid #1e2a42; border-radius: 8px; padding: 12px; }
  .card h2 { color: #60a5fa; font-size: 13px; text-transform: uppercase; letter-spacing: 1px; margin-bottom: 8px; border-bottom: 1px solid #1e2a42; padding-bottom: 4px; }
  .stat { display: flex; justify-content: space-between; padding: 3px 0; font-size: 13px; }
  .stat .label { color: #8899aa; }
  .stat .value { color: #e0e6f0; font-weight: 600; font-variant-numeric: tabular-nums; }
  .stat .value.hot { color: #f87171; }
  .stat .value.warm { color: #fbbf24; }
  .stat .value.cool { color: #4ade80; }
  .list { font-size: 12px; max-height: 200px; overflow-y: auto; }
  .list-item { padding: 3px 0; border-bottom: 1px solid #1a2235; display: flex; gap: 6px; align-items: flex-start; }
  .list-item .tag { font-size: 10px; font-weight: 700; padding: 1px 5px; border-radius: 3px; min-width: 54px; text-align: center; flex-shrink: 0; }
  .tag.DEATH { background: #7f1d1d; color: #fca5a5; }
  .tag.ALIVE { background: #14532d; color: #86efac; }
  .tag.KILL { background: #713f12; color: #fde68a; }
  .tag.LOOT { background: #14532d; color: #86efac; }
  .tag.ZONE { background: #1e3a5f; color: #93c5fd; }
  .tag.FAVOR { background: #4c1d95; color: #c4b5fd; }
  .tag.VENDOR { background: #78350f; color: #fed7aa; }
  .tag.GOLD { background: #854d0e; color: #fef08a; }
  .tag.STATS { background: #164e63; color: #a5f3fc; }
  .tag.BUFF { background: #166534; color: #bbf7d0; }
  .tag.DEBUFF { background: #991b1b; color: #fecaca; }
  .tag.ERROR { background: #7f1d1d; color: #fca5a5; }
  .tag.PLAYER { background: #312e81; color: #c7d2fe; }
  .tag.GARDEN { background: #365314; color: #d9f99d; }
  .tag.BOOK { background: #44403c; color: #e7e5e4; }
  .entity { display: inline-block; background: #1e2a42; padding: 2px 8px; border-radius: 4px; margin: 2px; font-size: 12px; }
  .entity.player { border-left: 3px solid #60a5fa; }
  .entity.creature { border-left: 3px solid #f87171; }
  .quest { padding: 3px 0; font-size: 12px; border-bottom: 1px solid #1a2235; }
  .quest::before { content: ''; display: inline-block; width: 6px; height: 6px; background: #fbbf24; border-radius: 50%; margin-right: 6px; }
  .effect { display: inline-block; background: #14532d; color: #86efac; padding: 2px 8px; border-radius: 4px; margin: 2px; font-size: 11px; }
  .effect.debuff { background: #7f1d1d; color: #fca5a5; }
  .skill-row { display: flex; justify-content: space-between; align-items: center; padding: 3px 0; border-bottom: 1px solid #1a2235; font-size: 12px; }
  .skill-name { color: #e0e6f0; min-width: 120px; }
  .skill-level { color: #60a5fa; font-weight: 700; min-width: 40px; text-align: right; }
  .skill-xp { color: #8899aa; font-size: 11px; min-width: 80px; text-align: right; }
  .inv-row { display: flex; justify-content: space-between; align-items: center; padding: 3px 0; border-bottom: 1px solid #1a2235; font-size: 12px; }
  .inv-name { color: #e0e6f0; flex: 1; }
  .inv-qty { color: #fbbf24; font-weight: 600; min-width: 40px; text-align: right; }
  .inv-equipped { color: #4ade80; font-size: 10px; margin-left: 4px; }
  .hp-bar { height: 8px; background: #1e2a42; border-radius: 4px; margin: 2px 0; overflow: hidden; }
  .hp-bar-fill { height: 100%; border-radius: 4px; transition: width 0.3s; }
  .full-width { grid-column: 1 / -1; }
  .two-col { grid-column: span 2; }
  .scroll { max-height: 300px; overflow-y: auto; }
  .scroll::-webkit-scrollbar { width: 4px; }
  .scroll::-webkit-scrollbar-track { background: #0a0e17; }
  .scroll::-webkit-scrollbar-thumb { background: #2d3a52; border-radius: 4px; }
  .time { color: #4b5563; font-size: 11px; min-width: 55px; flex-shrink: 0; }
  .attr-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 2px; }
  .footer { text-align: center; color: #4b5563; font-size: 11px; margin-top: 10px; }
  #chartCanvas { background: #0d1321; border-radius: 4px; width: 100%; }
  .empty { color: #4b5563; font-size: 12px; font-style: italic; }
</style>
</head>
<body>

<h1><span class=""dot""></span> Project Gorgon Live Dashboard <span id=""status"" style=""font-size:12px;color:#6b7280;margin-left:auto""></span></h1>

<div class=""grid"">
  <div class=""card"">
    <h2>Memory</h2>
    <div class=""stat""><span class=""label"">Working Set</span><span class=""value"" id=""memWS"">-</span></div>
    <div class=""stat""><span class=""label"">Private</span><span class=""value"" id=""memPriv"">-</span></div>
    <div class=""stat""><span class=""label"">Peak</span><span class=""value"" id=""memPeak"">-</span></div>
    <div class=""stat""><span class=""label"">Threads</span><span class=""value"" id=""threads"">-</span></div>
    <div class=""stat""><span class=""label"">Handles</span><span class=""value"" id=""handles"">-</span></div>
  </div>

  <div class=""card"">
    <h2>Combat Stats</h2>
    <div id=""combatHpBar"" class=""hp-bar""><div id=""combatHpFill"" class=""hp-bar-fill"" style=""background:#f87171;width:0%""></div></div>
    <div class=""stat""><span class=""label"">Health</span><span class=""value"" id=""combatHp"">-</span></div>
    <div id=""combatPwrBar"" class=""hp-bar""><div id=""combatPwrFill"" class=""hp-bar-fill"" style=""background:#60a5fa;width:0%""></div></div>
    <div class=""stat""><span class=""label"">Power</span><span class=""value"" id=""combatPwr"">-</span></div>
    <div id=""combatArmBar"" class=""hp-bar""><div id=""combatArmFill"" class=""hp-bar-fill"" style=""background:#fbbf24;width:0%""></div></div>
    <div class=""stat""><span class=""label"">Armor</span><span class=""value"" id=""combatArm"">-</span></div>
    <div class=""stat""><span class=""label"">Status</span><span class=""value"" id=""combatDead"">-</span></div>
  </div>

  <div class=""card"">
    <h2>Zone & Environment</h2>
    <div class=""stat""><span class=""label"">Current Zone</span><span class=""value"" id=""zone"">-</span></div>
    <div id=""biomes"" style=""margin-top:6px""></div>
    <h2 style=""margin-top:10px"">Active Effects (Log)</h2>
    <div id=""effects""></div>
  </div>

  <div class=""card full-width"">
    <h2>Memory Over Time</h2>
    <canvas id=""chartCanvas"" height=""120""></canvas>
  </div>

  <div class=""card"">
    <h2>Skills <span id=""skillCount"" style=""color:#8899aa;font-weight:normal""></span></h2>
    <div class=""scroll"" id=""skills"" style=""max-height:280px""></div>
  </div>

  <div class=""card"">
    <h2>Inventory <span id=""invCount"" style=""color:#8899aa;font-weight:normal""></span></h2>
    <div class=""scroll"" id=""inventory"" style=""max-height:280px""></div>
  </div>

  <div class=""card"">
    <h2>Active Effects (Memory)</h2>
    <div class=""scroll"" id=""memEffects"" style=""max-height:280px""></div>
  </div>

  <div class=""card two-col"">
    <h2>Live Event Log</h2>
    <div class=""list scroll"" id=""events"" style=""max-height:350px""></div>
  </div>

  <div class=""card"">
    <h2>Player Attributes (Log)</h2>
    <div class=""attr-grid scroll"" id=""attributes""></div>
  </div>

  <div class=""card"">
    <h2>Nearby Entities</h2>
    <div id=""entities"" style=""max-height:160px;overflow-y:auto""></div>
    <h2 style=""margin-top:10px"">Recent Kills</h2>
    <div id=""kills"" style=""max-height:100px;overflow-y:auto""></div>
  </div>

  <div class=""card"">
    <h2>Active Quests / Favors</h2>
    <div class=""scroll"" id=""quests"" style=""max-height:250px""></div>
  </div>

  <div class=""card"">
    <h2>Garden Status</h2>
    <div class=""scroll"" id=""garden""></div>
  </div>

  <div class=""card"">
    <h2>Players Encountered</h2>
    <div class=""scroll"" id=""players"" style=""max-height:250px""></div>
  </div>
</div>

<div class=""footer"">Refreshes every 2s | Stats polled every 3s | MemoryPoller every 2s | Log tailed every 500ms</div>

<script>
async function refresh() {
  try {
    const resp = await fetch('/api/data');
    const d = await resp.json();

    document.getElementById('status').textContent = 'Last scan: ' + (d.lastMemoryScan || '-') + ' | PID: ' + (d.gamePid || '-');

    var wsClass = d.memoryMB > 5000 ? 'hot' : d.memoryMB > 4000 ? 'warm' : 'cool';
    var el = document.getElementById('memWS');
    el.className = 'value ' + wsClass;
    el.textContent = (d.memoryMB || 0).toLocaleString() + ' MB';
    document.getElementById('memPriv').textContent = (d.privateMemoryMB || 0).toLocaleString() + ' MB';
    document.getElementById('memPeak').textContent = (d.peakMemoryMB || 0).toLocaleString() + ' MB';
    document.getElementById('threads').textContent = d.threadCount || 0;
    document.getElementById('handles').textContent = d.handleCount || 0;

    // Combat stats
    var c = d.combatant;
    if (c) {
      var hpPct = c.maxHealth > 0 ? Math.round(c.health / c.maxHealth * 100) : 0;
      var pwrPct = c.maxPower > 0 ? Math.round(c.power / c.maxPower * 100) : 0;
      var armPct = c.maxArmor > 0 ? Math.round(c.armor / c.maxArmor * 100) : 0;
      document.getElementById('combatHpFill').style.width = hpPct + '%';
      document.getElementById('combatPwrFill').style.width = pwrPct + '%';
      document.getElementById('combatArmFill').style.width = armPct + '%';
      document.getElementById('combatHp').textContent = Math.round(c.health) + ' / ' + Math.round(c.maxHealth);
      document.getElementById('combatPwr').textContent = Math.round(c.power) + ' / ' + Math.round(c.maxPower);
      document.getElementById('combatArm').textContent = Math.round(c.armor) + ' / ' + Math.round(c.maxArmor);
      document.getElementById('combatDead').textContent = c.isDead ? 'DEAD' : 'Alive';
      document.getElementById('combatDead').style.color = c.isDead ? '#f87171' : '#4ade80';
    } else {
      document.getElementById('combatHp').textContent = '-';
      document.getElementById('combatPwr').textContent = '-';
      document.getElementById('combatArm').textContent = '-';
      document.getElementById('combatDead').textContent = 'No data';
    }

    // Skills
    var skills = d.skills || [];
    document.getElementById('skillCount').textContent = '(' + skills.length + ')';
    document.getElementById('skills').innerHTML = skills.length > 0
      ? skills.sort(function(a,b){return b.level - a.level;}).map(function(s) {
          var xpStr = s.tnl > 0 ? Math.round(s.xp) + ' / ' + Math.round(s.tnl) + ' xp' : 'Max';
          var bonus = s.bonus > 0 ? ' <span style=""color:#4ade80"">+' + s.bonus + '</span>' : '';
          return '<div class=""skill-row""><span class=""skill-name"">' + esc(s.name) + bonus + '</span><span class=""skill-level"">' + s.level + '</span><span class=""skill-xp"">' + xpStr + '</span></div>';
        }).join('')
      : '<span class=""empty"">Waiting for MemoryPoller...</span>';

    // Inventory
    var inv = d.inventory || [];
    document.getElementById('invCount').textContent = '(' + inv.length + ')';
    document.getElementById('inventory').innerHTML = inv.length > 0
      ? inv.sort(function(a,b){return a.internalName.localeCompare(b.internalName);}).map(function(i) {
          var eq = i.isEquipped ? '<span class=""inv-equipped"">[E]</span>' : '';
          var qty = i.stackCount > 1 ? ' x' + i.stackCount : '';
          return '<div class=""inv-row""><span class=""inv-name"">' + esc(i.internalName) + eq + '</span><span class=""inv-qty"">' + qty + '</span></div>';
        }).join('')
      : '<span class=""empty"">Waiting for MemoryPoller...</span>';

    // Memory effects
    var mfx = d.memoryEffects || [];
    document.getElementById('memEffects').innerHTML = mfx.length > 0
      ? mfx.map(function(e) {
          var cls = e.isDebuff ? 'effect debuff' : 'effect';
          var timer = e.remainingTime > 0 ? ' ' + e.remainingTime.toFixed(1) + 's' : '';
          var stacks = e.stackCount > 1 ? ' x' + e.stackCount : '';
          return '<span class=""' + cls + '"">' + esc(e.name) + stacks + timer + '</span>';
        }).join('')
      : '<span class=""empty"">None</span>';

    document.getElementById('zone').textContent = d.currentZone || 'Unknown';
    document.getElementById('biomes').innerHTML = (d.currentBiomes || []).map(function(b) {
      return '<span class=""entity"" style=""border-left:3px solid #4ade80"">' + esc(b) + '</span>';
    }).join('');

    document.getElementById('effects').innerHTML = (d.activeEffects || []).map(function(e) {
      return '<span class=""effect"">' + esc(e) + '</span>';
    }).join('') || '<span class=""empty"">None</span>';

    var attrHtml = '';
    var attrs = d.playerAttributes || {};
    for (var k in attrs) {
      var short = k.replace(/_/g, ' ').toLowerCase().replace(/\b\w/g, function(c) { return c.toUpperCase(); });
      var color = k.indexOf('HEALTH') >= 0 ? '#f87171' : k.indexOf('POWER') >= 0 ? '#60a5fa' : k.indexOf('ARMOR') >= 0 ? '#fbbf24' : '#e0e6f0';
      attrHtml += '<div class=""stat""><span class=""label"" style=""font-size:11px"">' + esc(short) + '</span><span class=""value"" style=""color:' + color + ';font-size:12px"">' + esc(attrs[k]) + '</span></div>';
    }
    document.getElementById('attributes').innerHTML = attrHtml || '<span class=""empty"">Waiting for data...</span>';

    var events = (d.eventLog || []).slice().reverse();
    document.getElementById('events').innerHTML = events.map(function(e) {
      return '<div class=""list-item""><span class=""time"">' + esc(e.time) + '</span><span class=""tag ' + e.type + '"">' + e.type + '</span><span>' + esc(e.message) + '</span></div>';
    }).join('');

    document.getElementById('entities').innerHTML = (d.nearbyEntities || []).map(function(e) {
      return '<span class=""entity creature"">' + esc(e) + '</span>';
    }).join('') || '<span class=""empty"">None detected</span>';

    document.getElementById('kills').innerHTML = (d.recentKills || []).slice().reverse().map(function(k) {
      return '<span class=""entity"" style=""border-left:3px solid #f87171"">' + esc(k) + '</span>';
    }).join('');

    document.getElementById('quests').innerHTML = (d.activeQuests || []).map(function(q) {
      return '<div class=""quest"">' + esc(q) + '</div>';
    }).join('') || '<span class=""empty"">None tracked yet</span>';

    document.getElementById('garden').innerHTML = (d.gardenStatus || []).map(function(g) {
      return '<div class=""list-item"" style=""border-left:3px solid #4ade80;padding-left:6px"">' + esc(g) + '</div>';
    }).join('') || '<span class=""empty"">No garden data</span>';

    var playersHtml = '';
    var examined = d.playersExamined || {};
    for (var name in examined) {
      playersHtml += '<div class=""list-item""><span class=""entity player"">' + esc(name) + '</span><span style=""color:#8899aa;font-size:11px"">' + esc(examined[name]) + '</span></div>';
    }
    document.getElementById('players').innerHTML = playersHtml || '<span class=""empty"">None examined</span>';

    drawChart(d.memoryHistory || []);
  } catch(e) {
    document.getElementById('status').textContent = 'Connection lost...';
  }
}

function esc(s) { var d = document.createElement('div'); d.textContent = s; return d.innerHTML; }

function drawChart(history) {
  var canvas = document.getElementById('chartCanvas');
  var ctx = canvas.getContext('2d');
  var dpr = window.devicePixelRatio || 1;
  canvas.width = canvas.offsetWidth * dpr;
  canvas.height = 120 * dpr;
  ctx.scale(dpr, dpr);
  var W = canvas.offsetWidth, H = 120;
  ctx.clearRect(0, 0, W, H);
  if (history.length < 2) return;

  var wsVals = history.map(function(h) { return h.workingSetMB; });
  var privVals = history.map(function(h) { return h.privateMB; });
  var maxVal = Math.max.apply(null, wsVals.concat(privVals)) * 1.05;
  var minVal = Math.min.apply(null, wsVals) * 0.95;
  var range = maxVal - minVal || 1;

  ctx.strokeStyle = '#1a2235'; ctx.lineWidth = 1;
  for (var i = 0; i < 4; i++) {
    var gy = (i / 3) * H;
    ctx.beginPath(); ctx.moveTo(0, gy); ctx.lineTo(W, gy); ctx.stroke();
    ctx.fillStyle = '#4b5563'; ctx.font = '10px monospace';
    ctx.fillText(Math.round(maxVal - (i/3)*range) + ' MB', 4, gy + 12);
  }

  ctx.strokeStyle = '#374151'; ctx.lineWidth = 1.5;
  ctx.beginPath();
  for (var i = 0; i < history.length; i++) {
    var x = (i / (history.length - 1)) * W;
    var y = H - ((history[i].privateMB - minVal) / range) * H;
    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
  }
  ctx.stroke();

  ctx.strokeStyle = '#3b82f6'; ctx.lineWidth = 2;
  ctx.beginPath();
  for (var i = 0; i < history.length; i++) {
    var x = (i / (history.length - 1)) * W;
    var y = H - ((history[i].workingSetMB - minVal) / range) * H;
    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
  }
  ctx.stroke();
  ctx.lineTo(W, H); ctx.lineTo(0, H); ctx.closePath();
  ctx.fillStyle = 'rgba(59,130,246,0.1)'; ctx.fill();

  ctx.fillStyle = '#3b82f6'; ctx.font = 'bold 10px monospace';
  ctx.fillText('WS: ' + wsVals[wsVals.length-1] + ' MB', W - 130, 14);
  ctx.fillStyle = '#6b7280';
  ctx.fillText('Priv: ' + privVals[privVals.length-1] + ' MB', W - 130, 26);
  ctx.fillStyle = '#374151'; ctx.font = '9px monospace';
  ctx.fillText(history[0].time, 4, H - 4);
  ctx.fillText(history[history.length-1].time, W - 52, H - 4);
}

setInterval(refresh, 2000);
refresh();
</script>
</body>
</html>";
}

class DashboardData
{
    public string ProcessName { get; set; } = "";
    public int GamePid { get; set; }
    public double MemoryMB { get; set; }
    public double PrivateMemoryMB { get; set; }
    public double PeakMemoryMB { get; set; }
    public int ThreadCount { get; set; }
    public int HandleCount { get; set; }
    public string LastMemoryScan { get; set; } = "";
    public string CurrentZone { get; set; } = "";
    public List<string> CurrentBiomes { get; set; } = new();
    public List<string> NearbyEntities { get; set; } = new();
    public List<string> RecentKills { get; set; } = new();
    public List<string> ActiveQuests { get; set; } = new();
    public List<string> ActiveEffects { get; set; } = new();
    public List<string> GardenStatus { get; set; } = new();
    public List<string> RecentChat { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public Dictionary<string, string> PlayerAttributes { get; set; } = new();
    public Dictionary<string, string> PlayersExamined { get; set; } = new();
    public int LastVendorGold { get; set; }
    public List<MemorySample> MemoryHistory { get; set; } = new();
    public List<GameEvent> EventLog { get; set; } = new();

    // MemoryPoller data
    public List<MemoryLib.Models.SkillSnapshot>? Skills { get; set; }
    public List<MemoryLib.Models.InventoryItemSnapshot>? Inventory { get; set; }
    public MemoryLib.Models.CombatantSnapshot? Combatant { get; set; }
    public List<MemoryLib.Models.EffectSnapshot>? MemoryEffects { get; set; }
}

class MemorySample
{
    public string Time { get; set; } = "";
    public double WorkingSetMB { get; set; }
    public double PrivateMB { get; set; }
}

class GameEvent
{
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string Time { get; set; } = "";
}

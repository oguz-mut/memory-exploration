using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

class GameDashboard
{
    // Win32 API
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(IntPtr hProc, IntPtr baseAddr, byte[] buffer, IntPtr size, out IntPtr bytesRead);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr VirtualQueryEx(IntPtr hProc, IntPtr addr, out MEMORY_BASIC_INFORMATION64 info, IntPtr length);
    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORY_BASIC_INFORMATION64
    {
        public ulong BaseAddress, AllocationBase;
        public uint AllocationProtect, __alignment1;
        public ulong RegionSize;
        public uint State, Protect, Type, __alignment2;
    }

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

        // Backfill from prev log first (has history), then tail the active log
        // Figure out which log is active (being written to by the game right now)
        bool playerLogActive = File.Exists(_logPath) && new FileInfo(_logPath).Length > 0;
        string backfillPath = prevLogPath; // always backfill from prev if it exists

        if (!playerLogActive)
            _logPath = prevLogPath;

        Console.WriteLine($"Active log: {_logPath}");
        Console.WriteLine($"Backfill from: {backfillPath}");

        // Start background tasks
        var cts = new CancellationTokenSource();
        var memTask = Task.Run(() => MemoryScanLoop(cts.Token));
        var logTask = Task.Run(() => LogTailLoop(cts.Token));
        var httpTask = Task.Run(() => RunHttpServer(cts.Token));

        Console.WriteLine("\nDashboard running at http://localhost:9876");
        Console.WriteLine("Press Ctrl+C to stop.\n");

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

        try { await Task.WhenAll(memTask, logTask, httpTask); }
        catch (OperationCanceledException) { }

        Console.WriteLine("Dashboard stopped.");
    }

    static void MemoryScanLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                ScanMemory();
            }
            catch (Exception ex)
            {
                lock (_lock) { _data.Errors.Add($"Memory scan error: {ex.Message}"); }
            }
            ct.WaitHandle.WaitOne(3000);
        }
    }

    static void ScanMemory()
    {
        Process proc;
        try { proc = Process.GetProcessById(_gamePid); }
        catch { return; }

        var ws = proc.WorkingSet64;
        var priv = proc.PrivateMemorySize64;
        var peak = proc.PeakWorkingSet64;
        var threads = proc.Threads.Count;
        var handles = proc.HandleCount;

        lock (_lock)
        {
            _data.MemoryMB = Math.Round(ws / 1048576.0, 1);
            _data.PrivateMemoryMB = Math.Round(priv / 1048576.0, 1);
            _data.PeakMemoryMB = Math.Round(peak / 1048576.0, 1);
            _data.ThreadCount = threads;
            _data.HandleCount = handles;
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

        // Quick string scan for live game state
        IntPtr h = OpenProcess(0x0410, false, _gamePid);
        if (h == IntPtr.Zero) return;

        var info = new MEMORY_BASIC_INFORMATION64();
        int infoSize = Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64));
        ulong addr = 0;
        int scanned = 0;
        long totalRead = 0;
        long maxRead = 256L * 1024 * 1024;

        var nameplates = new List<string>();
        var gardenItems = new List<string>();
        var chatMessages = new List<string>();
        var zones = new HashSet<string>();

        while (totalRead < maxRead)
        {
            IntPtr ret = VirtualQueryEx(h, (IntPtr)addr, out info, (IntPtr)infoSize);
            if (ret == IntPtr.Zero) break;

            bool isCommitted = info.State == 0x1000;
            bool isPrivate = info.Type == 0x20000;
            bool isReadable = (info.Protect & 0x66) != 0;

            if (isCommitted && isPrivate && isReadable && info.RegionSize > 4096)
            {
                int chunkSize = (int)Math.Min((long)info.RegionSize, 4L * 1024 * 1024);
                byte[] buf = new byte[chunkSize];
                IntPtr bytesRead;

                if (ReadProcessMemory(h, (IntPtr)info.BaseAddress, buf, (IntPtr)chunkSize, out bytesRead))
                {
                    int read = (int)bytesRead;
                    totalRead += read;

                    var strings = ExtractUtf16(buf, read, 10);
                    foreach (var s in strings)
                    {
                        if (s.StartsWith("Nameplate: ") && s.Length < 80)
                            nameplates.Add(s.Substring(11));
                        else if (s.Contains("BIOME_") && s.Length < 80)
                        {
                            var m = Regex.Match(s, @"BIOME_\w+");
                            if (m.Success) zones.Add(m.Value);
                        }
                        else if ((s.Contains("Marigold") || s.Contains("Cabbage") || s.Contains("Blooming") ||
                                  s.Contains("Growing") || s.Contains("Thirsty") || s.Contains("Hungry") || s.Contains("Ripe")) &&
                                 s.Length > 10 && s.Length < 120 && !s.Contains("Unity") && !s.Contains("Assets/"))
                            gardenItems.Add(s);
                    }

                    var asciiStrings = ExtractAscii(buf, read, 15);
                    foreach (var s in asciiStrings)
                    {
                        if (s.StartsWith("Nameplate: ") && s.Length < 80)
                            nameplates.Add(s.Substring(11));
                        else if (s.Contains("BIOME_") && s.Length < 80)
                        {
                            var m = Regex.Match(s, @"BIOME_\w+");
                            if (m.Success) zones.Add(m.Value);
                        }
                        else if (s.StartsWith("<color=") && s.Contains("Global") && s.Length < 300)
                            chatMessages.Add(s);
                    }
                    scanned++;
                }
            }

            ulong next = info.BaseAddress + info.RegionSize;
            if (next <= addr) break;
            addr = next;
        }

        CloseHandle(h);

        lock (_lock)
        {
            _data.ScannedMB = Math.Round(totalRead / 1048576.0, 1);
            _data.RegionsScanned = scanned;

            if (nameplates.Count > 0)
                _data.NearbyEntities = nameplates.Distinct().OrderBy(x => x).ToList();
            if (zones.Count > 0)
                _data.CurrentBiomes = zones.OrderBy(x => x).ToList();
            if (gardenItems.Count > 0)
                _data.GardenStatus = gardenItems.Distinct().Take(20).ToList();
            if (chatMessages.Count > 0)
            {
                foreach (var msg in chatMessages.Distinct().Take(5))
                {
                    if (!_data.RecentChat.Contains(msg))
                    {
                        _data.RecentChat.Add(msg);
                        if (_data.RecentChat.Count > 30) _data.RecentChat.RemoveAt(0);
                    }
                }
            }
        }
    }

    static void LogTailLoop(CancellationToken ct)
    {
        long lastPos = 0;

        // Backfill: process recent log history to populate dashboard on startup
        // First backfill from prev log (last 1MB), then from active log
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
                long backfillStart = Math.Max(0, fi.Length - 1024 * 1024); // last 1MB
                using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek(backfillStart, SeekOrigin.Begin);
                using var reader = new StreamReader(fs);
                if (backfillStart > 0) reader.ReadLine(); // skip partial line
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

    static List<string> ExtractAscii(byte[] buf, int length, int minLen)
    {
        var results = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < length; i++)
        {
            byte b = buf[i];
            if (b >= 32 && b < 127) sb.Append((char)b);
            else { if (sb.Length >= minLen) results.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= minLen) results.Add(sb.ToString());
        return results;
    }

    static List<string> ExtractUtf16(byte[] buf, int length, int minLen)
    {
        var results = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < length - 1; i += 2)
        {
            char c = (char)(buf[i] | (buf[i + 1] << 8));
            if (c >= 32 && c < 127) sb.Append(c);
            else { if (sb.Length >= minLen) results.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length >= minLen) results.Add(sb.ToString());
        return results;
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
    <div class=""stat""><span class=""label"">Scanned</span><span class=""value"" id=""scanned"">-</span></div>
  </div>

  <div class=""card"">
    <h2>Zone & Environment</h2>
    <div class=""stat""><span class=""label"">Current Zone</span><span class=""value"" id=""zone"">-</span></div>
    <div id=""biomes"" style=""margin-top:6px""></div>
    <h2 style=""margin-top:10px"">Active Effects</h2>
    <div id=""effects""></div>
  </div>

  <div class=""card"">
    <h2>Player Attributes</h2>
    <div class=""attr-grid scroll"" id=""attributes""></div>
  </div>

  <div class=""card full-width"">
    <h2>Memory Over Time</h2>
    <canvas id=""chartCanvas"" height=""120""></canvas>
  </div>

  <div class=""card two-col"">
    <h2>Live Event Log</h2>
    <div class=""list scroll"" id=""events"" style=""max-height:350px""></div>
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

<div class=""footer"">Refreshes every 2s | Memory scanned every 3s | Log tailed every 500ms</div>

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
    document.getElementById('scanned').textContent = (d.scannedMB || 0) + ' MB / ' + (d.regionsScanned || 0) + ' rgns';

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
    public double ScannedMB { get; set; }
    public int RegionsScanned { get; set; }
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

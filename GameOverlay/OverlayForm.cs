using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace GameOverlay;

public class OverlayForm : Form
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOOLWINDOW = 0x80;
    const int WS_EX_TOPMOST = 0x8;

    private System.Windows.Forms.Timer _updateTimer = new();
    private System.Windows.Forms.Timer _repaintTimer = new();
    private string _logPath = "";
    private string _prevLogPath = "";
    private long _lastLogPos = 0;
    private bool _locked = false;
    private bool _dragging = false;
    private Point _dragStart;

    // === TRACKING DATA ===
    // XP tracking
    private Dictionary<string, SkillTrack> _skills = new();
    private DateTime _sessionStart = DateTime.Now;

    // Combat
    private bool _inCombat = false;
    private int _killCount = 0;
    private int _deathCount = 0;
    private List<string> _recentAbilitiesUsedOnYou = new();
    private Dictionary<string, int> _damageDealt = new(); // creature -> total dmg
    private string _lastKill = "";
    private DateTime _lastKillTime = DateTime.MinValue;
    private string _lastDeath = "";
    private DateTime _lastDeathTime = DateTime.MinValue;

    // Loot
    private List<LootEntry> _recentLoot = new();
    private int _totalItemsLooted = 0;
    private int _goldFromVendor = 0;

    // Stat changes
    private List<StatChange> _statChanges = new();

    // Effects
    private List<ActiveEffect> _effects = new();

    private int _hudWidth = 300;
    private int _hudPadding = 8;

    public OverlayForm()
    {
        this.Text = "PG HUD";
        this.FormBorderStyle = FormBorderStyle.None;
        this.StartPosition = FormStartPosition.Manual;
        this.Location = new Point(20, 150);
        this.Size = new Size(_hudWidth + _hudPadding * 2, 800);
        this.TopMost = true;
        this.ShowInTaskbar = true;
        this.BackColor = Color.Magenta;
        this.TransparencyKey = Color.Magenta;
        this.Opacity = 0.92;
        this.DoubleBuffered = true;
        this.SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "Low",
            "Elder Game", "Project Gorgon");
        _logPath = Path.Combine(logDir, "Player.log");
        _prevLogPath = Path.Combine(logDir, "Player-prev.log");

        if (!File.Exists(_logPath) || new FileInfo(_logPath).Length == 0)
            _logPath = _prevLogPath;

        // Backfill from prev log
        BackfillLog(_prevLogPath);
        if (_logPath != _prevLogPath) BackfillLog(_logPath);
        if (File.Exists(_logPath))
            _lastLogPos = new FileInfo(_logPath).Length;

        _updateTimer.Interval = 300;
        _updateTimer.Tick += (s, e) => TailLog();
        _updateTimer.Start();

        _repaintTimer.Interval = 150;
        _repaintTimer.Tick += (s, e) => this.Invalidate();
        _repaintTimer.Start();

        var ctx = new ContextMenuStrip();
        ctx.Items.Add("Toggle Lock (click-through)", null, (s, e) => ToggleLock());
        ctx.Items.Add(new ToolStripSeparator());
        ctx.Items.Add("Exit", null, (s, e) => this.Close());
        this.ContextMenuStrip = ctx;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_TOPMOST; return cp; }
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); }

    void ToggleLock()
    {
        if (_locked)
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, (ex & ~WS_EX_TRANSPARENT) | WS_EX_TOOLWINDOW);
            _locked = false; this.ShowInTaskbar = true;
        }
        else
        {
            int ex = GetWindowLong(Handle, GWL_EXSTYLE);
            SetWindowLong(Handle, GWL_EXSTYLE, ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
            _locked = true; this.ShowInTaskbar = false;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (!_locked && e.Button == MouseButtons.Left) { _dragging = true; _dragStart = e.Location; }
        base.OnMouseDown(e);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) this.Location = new Point(Location.X + e.X - _dragStart.X, Location.Y + e.Y - _dragStart.Y);
        base.OnMouseMove(e);
    }
    protected override void OnMouseUp(MouseEventArgs e) { _dragging = false; base.OnMouseUp(e); }

    void BackfillLog(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            var fi = new FileInfo(path);
            long start = Math.Max(0, fi.Length - 2 * 1024 * 1024); // last 2MB
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            if (start > 0) reader.ReadLine();
            string? line;
            while ((line = reader.ReadLine()) != null) ProcessLine(line);
        }
        catch { }
    }

    void TailLog()
    {
        try
        {
            if (!File.Exists(_logPath)) return;
            var fi = new FileInfo(_logPath);
            if (fi.Length <= _lastLogPos) { if (fi.Length < _lastLogPos) _lastLogPos = 0; return; }
            using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_lastLogPos, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null) ProcessLine(line);
            _lastLogPos = fi.Length;
        }
        catch { }
    }

    void ProcessLine(string line)
    {
        // === XP GAINS ===
        // ProcessUpdateSkill({type=Cartography,raw=20,bonus=0,xp=1637,tnl=2250,max=115}, True, 9, 0, 0)
        var skillMatch = Regex.Match(line, @"ProcessUpdateSkill\(\{type=(\w+),raw=(\d+),bonus=(\d+),xp=(\d+),tnl=(\d+),max=(\d+)\}, \w+, (\d+),");
        if (skillMatch.Success)
        {
            string name = skillMatch.Groups[1].Value;
            int level = int.Parse(skillMatch.Groups[2].Value);
            int bonus = int.Parse(skillMatch.Groups[3].Value);
            int xp = int.Parse(skillMatch.Groups[4].Value);
            int tnl = int.Parse(skillMatch.Groups[5].Value);
            int xpGained = int.Parse(skillMatch.Groups[7].Value);

            if (!_skills.ContainsKey(name))
                _skills[name] = new SkillTrack { Name = name, FirstSeen = DateTime.Now };

            var sk = _skills[name];
            sk.Level = level;
            sk.BonusLevels = bonus;
            sk.CurrentXP = xp;
            sk.XPToNextLevel = tnl;
            sk.TotalXPGained += xpGained;
            sk.LastGain = xpGained;
            sk.LastGainTime = DateTime.Now;
            sk.GainCount++;
            return;
        }

        // === COMBAT ===
        if (line.Contains("ProcessCombatModeStatus"))
        {
            _inCombat = line.Contains("InCombat") || line.Contains("WaitingForCombat");
            return;
        }

        // Skip entity combat logs - these fire for ALL entities in area, not just player
        if (line.Contains("OnAttackHitMe")) return;

        // Damage from corpse analysis: Borbo: 51 health dmg
        if (line.Contains("Detailed Analysis") && line.Contains("Borbo:"))
        {
            var dmgMatch = Regex.Match(line, @"Borbo: (\d+) (\w+) dmg");
            if (dmgMatch.Success)
            {
                int dmg = int.Parse(dmgMatch.Groups[1].Value);
                string type = dmgMatch.Groups[2].Value;
                _damageDealt[type] = _damageDealt.GetValueOrDefault(type, 0) + dmg;
            }
        }

        // Kill tracking from corpse search
        if (line.Contains("Search Corpse of"))
        {
            var m = Regex.Match(line, @"Search Corpse of (.+?)""");
            if (m.Success && m.Groups[1].Value != _lastKill)
            {
                _lastKill = m.Groups[1].Value;
                _lastKillTime = DateTime.Now;
                _killCount++;
            }
        }

        // Deaths
        if (line.Contains("causeOfDeath="))
        {
            var m = Regex.Match(line, @"causeOfDeath=([^,]+)");
            if (m.Success) { _lastDeath = m.Groups[1].Value; _lastDeathTime = DateTime.Now; _deathCount++; }
            return;
        }

        // === LOOT ===
        // ProcessAddItem(BattleHardenedSword(48314030), -1, False)
        var itemMatch = Regex.Match(line, @"ProcessAddItem\((\w+)\((\d+)\),");
        if (itemMatch.Success)
        {
            string itemName = FormatItemName(itemMatch.Groups[1].Value);
            _recentLoot.Add(new LootEntry { Name = itemName, Time = DateTime.Now });
            if (_recentLoot.Count > 15) _recentLoot.RemoveAt(0);
            _totalItemsLooted++;
            return;
        }

        // Vendor sell: ProcessVendorAddItem(100, RingOfStaff2(48038880), False)
        var vendorMatch = Regex.Match(line, @"ProcessVendorAddItem\((\d+), (\w+)\(");
        if (vendorMatch.Success)
        {
            int price = int.Parse(vendorMatch.Groups[1].Value);
            string item = FormatItemName(vendorMatch.Groups[2].Value);
            _goldFromVendor += price;
            _recentLoot.Add(new LootEntry { Name = $"SOLD {item} ({price}g)", Time = DateTime.Now, IsSold = true });
            if (_recentLoot.Count > 15) _recentLoot.RemoveAt(0);
            return;
        }

        // === STAT CHANGES ===
        if (line.Contains("ProcessSetAttributes"))
        {
            var m = Regex.Match(line, @"ProcessSetAttributes\(\d+, ""\[([^\]]+)\], \[([^\]]+)\]""");
            if (m.Success)
            {
                var names = m.Groups[1].Value.Split(", ");
                var values = m.Groups[2].Value.Split(", ");
                for (int i = 0; i < Math.Min(names.Length, values.Length); i++)
                {
                    var key = names[i].Trim();
                    if (double.TryParse(values[i].Trim(), out double val))
                    {
                        var existing = _statChanges.LastOrDefault(s => s.Key == key);
                        if (existing != null && Math.Abs(val - existing.NewValue) > 0.01)
                        {
                            _statChanges.Add(new StatChange { Key = key, OldValue = existing.NewValue, NewValue = val, Time = DateTime.Now });
                        }
                        else if (existing == null)
                        {
                            _statChanges.Add(new StatChange { Key = key, OldValue = val, NewValue = val, Time = DateTime.Now });
                        }
                    }
                }
                if (_statChanges.Count > 50) _statChanges.RemoveRange(0, _statChanges.Count - 50);
            }
            return;
        }

        // === EFFECTS ===
        if (line.Contains("ProcessUpdateEffectName"))
        {
            var m = Regex.Match(line, @"ProcessUpdateEffectName\(\d+, (\d+), ""([^""]+)""");
            if (m.Success)
            {
                string id = m.Groups[1].Value, name = m.Groups[2].Value;
                var ex = _effects.FirstOrDefault(e => e.Id == id);
                if (ex != null) ex.Name = name; else _effects.Add(new ActiveEffect { Id = id, Name = name, StartTime = DateTime.Now });
            }
            return;
        }
        if (line.Contains("Removing effect"))
        {
            var m = Regex.Match(line, @"IID (\d+)");
            if (m.Success) _effects.RemoveAll(e => e.Id == m.Groups[1].Value);
        }
    }

    string FormatItemName(string raw)
    {
        // "BattleHardenedSword" -> "Battle Hardened Sword"
        return Regex.Replace(raw, @"(\d+)$", "").Aggregate("", (s, c) =>
            s.Length > 0 && char.IsUpper(c) && !char.IsUpper(s[^1]) ? s + " " + c : s + c).Trim();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Magenta);

        int x = _hudPadding, y = _hudPadding, w = _hudWidth;
        var bg = Color.FromArgb(235, 8, 12, 20);
        var border = Color.FromArgb(200, 25, 35, 55);

        using var bgBrush = new SolidBrush(bg);
        using var borderPen = new Pen(border);
        using var sepPen = new Pen(Color.FromArgb(100, 30, 42, 66));

        // We'll calculate total height first, then draw bg
        int startY = y;

        // Fonts
        using var headerFont = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        using var smallFont = new Font("Segoe UI", 7.5f, FontStyle.Regular);
        using var tinyFont = new Font("Segoe UI", 7f, FontStyle.Regular);

        // Measure content height
        int contentHeight = MeasureContent(g, w, headerFont, bodyFont, smallFont, tinyFont);
        var bgRect = new Rectangle(0, 0, w + _hudPadding * 2, contentHeight + _hudPadding * 2);
        g.FillRoundedRectangle(bgBrush, bgRect, 6);
        g.DrawRoundedRectangle(borderPen, bgRect, 6);

        // Unlock indicator
        if (!_locked)
        {
            using var ub = new SolidBrush(Color.FromArgb(200, 251, 191, 36));
            g.DrawString("Drag to move | Right-click: menu", tinyFont, ub, x, y);
            y += 12;
        }

        // ========== XP TRACKER ==========
        var activeSkills = _skills.Values
            .Where(s => s.TotalXPGained > 0)
            .OrderByDescending(s => s.LastGainTime)
            .Take(8).ToList();

        if (activeSkills.Count > 0)
        {
            DrawHeader(g, "XP TRACKER", Color.FromArgb(196, 181, 253), headerFont, x, ref y, w, sepPen);

            double sessionMins = (DateTime.Now - _sessionStart).TotalMinutes;

            foreach (var sk in activeSkills)
            {
                float pct = sk.XPToNextLevel > 0 ? (float)sk.CurrentXP / sk.XPToNextLevel : 0;
                float xpPerHour = sessionMins > 0.5 ? (float)(sk.TotalXPGained / sessionMins * 60) : 0;
                var age = (DateTime.Now - sk.LastGainTime).TotalSeconds;
                var nameColor = age < 5 ? Color.White : Color.FromArgb(180, 180, 195);

                // Skill name + level
                using var nb = new SolidBrush(nameColor);
                string label = $"{sk.Name} Lv{sk.Level}{(sk.BonusLevels > 0 ? $"+{sk.BonusLevels}" : "")}";
                g.DrawString(label, bodyFont, nb, x, y);

                // XP/hr on right
                using var rateBrush = new SolidBrush(Color.FromArgb(160, 196, 181, 253));
                string rateStr = xpPerHour > 0 ? $"{xpPerHour:F0}/hr" : "";
                var rateSize = g.MeasureString(rateStr, smallFont);
                g.DrawString(rateStr, smallFont, rateBrush, x + w - rateSize.Width, y + 1);
                y += 15;

                // XP bar
                DrawProgressBar(g, x, y, w, 6, pct,
                    Color.FromArgb(100, 139, 92, 246), Color.FromArgb(200, 139, 92, 246),
                    $"{sk.CurrentXP}/{sk.XPToNextLevel}", tinyFont);

                // Recent gain flash
                if (age < 4)
                {
                    float fade = Math.Max(0, 1f - (float)age / 4f);
                    using var gainBrush = new SolidBrush(Color.FromArgb((int)(fade * 255), 74, 222, 128));
                    g.DrawString($"+{sk.LastGain} XP", smallFont, gainBrush, x + w - 60, y - 1);
                }
                y += 10;
            }
            y += 4;
        }

        // ========== COMBAT ==========
        DrawHeader(g, _inCombat ? "COMBAT (IN FIGHT)" : "COMBAT",
            _inCombat ? Color.FromArgb(248, 113, 113) : Color.FromArgb(251, 146, 60),
            headerFont, x, ref y, w, sepPen);

        using var dimBrush = new SolidBrush(Color.FromArgb(140, 150, 160));
        string combatLine = $"Kills: {_killCount}  Deaths: {_deathCount}  K/D: {(_deathCount > 0 ? ((float)_killCount / _deathCount).ToString("F1") : "-")}";
        g.DrawString(combatLine, bodyFont, dimBrush, x, y);
        y += 16;

        // Last kill
        if (!string.IsNullOrEmpty(_lastKill))
        {
            var killAge = (DateTime.Now - _lastKillTime).TotalSeconds;
            var killAlpha = killAge < 10 ? 255 : Math.Max(100, 255 - (int)((killAge - 10) * 5));
            using var kb = new SolidBrush(Color.FromArgb(killAlpha, 253, 224, 71));
            g.DrawString($"Last kill: {_lastKill}", smallFont, kb, x, y);
            y += 13;
        }

        // Death flash
        if ((DateTime.Now - _lastDeathTime).TotalSeconds < 20)
        {
            float fade = Math.Max(0, 1f - (float)(DateTime.Now - _lastDeathTime).TotalSeconds / 20f);
            using var deathBg = new SolidBrush(Color.FromArgb((int)(fade * 150), 127, 29, 29));
            using var deathTxt = new SolidBrush(Color.FromArgb((int)(fade * 255), 252, 165, 165));
            g.FillRoundedRectangle(deathBg, new Rectangle(x, y, w, 16), 3);
            g.DrawString($"DIED: {_lastDeath}", smallFont, deathTxt, x + 4, y + 1);
            y += 20;
        }

        // (removed incoming abilities - those logs fire for all entities, not just player)

        // Damage dealt summary
        if (_damageDealt.Count > 0)
        {
            int totalDmg = _damageDealt.Values.Sum();
            using var dmgBrush = new SolidBrush(Color.FromArgb(180, 251, 146, 60));
            g.DrawString($"Total damage dealt: {totalDmg} ({string.Join(", ", _damageDealt.Select(kv => $"{kv.Value} {kv.Key}"))})", smallFont, dmgBrush, x, y);
            y += 13;
        }
        y += 4;

        // ========== LOOT ==========
        if (_recentLoot.Count > 0)
        {
            string lootHeader = $"LOOT ({_totalItemsLooted} items{(_goldFromVendor > 0 ? $", {_goldFromVendor}g sold" : "")})";
            DrawHeader(g, lootHeader, Color.FromArgb(134, 239, 172), headerFont, x, ref y, w, sepPen);

            var recentItems = _recentLoot.AsEnumerable().Reverse().Take(8);
            foreach (var item in recentItems)
            {
                var age = (DateTime.Now - item.Time).TotalSeconds;
                int a = age < 5 ? 255 : Math.Max(120, 255 - (int)((age - 5) * 3));
                var c = item.IsSold
                    ? Color.FromArgb(a, 253, 224, 71)
                    : Color.FromArgb(a, 187, 247, 208);
                using var ib = new SolidBrush(c);
                g.DrawString(item.Name, smallFont, ib, x + 4, y);
                y += 12;
            }
            y += 4;
        }

        // ========== EFFECTS ==========
        if (_effects.Count > 0)
        {
            DrawHeader(g, "ACTIVE EFFECTS", Color.FromArgb(134, 239, 172), headerFont, x, ref y, w, sepPen);

            foreach (var eff in _effects.ToList())
            {
                var elapsed = DateTime.Now - eff.StartTime;
                string timeStr = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m{elapsed.Seconds:D2}s" : $"{elapsed.Seconds}s";

                using var pillBg = new SolidBrush(Color.FromArgb(160, 15, 60, 30));
                g.FillRoundedRectangle(pillBg, new Rectangle(x, y, w, 16), 3);

                using var eb = new SolidBrush(Color.FromArgb(220, 187, 247, 208));
                using var tb = new SolidBrush(Color.FromArgb(150, 134, 239, 172));
                g.DrawString(eff.Name, smallFont, eb, x + 4, y + 1);
                var ts = g.MeasureString(timeStr, tinyFont);
                g.DrawString(timeStr, tinyFont, tb, x + w - ts.Width - 4, y + 2);
                y += 19;
            }
            y += 4;
        }

        // ========== STAT CHANGES (recent, non-redundant) ==========
        var recentChanges = _statChanges
            .Where(s => Math.Abs(s.NewValue - s.OldValue) > 0.01 && (DateTime.Now - s.Time).TotalSeconds < 120)
            .OrderByDescending(s => s.Time)
            .Take(6).ToList();

        if (recentChanges.Count > 0)
        {
            DrawHeader(g, "RECENT STAT CHANGES", Color.FromArgb(165, 243, 252), headerFont, x, ref y, w, sepPen);

            foreach (var sc in recentChanges)
            {
                double delta = sc.NewValue - sc.OldValue;
                float age = (float)(DateTime.Now - sc.Time).TotalSeconds;
                int a = Math.Max(100, 255 - (int)(age * 2));
                var c = delta > 0 ? Color.FromArgb(a, 74, 222, 128) : Color.FromArgb(a, 248, 113, 113);
                string shortName = FormatStatName(sc.Key);
                string deltaStr = delta > 0 ? $"+{delta:F0}" : $"{delta:F0}";
                using var cb = new SolidBrush(c);
                using var nb2 = new SolidBrush(Color.FromArgb(a, 160, 170, 180));
                g.DrawString(shortName, smallFont, nb2, x, y);
                var ds = g.MeasureString($"{sc.NewValue:F0} ({deltaStr})", smallFont);
                g.DrawString($"{sc.NewValue:F0} ({deltaStr})", smallFont, cb, x + w - ds.Width, y);
                y += 13;
            }
        }

        y += _hudPadding;
        if (this.Height != y) this.Height = Math.Max(60, y);
    }

    int MeasureContent(Graphics g, int w, Font h, Font b, Font s, Font t)
    {
        // Rough estimate for auto-sizing
        int y = _hudPadding;
        if (!_locked) y += 12;
        var activeSkills = _skills.Values.Where(sk => sk.TotalXPGained > 0).Take(8).Count();
        if (activeSkills > 0) y += 20 + activeSkills * 25 + 4;
        y += 20 + 16 + 13; // combat header + stats + last kill
        if ((DateTime.Now - _lastDeathTime).TotalSeconds < 20) y += 20;
        if (_damageDealt.Count > 0) y += 13;
        y += 4;
        if (_recentLoot.Count > 0) y += 20 + Math.Min(_recentLoot.Count, 8) * 12 + 4;
        if (_effects.Count > 0) y += 20 + _effects.Count * 19 + 4;
        var changes = _statChanges.Count(s => Math.Abs(s.NewValue - s.OldValue) > 0.01 && (DateTime.Now - s.Time).TotalSeconds < 120);
        if (changes > 0) y += 20 + Math.Min(changes, 6) * 13;
        return y + _hudPadding;
    }

    void DrawHeader(Graphics g, string text, Color color, Font font, int x, ref int y, int w, Pen sepPen)
    {
        g.DrawLine(sepPen, x, y, x + w, y);
        y += 4;
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, x, y);
        y += 16;
    }

    void DrawProgressBar(Graphics g, int x, int y, int w, int h, float pct, Color bgCol, Color fgCol, string label, Font font)
    {
        using var bgB = new SolidBrush(Color.FromArgb(80, bgCol.R, bgCol.G, bgCol.B));
        using var fgB = new SolidBrush(fgCol);
        g.FillRoundedRectangle(bgB, new Rectangle(x, y, w, h), 2);
        int barW = Math.Max(0, (int)(w * Math.Min(1, pct)));
        if (barW > 0) g.FillRoundedRectangle(fgB, new Rectangle(x, y, barW, h), 2);

        using var lb = new SolidBrush(Color.FromArgb(180, 200, 210, 220));
        var ls = g.MeasureString(label, font);
        g.DrawString(label, font, lb, x + (w - ls.Width) / 2, y + (h - ls.Height) / 2);
    }

    string FormatStatName(string raw)
    {
        return raw.Replace("_", " ").ToLower()
            .Replace("noncombat ", "").Replace("combat ", "C:")
            .Replace("regen ", "Rgn ").Replace("delta", "")
            .Replace("  ", " ").Trim();
        // Capitalize first letters
    }
}

class SkillTrack
{
    public string Name { get; set; } = "";
    public int Level { get; set; }
    public int BonusLevels { get; set; }
    public int CurrentXP { get; set; }
    public int XPToNextLevel { get; set; }
    public int TotalXPGained { get; set; }
    public int LastGain { get; set; }
    public DateTime LastGainTime { get; set; }
    public DateTime FirstSeen { get; set; }
    public int GainCount { get; set; }
}

class LootEntry
{
    public string Name { get; set; } = "";
    public DateTime Time { get; set; }
    public bool IsSold { get; set; }
}

class StatChange
{
    public string Key { get; set; } = "";
    public double OldValue { get; set; }
    public double NewValue { get; set; }
    public DateTime Time { get; set; }
}

class ActiveEffect
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime StartTime { get; set; }
}

static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle rect, int r)
    { using var p = RoundedPath(rect, r); g.FillPath(brush, p); }
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle rect, int r)
    { using var p = RoundedPath(rect, r); g.DrawPath(pen, p); }
    static GraphicsPath RoundedPath(Rectangle rect, int r)
    {
        var p = new GraphicsPath();
        int d = r * 2;
        p.AddArc(rect.X, rect.Y, d, d, 180, 90);
        p.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        p.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        p.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

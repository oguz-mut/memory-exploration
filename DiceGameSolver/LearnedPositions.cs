using System.Text.Json;
using DiceGameSolver.Models;

namespace DiceGameSolver;

public sealed class LearnedPositions
{
    private readonly Dictionary<(string layoutKey, int responseCode), List<System.Drawing.Point>> _map = new();
    private readonly object _sync = new();

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectGorgonTools",
        "dicegame_learned.json");

    // ── Write ────────────────────────────────────────────────────────────────

    /// <summary>Append an observed point; keeps the last 8 observations per key.</summary>
    public void Record(string layoutKey, int responseCode, System.Drawing.Point p)
    {
        lock (_sync)
        {
            var key = (layoutKey, responseCode);
            if (!_map.TryGetValue(key, out var list))
            {
                list = [];
                _map[key] = list;
            }
            list.Add(p);
            while (list.Count > 8) list.RemoveAt(0);
        }
    }

    // ── Read ─────────────────────────────────────────────────────────────────

    /// <summary>Returns the median position (robust to outliers), or null if no data.</summary>
    public System.Drawing.Point? Lookup(string layoutKey, int responseCode)
    {
        lock (_sync)
        {
            if (!_map.TryGetValue((layoutKey, responseCode), out var list) || list.Count == 0)
                return null;
            var xs = list.Select(p => p.X).OrderBy(v => v).ToArray();
            var ys = list.Select(p => p.Y).OrderBy(v => v).ToArray();
            int mx = xs[xs.Length / 2];
            int my = ys[ys.Length / 2];
            return new System.Drawing.Point(mx, my);
        }
    }

    /// <summary>
    /// Infer this code's position from a learned SIBLING in the same layout.
    /// Returns null if no sibling data or ambiguous.
    ///
    /// Uses the order in <paramref name="codeOrder"/> (AvailableResponseCodes) as the vertical
    /// stacking order — index 0 is the top button. Assumes ~<paramref name="rowHeight"/>px
    /// row spacing.
    /// </summary>
    public System.Drawing.Point? LookupBySibling(string layoutKey, int responseCode, int[] codeOrder, int rowHeight = 40)
    {
        int targetIdx = Array.IndexOf(codeOrder, responseCode);
        if (targetIdx < 0) return null;

        // Find the CLOSEST sibling with learned data.
        lock (_sync)
        {
            System.Drawing.Point? best = null;
            int bestDistance = int.MaxValue;
            int bestSiblingIdx = -1;

            for (int i = 0; i < codeOrder.Length; i++)
            {
                if (i == targetIdx) continue;
                if (!_map.TryGetValue((layoutKey, codeOrder[i]), out var list) || list.Count == 0) continue;
                int dist = Math.Abs(i - targetIdx);
                if (dist < bestDistance)
                {
                    bestDistance = dist;
                    bestSiblingIdx = i;
                    var xs = list.Select(p => p.X).OrderBy(v => v).ToArray();
                    var ys = list.Select(p => p.Y).OrderBy(v => v).ToArray();
                    best = new System.Drawing.Point(xs[xs.Length / 2], ys[ys.Length / 2]);
                }
            }

            if (best is null) return null;

            int rowOffset = (targetIdx - bestSiblingIdx) * rowHeight;
            return new System.Drawing.Point(best.Value.X, best.Value.Y + rowOffset);
        }
    }

    /// <summary>Returns count of observations per (layoutKey, responseCode) pair.</summary>
    public IReadOnlyDictionary<(string, int), int> CoverageCounts()
    {
        lock (_sync)
        {
            return _map.ToDictionary(
                kvp => (kvp.Key.layoutKey, kvp.Key.responseCode),
                kvp => kvp.Value.Count);
        }
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    /// <summary>Atomic write: temp file → rename, so partial writes never corrupt data.</summary>
    public void Save()
    {
        List<LearnedEntry> entries;
        lock (_sync)
        {
            entries = _map.Select(kvp => new LearnedEntry
            {
                LayoutKey    = kvp.Key.layoutKey,
                ResponseCode = kvp.Key.responseCode,
                Points       = kvp.Value.Select(p => new PointDto { X = p.X, Y = p.Y }).ToList(),
            }).ToList();
        }

        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Load from disk; returns empty store on missing file or schema drift.</summary>
    public static LearnedPositions Load()
    {
        var result = new LearnedPositions();
        var path = FilePath;
        if (!File.Exists(path)) return result;
        try
        {
            var entries = JsonSerializer.Deserialize<List<LearnedEntry>>(File.ReadAllText(path));
            if (entries is null) return result;
            lock (result._sync)
            {
                foreach (var e in entries)
                    result._map[(e.LayoutKey, e.ResponseCode)] =
                        e.Points.Select(p => new System.Drawing.Point(p.X, p.Y)).ToList();
            }
        }
        catch { /* schema drift: return empty */ }
        return result;
    }

    // ── JSON DTOs ─────────────────────────────────────────────────────────────
    private sealed class LearnedEntry
    {
        public string LayoutKey    { get; set; } = "";
        public int ResponseCode    { get; set; }
        public List<PointDto> Points { get; set; } = [];
    }

    private sealed class PointDto { public int X { get; set; } public int Y { get; set; } }
}

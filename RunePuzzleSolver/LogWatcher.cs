using System.Text.RegularExpressions;

namespace RunePuzzleSolver;

public class LogWatcher
{
    public bool LastSolveDetected { get; private set; }
    public List<string> RecentItems { get; } = [];

    static readonly string LogPath = Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "..", "LocalLow", "Elder Game", "Project Gorgon", "Player.log"));

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(LogPath))
                {
                    try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
                    continue;
                }

                using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                fs.Seek(0, SeekOrigin.End);
                using var sr = new StreamReader(fs);

                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        string? line;
                        while ((line = sr.ReadLine()) != null)
                        {
                            if (Regex.IsMatch(line, @"ProcessAddItem\(.+\)"))
                            {
                                lock (RecentItems)
                                {
                                    RecentItems.Add(line);
                                    if (RecentItems.Count > 20) RecentItems.RemoveAt(0);
                                }
                                LastSolveDetected = true;
                                Console.WriteLine($"[log] item: {line}");
                            }
                            if (line.Contains("ProcessShowRunePuzzleScreen"))
                                Console.WriteLine("[log] puzzle screen detected");
                        }
                        await Task.Delay(500, ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { Console.WriteLine($"[log] read error: {ex.Message}"); break; }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Console.WriteLine($"[log] {ex.Message}");
                try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}

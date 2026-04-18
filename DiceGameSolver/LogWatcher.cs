using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DiceGameSolver;

public class LogWatcher
{
    public Channel<string> RawLineChannel { get; } = Channel.CreateUnbounded<string>();
    public string LogPath { get; set; }

    public LogWatcher()
    {
        LogPath = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "..", "LocalLow", "Elder Game", "Project Gorgon", "Player.log"));
    }

    public virtual async Task RunAsync(CancellationToken ct)
    {
        Console.WriteLine($"[log] tailing {LogPath}");

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
                            if (line.Contains("ProcessTalkScreen(-346,") && line.Contains("\"Dice Game\""))
                            {
                                Console.WriteLine($"[log] dice-game line (len={line.Length})");
                                await RawLineChannel.Writer.WriteAsync(line, ct);
                            }
                        }
                        await Task.Delay(500, ct);
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[log] {ex.Message}");
                        break;
                    }
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

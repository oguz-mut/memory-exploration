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

    /// <summary>
    /// Scan the tail of the log for the most recent ProcessTalkScreen(-346, "Dice Game", ...)
    /// line and emit it on the channel. This lets the pipeline resume from whatever screen
    /// the game is currently showing when the solver is restarted.
    ///
    /// Skips emission if there's an EndInteraction(-346) after the last TalkScreen — meaning
    /// the dialog was closed.
    /// </summary>
    async Task ReplayMostRecentDiceStateAsync(FileStream fs, CancellationToken ct)
    {
        try
        {
            const int windowBytes = 512 * 1024; // 512 KB is plenty to contain the last dice session
            long fileLen = fs.Length;
            long start = Math.Max(0, fileLen - windowBytes);
            fs.Seek(start, SeekOrigin.Begin);
            using var tailReader = new StreamReader(fs, leaveOpen: true);
            var tail = await tailReader.ReadToEndAsync(ct);

            string? lastDiceLine = null;
            bool dialogClosedAfter = false;
            foreach (var raw in tail.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Contains("ProcessTalkScreen(-346,") && line.Contains("\"Dice Game\""))
                {
                    lastDiceLine = line;
                    dialogClosedAfter = false;
                }
                else if (line.Contains("ProcessEndInteraction(-346") || line.Contains("ProcessEndInteraction(entityId=-346"))
                {
                    dialogClosedAfter = true;
                }
            }

            if (lastDiceLine is not null && !dialogClosedAfter)
            {
                Console.WriteLine($"[log] resuming from prior dice-game state (len={lastDiceLine.Length})");
                await RawLineChannel.Writer.WriteAsync(lastDiceLine, ct);
            }
            else if (lastDiceLine is not null && dialogClosedAfter)
            {
                Console.WriteLine("[log] most recent dice dialog was closed — waiting for new interaction");
            }
            else
            {
                Console.WriteLine("[log] no prior dice-game state found in tail window");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Console.WriteLine($"[log] resume-scan failed: {ex.Message}");
        }
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

                // On startup, scan the tail of the log for the most recent dice-game state
                // so we can resume from any screen the game is currently showing.
                await ReplayMostRecentDiceStateAsync(fs, ct);

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

using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DiceGameSolver;

// Worker B (sprint/dice-parser) will replace this stub.
// Contract: tails Player.log, emits raw ProcessTalkScreen(-346, "Dice Game", ...) log lines on RawLineChannel.
public class LogWatcher
{
    public Channel<string> RawLineChannel { get; } = Channel.CreateUnbounded<string>();
    public string LogPath { get; set; } = string.Empty;

    public virtual Task RunAsync(CancellationToken ct) => Task.CompletedTask;
}

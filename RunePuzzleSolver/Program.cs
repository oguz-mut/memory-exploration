using RunePuzzleSolver;
using RunePuzzleSolver.Models;

var reader     = new PuzzleStateReader();
var solver     = new MastermindSolver();
var clicker    = new ClickExecutor();
var logWatcher = new LogWatcher();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var readerTask  = Task.Run(() => reader.RunAsync(cts.Token));
var solverTask  = Task.Run(() => solver.RunAsync(reader.StateChannel, cts.Token));
var clickerTask = Task.Run(() => clicker.RunAsync(solver.ActionChannel, reader, cts.Token));
var logTask     = Task.Run(() => logWatcher.RunAsync(cts.Token));

Console.WriteLine("[main] RunePuzzleSolver started at http://localhost:9882/");

var listener = new System.Net.HttpListener();
listener.Prefixes.Add("http://localhost:9882/");
listener.Start();
cts.Token.Register(() => listener.Stop());

var httpTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        try
        {
            var ctx   = await listener.GetContextAsync();
            var html  = BuildDashboard(reader.LastState, solver, clicker, logWatcher);
            var bytes = System.Text.Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType     = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes, cts.Token);
            ctx.Response.Close();
        }
        catch (Exception ex) when (!cts.Token.IsCancellationRequested)
        {
            Console.WriteLine($"[main] HTTP error: {ex.Message}");
        }
    }
});

await Task.WhenAny(readerTask, solverTask, clickerTask, logTask, httpTask);
listener.Stop();

static string BuildDashboard(PuzzleState? state, MastermindSolver solver, ClickExecutor clicker, LogWatcher log)
{
    bool active = state?.IsActive == true;
    string dot  = active
        ? "<span style='color:#00ff88'>&#9679;</span>"
        : "<span style='color:#555'>&#9679;</span>";

    var sb = new System.Text.StringBuilder();
    sb.Append("""
        <!DOCTYPE html>
        <html>
        <head>
          <meta charset="utf-8">
          <meta http-equiv="refresh" content="1">
          <title>Rune Puzzle Solver</title>
          <style>
            body  { background:#1a1a2e; color:#e0e0e0; font-family:monospace; padding:20px; margin:0; }
            h2    { color:#a0cfff; margin-bottom:12px; }
            .sec  { margin:14px 0; }
            .lbl  { color:#888; font-size:.85em; }
            table { border-collapse:collapse; margin:6px 0; }
            th,td { border:1px solid #444; padding:4px 12px; text-align:center; }
            th    { background:#2a2a4e; }
            ul    { margin:4px 0; padding-left:18px; }
            li    { margin:2px 0; }
          </style>
        </head>
        <body>
        """);

    sb.Append($"<h2>Rune Puzzle Solver &nbsp;{dot}</h2>");

    // Puzzle status
    sb.Append("<div class='sec'>");
    if (active && state != null)
    {
        int remaining = state.NumGuessesAllowed - state.History.Count;
        sb.Append($"<div>Code length: <b>{state.CodeLength}</b> &nbsp;&nbsp; Guesses remaining: <b>{remaining}</b></div>");
    }
    else
    {
        sb.Append("<div class='lbl'>No active puzzle</div>");
    }
    sb.Append("</div>");

    // Solver + clicker
    sb.Append("<div class='sec'>");
    sb.Append($"<div><span class='lbl'>Solver:</span> {System.Net.WebUtility.HtmlEncode(solver.SolverStatus)}" +
              $" &nbsp; Candidates: <b>{solver.CandidateCount}</b></div>");
    sb.Append($"<div><span class='lbl'>Clicker:</span> {System.Net.WebUtility.HtmlEncode(clicker.ExecutorStatus)}</div>");
    sb.Append("</div>");

    // Guess history
    if (state?.History is { Count: > 0 } history)
    {
        sb.Append("<div class='sec'><b>Guess history</b>");
        sb.Append("<table><tr><th>Guess</th><th>Right Pos</th><th>Wrong Pos</th></tr>");
        foreach (var (guess, wrongPos, rightPos) in history)
        {
            sb.Append($"<tr><td>{System.Net.WebUtility.HtmlEncode(guess)}</td>" +
                      $"<td>{rightPos}</td><td>{wrongPos}</td></tr>");
        }
        sb.Append("</table></div>");
    }

    // Recent log items
    List<string> snapshot;
    lock (log.RecentItems) { snapshot = log.RecentItems.TakeLast(10).ToList(); }
    if (snapshot.Count > 0)
    {
        sb.Append("<div class='sec'><b>Recent items</b><ul>");
        foreach (var item in snapshot)
            sb.Append($"<li>{System.Net.WebUtility.HtmlEncode(item)}</li>");
        sb.Append("</ul></div>");
    }

    sb.Append("</body></html>");
    return sb.ToString();
}

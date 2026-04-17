using RunePuzzleSolver;

var reader = new PuzzleStateReader();
var solver = new MastermindSolver();
var clicker = new ClickExecutor();
var logWatcher = new LogWatcher();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var readerTask = Task.Run(() => reader.RunAsync(cts.Token));
var solverTask = Task.Run(() => solver.RunAsync(reader.StateChannel, cts.Token));
var clickerTask = Task.Run(() => clicker.RunAsync(solver.ActionChannel, reader, cts.Token));
var logTask = Task.Run(() => logWatcher.RunAsync(cts.Token));

Console.WriteLine("[main] RunePuzzleSolver stub started");
await Task.WhenAll(readerTask, solverTask, clickerTask, logTask);

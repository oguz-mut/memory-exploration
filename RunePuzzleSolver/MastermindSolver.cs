using System.Threading.Channels;
using RunePuzzleSolver.Models;

namespace RunePuzzleSolver;

public class MastermindSolver
{
    public Channel<GuessAction> ActionChannel { get; } = Channel.CreateBounded<GuessAction>(
        new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.DropOldest });

    public int CandidateCount { get; private set; }
    public string SolverStatus { get; private set; } = "idle";

    private static readonly string[] Symbols = ["7", "B", "C", "F", "K", "M", "P", "Q", "S", "T", "W", "X"];

    public async Task RunAsync(Channel<PuzzleState> input, CancellationToken ct)
    {
        int? pinnedCodeLength = null;
        List<int[]> candidates = [];
        List<(int[] guess, int wrongPos, int rightPos)> localHistory = [];
        bool guessPending = false;  // true after we submit until history count grows

        await foreach (PuzzleState state in input.Reader.ReadAllAsync(ct))
        {
            if (!state.IsActive)
            {
                SolverStatus = "idle — puzzle not active";
                pinnedCodeLength = null;
                candidates = [];
                localHistory = [];
                guessPending = false;
                continue;
            }

            // Pin codeLength on first active state. Trust the reader — it validates
            // inputDisplayButtons.Count == codeLength in the scan, so a stale pooled
            // object can't slip through.
            if (pinnedCodeLength == null)
            {
                pinnedCodeLength = state.CodeLength;
                candidates = GenerateAllCodes(12, state.CodeLength);
                localHistory = [];
                guessPending = false;
                CandidateCount = candidates.Count;
                SolverStatus = $"ready — {candidates.Count} candidates for length {state.CodeLength}";
                Console.WriteLine($"[solver] New puzzle length={state.CodeLength}, {candidates.Count} candidates");
            }
            else if (state.CodeLength != pinnedCodeLength)
            {
                Console.WriteLine($"[solver] WARNING: state.CodeLength={state.CodeLength} differs from pinned {pinnedCodeLength} — keeping pinned value");
            }

            int len = pinnedCodeLength.Value;

            // Prune from state history (ground truth). This also handles resuming
            // a mid-game puzzle: pre-existing entries get adopted and pruned normally.
            if (state.History.Count > localHistory.Count)
            {
                foreach (var entry in state.History.Skip(localHistory.Count))
                {
                    if (entry.Guess.Length != len)
                    {
                        Console.WriteLine($"[solver] WARNING: history entry '{entry.Guess}' length {entry.Guess.Length} != {len}, skipping");
                        continue;
                    }
                    var guessArr = entry.Guess.Select(c => Array.IndexOf(Symbols, c.ToString())).ToArray();
                    if (guessArr.Any(x => x < 0))
                    {
                        Console.WriteLine($"[solver] WARNING: history entry '{entry.Guess}' has unknown symbol, skipping");
                        continue;
                    }
                    localHistory.Add((guessArr, entry.WrongPos, entry.RightPos));
                    candidates = candidates.Where(c => Score(guessArr, c) == (entry.WrongPos, entry.RightPos)).ToList();
                }
                CandidateCount = candidates.Count;
                SolverStatus = $"pruned to {candidates.Count} candidates";
                Console.WriteLine($"[solver] {SolverStatus}");
                guessPending = false;  // history grew — our previous submission (if any) landed
            }

            // Check if solved
            if (localHistory.Count > 0 && localHistory[^1].rightPos == len)
            {
                SolverStatus = $"SOLVED in {localHistory.Count} guesses!";
                Console.WriteLine($"[solver] {SolverStatus}");
                continue;
            }

            // Send a guess when nothing is currently pending. This gate prevents
            // spamming duplicates while still allowing mid-puzzle resume: if we
            // attach to a puzzle that already has 3 guesses, the pruning loop
            // above consumes them, guessPending stays false, and we send #4.
            if (!guessPending && candidates.Count > 0)
            {
                var next = ComputeNextGuess(candidates, len);
                Console.WriteLine($"[solver] candidates={CandidateCount} next=[{string.Join(",", next)}]");
                await ActionChannel.Writer.WriteAsync(new GuessAction(next), ct);
                guessPending = true;
            }
        }
    }

    private static (int wrongPos, int rightPos) Score(int[] guess, int[] secret)
    {
        int rightPos = 0;
        int[] guessCounts = new int[12];
        int[] secretCounts = new int[12];

        for (int i = 0; i < guess.Length; i++)
        {
            if (guess[i] == secret[i])
            {
                rightPos++;
            }
            else
            {
                guessCounts[guess[i]]++;
                secretCounts[secret[i]]++;
            }
        }

        int wrongPos = 0;
        for (int s = 0; s < 12; s++)
            wrongPos += Math.Min(guessCounts[s], secretCounts[s]);

        return (wrongPos, rightPos);
    }

    private static List<int[]> GenerateAllCodes(int alphabetSize, int length)
    {
        int total = 1;
        for (int i = 0; i < length; i++) total *= alphabetSize;

        var codes = new List<int[]>(total);
        int[] current = new int[length];

        for (int i = 0; i < total; i++)
        {
            codes.Add((int[])current.Clone());

            for (int j = length - 1; j >= 0; j--)
            {
                current[j]++;
                if (current[j] < alphabetSize)
                    break;
                current[j] = 0;
            }
        }

        return codes;
    }

    private int[] ComputeNextGuess(List<int[]> candidates, int codeLength)
    {
        if (candidates.Count == 1)
            return candidates[0];

        if (candidates.Count <= 3000)
        {
            Console.WriteLine($"[solver] Strategy A (Knuth minimax), candidates={candidates.Count}");
            return KnuthMinimax(candidates, codeLength);
        }
        else
        {
            Console.WriteLine($"[solver] Strategy B (entropy heuristic), candidates={candidates.Count}");
            return EntropyHeuristic(candidates, codeLength);
        }
    }

    private static int[] KnuthMinimax(List<int[]> candidates, int codeLength)
    {
        var allCodes = GenerateAllCodes(12, codeLength);

        var candidateKeys = new HashSet<long>(candidates.Count);
        foreach (var c in candidates)
            candidateKeys.Add(EncodeCode(c));

        int bestWorstCase = int.MaxValue;
        int[] bestGuess = candidates[0];
        bool bestIsCandidate = true;

        foreach (var guess in allCodes)
        {
            var partitionSizes = new Dictionary<(int, int), int>();
            foreach (var candidate in candidates)
            {
                var fb = Score(guess, candidate);
                partitionSizes.TryGetValue(fb, out int count);
                partitionSizes[fb] = count + 1;
            }

            int worstCase = 0;
            foreach (var size in partitionSizes.Values)
                if (size > worstCase) worstCase = size;

            bool isCandidate = candidateKeys.Contains(EncodeCode(guess));

            if (worstCase < bestWorstCase || (worstCase == bestWorstCase && isCandidate && !bestIsCandidate))
            {
                bestWorstCase = worstCase;
                bestGuess = guess;
                bestIsCandidate = isCandidate;
            }
        }

        Console.WriteLine($"[solver] Minimax: worst-case={bestWorstCase}, isCandidate={bestIsCandidate}");
        return bestGuess;
    }

    private static int[] EntropyHeuristic(List<int[]> candidates, int codeLength)
    {
        int totalCodes = 1;
        for (int i = 0; i < codeLength; i++) totalCodes *= 12;

        // First guess opener when no history has pruned candidates yet
        if (candidates.Count == totalCodes)
        {
            var opener = GetOpener(codeLength);
            Console.WriteLine($"[solver] Entropy: using opener [{string.Join(",", opener)}]");
            return opener;
        }

        // Sample up to 300 guesses: all candidates if <= 300, else random sample from full space
        List<int[]> sampledGuesses;
        if (candidates.Count <= 300)
        {
            sampledGuesses = candidates;
        }
        else
        {
            var rng = new Random();
            sampledGuesses = new List<int[]>(300);
            for (int i = 0; i < 300; i++)
            {
                var code = new int[codeLength];
                for (int j = 0; j < codeLength; j++)
                    code[j] = rng.Next(12);
                sampledGuesses.Add(code);
            }
        }

        double bestEntropy = double.NegativeInfinity;
        int[] bestGuess = candidates[0];

        foreach (var guess in sampledGuesses)
        {
            var partitionCounts = new Dictionary<(int, int), int>();
            foreach (var candidate in candidates)
            {
                var fb = Score(guess, candidate);
                partitionCounts.TryGetValue(fb, out int count);
                partitionCounts[fb] = count + 1;
            }

            double entropy = 0;
            double total = candidates.Count;
            foreach (var count in partitionCounts.Values)
            {
                double p = count / total;
                entropy -= p * Math.Log2(p);
            }

            if (entropy > bestEntropy)
            {
                bestEntropy = entropy;
                bestGuess = guess;
            }
        }

        Console.WriteLine($"[solver] Entropy: best entropy={bestEntropy:F3}");
        return bestGuess;
    }

    private static int[] GetOpener(int codeLength)
    {
        // Pattern: 0,0,1,1,2,2,... (pairs of each symbol)
        var opener = new int[codeLength];
        for (int i = 0; i < codeLength; i++)
            opener[i] = i / 2;
        return opener;
    }

    private static long EncodeCode(int[] code)
    {
        long result = 0;
        foreach (int sym in code)
            result = result * 12 + sym;
        return result;
    }
}

namespace RunePuzzleSolver.Models;

public record PuzzleState(
    bool IsActive,
    int CodeLength,
    int NumGuessesAllowed,
    List<(string Guess, int WrongPos, int RightPos)> History,
    int GuessRowCount);

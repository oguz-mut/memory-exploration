using System.Text.Json;

namespace RunePuzzleSolver.Models;

public class CalibrationData
{
    public (int X, int Y)[] RunePositions { get; set; } = new (int X, int Y)[12];
    public (int X, int Y) SubmitPosition { get; set; }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProjectGorgonTools",
        "rune_puzzle_calibration.json");

    public static CalibrationData Load()
    {
        if (File.Exists(FilePath))
        {
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<CalibrationData>(json) ?? new CalibrationData();
        }
        return new CalibrationData();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(FilePath, json);
    }
}

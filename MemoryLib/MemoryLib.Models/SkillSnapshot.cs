namespace MemoryLib.Models;

public class SkillSnapshot
{
    public string Name { get; init; } = "";
    public ulong ObjectAddress { get; init; }
    public int Level { get; init; }
    public int RawLevel { get; init; }
    public int Bonus { get; init; }
    public float Xp { get; init; }
    public float Tnl { get; init; }
    public int Max { get; init; }
    public int ParagonLevel { get; init; }
}

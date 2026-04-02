namespace MemoryLib.Models;

public class QuestSnapshot
{
    public ulong ObjectAddress { get; init; }
    public int QuestId { get; init; }
    public string InternalName { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string DisplayedLocation { get; init; } = "";
    public bool IsReadyForTurnIn { get; init; }
    public bool IsTracked { get; init; }
    public bool IsWorkOrder { get; init; }
    public List<ObjectiveSnapshot> Objectives { get; init; } = new();
}

public class ObjectiveSnapshot
{
    public string Type { get; init; } = "";
    public string Description { get; init; } = "";
    public int TargetCount { get; init; }
    public int CurrentState { get; init; }     // from QuestState.States[i]
    public bool IsComplete { get; init; }
}

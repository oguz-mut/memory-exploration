namespace GorgonAddons.Core;

using MemoryLib;
using MemoryLib.Models;

public sealed class GameContext : IDisposable
{
    private readonly MemoryPoller _poller;
    private PlayerState _player = new();
    private List<SkillSnapshot> _skills = [];
    private List<InventoryItemSnapshot> _inventory = [];
    private List<EffectSnapshot> _effects = [];

    public PlayerState Player => _player;
    public IReadOnlyList<SkillSnapshot> Skills => _skills;
    public IReadOnlyList<InventoryItemSnapshot> Inventory => _inventory;
    public IReadOnlyList<EffectSnapshot> Effects => _effects;

    public event Action<SkillSnapshot, SkillSnapshot>? OnSkillLevelUp;
    public event Action<InventoryItemSnapshot>? OnItemAdded;
    public event Action<InventoryItemSnapshot>? OnItemRemoved;
    public event Action<EffectSnapshot>? OnEffectAdded;
    public event Action<EffectSnapshot>? OnEffectRemoved;
    public event Action<bool>? OnDeathChanged;
    public event Action<CombatantSnapshot>? OnCombatantChanged;

    public GameContext(ProcessMemory memory, MemoryRegionScanner scanner)
    {
        _poller = new MemoryPoller(memory, scanner);

        _poller.OnSkillLevelUp     += (prev, curr) => OnSkillLevelUp?.Invoke(prev, curr);
        _poller.OnSkillsChanged    += skills => _skills = skills;
        _poller.OnItemAdded        += item => OnItemAdded?.Invoke(item);
        _poller.OnItemRemoved      += item => OnItemRemoved?.Invoke(item);
        _poller.OnInventoryChanged += items => _inventory = items;
        _poller.OnEffectAdded      += e => OnEffectAdded?.Invoke(e);
        _poller.OnEffectRemoved    += e => OnEffectRemoved?.Invoke(e);
        _poller.OnEffectsChanged   += effects => _effects = effects;
        _poller.OnDeathChanged     += dead => OnDeathChanged?.Invoke(dead);
        _poller.OnCombatantChanged += combatant =>
        {
            _player = PlayerState.FromCombatant(combatant);
            OnCombatantChanged?.Invoke(combatant);
        };
    }

    public bool Initialize() => _poller.AutoDiscoverAll();

    public void StartPolling(int intervalMs = 500)
    {
        _poller.PollIntervalMs = intervalMs;
        _poller.Start();
    }

    public void StopPolling() => _poller.Stop();

    public void RefreshNow()
    {
        var snapshot = _poller.PollNow();
        if (snapshot.Combatant != null) _player    = PlayerState.FromCombatant(snapshot.Combatant);
        if (snapshot.Skills   != null) _skills     = snapshot.Skills;
        if (snapshot.Items    != null) _inventory   = snapshot.Items;
        if (snapshot.Effects  != null) _effects     = snapshot.Effects;
    }

    public void PressKey(string keyName)           => InputSender.PressKey(keyName);
    public Task SendCommand(string command)        => InputSender.SendCommand(command);
    public Task Click(int x, int y)               => InputSender.Click(x, y);

    public bool HasEffect(string name) =>
        _effects.Any(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public SkillSnapshot? GetSkill(string name) =>
        _skills.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public void Dispose() => _poller.Dispose();
}

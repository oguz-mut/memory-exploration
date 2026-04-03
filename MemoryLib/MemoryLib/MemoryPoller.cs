namespace MemoryLib;

using MemoryLib.Models;
using MemoryLib.Readers;

public sealed class MemoryPoller : IDisposable
{
    private readonly ProcessMemory _memory;
    private readonly MemoryRegionScanner _scanner;
    private readonly SkillReader _skillReader;
    private readonly InventoryReader _inventoryReader;
    private readonly CombatantReader _combatantReader;
    private readonly EffectReader _effectReader;

    private List<SkillSnapshot>? _prevSkills;
    private List<InventoryItemSnapshot>? _prevItems;
    private CombatantSnapshot? _prevCombatant;
    private List<EffectSnapshot>? _prevEffects;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollTask;

    public int PollIntervalMs { get; set; } = 2000;
    public bool IsRunning { get; private set; }
    public bool IsDiscovered { get; private set; }

    public event Action<List<SkillSnapshot>>? OnSkillsChanged;
    public event Action<SkillSnapshot, SkillSnapshot>? OnSkillLevelUp;
    public event Action<List<InventoryItemSnapshot>>? OnInventoryChanged;
    public event Action<InventoryItemSnapshot>? OnItemAdded;
    public event Action<InventoryItemSnapshot>? OnItemRemoved;
    public event Action<CombatantSnapshot>? OnCombatantChanged;
    public event Action<bool>? OnDeathChanged;
    public event Action<List<EffectSnapshot>>? OnEffectsChanged;
    public event Action<EffectSnapshot>? OnEffectAdded;
    public event Action<EffectSnapshot>? OnEffectRemoved;
    public event Action<Exception>? OnError;

    public MemoryPoller(ProcessMemory memory, MemoryRegionScanner scanner)
    {
        _memory = memory;
        _scanner = scanner;
        _skillReader = new SkillReader(memory, scanner);
        _inventoryReader = new InventoryReader(memory, scanner);
        _combatantReader = new CombatantReader(memory, scanner);
        _effectReader = new EffectReader(memory, scanner);
    }

    public bool AutoDiscoverAll()
    {
        bool anySuccess = false;

        TryLoadItemData();

        try
        {
            bool ok = _skillReader.AutoDiscover();
            Console.WriteLine(ok
                ? "[MemoryPoller] SkillReader: discovered"
                : "[MemoryPoller] SkillReader: not found");
            anySuccess |= ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemoryPoller] SkillReader AutoDiscover failed: {ex.Message}");
        }

        try
        {
            bool ok = _inventoryReader.AutoDiscover();
            Console.WriteLine(ok
                ? "[MemoryPoller] InventoryReader: discovered"
                : "[MemoryPoller] InventoryReader: not found");
            anySuccess |= ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemoryPoller] InventoryReader AutoDiscover failed: {ex.Message}");
        }

        try
        {
            bool ok = _combatantReader.AutoDiscover();
            Console.WriteLine(ok
                ? "[MemoryPoller] CombatantReader: discovered"
                : "[MemoryPoller] CombatantReader: not found");
            anySuccess |= ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemoryPoller] CombatantReader AutoDiscover failed: {ex.Message}");
        }

        try
        {
            bool ok = _effectReader.AutoDiscover();
            Console.WriteLine(ok
                ? "[MemoryPoller] EffectReader: discovered"
                : "[MemoryPoller] EffectReader: not found");
            anySuccess |= ok;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MemoryPoller] EffectReader AutoDiscover failed: {ex.Message}");
        }

        IsDiscovered = anySuccess;
        return anySuccess;
    }

    private void TryLoadItemData()
    {
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "items.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "items.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "items.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "items.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "items.json"),
            Path.Combine(Environment.CurrentDirectory, "items.json"),
        };

        foreach (string path in candidates)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (File.Exists(full))
                {
                    _inventoryReader.LoadItemData(full);
                    return;
                }
            }
            catch { }
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        if (!IsDiscovered) AutoDiscoverAll();

        _cancellationTokenSource = new CancellationTokenSource();
        _pollTask = Task.Run(PollLoop);
        IsRunning = true;
    }

    public void Stop()
    {
        _cancellationTokenSource?.Cancel();
        try { _pollTask?.Wait(5000); } catch { }
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        _cancellationTokenSource?.Dispose();
    }

    private async Task PollLoop()
    {
        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                PollOnce();
            }
            catch (Exception ex)
            {
                OnError?.Invoke(ex);
            }

            try
            {
                await Task.Delay(PollIntervalMs, _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void PollOnce()
    {
        // Skills
        try
        {
            var skills = _skillReader.ReadAllSkills();
            if (skills != null)
            {
                if (_prevSkills != null)
                    DetectSkillChanges(skills);
                _prevSkills = skills;
            }
        }
        catch { }

        // Inventory
        try
        {
            var items = _inventoryReader.ReadAllItems();
            if (items != null)
            {
                if (_prevItems != null)
                    DetectInventoryChanges(items);
                _prevItems = items;
            }
        }
        catch { }

        // Combatant
        CombatantSnapshot? combatant = null;
        try
        {
            combatant = _combatantReader.ReadLocalCombatant();
            if (combatant != null)
            {
                if (_prevCombatant != null)
                    DetectCombatantChanges(combatant);
                _prevCombatant = combatant;
            }
        }
        catch { }

        // Effects — pass freshly-read combatant address so EffectReader follows re-located combatant
        try
        {
            var effects = _effectReader.ReadEffects(combatant?.ObjectAddress);
            if (effects != null)
            {
                if (_prevEffects != null)
                    DetectEffectChanges(effects);
                _prevEffects = effects;
            }
        }
        catch { }
    }

    private void DetectSkillChanges(List<SkillSnapshot> current)
    {
        var prevMap = _prevSkills!.ToDictionary(s => s.Name);
        bool anyChanged = false;

        foreach (var skill in current)
        {
            if (prevMap.TryGetValue(skill.Name, out var prev))
            {
                if (skill.Level > prev.Level)
                {
                    OnSkillLevelUp?.Invoke(prev, skill);
                    anyChanged = true;
                }
                else if (skill.Level != prev.Level || skill.Xp != prev.Xp)
                {
                    anyChanged = true;
                }
            }
            else
            {
                anyChanged = true;
            }
        }

        if (!anyChanged && current.Count != _prevSkills!.Count)
            anyChanged = true;

        if (anyChanged)
            OnSkillsChanged?.Invoke(current);
    }

    private void DetectInventoryChanges(List<InventoryItemSnapshot> current)
    {
        var prevAddrs = new HashSet<ulong>(_prevItems!.Select(i => i.ObjectAddress));
        var currAddrs = new HashSet<ulong>(current.Select(i => i.ObjectAddress));
        bool anyChanged = false;

        foreach (var item in current)
        {
            if (!prevAddrs.Contains(item.ObjectAddress))
            {
                OnItemAdded?.Invoke(item);
                anyChanged = true;
            }
        }

        foreach (var item in _prevItems!)
        {
            if (!currAddrs.Contains(item.ObjectAddress))
            {
                OnItemRemoved?.Invoke(item);
                anyChanged = true;
            }
        }

        if (anyChanged)
            OnInventoryChanged?.Invoke(current);
    }

    private void DetectCombatantChanges(CombatantSnapshot current)
    {
        bool attrChanged = current.Attributes.Count != _prevCombatant!.Attributes.Count;

        if (!attrChanged)
        {
            foreach (var kvp in current.Attributes)
            {
                if (!_prevCombatant.Attributes.TryGetValue(kvp.Key, out double prevVal) || prevVal != kvp.Value)
                {
                    attrChanged = true;
                    break;
                }
            }
        }

        if (attrChanged)
            OnCombatantChanged?.Invoke(current);

        if (current.IsDead != _prevCombatant.IsDead)
            OnDeathChanged?.Invoke(current.IsDead);
    }

    private void DetectEffectChanges(List<EffectSnapshot> current)
    {
        var prevIids = new HashSet<int>(_prevEffects!.Select(e => e.EffectIID));
        var currIids = new HashSet<int>(current.Select(e => e.EffectIID));
        bool anyChanged = false;

        foreach (var effect in current)
        {
            if (!prevIids.Contains(effect.EffectIID))
            {
                OnEffectAdded?.Invoke(effect);
                anyChanged = true;
            }
        }

        foreach (var effect in _prevEffects!)
        {
            if (!currIids.Contains(effect.EffectIID))
            {
                OnEffectRemoved?.Invoke(effect);
                anyChanged = true;
            }
        }

        if (anyChanged)
            OnEffectsChanged?.Invoke(current);
    }

    public GameStateSnapshot PollNow()
    {
        PollOnce();
        return new GameStateSnapshot
        {
            Skills    = _prevSkills,
            Items     = _prevItems,
            Combatant = _prevCombatant,
            Effects   = _prevEffects,
            Timestamp = DateTime.UtcNow,
        };
    }

    public sealed class GameStateSnapshot
    {
        public List<SkillSnapshot>? Skills { get; init; }
        public List<InventoryItemSnapshot>? Items { get; init; }
        public CombatantSnapshot? Combatant { get; init; }
        public List<EffectSnapshot>? Effects { get; init; }
        public DateTime Timestamp { get; init; }
    }
}

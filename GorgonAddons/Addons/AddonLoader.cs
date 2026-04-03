namespace GorgonAddons.Addons;

using System.Reflection;
using GorgonAddons.Core;

public sealed class AddonLoader
{
    private readonly string _directory;
    private readonly List<(IAddon addon, bool enabled)> _addons = [];

    public AddonLoader(string directory)
    {
        _directory = directory;
    }

    public void DiscoverAndLoad()
    {
        if (!Directory.Exists(_directory)) return;

        foreach (var dll in Directory.GetFiles(_directory, "*.dll"))
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                foreach (var type in asm.GetTypes())
                {
                    if (type.IsAbstract || !typeof(IAddon).IsAssignableFrom(type)) continue;
                    try
                    {
                        var addon = (IAddon)Activator.CreateInstance(type)!;
                        _addons.Add((addon, true));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[AddonLoader] Failed to instantiate {type.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AddonLoader] Failed to load {Path.GetFileName(dll)}: {ex.Message}");
            }
        }
    }

    public void InitializeAll(GameContext ctx)
    {
        foreach (var (addon, enabled) in _addons)
        {
            if (!enabled) continue;
            try { addon.Initialize(ctx); }
            catch (Exception ex) { Console.WriteLine($"[AddonLoader] {addon.Name} Initialize failed: {ex.Message}"); }
        }
    }

    public void TickAll(GameContext ctx)
    {
        foreach (var (addon, enabled) in _addons)
        {
            if (!enabled) continue;
            try { addon.OnTick(ctx); }
            catch (Exception ex) { Console.WriteLine($"[AddonLoader] {addon.Name} Tick failed: {ex.Message}"); }
        }
    }

    public void ShutdownAll()
    {
        foreach (var (addon, _) in _addons)
        {
            try { addon.Shutdown(); }
            catch (Exception ex) { Console.WriteLine($"[AddonLoader] {addon.Name} Shutdown failed: {ex.Message}"); }
        }
    }

    public void Enable(string name)  => SetEnabled(name, true);
    public void Disable(string name) => SetEnabled(name, false);

    private void SetEnabled(string name, bool enabled)
    {
        for (int i = 0; i < _addons.Count; i++)
        {
            if (_addons[i].addon.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _addons[i] = (_addons[i].addon, enabled);
                return;
            }
        }
    }

    public IReadOnlyList<(IAddon addon, bool enabled)> ListAddons() => _addons;
}

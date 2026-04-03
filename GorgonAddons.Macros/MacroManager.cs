namespace GorgonAddons.Macros;

public class MacroManager
{
    private readonly string _directory;
    private readonly List<MacroDefinition> _macros = new();

    public MacroManager(string directory)
    {
        _directory = directory;
    }

    public int LoadAll()
    {
        _macros.Clear();

        if (!Directory.Exists(_directory)) return 0;

        foreach (var file in Directory.EnumerateFiles(_directory, "*.macro", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var content = File.ReadAllText(file);
                var macro = MacroParser.Parse(content, file);
                _macros.Add(macro);
            }
            catch
            {
                // Skip unparseable files
            }
        }

        return _macros.Count;
    }

    public int Reload()
    {
        _macros.Clear();
        return LoadAll();
    }

    public MacroDefinition? GetByName(string name) =>
        _macros.FirstOrDefault(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public MacroDefinition? GetByBind(string hotkeySpec) =>
        _macros.FirstOrDefault(m => m.Bind.Equals(hotkeySpec, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<MacroDefinition> GetAll() => _macros.AsReadOnly();

    public IEnumerable<string> GetAllBindKeys() =>
        _macros.Select(m => m.Bind).Where(b => b.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase);
}

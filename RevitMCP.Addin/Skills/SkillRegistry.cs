using RevitMCP.Addin.Skills.Models;

namespace RevitMCP.Addin.Skills;

/// <summary>
/// In-memory registry of all loaded <see cref="SkillDefinition"/> objects.
/// Call <see cref="Load"/> once at startup (or on demand to reload).
/// </summary>
public class SkillRegistry
{
    private readonly SkillLoader _loader;
    private Dictionary<string, SkillDefinition> _store = new();

    public SkillRegistry(SkillLoader loader) => _loader = loader;

    public void Load()
    {
        var list = _loader.LoadAll();
        _store = list.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
    }

    public void Reload() => Load();

    public SkillDefinition? Get(string skillId)
        => _store.TryGetValue(skillId, out var def) ? def : null;

    public IReadOnlyList<SkillDefinition> GetAll() => _store.Values.ToList();
}

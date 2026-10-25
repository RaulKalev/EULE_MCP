using System.IO;
using System.Text;
using System.Text.Json;

namespace RevitMCP.Addin.Families;

/// <summary>The file a family was last loaded from, as revit_load_family recorded it.</summary>
public sealed class FamilyPathEntry
{
    public string Path { get; set; } = string.Empty;
    public DateTime LoadedUtc { get; set; }
}

/// <summary>
/// Per-model record of where each family was loaded from (#102). Revit does not keep a loaded family's source path,
/// so revit_load_family writes it here and revit_reload_family reads it back. One JSON file per model:
/// <c>%LOCALAPPDATA%\RevitMCP\Families\&lt;modelId&gt;.json</c>, keyed by family name (case-insensitive). The store is
/// a hint, never the truth: a path that no longer exists is reported, not trusted. No Revit API: unit tested.
/// </summary>
public sealed class FamilyPathStore
{
    private static readonly object FileLock = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public FamilyPathStore(string folder) => Folder = folder;

    public string Folder { get; }

    public static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitMCP", "Families");

    public static FamilyPathStore Default() => new(DefaultFolder);

    /// <summary>The store file of a model. Characters outside [A-Za-z0-9-_] are replaced so a model id never leaves the folder.</summary>
    public string FileOf(string modelId) => System.IO.Path.Combine(Folder, SafeFileName(modelId) + ".json");

    public FamilyPathEntry? Get(string modelId, string familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName)) return null;
        lock (FileLock)
            return Read(modelId).TryGetValue(familyName.Trim(), out var entry) ? entry : null;
    }

    public IReadOnlyDictionary<string, FamilyPathEntry> All(string modelId)
    {
        lock (FileLock)
            return Read(modelId);
    }

    /// <summary>Records (or replaces) the source file of each family. Failures to write are returned, never thrown.</summary>
    public string? Remember(string modelId, IEnumerable<(string FamilyName, string Path)> loaded, DateTime? nowUtc = null)
    {
        var items = loaded.Where(i => !string.IsNullOrWhiteSpace(i.FamilyName) && !string.IsNullOrWhiteSpace(i.Path)).ToList();
        if (items.Count == 0) return null;
        lock (FileLock)
        {
            try
            {
                var map = Read(modelId);
                foreach (var (name, path) in items)
                    map[name.Trim()] = new FamilyPathEntry { Path = path, LoadedUtc = nowUtc ?? DateTime.UtcNow };
                Write(modelId, map);
                return null;
            }
            catch (Exception ex)
            {
                return $"The family path store could not be written ({FileOf(modelId)}): {ex.Message}";
            }
        }
    }

    /// <summary>Removes families from the store (e.g. after a purge). Returns how many entries were removed.</summary>
    public int Forget(string modelId, IEnumerable<string> familyNames)
    {
        lock (FileLock)
        {
            var map = Read(modelId);
            var removed = familyNames.Count(name => !string.IsNullOrWhiteSpace(name) && map.Remove(name.Trim()));
            if (removed > 0)
            {
                try { Write(modelId, map); }
                catch { return 0; }
            }
            return removed;
        }
    }

    private Dictionary<string, FamilyPathEntry> Read(string modelId)
    {
        var map = new Dictionary<string, FamilyPathEntry>(StringComparer.OrdinalIgnoreCase);
        var file = FileOf(modelId);
        try
        {
            if (!File.Exists(file)) return map;
            var document = JsonSerializer.Deserialize<StoreDocument>(File.ReadAllText(file), JsonOptions);
            if (document?.Families == null) return map;
            foreach (var pair in document.Families)
                if (pair.Value != null && !string.IsNullOrWhiteSpace(pair.Value.Path))
                    map[pair.Key] = pair.Value;
        }
        catch
        {
            // A damaged store is treated as empty: it only holds hints.
        }
        return map;
    }

    private void Write(string modelId, Dictionary<string, FamilyPathEntry> map)
    {
        Directory.CreateDirectory(Folder);
        var file = FileOf(modelId);
        var document = new StoreDocument
        {
            ModelId = modelId,
            Families = map.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(p => p.Key, p => p.Value)
        };
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
        if (File.Exists(file)) File.Delete(file);
        File.Move(temp, file);
    }

    public static string SafeFileName(string modelId)
    {
        var text = string.IsNullOrWhiteSpace(modelId) ? "unknown-model" : modelId.Trim();
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
            builder.Append(char.IsLetterOrDigit(c) && c < 128 || c == '-' || c == '_' ? c : '_');
        var safe = builder.ToString().Trim('_');
        if (safe.Length == 0) safe = "unknown-model";
        return safe.Length > 120 ? safe.Substring(0, 120) : safe;
    }

    internal sealed class StoreDocument
    {
        public string ModelId { get; set; } = string.Empty;
        public Dictionary<string, FamilyPathEntry> Families { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

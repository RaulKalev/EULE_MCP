using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP.Addin.PanelSchematics;

/// <summary>
/// The schematic symbols a row's "Sümbol" may name. The plugin maps each symbol name (DisplayName) to a detail
/// family and type in the team's KilbiskeemiTingmärgid.json; the file is found the way the plugin's
/// SymbolMappingConfig finds it. Read-only: the connector never changes the file.
/// </summary>
public static class PanelSchematicsSymbols
{
    public const string FileName = "KilbiskeemiTingmärgid.json";

    private const string RelativePath = @"EULE Dropbox\0_EULE  Team folder (kogu kollektiiv)\02_EULE REVIT TEMPLATE\" + FileName;

    public sealed class Symbol
    {
        public string Category { get; set; } = string.Empty;
        /// <summary>The name stored in a row's Symbol field and in the 'Sümbol' parameter.</summary>
        public string Name { get; set; } = string.Empty;
        public string FamilyName { get; set; } = string.Empty;
        public string FamilyType { get; set; } = string.Empty;
    }

    public sealed class Library
    {
        public string Path { get; set; } = string.Empty;
        /// <summary>False when the file is missing or unreadable; then names cannot be checked.</summary>
        public bool Loaded { get; set; }
        public string? Problem { get; set; }
        public List<Symbol> Symbols { get; set; } = new();

        public bool Contains(string name) => Symbols.Any(s => string.Equals(s.Name, name.Trim(), StringComparison.Ordinal));

        /// <summary>Symbol names drawn with this detail family and type (several names can share one).</summary>
        public List<string> NamesFor(string familyName, string typeName)
            => Symbols.Where(s => string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)
                                  && string.Equals(s.FamilyType, typeName, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.Name).Distinct(StringComparer.Ordinal).ToList();

        /// <summary>Names to offer for a name that is not in the file: same text in another case first, then names containing it.</summary>
        public List<string> Suggest(string name, int max = 6)
        {
            var wanted = name.Trim();
            if (wanted.Length == 0) return new List<string>();
            return Symbols.Select(s => s.Name)
                .Where(n => n.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0 || wanted.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(n => n.Length)
                .Distinct(StringComparer.Ordinal)
                .Take(max)
                .ToList();
        }
    }

    /// <summary>Reads the team's symbol file. Never throws.</summary>
    public static Library Load()
    {
        var path = ResolvePath();
        try
        {
            if (!File.Exists(path))
                return new Library { Path = path, Problem = $"{FileName} was not found (looked for {path})." };
            var library = Parse(File.ReadAllText(path));
            library.Path = path;
            return library;
        }
        catch (Exception ex)
        {
            return new Library { Path = path, Problem = $"{FileName} could not be read: {ex.Message}" };
        }
    }

    /// <summary>The symbols in the file's SymbolMappings list. Entries without a DisplayName are skipped, as in the plugin.</summary>
    public static Library Parse(string json)
    {
        var library = new Library();
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root || root["SymbolMappings"] is not JsonArray mappings)
            {
                library.Problem = $"{FileName} has no SymbolMappings list.";
                return library;
            }
            foreach (var entry in mappings.OfType<JsonObject>())
            {
                var name = Text(entry["DisplayName"]).Trim();
                if (name.Length == 0) continue;
                var category = Text(entry["Category"]).Trim();
                library.Symbols.Add(new Symbol
                {
                    Category = category.Length == 0 ? "Muu" : category,
                    Name = name,
                    FamilyName = Text(entry["FamilyName"]),
                    FamilyType = Text(entry["FamilyType"])
                });
            }
            library.Loaded = true;
        }
        catch (JsonException ex)
        {
            library.Problem = $"{FileName} is not valid JSON: {ex.Message}";
        }
        return library;
    }

    private static string Text(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : string.Empty;

    /// <summary>The user profile first, then the same Dropbox path under another drive's Users folder or drive root.</summary>
    public static string ResolvePath()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var defaultPath = Path.Combine(profile, RelativePath);
        if (File.Exists(defaultPath)) return defaultPath;

        var userName = Path.GetFileName(profile);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (!string.IsNullOrWhiteSpace(userName))
                {
                    var underUsers = Path.Combine(drive.RootDirectory.FullName, "Users", userName, RelativePath);
                    if (File.Exists(underUsers)) return underUsers;
                }
                var atRoot = Path.Combine(drive.RootDirectory.FullName, RelativePath);
                if (File.Exists(atRoot)) return atRoot;
            }
            catch
            {
                // A drive that cannot be read is skipped.
            }
        }
        return defaultPath;
    }
}

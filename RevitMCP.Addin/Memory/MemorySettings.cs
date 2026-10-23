using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.Memory;

/// <summary>
/// The <c>memory.*</c> configuration keys, user config first, then company config. Keys of later phases
/// (hints, observer, lifecycle days, company folder) are not read yet. Pure — no Revit API.
/// </summary>
public sealed class MemorySettings
{
    public const string Section = "memory";
    public static readonly string DefaultProjectFolderName = Path.Combine(".rktools", "memory");

    public bool Enabled { get; set; } = true;

    /// <summary>Project tier folder, relative to the project root.</summary>
    public string ProjectFolderName { get; set; } = DefaultProjectFolderName;
    public int ValueMaxLength { get; set; } = MemoryPolicy.DefaultValueMaxLength;
    public int MaxEntriesPerProject { get; set; } = MemoryPolicy.DefaultMaxEntriesPerProject;

    public static MemorySettings From(JsonObject? user, JsonObject? company)
    {
        var s = new MemorySettings();
        var sections = new[] { user?[Section] as JsonObject, company?[Section] as JsonObject };

        var enabled = First(sections, "enabled");
        if (enabled != null && bool.TryParse(enabled, out var on)) s.Enabled = on;

        var folder = First(sections, "projectFolderName");
        if (!string.IsNullOrWhiteSpace(folder) && !Path.IsPathRooted(folder) && !folder!.Contains(".."))
            s.ProjectFolderName = folder.Trim().Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);

        s.ValueMaxLength = Int(First(sections, "valueMaxLength"), s.ValueMaxLength, 80, 2000);
        s.MaxEntriesPerProject = Int(First(sections, "maxEntriesPerProject"), s.MaxEntriesPerProject, 10, 100_000);
        return s;
    }

    /// <summary>Reads the user and company config files. Never throws: unreadable files give the defaults.</summary>
    public static MemorySettings Load(string? userConfigPath = null, string? companyConfigPath = null)
    {
        var svc = new JsonConfigService();
        return From(
            Read(svc, userConfigPath ?? ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser).Path),
            Read(svc, companyConfigPath ?? ConfigPathResolver.Resolve(ConfigPathResolver.ScopeCompany).Path));
    }

    private static JsonObject? Read(JsonConfigService svc, string? path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var (config, _) = svc.Read(path!);
            return config;
        }
        catch
        {
            return null;
        }
    }

    private static string? First(JsonObject?[] sections, string key)
    {
        foreach (var section in sections)
        {
            var node = section?[key];
            if (node != null) return node.ToString();
        }
        return null;
    }

    private static int Int(string? raw, int fallback, int min, int max) =>
        raw != null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? Math.Max(min, Math.Min(max, n))
            : fallback;
}

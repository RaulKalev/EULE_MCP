using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.Usage;

/// <summary>Manual-time figures for one tool (docs/usage-reporting.md, "Theoretical time saved").</summary>
public sealed class TimeSavedBaseline
{
    public double? SecondsPerElement { get; set; }
    public double? SecondsPerCall { get; set; }
}

/// <summary>
/// The <c>usage.*</c> configuration keys: <c>sharedFolder</c>, <c>reportFolder</c>, <c>costExportFolder</c> and
/// <c>timeSavedBaselines</c>. The company config sets them; the user config may override a key. Pure — no Revit API.
/// </summary>
public sealed class UsageSettings
{
    public const string Section = "usage";

    /// <summary>Where every user's log lines are copied; null = no shared copy (the default, see the consent note).</summary>
    public string? SharedFolder { get; set; }
    public string? ReportFolderSetting { get; set; }
    public string? CostExportFolderSetting { get; set; }
    public Dictionary<string, TimeSavedBaseline> Baselines { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary><c>usage.reportFolder</c>, else <c>&lt;sharedFolder&gt;\reports</c>.</summary>
    public string? ReportFolder => ReportFolderSetting ?? (SharedFolder == null ? null : Path.Combine(SharedFolder, "reports"));

    /// <summary><c>usage.costExportFolder</c>, else <c>&lt;sharedFolder&gt;\costs</c>.</summary>
    public string? CostExportFolder => CostExportFolderSetting ?? (SharedFolder == null ? null : Path.Combine(SharedFolder, "costs"));

    public static UsageSettings From(JsonObject? user, JsonObject? company)
    {
        var s = new UsageSettings();
        var sections = new[] { user?[Section] as JsonObject, company?[Section] as JsonObject };
        s.SharedFolder = Folder(First(sections, "sharedFolder"));
        s.ReportFolderSetting = Folder(First(sections, "reportFolder"));
        s.CostExportFolderSetting = Folder(First(sections, "costExportFolder"));

        // Baselines: the company table, with any user entries on top.
        foreach (var section in sections.AsEnumerable().Reverse())
        {
            if (section?["timeSavedBaselines"] is not JsonObject table) continue;
            foreach (var pair in table)
            {
                if (pair.Value is not JsonObject b) continue;
                var baseline = new TimeSavedBaseline
                {
                    SecondsPerElement = Number(b["secondsPerElement"]),
                    SecondsPerCall = Number(b["secondsPerCall"])
                };
                if (baseline.SecondsPerElement != null || baseline.SecondsPerCall != null) s.Baselines[pair.Key] = baseline;
            }
        }
        return s;
    }

    /// <summary>Reads the user and company config files. Never throws: unreadable files give the defaults.</summary>
    public static UsageSettings Load(string? userConfigPath = null, string? companyConfigPath = null)
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

    private static string? Folder(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : Environment.ExpandEnvironmentVariables(raw!.Trim());

    private static double? Number(JsonNode? node)
    {
        if (node == null) return null;
        return double.TryParse(node.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 ? d : null;
    }
}

/// <summary>
/// The <c>projectId</c> written to every log line: the project config's <c>projectId</c> (above the model file), else
/// the Project Information number (the key the model graph and shared memory use), else the model name's leading
/// token when it holds a digit ("1626_PP_EN" → "1626"), else the model name. Pure — no Revit API.
/// </summary>
public static class UsageProjectId
{
    public static string? Resolve(string? configProjectId, string? projectNumber, string? modelName)
    {
        if (!string.IsNullOrWhiteSpace(configProjectId)) return configProjectId!.Trim();
        if (!string.IsNullOrWhiteSpace(projectNumber)) return projectNumber!.Trim();
        return FromModelName(modelName);
    }

    public static string? FromModelName(string? modelName)
    {
        var name = (modelName ?? string.Empty).Trim();
        if (name.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
        if (name.Length == 0) return null;
        var cut = name.IndexOfAny(new[] { '_', ' ', '-', '.' });
        var head = cut > 0 ? name.Substring(0, cut) : name;
        return head.Any(char.IsDigit) ? head : name;
    }

    /// <summary>The <c>projectId</c> key of the project config above <paramref name="modelPath"/>, or null.</summary>
    public static string? FromProjectConfig(string? modelPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelPath) || modelPath!.Contains("://")) return null;
            for (var dir = Path.GetDirectoryName(Path.GetFullPath(modelPath)); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            {
                var file = Path.Combine(dir, ".rktools", "mcp.project.config.json");
                if (!File.Exists(file)) continue;
                var (config, _) = new JsonConfigService().Read(file);
                var id = config?["projectId"]?.ToString();
                return string.IsNullOrWhiteSpace(id) ? null : id!.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return null;
    }
}

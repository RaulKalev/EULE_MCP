using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.Memory;

/// <summary>
/// The <c>memory.*</c> configuration keys, user config first, then company config. Pure — no Revit API.
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

    /// <summary>The company tier (read-only for agents; written by an administrator from proposals). Null: none.</summary>
    public string? CompanyFolder { get; set; }

    /// <summary>
    /// Folder names skipped when the project folder is guessed (no <c>.rktools</c> above the model), case-insensitive:
    /// an optional number prefix, then a design stage, a discipline or a software/output folder.
    /// </summary>
    public const string DefaultProjectRootSkipFolders =
        @"^(\d+[\s._-]*)?(EP|PP|TP|TJ|EH|EN|EL|ES|ATS|NV|TV|AR|KV|VK|VVK|SK|IT|AV|Revit|BIM|Mudel|Mudelid|Model|Models|CAD|DWG|IFC|Joonised|Drawings)$";

    /// <summary><c>memory.projectRootSkipFolders</c>: the regex of folder names skipped when guessing the project folder.</summary>
    public string ProjectRootSkipFolders { get; set; } = DefaultProjectRootSkipFolders;

    /// <summary>Syncs (or saves) the model may move on before an entry written in it is reported stale.</summary>
    public int StaleAfterVersions { get; set; } = MemoryRecall.DefaultStaleAfterVersions;

    /// <summary>
    /// Memory hints inside revit_graph_route. On since the memory-on/off benchmark on a real project
    /// (docs/benchmarks, IPI2519, after the recall matching fix #113); <c>memory.hints=false</c> switches them off.
    /// </summary>
    public bool Hints { get; set; } = true;
    public int HintLimit { get; set; } = 5;

    public bool ObserverEnabled { get; set; } = true;

    /// <summary>Sightings before an observed pattern becomes a candidate.</summary>
    public int ObserverMinSupport { get; set; } = 3;

    /// <summary>Distinct sessions those sightings must span.</summary>
    public int ObserverMinSessions { get; set; } = 2;

    /// <summary>Successful uses (or authors) that confirm a candidate.</summary>
    public int ConfirmAfterUses { get; set; } = 3;
    public int DormantAfterDays { get; set; } = 90;
    public int RetireAfterDays { get; set; } = 365;

    /// <summary>Project roots the consolidation pass reads when none are passed (for the unattended nightly run).</summary>
    public List<string> ConsolidationRoots { get; set; } = new();

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

        s.StaleAfterVersions = Int(First(sections, "staleAfterVersions"), s.StaleAfterVersions, 1, 100_000);

        var skipFolders = First(sections, "projectRootSkipFolders");
        if (!string.IsNullOrWhiteSpace(skipFolders)) s.ProjectRootSkipFolders = skipFolders!.Trim();

        var companyFolder = First(sections, "companyFolder");
        if (!string.IsNullOrWhiteSpace(companyFolder)) s.CompanyFolder = Environment.ExpandEnvironmentVariables(companyFolder!.Trim());

        var hints = First(sections, "hints");
        if (hints != null && bool.TryParse(hints, out var hintsOn)) s.Hints = hintsOn;
        s.HintLimit = Int(First(sections, "hintLimit"), s.HintLimit, 1, 20);

        var observer = new[] { (user?[Section] as JsonObject)?["observer"] as JsonObject, (company?[Section] as JsonObject)?["observer"] as JsonObject };
        var observerOn = First(observer, "enabled");
        if (observerOn != null && bool.TryParse(observerOn, out var obs)) s.ObserverEnabled = obs;
        s.ObserverMinSupport = Int(First(observer, "minSupport"), s.ObserverMinSupport, 1, 100);
        s.ObserverMinSessions = Int(First(observer, "minSessions"), s.ObserverMinSessions, 1, 100);

        s.ConfirmAfterUses = Int(First(sections, "confirmAfterUses"), s.ConfirmAfterUses, 1, 100);
        s.DormantAfterDays = Int(First(sections, "dormantAfterDays"), s.DormantAfterDays, 1, 3650);
        s.RetireAfterDays = Int(First(sections, "retireAfterDays"), s.RetireAfterDays, 1, 3650);

        foreach (var section in sections)
        {
            if ((section?["consolidation"] as JsonObject)?["projectRoots"] is not JsonArray roots) continue;
            s.ConsolidationRoots = roots.Select(r => r?.ToString().Trim() ?? string.Empty).Where(r => r.Length > 0).ToList();
            break;
        }
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

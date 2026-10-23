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
    /// Memory hints inside revit_graph_route. Off until the benchmark on a real project shows they pay for
    /// themselves (docs/shared-memory.md, "Credit usage"); <c>memory.hints=true</c> switches them on.
    /// </summary>
    public bool Hints { get; set; }
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

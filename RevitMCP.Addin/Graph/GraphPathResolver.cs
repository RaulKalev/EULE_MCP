using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RevitMCP.Addin.Configuration;

namespace RevitMCP.Addin.Graph;

/// <summary>Where a model's graph database lives and why that location was chosen.</summary>
public sealed class GraphLocation
{
    public string DatabasePath { get; set; } = string.Empty;
    public string Root { get; set; } = string.Empty;

    /// <summary>One of the <c>GraphPathResolver.Source*</c> constants.</summary>
    public string RootSource { get; set; } = string.Empty;
    public string ProjectSegment { get; set; } = string.Empty;
    public string ModelSegment { get; set; } = string.Empty;
}

/// <summary>
/// Resolves <c>&lt;root&gt;\&lt;project&gt;\&lt;model-name&gt;.graph.db</c>. The root comes from, in order:
/// an explicit <c>dbPath</c> argument, a <c>sharedFolder</c> argument, the <c>graph.sharedFolder</c>
/// key in the user config, the same key in the company config, and finally the local fallback
/// <c>%LOCALAPPDATA%\RKTools\RevitMCP\Graph</c>. No Revit API dependency.
/// </summary>
public static class GraphPathResolver
{
    public const string SourceExplicitPath  = "dbPath argument";
    public const string SourceArgument      = "sharedFolder argument";
    public const string SourceUserConfig    = "user config (graph.sharedFolder)";
    public const string SourceCompanyConfig = "company config (graph.sharedFolder)";
    public const string SourceLocalFallback = "local fallback (%LOCALAPPDATA%\\RKTools\\RevitMCP\\Graph)";

    private const string UnnamedProject = "_unfiled";
    private const string UnnamedModel = "_untitled";
    private const int MaxSegmentLength = 100;

    private static readonly Regex InvalidChars = new(@"[\\/:*?""<>|\p{C}]+", RegexOptions.Compiled);

    /// <summary>Local root used when no shared folder is configured, and for temp/cache files.</summary>
    public static string DefaultLocalRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RKTools", "RevitMCP", "Graph");

    /// <summary>Makes a project/model name safe as a single path segment.</summary>
    public static string SanitizeSegment(string? value, string fallback)
    {
        var s = (value ?? string.Empty).Trim();
        s = InvalidChars.Replace(s, "_").Trim(' ', '.', '_');
        if (s.Length == 0) return fallback;
        if (s.Length > MaxSegmentLength) s = s.Substring(0, MaxSegmentLength).TrimEnd(' ', '.', '_');
        return s.Length == 0 ? fallback : s;
    }

    public static GraphLocation Resolve(
        string? dbPathOverride,
        string? sharedFolderOverride,
        string? configuredSharedFolder,
        string? configuredSource,
        string? projectKey,
        string? modelName,
        string? localRoot = null)
    {
        var project = SanitizeSegment(projectKey, UnnamedProject);
        var model = SanitizeSegment(modelName, UnnamedModel);

        if (!string.IsNullOrWhiteSpace(dbPathOverride))
        {
            var full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dbPathOverride!.Trim()));
            return new GraphLocation
            {
                DatabasePath = full,
                Root = Path.GetDirectoryName(full) ?? string.Empty,
                RootSource = SourceExplicitPath,
                ProjectSegment = project,
                ModelSegment = model
            };
        }

        string root;
        string source;
        if (!string.IsNullOrWhiteSpace(sharedFolderOverride))
        {
            root = Environment.ExpandEnvironmentVariables(sharedFolderOverride!.Trim());
            source = SourceArgument;
        }
        else if (!string.IsNullOrWhiteSpace(configuredSharedFolder))
        {
            root = Environment.ExpandEnvironmentVariables(configuredSharedFolder!.Trim());
            source = string.IsNullOrWhiteSpace(configuredSource) ? SourceUserConfig : configuredSource!;
        }
        else
        {
            root = localRoot ?? DefaultLocalRoot();
            source = SourceLocalFallback;
        }

        root = Path.GetFullPath(root);
        return new GraphLocation
        {
            DatabasePath = Path.Combine(root, project, model + GraphSchema.FileExtension),
            Root = root,
            RootSource = source,
            ProjectSegment = project,
            ModelSegment = model
        };
    }

    /// <summary>Reads <c>graph.sharedFolder</c> from a parsed config object. Returns null when absent or blank.</summary>
    public static string? ExtractSharedFolder(JsonObject? config)
    {
        if (config == null) return null;
        if (config["graph"] is not JsonObject graph) return null;
        var value = graph["sharedFolder"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
    }

    /// <summary>
    /// Looks up the configured shared folder: user config first, then company config.
    /// Paths can be overridden for tests; by default the standard EULE MCP scope paths are used.
    /// </summary>
    public static (string? Folder, string? Source) LoadConfiguredSharedFolder(
        string? userConfigPath = null,
        string? companyConfigPath = null)
    {
        var svc = new JsonConfigService();

        var userPath = userConfigPath ?? ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser).Path;
        var fromUser = ReadFolder(svc, userPath);
        if (fromUser != null) return (fromUser, SourceUserConfig);

        var companyPath = companyConfigPath ?? ConfigPathResolver.Resolve(ConfigPathResolver.ScopeCompany).Path;
        var fromCompany = ReadFolder(svc, companyPath);
        if (fromCompany != null) return (fromCompany, SourceCompanyConfig);

        return (null, null);
    }

    private static string? ReadFolder(JsonConfigService svc, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var (config, _) = svc.Read(path!, createIfMissing: false);
        return ExtractSharedFolder(config);
    }
}

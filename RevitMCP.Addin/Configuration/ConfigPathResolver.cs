using System.IO;

namespace RevitMCP.Addin.Configuration;

/// <summary>
/// Resolves file paths for the four configuration scopes used by EULE MCP.
/// </summary>
public static class ConfigPathResolver
{
    // Scope names recognised by the tools
    public const string ScopeCompany  = "company";
    public const string ScopeUser     = "user";
    public const string ScopeProject  = "project";
    public const string ScopeToolState = "tool-state";

    // Fixed path components
    private const string RkToolsSubPath     = @"RKTools\MCP";
    private const string CompanyConfigFile   = "company.config.json";
    private const string UserConfigFile      = "user.config.json";
    private const string ToolStateConfigFile = "tool-state.json";
    private const string ProjectConfigFile   = "mcp.project.config.json";
    private const string ProjectConfigFolder = ".rktools";

    /// <summary>
    /// Returns the full path for the given scope.
    /// For the "project" scope the <paramref name="projectRoot"/> parameter is required.
    /// </summary>
    public static (string? Path, string? Error) Resolve(string scope, string? projectRoot = null)
    {
        switch (scope.Trim().ToLowerInvariant())
        {
            case ScopeCompany:
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return (System.IO.Path.Combine(programData, RkToolsSubPath, "Config", CompanyConfigFile), null);
            }
            case ScopeUser:
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return (System.IO.Path.Combine(appData, RkToolsSubPath, UserConfigFile), null);
            }
            case ScopeToolState:
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return (System.IO.Path.Combine(appData, RkToolsSubPath, "State", ToolStateConfigFile), null);
            }
            case ScopeProject:
            {
                if (string.IsNullOrWhiteSpace(projectRoot))
                    return (null, "projectRoot is required for the 'project' scope.");
                var fullRoot = System.IO.Path.GetFullPath(projectRoot);
                if (!Directory.Exists(fullRoot))
                    return (null, $"Project root directory not found: {fullRoot}");
                return (System.IO.Path.Combine(fullRoot, ProjectConfigFolder, ProjectConfigFile), null);
            }
            default:
                return (null, $"Unknown scope '{scope}'. Valid scopes: {ScopeCompany}, {ScopeUser}, {ScopeProject}, {ScopeToolState}.");
        }
    }

    /// <summary>Returns all four well-known scope paths regardless of whether they exist.</summary>
    public static IReadOnlyList<(string Scope, string Path)> AllScopePaths(string? projectRoot = null)
    {
        var list = new List<(string, string)>();
        foreach (var scope in new[] { ScopeCompany, ScopeUser, ScopeProject, ScopeToolState })
        {
            var (path, _) = Resolve(scope, scope == ScopeProject ? projectRoot : null);
            if (path != null) list.Add((scope, path));
        }
        return list;
    }
}

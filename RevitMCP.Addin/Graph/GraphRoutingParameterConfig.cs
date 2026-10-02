using System.IO;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.RoomDevices;

namespace RevitMCP.Addin.Graph;

/// <summary>
/// Loads the routing-parameter allowlist (#63) for a document: <c>graph.routingParameters</c> from the
/// project config (.rktools above the model), else the user config, else the company config. The first
/// scope that defines the key wins, so a project can narrow or clear (empty array) a company list.
/// </summary>
internal static class GraphRoutingParameterConfig
{
    public static (List<RoutingParameterSpec> Specs, string? Source, List<string> Warnings) Load(Document doc)
    {
        var warnings = new List<string>();
        foreach (var (scope, path) in Candidates(doc))
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                var (config, _) = new JsonConfigService().Read(path!, createIfMissing: false);
                var node = config?[GraphRoutingParameters.ConfigSection]?[GraphRoutingParameters.ConfigKey];
                if (node == null) continue;
                var (specs, parseWarnings) = GraphRoutingParameters.Parse(node);
                warnings.AddRange(parseWarnings);
                return (specs, $"{scope} config ({path})", warnings);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not read {GraphRoutingParameters.ConfigSection}.{GraphRoutingParameters.ConfigKey} from {path}: {ex.Message}");
            }
        }
        return (new List<RoutingParameterSpec>(), null, warnings);
    }

    private static IEnumerable<(string Scope, string? Path)> Candidates(Document doc)
    {
        var root = ProjectConfigLocator.FindProjectRoot(doc, string.Empty);
        if (root != null)
            yield return (ConfigPathResolver.ScopeProject, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeProject, root).Path);
        yield return (ConfigPathResolver.ScopeUser, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeUser).Path);
        yield return (ConfigPathResolver.ScopeCompany, ConfigPathResolver.Resolve(ConfigPathResolver.ScopeCompany).Path);
    }
}

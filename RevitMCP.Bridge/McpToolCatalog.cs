using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>
/// Builds an opt-in reduced MCP tool catalog. The default "full" profile continues
/// to use the SDK's normal attributed-tool registration path.
/// </summary>
internal static class McpToolCatalog
{
    private static readonly HashSet<string> QueryProfileNames = new(
        new[]
        {
            "revit_get_connection_status",
            "revit_graph_route",
            "revit_graph_status",
            "revit_graph_summary",
            "revit_graph_query",
            "revit_graph_build",
            "revit_list_instances",
            "revit_select_instance",
            "revit_get_selected_elements",
            "revit_inspect_selected_elements",
            "revit_list_views",
            "revit_list_sheets",
            "revit_list_schedules",
            "revit_get_element_parameters",
            "revit_count_elements",
            "revit_group_by_parameter",
            "revit_get_available_parameters",
            "revit_list_query_presets",
            "revit_run_query_preset",
            "revit_check_parameter_completeness",
            "revit_query_linked_elements",
            "revit_find_elements_by_parameter",
            "revit_get_elements_info",
            "revit_group_elements",
            "revit_get_text_notes",
            "revit_list_tag_types",
            "revit_list_family_types",
            "revit_list_dimension_types",
            "revit_get_electrical_circuits",
            "revit_get_circuit_info",
            "revit_get_available_panels",
            "revit_list_titleblocks",
            "revit_list_view_templates",
            "revit_list_revisions",
            "revit_get_view_sheet_summary",
            "revit_get_clash_summary",
            "revit_list_skills"
        },
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Profiles the bridge understands (shown in errors and docs).</summary>
    public const string SupportedProfiles = "core, full, query, read-only";

    public static IReadOnlyList<McpServerTool> CreateSelectedTools(
        string profile,
        string? explicitToolNames,
        string? toolGroups = null)
    {
        var methods = GetToolMethods();
        var requestedNames = ParseExplicitNames(explicitToolNames);
        var extraGroups = ParseExplicitNames(toolGroups);

        IEnumerable<(MethodInfo Method, McpServerToolAttribute Attribute)> selected;
        if (requestedNames.Count > 0)
        {
            selected = methods.Where(item => requestedNames.Contains(GetToolName(item)));
            ValidateNames(requestedNames, methods);
        }
        else if (profile.Equals("core", StringComparison.OrdinalIgnoreCase))
        {
            var names = new HashSet<string>(
                BridgeToolGroups.SelectByGroups(methods.Select(GetToolName), extraGroups, out var unknown),
                StringComparer.OrdinalIgnoreCase);
            ThrowOnUnknownGroups(unknown);
            selected = methods.Where(item => names.Contains(GetToolName(item)));
        }
        else if (profile.Equals("full", StringComparison.OrdinalIgnoreCase))
        {
            selected = methods;
        }
        else if (profile.Equals("query", StringComparison.OrdinalIgnoreCase))
        {
            selected = methods.Where(item => QueryProfileNames.Contains(GetToolName(item)));
        }
        else if (profile.Equals("read-only", StringComparison.OrdinalIgnoreCase))
        {
            selected = methods.Where(item => item.Attribute.ReadOnly);
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown RevitMCP tool profile '{profile}'. Supported profiles: {SupportedProfiles}.");
        }

        // --tool-groups adds whole groups to any non-core profile as well.
        if (extraGroups.Count > 0 && !profile.Equals("core", StringComparison.OrdinalIgnoreCase) && requestedNames.Count == 0)
        {
            var groupNames = new HashSet<string>(
                BridgeToolGroups.SelectByGroups(methods.Select(GetToolName), extraGroups, out var unknown),
                StringComparer.OrdinalIgnoreCase);
            ThrowOnUnknownGroups(unknown);
            selected = selected.Concat(methods.Where(item => groupNames.Contains(GetToolName(item)))).Distinct();
        }

        return selected
            .OrderBy(item => item.Method.DeclaringType == typeof(ToolDiscoveryTools) ? 1 : 0)
            .ThenBy(item => item.Method.MetadataToken)
            .Select(item => CreateTool(item.Method))
            .ToList();
    }

    /// <summary>Every tool, advertised or not — the registry behind tool discovery and dispatch (#65).</summary>
    public static IReadOnlyList<McpServerTool> CreateAllTools() =>
        GetToolMethods().Select(item => CreateTool(item.Method)).ToList();

    private static McpServerTool CreateTool(MethodInfo method) =>
        McpServerTool.Create(method, context => context.Services!.GetRequiredService(method.DeclaringType!));

    public static bool IsFullProfile(string profile, string? explicitToolNames, string? toolGroups = null)
    {
        return string.IsNullOrWhiteSpace(explicitToolNames) &&
               string.IsNullOrWhiteSpace(toolGroups) &&
               profile.Equals("full", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>All tool names the bridge defines (for discovery and tests).</summary>
    public static IReadOnlyList<string> AllToolNames() => GetToolMethods().Select(GetToolName).ToList();

    private static void ThrowOnUnknownGroups(List<string> unknown)
    {
        if (unknown.Count > 0)
            throw new InvalidOperationException(
                "Unknown RevitMCP tool group(s): " + string.Join(", ", unknown) +
                ". Known groups: " + string.Join(", ", BridgeToolGroups.Descriptions.Keys) + ", all.");
    }

    private static List<(MethodInfo Method, McpServerToolAttribute Attribute)> GetToolMethods()
    {
        return new[] { typeof(RevitMcpTools), typeof(ToolDiscoveryTools) }
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Select(method => new
            {
                Method = method,
                Attribute = method.GetCustomAttribute<McpServerToolAttribute>()
            })
            .Where(item => item.Attribute != null)
            .Select(item => (item.Method, item.Attribute!))
            .ToList();
    }

    private static HashSet<string> ParseExplicitNames(string? explicitToolNames)
    {
        if (string.IsNullOrWhiteSpace(explicitToolNames))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return new HashSet<string>(
            explicitToolNames.Split(
                new[] { ',', ';' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    private static string GetToolName(
        (MethodInfo Method, McpServerToolAttribute Attribute) item)
    {
        return string.IsNullOrWhiteSpace(item.Attribute.Name)
            ? item.Method.Name
            : item.Attribute.Name;
    }

    private static void ValidateNames(
        HashSet<string> requestedNames,
        IReadOnlyList<(MethodInfo Method, McpServerToolAttribute Attribute)> methods)
    {
        var available = new HashSet<string>(
            methods.Select(GetToolName),
            StringComparer.OrdinalIgnoreCase);
        var unknown = requestedNames.Where(name => !available.Contains(name)).ToList();

        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                "Unknown RevitMCP tool name(s): " + string.Join(", ", unknown));
        }
    }
}

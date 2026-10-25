using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Plans;

/// <summary>
/// One bridge tool that sends to several add-in tools: which ones, and (when an argument picks it) which value
/// routes to which.
/// </summary>
public sealed class BridgeToolRoute
{
    public BridgeToolRoute(string bridgeTool, string? routeArgument, IReadOnlyDictionary<string, string> byValue, params string[] extraTargets)
    {
        BridgeTool = bridgeTool;
        RouteArgument = routeArgument;
        ByValue = new Dictionary<string, string>(byValue.ToDictionary(p => p.Key, p => p.Value), StringComparer.OrdinalIgnoreCase);
        Targets = byValue.Values.Concat(extraTargets).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>The name the agent sees (RevitMCP.Bridge/RevitMcpTools.cs).</summary>
    public string BridgeTool { get; }

    /// <summary>The argument whose value picks the add-in tool (entity, target, mode …); null when the bridge decides otherwise.</summary>
    public string? RouteArgument { get; }

    /// <summary>Route argument value (case-insensitive) → add-in tool.</summary>
    public IReadOnlyDictionary<string, string> ByValue { get; }

    /// <summary>Every add-in tool the bridge tool may send to.</summary>
    public IReadOnlyList<string> Targets { get; }
}

/// <summary>
/// The bridge's combined tools (H10): an agent only sees <c>revit_set_parameters_bulk</c>, <c>revit_rename</c>,
/// <c>revit_delete</c> …, while the add-in registers and dispatches <c>revit_set_view_parameters_bulk</c>,
/// <c>revit_rename_sheets</c>, <c>revit_delete_elements</c> …. Plans and jobs accept the bridge names through this
/// table. A test scans the bridge for methods that send to more than their own name and checks each against it,
/// so the table cannot drift from the bridge. Pure.
/// </summary>
public static class BridgeToolRoutes
{
    private static readonly Dictionary<string, BridgeToolRoute> Routes = new[]
    {
        Route("revit_export_list_to_excel", "kind",
            ("views", "revit_export_view_list_to_excel"), ("sheets", "revit_export_sheet_list_to_excel"),
            ("schedules", "revit_export_schedule_list_to_excel")),
        Route("revit_export_issues", "format",
            ("json", "revit_export_issues_json"), ("excel", "revit_export_issues_excel"),
            ("markdown", "revit_export_issues_markdown"), ("html_dashboard", "revit_export_issues_html_dashboard")),
        // circuitId > 0 picks the single tool, which has the bridge's own name, so a job step keeps it as is.
        new BridgeToolRoute("revit_estimate_circuit_length", null, new Dictionary<string, string>(),
            "revit_estimate_circuit_length", "revit_estimate_circuit_lengths"),
        Route("revit_preview_duplicate", "entity",
            ("sheets", "revit_preview_duplicate_sheets"), ("views", "revit_preview_duplicate_views"),
            ("familyTypes", "revit_preview_duplicate_family_types")),
        Route("revit_duplicate", "entity",
            ("sheets", "revit_duplicate_sheets"), ("views", "revit_duplicate_views"),
            ("familyTypes", "revit_duplicate_family_types")),
        Route("revit_preview_rename", "entity",
            ("views", "revit_preview_rename_views"), ("sheets", "revit_preview_rename_sheets")),
        Route("revit_rename", "entity",
            ("views", "revit_rename_views"), ("sheets", "revit_rename_sheets")),
        Route("revit_set_parameters_bulk", "entity",
            ("views", "revit_set_view_parameters_bulk"), ("sheets", "revit_set_sheet_parameters_bulk")),
        Route("revit_preview_delete", "target",
            ("views", "revit_preview_delete_views"), ("sheets", "revit_preview_delete_sheets"),
            ("elements", "revit_preview_delete_elements")),
        Route("revit_delete", "target",
            ("views", "revit_delete_views"), ("sheets", "revit_delete_sheets"), ("elements", "revit_delete_elements")),
        Route("revit_detect_clashes", "mode",
            ("hard", "revit_detect_hard_clashes"), ("clearance", "revit_detect_clearance_clashes")),
        Route("revit_get_adjacent_clash", "direction",
            ("next", "revit_get_next_clash"), ("previous", "revit_get_previous_clash"), ("prev", "revit_get_previous_clash")),
        Route("revit_manage_project_skill_override", "action",
            ("create", "revit_create_project_skill_override"), ("update", "revit_update_project_skill_override"),
            ("reset", "revit_reset_project_skill_override"))
    }.ToDictionary(r => r.BridgeTool, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<BridgeToolRoute> All => Routes.Values;

    public static BridgeToolRoute? Find(string? bridgeTool)
        => bridgeTool != null && Routes.TryGetValue(bridgeTool, out var route) ? route : null;

    /// <summary>
    /// The name the agent knows an add-in tool by: the combined bridge tool that routes to it, else the name itself.
    /// Issue reports said "revit_detect_clearance_clashes", which no client can call (2026-10-25 check).
    /// </summary>
    public static string PublicName(string addinTool)
        => Routes.Values.FirstOrDefault(r => r.Targets.Contains(addinTool, StringComparer.Ordinal))?.BridgeTool ?? addinTool;

    /// <summary>The add-in tools a name stands for: the route's targets for a combined bridge tool, else the name itself.</summary>
    public static IReadOnlyList<string> Expand(string tool)
        => Find(tool)?.Targets ?? new[] { tool };

    /// <summary>
    /// A job step that names a combined bridge tool → the add-in tool its route argument picks, with that argument
    /// removed (the add-in tools take the rest under the same names). False with <paramref name="error"/> when the
    /// argument is missing, not one of the route's values, or a placeholder (the tool must be known at submit time).
    /// Not applicable (false, no error) for a name that is no combined tool or that the bridge routes without an argument.
    /// </summary>
    public static bool TryResolveStep(string tool, JObject arguments, out string resolvedTool, out JObject resolvedArguments, out string? error)
    {
        resolvedTool = tool;
        resolvedArguments = arguments;
        error = null;
        var route = Find(tool);
        if (route?.RouteArgument == null) return false;

        var property = arguments.Properties().FirstOrDefault(p => string.Equals(p.Name, route.RouteArgument, StringComparison.OrdinalIgnoreCase));
        var value = PlanArgs.String(property?.Value).Trim();
        var expected = string.Join(" | ", route.ByValue.Keys);
        if (value.Length == 0)
        {
            error = $"{tool} needs arguments.{route.RouteArgument} = {expected}.";
            return false;
        }
        if (value.Contains("{{"))
        {
            error = $"{tool}: arguments.{route.RouteArgument} must be a literal value ({expected}), not a placeholder.";
            return false;
        }
        if (!route.ByValue.TryGetValue(value, out var target))
        {
            error = $"{tool}: arguments.{route.RouteArgument} '{value}' is not one of {expected}.";
            return false;
        }

        resolvedTool = target;
        resolvedArguments = (JObject)arguments.DeepClone();
        resolvedArguments.Remove(property!.Name);
        return true;
    }

    private static BridgeToolRoute Route(string bridgeTool, string routeArgument, params (string Value, string Tool)[] byValue)
        => new(bridgeTool, routeArgument, byValue.ToDictionary(p => p.Value, p => p.Tool, StringComparer.OrdinalIgnoreCase));
}

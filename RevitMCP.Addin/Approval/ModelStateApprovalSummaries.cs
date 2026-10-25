using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>Approval summaries of the model-state write tools (#109); null for every other tool.</summary>
public static class ModelStateApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request) => request.ToolName switch
    {
        "revit_set_phase" => SetPhase(request),
        "revit_set_view_phase" => SetViewPhase(request),
        _ => null
    };

    private static string SetPhase(McpToolRequest request)
    {
        var args = request.Arguments;
        var target = ToolArguments.GetBool(args, "useSelection")
            ? "the selected elements"
            : $"{ToolArguments.GetLongArray(args, "elementIds").Distinct().Count()} element(s)";
        var parts = new List<string>();
        var created = ToolArguments.GetString(args, "phaseCreated").Trim();
        var demolished = ToolArguments.GetString(args, "phaseDemolished").Trim();
        if (created.Length > 0) parts.Add($"Phase Created to '{created}'");
        if (demolished.Length > 0)
            parts.Add(string.Equals(demolished, "none", StringComparison.OrdinalIgnoreCase) || demolished == "-"
                ? "clear Phase Demolished (not demolished)"
                : $"Phase Demolished to '{demolished}'");
        var what = parts.Count > 0 ? string.Join(" and ", parts) : "the phases";
        var atomic = ToolArguments.GetBool(args, "atomic", true) ? "all or nothing" : "element by element";
        return $"Set {what} on {target}{DocumentSuffix(request)} ({atomic}, one undo step).";
    }

    private static string SetViewPhase(McpToolRequest request)
    {
        var args = request.Arguments;
        var views = ToolArguments.GetLongArray(args, "viewIds").Distinct().Count();
        var parts = new List<string>();
        var phase = ToolArguments.GetString(args, "phase").Trim();
        var filter = ToolArguments.GetString(args, "phaseFilter").Trim();
        if (phase.Length > 0) parts.Add($"phase '{phase}'");
        if (filter.Length > 0) parts.Add($"phase filter '{filter}'");
        var what = parts.Count > 0 ? string.Join(" and ", parts) : "the phase settings";
        return $"Set {what} on {views} view(s){DocumentSuffix(request)} (one undo step).";
    }

    private static string DocumentSuffix(McpToolRequest request)
        => ToolArguments.GetString(request.Arguments, "document") is { Length: > 0 } doc ? $" in '{doc}'" : string.Empty;
}

using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>Approval summary of the clash fix write tool; null for every other tool.</summary>
public static class ClashFixApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request) => request.ToolName switch
    {
        "revit_apply_clash_fixes" => ApplyClashFixes(request),
        _ => null
    };

    private static string ApplyClashFixes(McpToolRequest request)
    {
        var args = request.Arguments;
        var fixes = args.TryGetValue("fixes", out var raw) ? raw as JArray : null;
        var count = fixes?.Count ?? 0;
        var elements = fixes?.OfType<JObject>().Select(f => f.Value<long?>("elementId")).Where(id => id != null).Distinct().Count() ?? 0;
        var longest = fixes?.OfType<JObject>()
            .Select(f => Math.Sqrt(Math.Pow(f.Value<double?>("deltaXmm") ?? 0, 2) + Math.Pow(f.Value<double?>("deltaYmm") ?? 0, 2) + Math.Pow(f.Value<double?>("deltaZmm") ?? 0, 2)))
            .DefaultIfEmpty(0).Max() ?? 0;
        var keep = new List<string>();
        if (ToolArguments.GetBool(args, "keepIfStillClashing")) keep.Add("keeps moves that still clash");
        if (ToolArguments.GetBool(args, "keepIfNewClashes")) keep.Add("keeps moves that create new clashes");
        var policy = keep.Count > 0 ? string.Join(", ", keep) : "each move is undone if it still clashes, creates new clashes or pulls runs apart";
        var document = ToolArguments.GetString(args, "document") is { Length: > 0 } doc ? $" in '{doc}'" : string.Empty;
        return $"Fix {count} clash(es) by moving {elements} element(s) up to {Math.Round(longest)} mm{document} ({policy}; one undo step).";
    }
}

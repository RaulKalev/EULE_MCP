using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>Approval summaries of the line tools (#140), for ApprovalSummaryBuilder's default case.</summary>
public static class AnnotationApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request)
    {
        switch (request.ToolName)
        {
            case "revit_set_line_geometry":
            {
                request.Arguments.TryGetValue("lines", out var raw);
                var lines = raw as JArray ?? (raw is string s ? ToolArguments.TryParseJArray(s) : null);
                var style = ToolArguments.GetString(request.Arguments, "lineStyle");
                return $"Change the endpoints of {lines?.Count ?? 0} line(s) in place" +
                       (string.IsNullOrWhiteSpace(style) ? "." : $" and set line style '{style}'.");
            }
            case "revit_create_rectangle":
            {
                var text = ToolArguments.GetString(request.Arguments, "text");
                return "Draw a rectangle of 4 detail lines" +
                       (string.IsNullOrEmpty(text) ? string.Empty : $" with the text '{text}'") +
                       (ToolArguments.GetBool(request.Arguments, "asGroup") ? " as a detail group." : ".");
            }
            default:
                return null;
        }
    }
}

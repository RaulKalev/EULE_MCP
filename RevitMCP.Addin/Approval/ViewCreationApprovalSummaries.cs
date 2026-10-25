using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Tools.Views;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of the view creation write tools (#104), for ApprovalSummaryBuilder's default case. The tools
/// also build a live summary (IApprovalSummaryTool) that knows the model; this one works from the request alone.
/// </summary>
public static class ViewCreationApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request)
    {
        switch (request.ToolName)
        {
            case "revit_create_views":
            {
                var parsed = ViewCreateSpecParser.Parse(request.Arguments, out var error);
                return parsed == null ? $"Create views: the request is invalid ({error})." : ViewCreatePlanner.Summarize(parsed);
            }
            case "revit_set_view_extent":
                return ViewCreationApprovalSummaryText.SetViewExtent(request, null, null);
            default:
                return null;
        }
    }
}

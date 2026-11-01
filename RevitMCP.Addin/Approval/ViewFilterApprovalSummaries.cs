using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of the view filter write tools (#134), for ApprovalSummaryBuilder's default case. Delete, apply and
/// copy also build a live summary (IApprovalSummaryTool) that knows the model; this one works from the request alone.
/// </summary>
public static class ViewFilterApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request)
    {
        switch (request.ToolName)
        {
            case "revit_create_view_filters":
            {
                var parsed = ViewFilterRequestParser.ParseCreate(request.Arguments, out var error);
                return parsed == null ? $"Create view filters: the request is invalid ({error})." : ViewFilterSummaries.Create(parsed);
            }
            case "revit_edit_view_filters":
            {
                var parsed = ViewFilterRequestParser.ParseEdit(request.Arguments, out var error);
                return parsed == null ? $"Edit view filters: the request is invalid ({error})." : ViewFilterSummaries.Edit(parsed);
            }
            case "revit_delete_view_filters":
            {
                var parsed = ViewFilterRequestParser.ParseDelete(request.Arguments, out var error);
                return parsed == null ? $"DESTRUCTIVE: Delete view filters: the request is invalid ({error}). Manual approval required." : ViewFilterSummaries.Delete(parsed);
            }
            case "revit_apply_view_filters":
            {
                var parsed = ViewFilterRequestParser.ParseApply(request.Arguments, out var error);
                return parsed == null ? $"Apply view filters: the request is invalid ({error})." : ViewFilterSummaries.Apply(parsed);
            }
            case "revit_copy_view_filters":
            {
                var parsed = ViewFilterRequestParser.ParseCopy(request.Arguments, out var error);
                return parsed == null ? $"Copy view filters: the request is invalid ({error})." : ViewFilterSummaries.Copy(parsed);
            }
            default:
                return null;
        }
    }
}

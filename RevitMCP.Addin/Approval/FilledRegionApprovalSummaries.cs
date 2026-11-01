using RevitMCP.Addin.FilledRegions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of the filled region write tools (#135), for ApprovalSummaryBuilder's default case. The tools also
/// build a live summary (IApprovalSummaryTool) that knows the model; this one works from the request alone.
/// </summary>
public static class FilledRegionApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request)
    {
        switch (request.ToolName)
        {
            case "revit_create_filled_regions":
            {
                var parsed = FilledRegionSpecParser.ParseCreate(request.Arguments, out var error);
                return parsed == null ? $"Create filled regions: the request is invalid ({error})." : FilledRegionSummaries.Create(parsed);
            }
            case "revit_edit_filled_regions":
            {
                var parsed = FilledRegionEditParser.ParseEdits(request.Arguments, out var error);
                return parsed == null ? $"Edit filled regions: the request is invalid ({error})." : FilledRegionSummaries.Edit(parsed);
            }
            case "revit_set_filled_region_types":
            {
                var parsed = FilledRegionEditParser.ParseTypes(request.Arguments, out var error);
                return parsed == null ? $"Set filled region types: the request is invalid ({error})." : FilledRegionSummaries.Types(parsed);
            }
            default:
                return null;
        }
    }
}

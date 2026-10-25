using RevitMCP.Addin.Mep;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of the MEP routing write tools (#106), built from the arguments alone. The create tool builds a
/// better one from the live model (CreateMepRunTool implements IApprovalSummaryTool: the type's real name and the level);
/// this is the fallback, chained into ApprovalSummaryBuilder's default case.
/// </summary>
public static class MepApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request)
    {
        switch (request.ToolName)
        {
            case "revit_create_mep_run":
            {
                var errors = new List<string>();
                var spec = MepRunSpec.Parse(request.Arguments, errors, out _);
                if (spec == null)
                    return $"Create a cable tray or conduit run (the arguments do not describe a valid run: {errors.FirstOrDefault()}).";
                // Without the model the level elevation is unknown; lengths of level-based points do not depend on it.
                var plan = MepRunPlanner.Plan(spec.ResolvePoints(0), spec.MinSegmentMm, spec.AutoFittings);
                return MepApprovalText.CreateRun(spec.Kind, spec.Type, plan.Segments.Count, plan.TotalLengthMm, plan.ElbowCount,
                    spec.ConnectStart, spec.ConnectEnd, spec.SnapMm, spec.Level.Length > 0 ? spec.Level : null);
            }
            case "revit_connect_mep_curves":
            {
                var a = MepArgs.Long(request.Arguments, "elementIdA");
                var b = MepArgs.Long(request.Arguments, "elementIdB");
                var mode = MepArgs.String(request.Arguments, "mode", "auto").Trim().ToLowerInvariant();
                return MepApprovalText.Connect(a, b, mode.Length == 0 ? "auto" : mode);
            }
            default:
                return null;
        }
    }
}

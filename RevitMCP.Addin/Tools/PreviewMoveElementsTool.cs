using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewMoveElementsTool : IRevitMcpTool
{
    public string Name => "revit_preview_move_elements";

    public string Description =>
        "Previews moving existing elements — onto exact model coordinates or by a displacement — without " +
        "changing anything. Same arguments as revit_move_elements. Each moves entry is one of: " +
        "{elementId, targetXmm, targetYmm, targetZmm} (absolute, needs a LocationPoint; an omitted axis keeps " +
        "its value), {elementId, deltaXmm, deltaYmm, deltaZmm} (displacement along the model axes), or " +
        "{elementId, deltaRightMm, deltaUpMm} (displacement along the owner view's axes — for Detail Items, " +
        "detail lines, text and other view-specific elements). Optional expectedXmm/Ymm/Zmm are a concurrency " +
        "check against positionToleranceMm (default 1.0). Optional: viewId, skipPinned (default true). Returns " +
        "per element the mode, the current and target point, the translation and distance in mm, the owner view " +
        "and its axes in model coordinates, whether it is pinned or a group member, and whether it can move. " +
        "Absolute targets on elements without a LocationPoint are reported as unsupported rather than guessed " +
        "at from a bounding box. Run this before revit_move_elements.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var warnings = new List<string>();
        var options = MoveElementsService.ParseOptions(request.Arguments, warnings);

        request.Arguments.TryGetValue("moves", out var rawMoves);
        var moves = MoveElementsMath.ParseMoves(rawMoves, warnings, out var error);
        if (moves == null)
            return Task.FromResult(Fail(request, error!));

        var plans = MoveElementsService.BuildPlans(doc, moves, options, cancellationToken);
        var summary = MoveElementsMath.Summarise(plans);
        var failures = MoveElementsMath.CountFailures(plans);

        foreach (var plan in plans)
        {
            if (plan.Status is not (MoveStatus.Ready or MoveStatus.AlreadyThere) && plan.Reason != null)
                warnings.Add($"Element {plan.ElementId}: {plan.Reason}");
        }

        var ready = plans.Count(plan => plan.CanMove);
        var totalTravelMm = plans.Where(plan => plan.CanMove).Sum(plan => plan.DistanceMm);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {ready} of {plans.Count} element(s) would move" +
                      $"{(summary.Skipped.Count > 0 ? $", {summary.Skipped.Count} already there" : string.Empty)}" +
                      $"{(failures > 0 ? $", {failures} blocked" : string.Empty)}." +
                      (failures > 0 && options.Atomic
                          ? " With atomic=true the whole batch would be rejected."
                          : string.Empty),
            Data = new
            {
                total = plans.Count,
                canMove = ready,
                blocked = failures,
                atomic = options.Atomic,
                skipPinned = options.SkipPinned,
                positionToleranceMm = options.PositionToleranceMm,
                totalTravelMm = MoveElementsMath.Round(totalTravelMm),
                elementIds = MoveElementsPayload.Summarise(summary),
                moves = plans.Select(MoveElementsPayload.Describe).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

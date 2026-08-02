using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewAlignInViewTool : IRevitMcpTool
{
    public string Name => "revit_preview_align_in_view";

    public string Description =>
        "Previews lining elements up in a view — tags, text notes, detail lines, dimensions, " +
        "viewports on a sheet, model elements — without changing anything. Same arguments as " +
        "revit_align_in_view: elementIds or useSelection, mode (left|right|top|bottom|" +
        "centerVertical|centerHorizontal|distributeHorizontal|distributeVertical), alignTo, " +
        "referenceElementId, spread, spacingMm, anchor, viewId. Returns the slide each element " +
        "would make, in mm and by direction, plus how far out of line they are now.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var warnings = new List<string>();

        var view = ViewAlignmentRequest.ResolveView(uidoc, request.Arguments, warnings, out var error);
        if (view == null)
            return Task.FromResult(Fail(request, error!));

        var elements = ViewAlignmentRequest.ResolveElements(uidoc, view, request.Arguments, warnings, out error);
        if (elements == null)
            return Task.FromResult(Fail(request, error!));

        var referenceIndex = ViewAlignmentRequest.FindReferenceIndex(request.Arguments, elements, warnings);
        var options = ViewAlignmentRequest.ParseOptions(
            request.Arguments, elements.Count, referenceIndex, warnings, out error);
        if (options == null)
            return Task.FromResult(Fail(request, error!));

        var service = new ViewAlignmentService(view, options);
        var targets = new List<ViewAlignmentTarget>(elements.Count);
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add(service.Measure(element));
        }

        var plan = ViewAlignmentPlanner.Build(targets, options, warnings);

        var willMove = 0;
        var alreadyInLine = 0;
        var blocked = 0;
        var proposals = new List<object>();
        foreach (var move in plan.Moves)
        {
            if (!move.Target.CanAlign)
            {
                blocked++;
                warnings.Add($"{move.Target.ElementName} ({move.Target.ElementId}): {move.Target.BlockedReason}");
            }
            else if (move.IsNegligible)
            {
                alreadyInLine++;
            }
            else
            {
                willMove++;
                if (move.Target.Pinned)
                {
                    warnings.Add(
                        $"{move.Target.ElementName} ({move.Target.ElementId}) is pinned — " +
                        "unpin it or it will be skipped.");
                }
            }

            proposals.Add(ViewAlignmentPayload.Describe(move, options, willMove: true));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {ViewAlignmentPayload.Summarise(options, elements.Count)} in " +
                      $"'{view.Name}' — {willMove} would move, {alreadyInLine} already in line" +
                      $"{(blocked > 0 ? $", {blocked} could not be measured" : string.Empty)}.",
            Data = new
            {
                mode = options.Mode,
                axis = ViewAlignmentPayload.Axis(options.Mode),
                alignTo = options.Reference,
                anchor = options.Anchor,
                spread = options.IsDistribute ? options.Spread : null,
                spacingMm = options.SpacingFt.HasValue
                    ? Math.Round(ViewAlignmentRequest.FtToMm(options.SpacingFt.Value), 1)
                    : (double?)null,
                viewId = view.Id.Value,
                viewName = view.Name,
                total = elements.Count,
                willMove,
                alreadyInLine,
                blocked,
                misalignmentMm = Math.Round(ViewAlignmentRequest.FtToMm(plan.MisalignmentFt), 2),
                resultingSpacingMm = plan.StepFt.HasValue
                    ? Math.Round(ViewAlignmentRequest.FtToMm(plan.StepFt.Value), 1)
                    : (double?)null,
                proposals
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

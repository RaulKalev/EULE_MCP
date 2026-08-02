using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class AlignInViewTool : IRevitMcpTool
{
    public string Name => "revit_align_in_view";

    public string Description =>
        "Lines elements up in a view the way a drafting Align tool does: tags, text notes, detail " +
        "lines, dimensions, viewports on a sheet, and model elements. Requires approval. Required: " +
        "elementIds or useSelection, and mode — left, right, top, bottom, centerVertical (centres " +
        "on one vertical line), centerHorizontal (centres on one horizontal line), " +
        "distributeHorizontal, or distributeVertical. Optional: alignTo (extreme|first|last|min|" +
        "max|average, default extreme), referenceElementId (align everything to this one), spread " +
        "(centers|gaps) and spacingMm for the distribute modes, anchor (auto|boundingBox|origin), " +
        "viewId (default the active view). Everything moves in the plane of the view only. Run " +
        "revit_preview_align_in_view first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
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

        var doc = uidoc.Document;
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

        // Everything is measured before anything moves: each move changes what the next measurement
        // would see, and a distribute in particular is only stable if the span is read once.
        var service = new ViewAlignmentService(view, options);
        var targets = new List<ViewAlignmentTarget>(elements.Count);
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            targets.Add(service.Measure(element));
        }

        var plan = ViewAlignmentPlanner.Build(targets, options, warnings);
        var axis = service.AxisFor(options.Mode);

        var results = new List<object>();
        var moved = 0;
        var skipped = 0;
        var failed = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Align In View");
        transaction.Start();

        foreach (var move in plan.Moves)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = move.Target;

            if (!target.CanAlign)
            {
                failed++;
                warnings.Add($"{target.ElementName} ({target.ElementId}): {target.BlockedReason}");
                results.Add(ViewAlignmentPayload.Describe(move, options, false, "blocked"));
                continue;
            }

            if (move.IsNegligible)
            {
                skipped++;
                results.Add(ViewAlignmentPayload.Describe(move, options, false, "alreadyAligned"));
                continue;
            }

            var element = doc.GetElement(new ElementId(target.ElementId));
            if (element == null)
            {
                failed++;
                warnings.Add($"Element {target.ElementId} disappeared before it could be moved.");
                results.Add(ViewAlignmentPayload.Describe(move, options, false, "missing"));
                continue;
            }

            if (element.Pinned)
            {
                skipped++;
                warnings.Add($"{target.ElementName} ({target.ElementId}) is pinned — unpin it to align it.");
                results.Add(ViewAlignmentPayload.Describe(move, options, false, "pinned"));
                continue;
            }

            var translation = axis.Multiply(move.OffsetFt);

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                MoveInView(doc, element, translation);
                subTransaction.Commit();
                moved++;
                results.Add(ViewAlignmentPayload.Describe(move, options, true, "aligned"));
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();

                failed++;
                warnings.Add($"Failed to align '{target.ElementName}' ({target.ElementId}): {ex.Message}");
                results.Add(ViewAlignmentPayload.Describe(move, options, false, "failed"));
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = moved > 0 || skipped > 0,
            Message = $"{ViewAlignmentPayload.Summarise(options, elements.Count)} in '{view.Name}': " +
                      $"moved {moved}, {skipped} already in line" +
                      $"{(failed > 0 ? $", {failed} failed" : string.Empty)}.",
            Data = new
            {
                mode = options.Mode,
                axis = ViewAlignmentPayload.Axis(options.Mode),
                alignTo = options.Reference,
                anchor = options.Anchor,
                spread = options.IsDistribute ? options.Spread : null,
                viewId = view.Id.Value,
                viewName = view.Name,
                total = elements.Count,
                aligned = moved,
                alreadyAligned = skipped,
                failed,
                misalignmentMm = Math.Round(ViewAlignmentRequest.FtToMm(plan.MisalignmentFt), 2),
                resultingSpacingMm = plan.StepFt.HasValue
                    ? Math.Round(ViewAlignmentRequest.FtToMm(plan.StepFt.Value), 1)
                    : (double?)null,
                results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// Moves one element by <paramref name="translation"/>, which always lies in the view plane.
    /// Tags and text notes get a direct-property fallback: Revit refuses to transform some of them
    /// (a tag whose host is in a link, for one), but setting the head or insertion point works.
    /// </summary>
    private static void MoveInView(Document doc, Element element, XYZ translation)
    {
        try
        {
            ElementTransformUtils.MoveElement(doc, element.Id, translation);
            return;
        }
        catch (Exception) when (element is IndependentTag or TextNote)
        {
        }

        switch (element)
        {
            case IndependentTag tag:
                tag.TagHeadPosition = tag.TagHeadPosition + translation;
                break;
            case TextNote note:
                note.Coord = note.Coord + translation;
                break;
        }
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class AlignElementsTool : IRevitMcpTool
{
    private const int MaxAlternates = 2;

    /// <summary>Moves under this are numerical noise — the element is already flush.</summary>
    private const double NegligibleMoveMm = 0.5;

    public string Name => "revit_align_elements";

    public string Description =>
        "Moves elements until they sit against the nearest wall, ceiling, floor, or whatever is " +
        "closest. Requires approval. Required: elementIds or useSelection, and surface " +
        "(wall|ceiling|floor|nearest). Surfaces are found by ray casting and classified by face " +
        "orientation, not category, so walls and slabs that a linked IFC mapped onto Generic Models " +
        "are still found. Optional: searchRadiusMm (default 3000), gapMm (clearance to leave; " +
        "negative embeds), scope (both|links|host), targetCategories, linkInstanceIds, " +
        "rotateToSurface (turn the element square to a wall), angleToleranceDegrees (default 30), " +
        "horizontalSamples, searchViewId. Run revit_preview_align_elements first.";

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

        var options = AlignmentRequest.ParseOptions(request.Arguments, warnings, out var error);
        if (options == null)
            return Task.FromResult(Fail(request, error!));

        var elements = AlignmentRequest.ResolveElements(uidoc, request.Arguments, warnings, out error);
        if (elements == null)
            return Task.FromResult(Fail(request, error!));

        var view = AlignmentRequest.ResolveSearchView(uidoc, request.Arguments, warnings, out error);
        if (view == null)
            return Task.FromResult(Fail(request, error!));

        foreach (var element in elements)
            options.ExcludedElementIds.Add(element.Id.Value);

        // Every plan is resolved before anything moves: a move changes the geometry the rays see,
        // so resolving mid-transaction would make later elements chase the ones already moved.
        var service = new AlignmentService(doc, view, options);
        var plans = new List<AlignmentPlan>();
        var rotations = new Dictionary<long, double?>();
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = service.Resolve(element);
            plans.Add(plan);
            rotations[plan.ElementId] = AlignmentPayload.RotationRadians(doc, plan, options);
        }

        var results = new List<object>();
        var moved = 0;
        var rotated = 0;
        var skipped = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Align Elements");
        transaction.Start();

        foreach (var plan in plans)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!plan.CanAlign)
            {
                warnings.Add($"{plan.ElementName} ({plan.ElementId}): {plan.BlockedReason}");
                results.Add(AlignmentPayload.Describe(plan, options, MaxAlternates, false, null, "blocked"));
                continue;
            }

            var element = doc.GetElement(new ElementId(plan.ElementId));
            if (element == null)
            {
                warnings.Add($"Element {plan.ElementId} disappeared before it could be moved.");
                results.Add(AlignmentPayload.Describe(plan, options, MaxAlternates, false, null, "missing"));
                continue;
            }

            if (element.Pinned)
            {
                warnings.Add($"{plan.ElementName} ({plan.ElementId}) is pinned — unpin it to align it.");
                results.Add(AlignmentPayload.Describe(plan, options, MaxAlternates, false, null, "pinned"));
                continue;
            }

            var chosen = plan.Chosen!;
            var travelMm = AlignmentRequest.FtToMm(chosen.TravelDistanceFt);
            var rotation = rotations[plan.ElementId];

            if (Math.Abs(travelMm) < NegligibleMoveMm && rotation == null)
            {
                skipped++;
                results.Add(AlignmentPayload.Describe(plan, options, MaxAlternates, false, null, "alreadyFlush"));
                continue;
            }

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                // Rotation comes first, about the element's own centre so the ray distance stays
                // valid, and the travel is then recomputed: turning an element changes how far it
                // reaches toward the surface.
                var didRotate = false;
                if (rotation.HasValue)
                {
                    var pivot = element.get_BoundingBox(null) is { } box
                        ? (box.Min + box.Max) * 0.5
                        : plan.Origin;
                    var axis = Line.CreateBound(pivot, pivot + XYZ.BasisZ);
                    ElementTransformUtils.RotateElement(doc, element.Id, axis, rotation.Value);
                    didRotate = true;

                    travelMm = AlignmentRequest.FtToMm(RecomputeTravelFt(element, chosen, options));
                }

                if (Math.Abs(travelMm) >= NegligibleMoveMm)
                {
                    var translation = AlignmentService.ToXyz(chosen.Direction.Normalized())
                        .Multiply(AlignmentRequest.MmToFt(travelMm));
                    ElementTransformUtils.MoveElement(doc, element.Id, translation);
                }

                subTransaction.Commit();

                moved++;
                if (didRotate) rotated++;
                results.Add(AlignmentPayload.Describe(plan, options, MaxAlternates, true, rotation, "aligned"));
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();

                warnings.Add($"Failed to align '{plan.ElementName}' ({plan.ElementId}): {ex.Message}");
                results.Add(AlignmentPayload.Describe(plan, options, MaxAlternates, false, rotation, "failed"));
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = moved > 0 || skipped > 0,
            Message = $"Aligned {moved}/{plans.Count} element(s) to a '{options.Surface}' surface" +
                      $"{(rotated > 0 ? $", rotated {rotated}" : string.Empty)}" +
                      $"{(skipped > 0 ? $", {skipped} already flush" : string.Empty)}.",
            Data = new
            {
                surface = options.Surface,
                searchViewId = view.Id.Value,
                searchViewName = view.Name,
                aligned = moved,
                alreadyFlush = skipped,
                rotated,
                failed = plans.Count - moved - skipped,
                results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// Re-measures how far a rotated element still has to travel. The distance to the surface is
    /// unchanged — the rotation pivots about the element's own centre — but its reach that way is not.
    /// </summary>
    private static double RecomputeTravelFt(
        Element element,
        AlignmentCandidate chosen,
        AlignmentOptions options)
    {
        var box = element.get_BoundingBox(null);
        if (box == null)
            return chosen.TravelDistanceFt;

        var halfExtents = new Vec3(
            Math.Abs(box.Max.X - box.Min.X) * 0.5,
            Math.Abs(box.Max.Y - box.Min.Y) * 0.5,
            Math.Abs(box.Max.Z - box.Min.Z) * 0.5);

        var reach = AlignmentMath.SupportDistance(halfExtents, chosen.Direction);
        return AlignmentMath.TravelDistance(chosen.HitDistanceFt, reach, options.GapFt);
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

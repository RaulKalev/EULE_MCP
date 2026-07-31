using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewAlignElementsTool : IRevitMcpTool
{
    /// <summary>How many rejected candidates to return per element before it becomes noise.</summary>
    private const int MaxAlternates = 3;

    public string Name => "revit_preview_align_elements";

    public string Description =>
        "Previews moving elements against the nearest wall, ceiling, floor, or whatever is closest, " +
        "without changing the model. Same arguments as revit_align_elements: elementIds or " +
        "useSelection, surface (wall|ceiling|floor|nearest), searchRadiusMm, gapMm, scope, " +
        "targetCategories, linkInstanceIds, rotateToSurface, angleToleranceDegrees, searchViewId. " +
        "Returns the surface each element would land on — its kind, the model and category it came " +
        "from, the current gap, and the move distance — plus the other surfaces found in range.";

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

        var service = new AlignmentService(uidoc.Document, view, options);
        var proposals = new List<object>();
        var canAlign = 0;
        var alreadyFlush = 0;

        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var plan = service.Resolve(element);
            if (plan.CanAlign)
            {
                canAlign++;
                if (Math.Abs(AlignmentRequest.FtToMm(plan.Chosen!.TravelDistanceFt)) < 0.5)
                    alreadyFlush++;
            }
            else
            {
                warnings.Add($"{plan.ElementName} ({plan.ElementId}): {plan.BlockedReason}");
            }

            var rotation = AlignmentPayload.RotationRadians(uidoc.Document, plan, options);
            proposals.Add(AlignmentPayload.Describe(plan, options, MaxAlternates, willMove: true, rotation));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {canAlign} of {elements.Count} element(s) can be aligned to a " +
                      $"'{options.Surface}' surface ({alreadyFlush} already flush).",
            Data = new
            {
                surface = options.Surface,
                searchViewId = view.Id.Value,
                searchViewName = view.Name,
                searchRadiusMm = Math.Round(AlignmentRequest.FtToMm(options.SearchRadiusFt), 1),
                gapMm = Math.Round(AlignmentRequest.FtToMm(options.GapFt), 1),
                total = elements.Count,
                canAlign,
                blocked = elements.Count - canAlign,
                alreadyFlush,
                proposals
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

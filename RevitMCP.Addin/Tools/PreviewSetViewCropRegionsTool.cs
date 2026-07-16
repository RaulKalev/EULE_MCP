using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewSetViewCropRegionsTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_view_crop_regions";
    public string Description =>
        "Previews copying a reference view's crop region to target views without changing the model. " +
        "Required: referenceViewId and targetViewIds. Supported views: floor, ceiling, engineering and area plans, " +
        "sections, elevations, details, and 3D views. A single non-split custom crop shape is copied exactly; " +
        "split or multi-loop crops fall back to the rectangular crop box. The target crop is activated and its " +
        "visibility is copied from the reference view.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var referenceViewId = ToolArguments.GetLong(request.Arguments, "referenceViewId", 0L);
        var targetViewIds = ToolArguments.GetLongArray(request.Arguments, "targetViewIds");
        if (referenceViewId <= 0)
            return Task.FromResult(Fail(request, "referenceViewId is required."));
        if (targetViewIds.Length == 0)
            return Task.FromResult(Fail(request, "targetViewIds is required."));

        var referenceView = doc.GetElement(new ElementId(referenceViewId)) as View;
        if (!ViewCropRegionToolSupport.IsCroppableView(referenceView))
            return Task.FromResult(Fail(request,
                "The reference view was not found or does not support crop regions."));

        ViewCropRegionToolSupport.CropRegionSnapshot snapshot;
        try
        {
            snapshot = ViewCropRegionToolSupport.Capture(referenceView!);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Could not read the reference crop region: {ex.Message}"));
        }

        var targets = new List<object>();
        var canApply = 0;
        foreach (var targetViewId in targetViewIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetView = doc.GetElement(new ElementId(targetViewId)) as View;
            var isReference = targetViewId == referenceViewId;
            var supported = ViewCropRegionToolSupport.IsCroppableView(targetView);
            var applicable = supported && !isReference;
            var targetShapeMode = "Rectangular";

            if (applicable)
            {
                canApply++;
                try
                {
                    var manager = targetView!.GetCropRegionShapeManager();
                    if (snapshot.CustomShape != null && manager.CanHaveShape)
                        targetShapeMode = "CustomSingleLoop";
                }
                catch
                {
                    targetShapeMode = "Rectangular";
                }
            }

            targets.Add(new
            {
                targetViewId,
                targetViewName = targetView?.Name,
                targetViewType = targetView?.ViewType.ToString(),
                canApply = applicable,
                resultingCropActive = applicable ? true : (bool?)null,
                resultingCropVisible = applicable ? snapshot.CropBoxVisible : (bool?)null,
                resultingShapeMode = applicable ? targetShapeMode : null,
                reason = applicable
                    ? "Crop region can be copied."
                    : isReference
                        ? "The reference view is not updated."
                        : targetView == null
                            ? "Target view was not found."
                            : "Target view type does not support crop regions."
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: crop region can be copied to {canApply} view(s).",
            Data = new
            {
                referenceViewId = referenceView!.Id.Value,
                referenceViewName = referenceView.Name,
                referenceViewType = referenceView.ViewType.ToString(),
                referenceCropActive = snapshot.CropBoxActive,
                referenceCropVisible = snapshot.CropBoxVisible,
                referenceShapeMode = snapshot.ShapeMode,
                referenceCropIsSplit = snapshot.IsSplit,
                splitOrMultiLoopFallsBackToRectangle = snapshot.CustomShape == null,
                requested = targetViewIds.Distinct().Count(),
                canApply,
                targets
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

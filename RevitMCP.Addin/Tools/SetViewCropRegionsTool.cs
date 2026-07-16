using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SetViewCropRegionsTool : IRevitMcpTool
{
    public string Name => "revit_set_view_crop_regions";
    public string Description =>
        "Copies a reference view's crop region to target views in one transaction. Requires approval. " +
        "Required: referenceViewId and targetViewIds. Supported views: floor, ceiling, engineering and area plans, " +
        "sections, elevations, details, and 3D views. A single non-split custom crop shape is copied exactly; " +
        "split or multi-loop crops fall back to the rectangular crop box. Existing target custom crops are removed, " +
        "the target crop is activated, and visibility is copied from the reference. " +
        "Use revit_preview_set_view_crop_regions first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
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

        var warnings = new List<string>();
        var results = new List<object>();
        var applied = 0;
        var targetIds = targetViewIds.Distinct().ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Set View Crop Regions");
        transaction.Start();

        foreach (var targetViewId in targetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetView = doc.GetElement(new ElementId(targetViewId)) as View;
            if (targetViewId == referenceViewId)
            {
                warnings.Add($"View {targetViewId} is the reference view and was skipped.");
                continue;
            }
            if (!ViewCropRegionToolSupport.IsCroppableView(targetView))
            {
                warnings.Add(targetView == null
                    ? $"Target view {targetViewId} was not found."
                    : $"View '{targetView.Name}' ({targetView.ViewType}) does not support crop regions.");
                continue;
            }

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                ViewCropRegionToolSupport.Apply(targetView!, snapshot);
                subTransaction.Commit();
                applied++;
                results.Add(new
                {
                    targetViewId = targetView!.Id.Value,
                    targetViewName = targetView.Name,
                    targetViewType = targetView.ViewType.ToString(),
                    cropActive = true,
                    cropVisible = snapshot.CropBoxVisible,
                    requestedShapeMode = snapshot.ShapeMode
                });
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();
                warnings.Add($"Failed to set crop region on '{targetView!.Name}': {ex.Message}");
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = applied > 0,
            Message = $"Copied crop region to {applied}/{targetIds.Length} target view(s).",
            Data = new
            {
                referenceViewId = referenceView!.Id.Value,
                referenceViewName = referenceView.Name,
                referenceShapeMode = snapshot.ShapeMode,
                referenceCropVisible = snapshot.CropBoxVisible,
                requested = targetIds.Length,
                applied,
                failedOrSkipped = targetIds.Length - applied,
                results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

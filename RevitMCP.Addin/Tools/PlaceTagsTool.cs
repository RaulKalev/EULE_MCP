#nullable disable

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tagging;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools
{
    public sealed class PlaceTagsTool : IRevitMcpTool
    {
        public string Name => "revit_place_tags";

        public string Description =>
            "Places SmartTags-compatible IndependentTags with the full intelligent placement pipeline. " +
            "Targets: elementIds, useSelection=true, or tagAllInView=true plus categoryId. " +
            "Tag type: tagTypeId, unique tagFamilyName/tagTypeName, or automatic category resolution. " +
            "Placement: direction (Right/Left/Up/Down), anchorPoint (nine positions), " +
            "attachedLengthMm, freeLengthMm, addLeader, leaderEndCondition, orientation, " +
            "rotationDegrees, detectElementRotation. Direction-specific types can be supplied as " +
            "leftTagTypeId/rightTagTypeId/upTagTypeId/downTagTypeId or auto-loaded with directionKeyword. " +
            "Collision avoidance defaults on and supports collisionGapMm and minimumOffsetMm. " +
            "skipAlreadyTagged defaults true. Created tags carry the original SmartTags-compatible " +
            "Extensible Storage marker. Requires approval and is reversible with Revit Undo.";

        public ToolPermission Permission => ToolPermission.RequiresApproval;
        public ToolCategory Category => ToolCategory.Documentation;

        public Task<McpToolResult> ExecuteAsync(
            UIApplication uiapp,
            McpToolRequest request,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
                return Task.FromResult(
                    TaggingToolSupport.Fail(request, "No active document."));

            var view = TaggingToolSupport.ResolveView(
                uidoc,
                doc,
                request.Arguments,
                out var error);
            if (view == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

            var elements = TaggingToolSupport.ResolveElements(
                uidoc,
                doc,
                view,
                request.Arguments,
                out error);
            if (elements.Count == 0)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

            var baseTagType = TaggingToolSupport.ResolveBaseTagType(
                doc,
                elements,
                request.Arguments,
                out error);
            if (baseTagType == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

            var options = TaggingToolSupport.ParseOptions(
                request.Arguments,
                out error);
            if (options == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));
            var directionTypes = TaggingToolSupport.ResolveDirectionTypes(
                doc,
                baseTagType,
                request.Arguments);

            SmartTagPlacementResult placementResult = null;
            var transactionResult = RevitTransactionRunner.Run(
                doc,
                "Revit MCP - Smart Tag Placement",
                () =>
                {
                    placementResult = new SmartTagPlacementService().Place(
                        doc,
                        view,
                        elements,
                        baseTagType,
                        directionTypes,
                        options,
                        cancellationToken);
                });
            stopwatch.Stop();

            if (!transactionResult.Success)
            {
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = transactionResult.Diagnostics.OriginalError ??
                              "Smart tag placement transaction failed.",
                    Data = new
                    {
                        transactionDiagnostics = transactionResult.Diagnostics
                    },
                    DurationMs = stopwatch.ElapsedMilliseconds
                });
            }

            placementResult = placementResult ?? new SmartTagPlacementResult();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = placementResult.PlacedCount > 0,
                Message = string.Format(
                    "Placed {0} smart tag(s) in view '{1}'; {2} already tagged, {3} collision fallback(s), {4} error(s).",
                    placementResult.PlacedCount,
                    view.Name,
                    placementResult.SkippedAlreadyTaggedCount,
                    placementResult.CollisionFallbackCount,
                    placementResult.Errors.Count),
                Data = new
                {
                    viewId = view.Id.Value,
                    viewName = view.Name,
                    placementResult.CandidateCount,
                    placementResult.PlacedCount,
                    placementResult.SkippedAlreadyTaggedCount,
                    placementResult.CollisionFallbackCount,
                    placementResult.CollisionDiagnostics,
                    items = placementResult.Items
                },
                Errors = placementResult.Errors,
                DurationMs = stopwatch.ElapsedMilliseconds
            });
        }
    }
}

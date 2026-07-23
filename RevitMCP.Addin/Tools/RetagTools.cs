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
    public sealed class PreviewRetagTool : IRevitMcpTool
    {
        public string Name => "revit_preview_retag";

        public string Description =>
            "Previews SmartTags-compatible retag/normalize adjustments without changing the model. " +
            "Optional viewId, tagIds, or referenced elementIds; with no filters, processes every " +
            "managed tag in the view. Placement settings match revit_place_tags: direction, " +
            "anchorPoint, attachedLengthMm, freeLengthMm, addLeader, leaderEndCondition, " +
            "orientation, rotationDegrees, detectElementRotation, collision settings, and minimumOffsetMm.";

        public ToolPermission Permission => ToolPermission.ReadOnly;
        public ToolCategory Category => ToolCategory.Documentation;

        public Task<McpToolResult> ExecuteAsync(
            UIApplication uiapp,
            McpToolRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(RetagToolExecutor.Execute(
                uiapp,
                request,
                cancellationToken,
                true));
        }
    }

    public sealed class RetagTool : IRevitMcpTool
    {
        public string Name => "revit_retag";

        public string Description =>
            "Applies the SmartTags-compatible retag/normalize workflow to managed tags. " +
            "Optional viewId, tagIds, or referenced elementIds; with no filters, normalizes every " +
            "managed tag in the view. Uses the same placement, leader, orientation, rotation, " +
            "self-collision exclusion, and deterministic collision fallback settings as revit_place_tags. " +
            "Requires approval and is reversible with Revit Undo. Use revit_preview_retag first.";

        public ToolPermission Permission => ToolPermission.RequiresApproval;
        public ToolCategory Category => ToolCategory.Documentation;

        public Task<McpToolResult> ExecuteAsync(
            UIApplication uiapp,
            McpToolRequest request,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(RetagToolExecutor.Execute(
                uiapp,
                request,
                cancellationToken,
                false));
        }
    }

    internal static class RetagToolExecutor
    {
        public static McpToolResult Execute(
            UIApplication uiapp,
            McpToolRequest request,
            CancellationToken cancellationToken,
            bool previewOnly)
        {
            var stopwatch = Stopwatch.StartNew();
            var uidoc = uiapp.ActiveUIDocument;
            var doc = uidoc?.Document;
            if (doc == null)
                return TaggingToolSupport.Fail(request, "No active document.");

            var view = TaggingToolSupport.ResolveView(
                uidoc,
                doc,
                request.Arguments,
                out var error);
            if (view == null)
                return TaggingToolSupport.Fail(request, error);

            var options = TaggingToolSupport.ParseOptions(
                request.Arguments,
                out error);
            if (options == null)
                return TaggingToolSupport.Fail(request, error);

            var tagIds = ToolArguments.GetLongArray(request.Arguments, "tagIds")
                .Select(value => new ElementId(value))
                .ToList();
            var elementIds = ToolArguments.GetLongArray(
                    request.Arguments,
                    "elementIds")
                .Select(value => new ElementId(value))
                .ToList();
            var tags = TagDiscoveryService.FindManagedTags(
                doc,
                view,
                tagIds,
                elementIds);
            if (tags.Count == 0)
                return TaggingToolSupport.Fail(
                    request,
                    "No SmartTags-compatible managed tags matched the request.");

            cancellationToken.ThrowIfCancellationRequested();
            var proposals = new TagAdjustmentService().Compute(
                doc,
                view,
                tags,
                options);

            if (previewOnly)
            {
                stopwatch.Stop();
                return BuildResult(
                    request,
                    view,
                    tags.Count,
                    proposals,
                    0,
                    0,
                    true,
                    stopwatch.ElapsedMilliseconds,
                    null);
            }

            var applied = 0;
            var failed = 0;
            var transactionResult = RevitTransactionRunner.Run(
                doc,
                "Revit MCP - Retag / Normalize",
                () =>
                {
                    foreach (var proposal in proposals)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            var tag = doc.GetElement(proposal.TagId) as IndependentTag;
                            if (tag == null)
                            {
                                failed++;
                                continue;
                            }
                            proposal.NewState.ApplyTo(tag);
                            applied++;
                        }
                        catch
                        {
                            failed++;
                        }
                    }
                });
            stopwatch.Stop();

            if (!transactionResult.Success)
            {
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = transactionResult.Diagnostics.OriginalError ??
                              "Retag transaction failed.",
                    Data = new
                    {
                        transactionDiagnostics = transactionResult.Diagnostics
                    },
                    DurationMs = stopwatch.ElapsedMilliseconds
                };
            }

            return BuildResult(
                request,
                view,
                tags.Count,
                proposals,
                applied,
                failed,
                false,
                stopwatch.ElapsedMilliseconds,
                null);
        }

        private static McpToolResult BuildResult(
            McpToolRequest request,
            View view,
            int totalTags,
            IList<TagAdjustmentProposal> proposals,
            int applied,
            int failed,
            bool previewOnly,
            long durationMilliseconds,
            object diagnostics)
        {
            var data = proposals.Select(proposal => new
            {
                tagId = proposal.TagId.Value,
                referencedElementId = proposal.ReferencedElementId.Value,
                reason = proposal.Reason,
                oldHead = Point(proposal.OldState.TagHeadPosition),
                newHead = Point(proposal.NewState.TagHeadPosition),
                oldHasLeader = proposal.OldState.HasLeader,
                newHasLeader = proposal.NewState.HasLeader,
                oldOrientation = proposal.OldState.Orientation.ToString(),
                newOrientation = proposal.NewState.Orientation.ToString()
            }).ToList();
            var unchanged = totalTags - proposals.Count;
            var message = previewOnly
                ? string.Format(
                    "Preview: {0} of {1} managed tag(s) would change; {2} unchanged.",
                    proposals.Count,
                    totalTags,
                    unchanged)
                : string.Format(
                    "Adjusted {0} of {1} managed tag(s); {2} unchanged, {3} failed.",
                    applied,
                    totalTags,
                    unchanged,
                    failed);
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = previewOnly || failed == 0 || applied > 0,
                Message = message,
                Data = new
                {
                    viewId = view.Id.Value,
                    viewName = view.Name,
                    totalTags,
                    proposedCount = proposals.Count,
                    appliedCount = applied,
                    unchangedCount = unchanged,
                    failedCount = failed,
                    proposals = data,
                    diagnostics
                },
                DurationMs = durationMilliseconds
            };
        }

        private static object Point(XYZ point)
        {
            return point == null
                ? null
                : new { x = point.X, y = point.Y, z = point.Z };
        }
    }
}

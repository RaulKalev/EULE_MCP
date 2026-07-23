#nullable disable

using System;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tagging;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools
{
    public sealed class AnalyzeSelectedTagTemplateTool : IRevitMcpTool
    {
        public string Name => "revit_analyze_selected_tag_template";

        public string Description =>
            "Read-only analysis for 'tag elements like the selected example tag'. " +
            "Select one IndependentTag (optionally also its referenced host). The tool resolves " +
            "the source FamilyInstance, learns host-local right/front offsets, exact tag type, " +
            "rotation mode, orientation, leader/elbow/free-end geometry, and previews targets. " +
            "scope defaults to sameFamily; also supports sameFamilyAndType, sameCategory, selection, " +
            "and explicitElementIds. anchorMode supports SmartTagCenter, LocationPoint, and " +
            "ViewBoundingBoxCenter. Returns paged target details and full counts without changing Revit.";

        public ToolPermission Permission => ToolPermission.ReadOnly;
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

            var options = TaggingToolSupport.ParseTemplateOptions(
                request.Arguments,
                out var error);
            if (options == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

            var analysis = new SelectedTagTemplateService().Analyze(
                doc,
                uidoc.Selection.GetElementIds(),
                options,
                cancellationToken);
            stopwatch.Stop();
            return Task.FromResult(BuildAnalysisResult(
                request,
                analysis,
                options,
                stopwatch.ElapsedMilliseconds));
        }

        internal static McpToolResult BuildAnalysisResult(
            McpToolRequest request,
            TagTemplateAnalysisResult analysis,
            TagTemplateRequestOptions options,
            long durationMilliseconds)
        {
            if (analysis.Template == null)
            {
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Message = analysis.Errors.FirstOrDefault() ??
                              "The selected tag could not be analyzed.",
                    Errors = analysis.Errors,
                    Data = new { warnings = analysis.Warnings },
                    DurationMs = durationMilliseconds
                };
            }

            var page = Math.Max(1, options.Page);
            var pageSize = Math.Min(500, Math.Max(1, options.PageSize));
            var skip = (page - 1) * pageSize;
            var pageItems = analysis.Targets
                .Skip(skip)
                .Take(pageSize)
                .Select(TargetData)
                .ToList();
            var success = analysis.Errors.Count == 0 &&
                          analysis.EligibleCount > 0;
            var message = success
                ? string.Format(
                    "Learned tag template from tag {0}: {1} eligible, {2} already tagged, {3} unsupported, {4} other skipped.",
                    analysis.Template.SourceTagId,
                    analysis.EligibleCount,
                    analysis.AlreadyTaggedCount,
                    analysis.UnsupportedCount,
                    Math.Max(
                        0,
                        analysis.SkippedCount -
                        analysis.AlreadyTaggedCount))
                : analysis.EligibleCount == 0
                    ? "The selected tag was analyzed, but no eligible target elements remain."
                    : analysis.Errors.FirstOrDefault() ??
                      "The selected tag analysis did not produce a usable target set.";

            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = success,
                Message = message,
                Data = new
                {
                    source = SourceData(analysis.Template),
                    inferredRule = RuleData(analysis.Template),
                    scope = new
                    {
                        mode = options.ScopeMode.ToString(),
                        analysis.CandidateCount,
                        analysis.AlreadyTaggedCount,
                        analysis.EligibleCount,
                        analysis.SkippedCount,
                        analysis.UnsupportedCount
                    },
                    targets = pageItems,
                    paging = new
                    {
                        page,
                        pageSize,
                        total = analysis.Targets.Count,
                        returned = pageItems.Count,
                        truncated = skip + pageItems.Count <
                                    analysis.Targets.Count
                    },
                    warnings = analysis.Warnings
                },
                Errors = analysis.Errors,
                DurationMs = durationMilliseconds
            };
        }

        internal static object SourceData(TagPlacementTemplate template)
        {
            return new
            {
                tagId = template.SourceTagId,
                hostElementId = template.SourceHostElementId,
                viewId = template.SourceViewId,
                tagTypeId = template.TagTypeId,
                tagFamilyName = template.TagFamilyName,
                tagTypeName = template.TagTypeName,
                hostCategoryId = template.HostCategoryId,
                hostCategoryName = template.HostCategoryName,
                hostFamilyId = template.HostFamilyId,
                hostFamilyName = template.HostFamilyName,
                hostTypeId = template.HostTypeId,
                hostTypeName = template.HostTypeName
            };
        }

        internal static object RuleData(TagPlacementTemplate template)
        {
            return new
            {
                anchorMode = template.AnchorMode.ToString(),
                template.AnchorSource,
                template.OrientationSource,
                template.OrientationFallbackUsed,
                template.SourceFacingFlipped,
                template.SourceHandFlipped,
                template.SourceMirrored,
                localRightOffsetMm =
                    template.LocalRightOffsetMillimeters,
                localFrontOffsetMm =
                    template.LocalFrontOffsetMillimeters,
                placementSide =
                    template.PlacementSide.ToString(),
                distanceFromAnchorMm =
                    template.DistanceFromAnchorMillimeters,
                rotationMode =
                    template.RotationMode.ToString(),
                template.SourceHostRotationDegrees,
                template.SourceTagRotationDegrees,
                template.RelativeRotationDegrees,
                orientation = template.Orientation.ToString(),
                template.HasLeader,
                leaderEndCondition =
                    template.LeaderEndCondition.ToString(),
                template.HasLeaderElbow,
                leaderElbowLocalRightOffsetMm =
                    template.LeaderElbowLocalRightOffsetMillimeters,
                leaderElbowLocalFrontOffsetMm =
                    template.LeaderElbowLocalFrontOffsetMillimeters,
                template.HasFreeLeaderEnd,
                leaderEndLocalRightOffsetMm =
                    template.LeaderEndLocalRightOffsetMillimeters,
                leaderEndLocalFrontOffsetMm =
                    template.LeaderEndLocalFrontOffsetMillimeters
            };
        }

        internal static object TargetData(TagTemplateTargetItem item)
        {
            return new
            {
                item.HostElementId,
                item.CategoryName,
                item.FamilyName,
                item.TypeName,
                item.Eligible,
                item.AlreadyTagged,
                item.HostOrientationSource,
                item.OrientationFallbackUsed,
                item.FacingFlipped,
                item.HandFlipped,
                item.Mirrored,
                existingTagIds = item.ExistingTagIds,
                item.Status,
                item.Reason,
                proposedHeadMillimeters = item.ProposedHead == null
                    ? null
                    : new
                    {
                        x = item.ProposedHeadX * 304.8,
                        y = item.ProposedHeadY * 304.8,
                        z = item.ProposedHeadZ * 304.8
                    }
            };
        }
    }

    public sealed class ApplySelectedTagTemplateTool : IRevitMcpTool
    {
        public string Name => "revit_apply_selected_tag_template";

        public string Description =>
            "Tags matching FamilyInstances like one selected example IndependentTag. " +
            "Re-analyzes the live source tag, validates any analyzedTemplate JSON, and preserves " +
            "host-local right/front placement across rotated, flipped, and mirrored targets. " +
            "Uses the exact source tag type, orientation, inferred/overridden rotation mode, and " +
            "leader geometry. scope defaults to sameFamily. Existing matching tags are skipped by " +
            "default. Optional collision detection runs after reproducing the learned rule. " +
            "Requires normal MCP approval; all successful tags commit as one Revit Undo operation.";

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

            var options = TaggingToolSupport.ParseTemplateOptions(
                request.Arguments,
                out var error);
            if (options == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

            var analysis = new SelectedTagTemplateService().Analyze(
                doc,
                uidoc.Selection.GetElementIds(),
                options,
                cancellationToken);
            if (analysis.Template == null ||
                analysis.Errors.Count > 0 ||
                analysis.EligibleCount == 0)
            {
                stopwatch.Stop();
                return Task.FromResult(
                    AnalyzeSelectedTagTemplateTool.BuildAnalysisResult(
                        request,
                        analysis,
                        options,
                        stopwatch.ElapsedMilliseconds));
            }

            TagTemplatePlacementResult placement = null;
            var transactionResult = RevitTransactionRunner.Run(
                doc,
                "Revit MCP - Tag Elements Like Selected Example",
                () =>
                {
                    placement = new SelectedTagTemplatePlacementService()
                        .Apply(
                            doc,
                            analysis,
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
                    Message =
                        "The tag-template transaction failed and was rolled back. No new tags were retained. " +
                        (transactionResult.Diagnostics.OriginalError ??
                         string.Empty),
                    Data = new
                    {
                        createdCount = 0,
                        retainedChanges = false,
                        source = AnalyzeSelectedTagTemplateTool
                            .SourceData(analysis.Template),
                        inferredRule = AnalyzeSelectedTagTemplateTool
                            .RuleData(analysis.Template),
                        transactionDiagnostics =
                            transactionResult.Diagnostics
                    },
                    Errors = placement?.Errors ??
                             analysis.Errors,
                    DurationMs = stopwatch.ElapsedMilliseconds
                });
            }

            placement = placement ??
                        new TagTemplatePlacementResult();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = placement.CreatedCount > 0,
                Message = string.Format(
                    "Created {0} tag(s) like source tag {1}; {2} skipped, {3} failed, {4} collision-adjusted.",
                    placement.CreatedCount,
                    analysis.Template.SourceTagId,
                    placement.SkippedCount,
                    placement.FailedCount,
                    placement.CollisionAdjustedCount),
                Data = new
                {
                    source = AnalyzeSelectedTagTemplateTool
                        .SourceData(analysis.Template),
                    targetView = new
                    {
                        viewId = analysis.Template.SourceViewId,
                        viewName = analysis.SourceView.Name
                    },
                    inferredRule = AnalyzeSelectedTagTemplateTool
                        .RuleData(analysis.Template),
                    placement.CreatedCount,
                    placement.SkippedCount,
                    placement.FailedCount,
                    placement.CollisionAdjustedCount,
                    placement.CollisionDiagnostics,
                    items = placement.Items.Select(item => new
                    {
                        hostElementId = item.HostElementId,
                        createdTagId = item.CreatedTagId,
                        item.Status,
                        item.Reason,
                        item.ExistingTagIds,
                        item.CollisionAdjusted,
                        item.CollisionFree
                    }).ToList(),
                    transactionDiagnostics =
                        transactionResult.Diagnostics
                },
                Errors = placement.Errors,
                DurationMs = stopwatch.ElapsedMilliseconds
            });
        }
    }
}

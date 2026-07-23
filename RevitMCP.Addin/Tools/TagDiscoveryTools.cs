#nullable disable

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tagging;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools
{
    public sealed class ListTagTypesTool : IRevitMcpTool
    {
        public string Name => "revit_list_tag_types";

        public string Description =>
            "Lists loaded tag family types with typeId, family, type, and tag category. " +
            "Optional tagCategoryId filters the result. Optional directionKeyword finds the " +
            "best Left/Right/Up/Down variants in that category for revit_place_tags.";

        public ToolPermission Permission => ToolPermission.ReadOnly;
        public ToolCategory Category => ToolCategory.Documentation;

        public Task<McpToolResult> ExecuteAsync(
            UIApplication uiapp,
            McpToolRequest request,
            CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var doc = uiapp.ActiveUIDocument?.Document;
            if (doc == null)
                return Task.FromResult(
                    TaggingToolSupport.Fail(request, "No active document."));

            var categoryIdValue = ToolArguments.GetLong(
                request.Arguments,
                "tagCategoryId");
            var symbols = TagTypeResolverService.GetAllTagTypes(doc)
                .Where(symbol =>
                    categoryIdValue <= 0 ||
                    (symbol.Category != null &&
                     symbol.Category.Id.Value == categoryIdValue))
                .Select(symbol => new
                {
                    typeId = symbol.Id.Value,
                    familyName = symbol.Family.Name,
                    typeName = symbol.Name,
                    tagCategoryId = symbol.Category == null
                        ? 0L
                        : symbol.Category.Id.Value,
                    tagCategory = symbol.Category == null
                        ? null
                        : symbol.Category.Name,
                    isActive = symbol.IsActive
                })
                .ToList();

            object directionMatches = null;
            var keyword = ToolArguments.GetString(
                request.Arguments,
                "directionKeyword");
            if (categoryIdValue > 0 && !string.IsNullOrWhiteSpace(keyword))
            {
                var matches = TagTypeResolverService.FindDirectionTypes(
                    doc,
                    new ElementId(categoryIdValue),
                    keyword);
                directionMatches = new
                {
                    keyword,
                    leftTagTypeId = matches.LeftTagTypeId.Value,
                    rightTagTypeId = matches.RightTagTypeId.Value,
                    upTagTypeId = matches.UpTagTypeId.Value,
                    downTagTypeId = matches.DownTagTypeId.Value
                };
            }

            stopwatch.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = symbols.Count + " loaded tag type(s) found.",
                Data = new { tagTypes = symbols, directionMatches },
                DurationMs = stopwatch.ElapsedMilliseconds
            });
        }
    }

    public sealed class FindManagedTagsTool : IRevitMcpTool
    {
        public string Name => "revit_find_managed_tags";

        public string Description =>
            "Finds tags carrying the SmartTags-compatible management marker in a graphical view. " +
            "Optional viewId; defaults to active view. Optional tagIds and elementIds filter by " +
            "tag IDs or referenced host element IDs. Returns placement, type, leader state, " +
            "referenced element IDs, creator, version, and creation timestamp.";

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

            var view = TaggingToolSupport.ResolveView(
                uidoc,
                doc,
                request.Arguments,
                out var error);
            if (view == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

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

            var data = tags.Select(tag =>
            {
                SmartTagMetadata metadata;
                SmartTagMarkerStorage.TryGetMetadata(tag, out metadata);
                var head = tag.TagHeadPosition;
                return new
                {
                    tagId = tag.Id.Value,
                    tagTypeId = tag.GetTypeId().Value,
                    tagTypeName = doc.GetElement(tag.GetTypeId())?.Name,
                    tagCategory = tag.Category?.Name,
                    referencedElementIds = TagDiscoveryService
                        .GetTaggedElementIds(tag)
                        .Select(id => id.Value)
                        .ToArray(),
                    hasLeader = tag.HasLeader,
                    orientation = tag.TagOrientation.ToString(),
                    head = head == null
                        ? null
                        : new { x = head.X, y = head.Y, z = head.Z },
                    pluginName = metadata?.PluginName,
                    pluginVersion = metadata?.PluginVersion,
                    creationTimestamp = metadata?.CreationTimestamp
                };
            }).ToList();

            stopwatch.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = data.Count + " managed tag(s) found in view '" +
                          view.Name + "'.",
                Data = new
                {
                    viewId = view.Id.Value,
                    viewName = view.Name,
                    tags = data
                },
                DurationMs = stopwatch.ElapsedMilliseconds
            });
        }
    }

    public sealed class PreviewPlaceTagsTool : IRevitMcpTool
    {
        public string Name => "revit_preview_place_tags";

        public string Description =>
            "Previews revit_place_tags without modifying the model. Accepts the same targets, " +
            "tag type, direction overrides, anchor, leader, rotation, duplicate-skip, and " +
            "collision settings. Returns proposed tag type and head coordinates for every element.";

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
            var tagType = TaggingToolSupport.ResolveBaseTagType(
                doc,
                elements,
                request.Arguments,
                out error);
            if (tagType == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));
            var options = TaggingToolSupport.ParseOptions(
                request.Arguments,
                out error);
            if (options == null)
                return Task.FromResult(TaggingToolSupport.Fail(request, error));

            var result = new SmartTagPlacementService().Preview(
                doc,
                view,
                elements,
                tagType,
                TaggingToolSupport.ResolveDirectionTypes(
                    doc,
                    tagType,
                    request.Arguments),
                options,
                cancellationToken);
            stopwatch.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = string.Format(
                    "Previewed {0} candidate(s): {1} eligible, {2} already tagged, {3} collision fallback(s).",
                    result.CandidateCount,
                    result.Items.Count(item => item.WouldPlace),
                    result.SkippedAlreadyTaggedCount,
                    result.CollisionFallbackCount),
                Data = new
                {
                    viewId = view.Id.Value,
                    viewName = view.Name,
                    result.CandidateCount,
                    result.SkippedAlreadyTaggedCount,
                    result.CollisionFallbackCount,
                    result.CollisionDiagnostics,
                    items = result.Items
                },
                Errors = result.Errors,
                DurationMs = stopwatch.ElapsedMilliseconds
            });
        }
    }
}

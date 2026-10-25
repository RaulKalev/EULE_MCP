using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Views;

/// <summary>revit_preview_set_view_extent (#104): the crop region or section box a room, elements, a box or a scope box would give a view.</summary>
public sealed class PreviewSetViewExtentTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_view_extent";

    public string Description =>
        "Previews setting a view's extent without changing the model. Same arguments as revit_set_view_extent. Returns " +
        "the source box in model mm (padded), what would change (crop box of a plan, section, elevation or callout; " +
        "section box of a 3D view; scope box assignment; plan view range), the new crop in the view's own frame and " +
        "the current one.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetViewExtentExecutor.Execute(uiapp, request, apply: false));
}

/// <summary>revit_set_view_extent (#104): crop region / section box / view range of an existing view from a room, elements, a box or a scope box.</summary>
public sealed class SetViewExtentTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_set_view_extent";

    public string Description =>
        "Sets an existing view's extent. Requires approval. Required: viewId and one of room (id; roomLinkInstanceId for a " +
        "room in a link, moved into host coordinates), elements (ids), box {min, max} in mm, or scopeBox (name or id); " +
        "paddingMm (default 500). Plans, sections, elevations and callouts get a rectangular crop box (cropVisible " +
        "default false; a scope box is assigned instead of copied); 3D views get a section box. Plans also take " +
        "viewRange {topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm}. One transaction, one undo. Run " +
        "revit_preview_set_view_extent first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(SetViewExtentExecutor.Execute(uiapp, request, apply: true));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var view = target.Document.GetElement(new ElementId(ToolArguments.GetLong(request.Arguments, "viewId"))) as View;
        return ViewCreationApprovalSummaryText.SetViewExtent(request, view == null ? null : $"'{view.Name}' ({view.ViewType})", view is View3D);
    }
}

/// <summary>Approval wording shared by the live summary and the fallback in ViewCreationApprovalSummaries.</summary>
internal static class ViewCreationApprovalSummaryText
{
    public static string SetViewExtent(McpToolRequest request, string? viewLabel, bool? is3D)
    {
        var region = SetViewExtentExecutor.ReadRegion(request.Arguments, new List<string>());
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var what = is3D switch
        {
            true => "section box",
            false => region.ScopeBox != null ? "scope box" : "crop region",
            null => "crop region (or 3D section box)"
        };
        var padding = region.ScopeBox != null && is3D != true ? string.Empty : $" + {region.PaddingMm ?? ViewExtentMath.DefaultPaddingMm:0.#} mm padding";
        var source = region.IsEmpty ? null : region.Describe();
        var range = ViewCreateSpecParser.ParseViewRange(ViewCreateSpecParser.ToObject(
            request.Arguments.TryGetValue("viewRange", out var raw) ? raw : null), new List<string>());
        var parts = new List<string>();
        if (source != null) parts.Add($"set the {what} of view {viewLabel ?? viewId.ToString()} to {source}{padding}");
        if (range != null) parts.Add($"set {(source == null ? $"view {viewLabel ?? viewId.ToString()}'s" : "its")} view range");
        if (parts.Count == 0) parts.Add($"change the extent of view {viewLabel ?? viewId.ToString()}");
        var text = string.Join(" and ", parts);
        return char.ToUpperInvariant(text[0]) + text.Substring(1) + ". One transaction, reversible via Undo.";
    }
}

internal static class SetViewExtentExecutor
{
    public const string ModeCropBox = "cropBox";
    public const string ModeSectionBox = "sectionBox";
    public const string ModeScopeBox = "scopeBox";

    public static RegionSpec ReadRegion(Dictionary<string, object?> args, List<string> errors)
    {
        var region = new RegionSpec();
        var boxObject = ViewCreateSpecParser.ToObject(args.TryGetValue("box", out var rawBox) ? rawBox : null);
        if (boxObject != null) region.Box = ViewCreateSpecParser.ParseBox(boxObject, errors, "box");
        region.ElementIds = ToolArguments.GetLongArray(args, "elements").Where(id => id > 0).Distinct().ToList();
        var room = ToolArguments.GetLong(args, "room");
        if (room > 0) region.RoomId = room;
        region.RoomLinkInstanceId = ToolArguments.GetLong(args, "roomLinkInstanceId");
        var scopeBox = ToolArguments.GetString(args, "scopeBox").Trim();
        if (scopeBox.Length > 0) region.ScopeBox = scopeBox;
        if (args.ContainsKey("paddingMm")) region.PaddingMm = ToolArguments.GetDouble(args, "paddingMm", ViewExtentMath.DefaultPaddingMm);
        return region;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;
        var args = request.Arguments;

        var viewId = ToolArguments.GetLong(args, "viewId");
        if (viewId <= 0) return Fail(request, "viewId is required (revit_list_views lists the views).");
        if (doc.GetElement(new ElementId(viewId)) is not View view || view.IsTemplate)
            return Fail(request, $"Element {viewId} is not a view (templates have no extent).");
        var cropVisible = ToolArguments.GetBool(args, "cropVisible", false);

        var errors = new List<string>();
        var region = ReadRegion(args, errors);
        var range = ViewCreateSpecParser.ParseViewRange(ViewCreateSpecParser.ToObject(args.TryGetValue("viewRange", out var rawRange) ? rawRange : null), errors);
        if (region.PaddingMm is < 0) errors.Add("paddingMm cannot be negative.");
        if (region.SourceCount > 1) errors.Add($"Give one extent source: room, elements, box or scopeBox (got {region.SourceCount}).");
        if (region.IsEmpty && range == null) errors.Add("Give an extent (room, elements, box or scopeBox) or a viewRange.");
        if (errors.Count > 0) return Fail(request, string.Join(" ", errors));

        var is3D = view is View3D;
        if (!is3D && !region.IsEmpty && !ViewCropRegionToolSupport.IsCroppableView(view))
            return Fail(request, $"View '{view.Name}' ({view.ViewType}) has no crop region: plans, sections, elevations, callouts and 3D views do.");
        if (range != null && view is not ViewPlan)
            return Fail(request, $"viewRange applies to plans; '{view.Name}' is a {view.ViewType} view.");

        var warnings = new List<string>();
        string? mode = null;
        BoxMm? box = null;
        Element? scopeBox = null;
        object? newCrop = null;

        if (!region.IsEmpty)
        {
            if (region.ScopeBox != null && ViewExtentService.CanTakeScopeBox(view))
            {
                scopeBox = ViewExtentService.FindScopeBox(doc, region.ScopeBox, out var error);
                if (scopeBox == null) return Fail(request, error!, ViewCreateCodes.ScopeBoxNotFound);
                mode = ModeScopeBox;
                if (region.PaddingMm.HasValue) warnings.Add("paddingMm is ignored: the scope box itself becomes the view's extent.");
                box = ViewExtentService.ToBoxMm(scopeBox.get_BoundingBox(null));
            }
            else
            {
                box = ViewExtentService.RegionBox(doc, region, warnings, out scopeBox, out var error);
                if (box == null) return Fail(request, error!, ViewCreateCodes.ExtentNotFound);
                mode = is3D ? ModeSectionBox : ModeCropBox;
                if (mode == ModeCropBox)
                {
                    BoxMm localCrop;
                    try { localCrop = ViewExtentService.CropFor(view, box).LocalMm; }
                    catch (Exception ex) { return Fail(request, $"The crop region of '{view.Name}' cannot be read: {ex.Message}"); }
                    if (!ViewExtentMath.HasPlanArea(localCrop))
                        return Fail(request, "The extent has no area in the view's plane.", ViewCreateCodes.ExtentNotFound);
                    newCrop = localCrop.ToMm();
                }
                if (mode == ModeSectionBox && !ViewExtentMath.HasVolume(box))
                    return Fail(request, "The extent has no volume (a section box needs size in x, y and z).", ViewCreateCodes.ExtentNotFound);
            }

            var assigned = ViewExtentService.AssignedScopeBox(view);
            if (mode == ModeCropBox && assigned != null)
                warnings.Add($"The view's crop is controlled by scope box '{assigned.Name}'; it is cleared so the new crop region can be set.");
        }

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Preview: view '{view.Name}' ({view.ViewType}) would get " +
                          (mode switch
                          {
                              ModeCropBox => $"a crop region from {region.Describe()}",
                              ModeSectionBox => $"a section box from {region.Describe()}",
                              ModeScopeBox => $"scope box '{scopeBox!.Name}'",
                              _ => "no new extent"
                          }) + (range != null ? " and a new view range" : string.Empty) + ".",
                Data = Describe(view, mode, region, box, scopeBox, newCrop, range, cropVisible, applied: false),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Set View Extent", () =>
        {
            switch (mode)
            {
                case ModeScopeBox:
                    ViewExtentService.AssignScopeBox(view, scopeBox!);
                    break;
                case ModeSectionBox:
                    ViewExtentService.ApplySectionBox((View3D)view, box!);
                    break;
                case ModeCropBox:
                    var assigned = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
                    if (assigned != null && !assigned.IsReadOnly && assigned.AsElementId() != ElementId.InvalidElementId)
                        assigned.Set(ElementId.InvalidElementId);
                    newCrop = ViewExtentService.ApplyCrop(view, box!, cropVisible).ToMm();
                    break;
            }
            if (range != null) ViewCreator.ApplyViewRange((ViewPlan)view, range);
        });

        sw.Stop();
        if (!txSuccess)
        {
            warnings.AddRange(diagnostics.ToErrorLines());
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = ViewCreateCodes.RevitRefused,
                Message = $"Revit refused the new extent of '{view.Name}'; the model is unchanged.",
                Data = new { viewId = view.Id.Value, transaction = diagnostics },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        warnings.AddRange(diagnostics.FailureMessages);
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Set the extent of '{view.Name}' ({view.ViewType}): " +
                      (mode switch
                      {
                          ModeCropBox => $"crop region from {region.Describe()}",
                          ModeSectionBox => $"section box from {region.Describe()}",
                          ModeScopeBox => $"scope box '{scopeBox!.Name}'",
                          _ => "extent unchanged"
                      }) + (range != null ? ", view range updated" : string.Empty) + ".",
            Data = Describe(view, mode, region, box, scopeBox, newCrop, range, cropVisible, applied: true),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(View view, string? mode, RegionSpec region, BoxMm? box, Element? scopeBox, object? newCrop,
        ViewRangeSpec? range, bool cropVisible, bool applied) => new
    {
        viewId = view.Id.Value,
        viewName = view.Name,
        viewType = view.ViewType.ToString(),
        mode,
        source = region.IsEmpty ? null : region.Describe(),
        paddingMm = mode == ModeScopeBox ? 0 : region.PaddingMm ?? ViewExtentMath.DefaultPaddingMm,
        boxMm = box?.ToMm(),
        scopeBox = scopeBox == null ? null : new { id = scopeBox.Id.Value, name = scopeBox.Name },
        newCropLocalMm = newCrop,
        cropVisible = mode == ModeCropBox ? cropVisible : (bool?)null,
        currentCrop = applied ? null : ViewExtentService.DescribeCrop(view),
        crop = applied ? ViewExtentService.DescribeCrop(view) : null,
        viewRangeRequested = range,
        viewRange = view is ViewPlan plan ? ViewCreator.DescribeViewRange(plan) : null
    };

    private static McpToolResult Fail(McpToolRequest request, string message, string status = "validation_failed") =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };
}

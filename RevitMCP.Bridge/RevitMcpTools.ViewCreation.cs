using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>View creation (#104, docs/view-creation.md): view family types, creating views from specs, view extents.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ViewSpecsDescription =
        "View specs (max 100 views after fan-out). Each: {kind, name?, viewFamilyType?, template?, scale?, detailLevel?, discipline?, phase?, scopeBox?, placeOnSheet? {sheet, xMm, yMm} or {sheets: [...]}} plus per kind: " +
        "floorPlan/ceilingPlan/structuralPlan/areaPlan: level or levels (names/ids or \"all\"; {level} and {n} in name and sheet), areaScheme (areaPlan), viewRange {topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm}, cropTo {room | elements | box | scopeBox, paddingMm}; " +
        "section: from {x,y,z}, to {x,y,z} mm (section line in plan; looks LEFT of from->to, flip=true looks right), depthMm (default 3000), bottomMm/heightMm (default: level range); " +
        "callout: parentView, from/to corners mm or aroundElements/aroundRoom/box + paddingMm (default 500), referenceView for a reference callout; " +
        "threeD: box {min,max} or aroundElements/aroundRoom + paddingMm as section box, isometric (default true), orientation (iso-ne|iso-nw|iso-se|iso-sw|top or {eye, forward, up}); " +
        "drafting; legend: sourceLegend (Revit cannot create legends: an existing one is duplicated).";

    [McpServerTool(Name = "revit_list_view_family_types", ReadOnly = true),
     Description("Lists the view family types per ViewFamily (FloorPlan, CeilingPlan, StructuralPlan, AreaPlan, Section, Detail, Elevation, ThreeDimensional, Drafting, Legend, ...) with id, name, viewFamily, defaultTemplateId/defaultTemplateName, isDocumentDefault and the revit_create_views kinds that use each. Also returns the area schemes and the legends. Use before revit_create_views to pick viewFamilyType, areaScheme and sourceLegend.")]
    public async Task<string> ListViewFamilyTypes(
        [Description("Filter: a ViewFamily name (FloorPlan, Section, ThreeDimensional, Detail, ...) or a kind (floorPlan, section, threeD, callout, ...). Empty = all.")] string? viewFamily = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["viewFamily"] = viewFamily ?? string.Empty };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_view_family_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_views", ReadOnly = true),
     Description("Previews revit_create_views without changing the model: resolves every reference (level, type, template, scope box, parent view, source legend, area scheme, sheet), fans plans out per level, computes unique names and the section box in mm, and reports per view willCreate | skipped | invalid with a status code (level_not_found, view_family_type_incompatible with the compatible types, legend_source_required, view_name_exists, template_not_found, sheet_not_found, ...). dryRun=true (default) also tries the creation in a rolled-back transaction so Revit refusals show here.")]
    public async Task<string> PreviewCreateViews(
        [Description(ViewSpecsDescription)] object[] views,
        [Description("When a name is taken: suffix (default, 'Name 1'), skip, or fail.")] string ifNameExists = "suffix",
        [Description("true: any invalid or refused view rejects the whole request. Default false.")] bool atomic = false,
        [Description("Try the creation in a rolled-back transaction (default true). false = static checks only.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["views"] = ToJToken(views),
            ["ifNameExists"] = ifNameExists ?? "suffix",
            ["atomic"] = atomic,
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_create_views", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_create_views"),
     Description("Creates views from specs: floor, ceiling, structural and area plans (one per level with levels=[...] or \"all\"), sections from two plan points, callouts, 3D views with a section box from a box, elements or a room, drafting views, and legends by duplicating an existing legend. Applies name (company pattern with {level}/{n}), template, scale, detail level, discipline, phase, scope box, view range and crop, and places views on sheets. Requires approval. One transaction, one undo; each view in its own sub-transaction (atomic=true undoes all on any failure). Returns per view {index, name, viewId, kind, level, templateApplied, sheetId, viewportId, warnings}. Run revit_preview_create_views first.")]
    public async Task<string> CreateViews(
        [Description(ViewSpecsDescription)] object[] views,
        [Description("When a name is taken: suffix (default, 'Name 1'), skip, or fail.")] string ifNameExists = "suffix",
        [Description("true: any invalid or refused view rejects the whole request and nothing is created. Default false.")] bool atomic = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["views"] = ToJToken(views),
            ["ifNameExists"] = ifNameExists ?? "suffix",
            ["atomic"] = atomic
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_create_views", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_view_extent", ReadOnly = true),
     Description("Previews revit_set_view_extent without changing the model: the padded source box in model mm, the mode (cropBox for plans, sections, elevations and callouts; sectionBox for 3D views; scopeBox when a scope box is assigned), the new crop in the view's own frame, the current crop and the plan view range.")]
    public async Task<string> PreviewSetViewExtent(
        [Description("The view to change (revit_list_views).")] long viewId,
        [Description("Room (or space) element id to crop to. In a link: also roomLinkInstanceId.")] long room = 0,
        [Description("Link instance id when room lives in a linked model; its transform moves the room into host coordinates.")] long roomLinkInstanceId = 0,
        [Description("Element ids whose bounding boxes (union) give the extent.")] long[]? elements = null,
        [Description("Box in model mm: {min: {x,y,z}, max: {x,y,z}}.")] object? box = null,
        [Description("Scope box name or id: assigned to the view (3D views too).")] string? scopeBox = null,
        [Description("Padding around the extent in mm (default 500; ignored for a scope box).")] double paddingMm = 500,
        [Description("Show the crop region (default false).")] bool cropVisible = false,
        [Description("Plans only: {topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm}; each plane keeps its level.")] object? viewRange = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ViewExtentArgs(viewId, room, roomLinkInstanceId, elements, box, scopeBox, paddingMm, cropVisible, viewRange);
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_view_extent", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_view_extent"),
     Description("Sets an existing view's extent from a room (host or linked), elements, a box or a scope box, with paddingMm (default 500): a rectangular crop box for plans, sections, elevations and callouts (cropVisible default false), a section box for 3D views, or the scope box assigned. Plans also take viewRange. Requires approval. One transaction, one undo. Run revit_preview_set_view_extent first.")]
    public async Task<string> SetViewExtent(
        [Description("The view to change (revit_list_views).")] long viewId,
        [Description("Room (or space) element id to crop to. In a link: also roomLinkInstanceId.")] long room = 0,
        [Description("Link instance id when room lives in a linked model; its transform moves the room into host coordinates.")] long roomLinkInstanceId = 0,
        [Description("Element ids whose bounding boxes (union) give the extent.")] long[]? elements = null,
        [Description("Box in model mm: {min: {x,y,z}, max: {x,y,z}}.")] object? box = null,
        [Description("Scope box name or id: assigned to the view (3D views too).")] string? scopeBox = null,
        [Description("Padding around the extent in mm (default 500; ignored for a scope box).")] double paddingMm = 500,
        [Description("Show the crop region (default false).")] bool cropVisible = false,
        [Description("Plans only: {topOffsetMm, cutOffsetMm, bottomOffsetMm, depthOffsetMm}; each plane keeps its level.")] object? viewRange = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ViewExtentArgs(viewId, room, roomLinkInstanceId, elements, box, scopeBox, paddingMm, cropVisible, viewRange);
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_view_extent", args, cancellationToken));
    }

    private static Dictionary<string, object?> ViewExtentArgs(
        long viewId, long room, long roomLinkInstanceId, long[]? elements, object? box, string? scopeBox, double paddingMm,
        bool cropVisible, object? viewRange)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewId"] = viewId,
            ["room"] = room,
            ["roomLinkInstanceId"] = roomLinkInstanceId,
            ["elements"] = elements ?? [],
            ["scopeBox"] = scopeBox ?? string.Empty,
            ["paddingMm"] = paddingMm,
            ["cropVisible"] = cropVisible
        };
        if (box != null) args["box"] = ToJToken(box);
        if (viewRange != null) args["viewRange"] = ToJToken(viewRange);
        return args;
    }
}

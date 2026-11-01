using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Filled regions (#135, docs/filled-regions.md): read regions and types, create from specs or rules, edit, manage types.</summary>
internal sealed partial class RevitMcpTools
{
    private const string FilledRegionShapeDescription =
        "One shape: loops [[{x,y,z},...],...] closed point lists in model mm (repeat the first point or closeLoops=true; first the outline, then holes; arcMid on a point makes the segment to the next point an arc; z is required in sections and elevations; space=\"view\" takes {u,v} along the view's right and up directions) | " +
        "rectangle {from,to} or {center,widthMm,heightMm}, along the view's axes | room id, {id,linkInstanceId} or {number,level,linkInstanceId}, with boundaryLocation finish (default), center, coreCenter or coreBoundary; a linked room is moved with the link transform | " +
        "detailLines [ids], any order | element id or {id,linkInstanceId}, its bounding box. paddingMm grows the shape (negative shrinks).";

    private const string FilledRegionSpecsDescription =
        "Direct region specs: {view (name or id), type (name or id), lineStyle?, parameters? {name: value}} plus " + FilledRegionShapeDescription;

    private const string FilledRegionRulesDescription =
        "Rules: {view, source {kind rooms|elements, categories?, filters? [{parameterName, operator, value}], inView?, level?, linkInstanceId?, elementIds?, limit?}, shape {kind roomBoundary|boundingBox, paddingMm?, boundaryLocation?}, type OR typeMap {parameter, map {value: type}}, lineStyle?, parameters?}. A source whose value is not in the map is listed in skippedSources, never given a guessed type.";

    [McpServerTool(Name = "revit_list_filled_regions", ReadOnly = true),
     Description("Lists filled and masking regions in a view, of a type or by elementIds (with none of these: every region up to limit). Per region: id, view, type, isMasking, areaM2 (Revit's Area), loops (closed point lists in model mm with u/v and arcMid, the format revit_create_filled_regions takes) and instance parameters.")]
    public async Task<string> ListFilledRegions(
        [Description("View name or id: regions drawn in it.")] string? view = null,
        [Description("Filled region type name or id.")] string? type = null,
        [Description("Region element ids.")] long[]? elementIds = null,
        [Description("Include the boundary loops (default true).")] bool includeBoundary = true,
        [Description("Include instance parameters (default true).")] bool includeParameters = true,
        [Description("Maximum regions (default 200).")] int limit = 200,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["view"] = view ?? string.Empty,
            ["type"] = type ?? string.Empty,
            ["elementIds"] = elementIds ?? [],
            ["includeBoundary"] = includeBoundary,
            ["includeParameters"] = includeParameters,
            ["limit"] = limit
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_filled_regions", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_list_filled_region_types", ReadOnly = true),
     Description("Lists filled region types: foreground and background pattern and colour, line weight, isMasking and instanceCount; with includePatterns (default true) also the fill patterns and the line styles a filled region can use.")]
    public async Task<string> ListFilledRegionTypes(
        [Description("Also list fill patterns and valid line styles (default true).")] bool includePatterns = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["includePatterns"] = includePatterns };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_filled_region_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_filled_regions", ReadOnly = true),
     Description("Previews revit_create_filled_regions without changing the model: every region a spec or rule would create (view, type, outlines, holes, area, extent), status willCreate | invalid | skipped with a code (loop_open, loop_self_intersecting, loops_intersect, hole_outside_outer, view_not_supported, boundary_not_in_view_plane, coordinate_z_required, type_not_found, ...), skippedSources and unmappedValues of rules. dryRun=true (default) also tries the creation in a rolled-back transaction.")]
    public async Task<string> PreviewCreateFilledRegions(
        [Description(FilledRegionSpecsDescription)] object[]? regions = null,
        [Description(FilledRegionRulesDescription)] object[]? rules = null,
        [Description("true: any invalid or refused region rejects the whole request. Default false.")] bool atomic = false,
        [Description("Return each region's boundary points (default false; extentUv is always returned).")] bool includeLoops = false,
        [Description("Try the creation in a rolled-back transaction (default true). false = static checks only.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = FilledRegionCreateArgs(regions, rules, atomic, includeLoops);
        args["dryRun"] = dryRun;
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_create_filled_regions", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_create_filled_regions"),
     Description("Creates filled regions in plans, sections, elevations, drafting views and sheets, from direct specs (coordinates with holes, rectangles, host or linked room boundaries, detail lines, element boxes) or rules (a region per room or element, with one type or a parameter value to type map). Requires approval. One transaction, one undo; each region in its own sub-transaction (atomic=true undoes all on any failure). Returns newRegionIds and per region {index, status, code, regionId, view, type, areaM2, revitAreaM2}, plus skippedSources. Max 500 regions. Run revit_preview_create_filled_regions first.")]
    public async Task<string> CreateFilledRegions(
        [Description(FilledRegionSpecsDescription)] object[]? regions = null,
        [Description(FilledRegionRulesDescription)] object[]? rules = null,
        [Description("true: any invalid or refused region rejects the whole request and nothing is created. Default false.")] bool atomic = false,
        [Description("Return each region's boundary points (default false).")] bool includeLoops = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = FilledRegionCreateArgs(regions, rules, atomic, includeLoops);
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_create_filled_regions", args, cancellationToken));
    }

    private const string FilledRegionEditsDescription =
        "Edits: {id, type? (name or id), lineStyle?, parameters? {name: value}, boundary? {" + FilledRegionShapeDescription + "}}. Type, line style and parameters keep the id. A boundary replaces the element: same type, view and parameters, new id.";

    [McpServerTool(Name = "revit_preview_edit_filled_regions", ReadOnly = true),
     Description("Previews revit_edit_filled_regions without changing the model: per edit status willEdit | invalid with a code (region_not_found, not_a_filled_region, loop_open, loop_self_intersecting, type_not_found, parameter_not_settable, region_in_group, region_owned_by_other, ...), the type and parameter changes, keepsId, and for a reshape the new boundary and the attached tags and dimensions that may be lost. dryRun=true (default) also tries the edits in a rolled-back transaction.")]
    public async Task<string> PreviewEditFilledRegions(
        [Description(FilledRegionEditsDescription)] object[] edits,
        [Description("true: any invalid or refused edit rejects the whole request. Default false.")] bool atomic = false,
        [Description("Try the edits in a rolled-back transaction (default true). false = static checks only.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["edits"] = ToJToken(edits),
            ["atomic"] = atomic,
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_edit_filled_regions", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_edit_filled_regions"),
     Description("Changes filled regions. Type, line style and parameters keep the element id. The Revit API cannot edit a boundary, so a reshape creates a new region and deletes the old one: it keeps type, view and parameters, returns idMap [{oldId, newId}] and reports the tags and dimensions on the old region as lost. Plans never cover a reshape, because it deletes. Requires approval. One transaction, one undo. Run revit_preview_edit_filled_regions first.")]
    public async Task<string> EditFilledRegions(
        [Description(FilledRegionEditsDescription)] object[] edits,
        [Description("true: any invalid or refused edit rejects the whole request and nothing changes. Default false.")] bool atomic = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["edits"] = ToJToken(edits),
            ["atomic"] = atomic
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_edit_filled_regions", args, cancellationToken));
    }

    private const string FilledRegionTypesDescription =
        "Type specs: {type (edit this type; name renames it) | duplicateFrom + name (create), foregroundPattern?, backgroundPattern? (name, id, \"solid\" or \"none\"; a background must be a drafting pattern), foregroundColor?, backgroundColor? (\"#RRGGBB\"), lineWeight? (1-16), isMasking?}.";

    [McpServerTool(Name = "revit_preview_set_filled_region_types", ReadOnly = true),
     Description("Previews revit_set_filled_region_types without changing the model: per type from/to per field, a status code (type_not_found, type_name_exists, pattern_not_found, pattern_not_valid, line_weight_not_valid, masking_not_valid) and affectedRegions, every region of the type by view. dryRun=true (default) also tries the changes in a rolled-back transaction.")]
    public async Task<string> PreviewSetFilledRegionTypes(
        [Description(FilledRegionTypesDescription)] object[] types,
        [Description("true: any invalid or refused spec rejects the whole request. Default false.")] bool atomic = false,
        [Description("Try the changes in a rolled-back transaction (default true). false = static checks only.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["types"] = ToJToken(types),
            ["atomic"] = atomic,
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_filled_region_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_filled_region_types"),
     Description("Creates filled region types by duplicating an existing one and edits types: foreground and background pattern and colour, line weight, masking, name. A type edit changes every region of the type. Requires approval. One transaction, one undo. Run revit_preview_set_filled_region_types first: it lists the regions affected.")]
    public async Task<string> SetFilledRegionTypes(
        [Description(FilledRegionTypesDescription)] object[] types,
        [Description("true: any invalid or refused spec rejects the whole request and nothing changes. Default false.")] bool atomic = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["types"] = ToJToken(types),
            ["atomic"] = atomic
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_filled_region_types", args, cancellationToken));
    }

    private static Dictionary<string, object?> FilledRegionCreateArgs(object[]? regions, object[]? rules, bool atomic, bool includeLoops)
    {
        var args = new Dictionary<string, object?>
        {
            ["atomic"] = atomic,
            ["includeLoops"] = includeLoops
        };
        if (regions != null) args["regions"] = ToJToken(regions);
        if (rules != null) args["rules"] = ToJToken(rules);
        return args;
    }
}

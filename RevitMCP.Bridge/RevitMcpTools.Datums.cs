using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Model datums (#164, docs/datums.md): list, create and edit levels, grids and reference planes.</summary>
internal sealed partial class RevitMcpTools
{
    private const string DatumLevelsDescription =
        "Levels to create: [{name, elevationMm}]. Millimetres, host internal coordinates. The name is required.";
    private const string DatumGridsDescription =
        "Straight horizontal grids to create: [{name?, startMm:[x,y], endMm:[x,y]}]. An unnamed grid is numbered by Revit.";
    private const string DatumPlanesDescription =
        "Reference planes to create: [{name?, viewId, bubbleEndMm:[x,y(,z)], freeEndMm:[x,y(,z)]}] drawn in a plan, ceiling plan, elevation, section or detail view.";
    private const string DatumEditsDescription =
        "Changes: [{id, newName?, elevationMm? (levels), moveByMm? [dx,dy(,dz)] (grids and reference planes)}]. Ids from revit_list_datums.";

    [McpServerTool(Name = "revit_list_datums", ReadOnly = true),
     Description("Lists the levels, grids and reference planes of the host model with ids, names and geometry in millimetres: levels elevationMm/projectElevationMm, grids startMm/endMm, reference planes bubbleEndMm/freeEndMm; pinned flags. Filters: kinds (levels, grids, referencePlanes; default all), nameFilter, limit per kind. revit_list_levels additionally maps a link's levels to host levels.")]
    public async Task<string> ListDatums(
        [Description("Kinds to list: levels, grids, referencePlanes (array or comma-separated). Default all.")] string[]? kinds = null,
        [Description("Only datums whose name contains this text (ignoring case).")] string? nameFilter = null,
        [Description("Maximum per kind (default 200).")] int limit = 200,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["kinds"] = kinds == null ? null : ToJToken(kinds),
            ["nameFilter"] = nameFilter ?? string.Empty,
            ["limit"] = limit,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_datums", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_datums", ReadOnly = true),
     Description("Previews creating levels, grids and reference planes WITHOUT changing the model. Same arguments as revit_create_datums. Per item: created or refused with the reason (invalid or duplicate name, a level at an existing level's elevation, out-of-range or too short geometry, a grid or reference plane identical to an existing one, a view that cannot hold a reference plane).")]
    public Task<string> PreviewCreateDatums(
        [Description(DatumLevelsDescription)] object? levels = null,
        [Description(DatumGridsDescription)] object? grids = null,
        [Description(DatumPlanesDescription)] object? referencePlanes = null,
        [Description("Elevations closer than this (mm, default 1) count as the same level.")] double? elevationToleranceMm = null,
        [Description("Allow a level at the elevation of an existing one.")] bool allowEquivalentLevels = false,
        [Description("Allow a grid or reference plane identical to an existing one.")] bool allowDuplicateGeometry = false,
        [Description("allOrNothing (default) or perItem.")] string mode = "allOrNothing",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendDatumCreate("revit_preview_create_datums", levels, grids, referencePlanes, elevationToleranceMm, allowEquivalentLevels,
            allowDuplicateGeometry, mode, document, cancellationToken);

    [McpServerTool(Name = "revit_create_datums"),
     Description("Creates levels, grids and reference planes in the host document (never in a linked model) in one transaction and one undo step. Requires approval. Millimetres. A level at an existing level's elevation, a repeated name and an identical grid or reference plane are refused unless allowEquivalentLevels / allowDuplicateGeometry. mode=allOrNothing (default) writes nothing when any item is refused; perItem writes the rest. Returns the new ids per item. No views are created: use revit_create_views for the plan views of a new level. Run revit_preview_create_datums first.")]
    public Task<string> CreateDatums(
        [Description(DatumLevelsDescription)] object? levels = null,
        [Description(DatumGridsDescription)] object? grids = null,
        [Description(DatumPlanesDescription)] object? referencePlanes = null,
        [Description("Elevations closer than this (mm, default 1) count as the same level.")] double? elevationToleranceMm = null,
        [Description("Allow a level at the elevation of an existing one.")] bool allowEquivalentLevels = false,
        [Description("Allow a grid or reference plane identical to an existing one.")] bool allowDuplicateGeometry = false,
        [Description("allOrNothing (default) or perItem.")] string mode = "allOrNothing",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendDatumCreate("revit_create_datums", levels, grids, referencePlanes, elevationToleranceMm, allowEquivalentLevels,
            allowDuplicateGeometry, mode, document, cancellationToken);

    [McpServerTool(Name = "revit_preview_edit_datums", ReadOnly = true),
     Description("Previews renaming and moving levels, grids and reference planes WITHOUT changing the model. Same arguments as revit_edit_datums. Per datum: the change, its impact (none, low, high), what depends on it (hosted elements, associated views, other dependents with categories) and why it would be refused (name conflict, elevation clash, pinned, checked out by someone else, not a datum of this document).")]
    public Task<string> PreviewEditDatums(
        [Description(DatumEditsDescription)] object? edits = null,
        [Description("Elevations closer than this (mm, default 1) count as the same level.")] double? elevationToleranceMm = null,
        [Description("Allow a level to move onto the elevation of another.")] bool allowEquivalentLevels = false,
        [Description("Accept high-impact changes (see the preview).")] bool confirmHighImpact = false,
        [Description("allOrNothing (default) or perItem.")] string mode = "allOrNothing",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendDatumEdit("revit_preview_edit_datums", edits, elevationToleranceMm, allowEquivalentLevels, confirmHighImpact, mode, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_datums", Destructive = true),
     Description("Renames and moves existing levels, grids and reference planes of the host document in one transaction. Requires manual approval: moving a level or grid carries every element, view and dimension that depends on it. A change with dependents, or a move of 1 m or more, is high impact and refused unless confirmHighImpact=true. Name conflicts, a level onto another level's elevation, pinned and checked-out datums are refused. Datums of linked models cannot be edited. Run revit_preview_edit_datums first; revit_checkpoint_create to be able to roll back.")]
    public Task<string> EditDatums(
        [Description(DatumEditsDescription)] object? edits = null,
        [Description("Elevations closer than this (mm, default 1) count as the same level.")] double? elevationToleranceMm = null,
        [Description("Allow a level to move onto the elevation of another.")] bool allowEquivalentLevels = false,
        [Description("Accept high-impact changes (see the preview).")] bool confirmHighImpact = false,
        [Description("allOrNothing (default) or perItem.")] string mode = "allOrNothing",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendDatumEdit("revit_edit_datums", edits, elevationToleranceMm, allowEquivalentLevels, confirmHighImpact, mode, document, cancellationToken);

    private async Task<string> SendDatumCreate(string toolName, object? levels, object? grids, object? referencePlanes,
        double? elevationToleranceMm, bool allowEquivalentLevels, bool allowDuplicateGeometry, string mode, string? document,
        CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["levels"] = levels == null ? null : ToJToken(levels),
            ["grids"] = grids == null ? null : ToJToken(grids),
            ["referencePlanes"] = referencePlanes == null ? null : ToJToken(referencePlanes),
            ["elevationToleranceMm"] = elevationToleranceMm ?? -1.0,
            ["allowEquivalentLevels"] = allowEquivalentLevels,
            ["allowDuplicateGeometry"] = allowDuplicateGeometry,
            ["mode"] = string.IsNullOrWhiteSpace(mode) ? "allOrNothing" : mode,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    private async Task<string> SendDatumEdit(string toolName, object? edits, double? elevationToleranceMm, bool allowEquivalentLevels,
        bool confirmHighImpact, string mode, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["edits"] = edits == null ? null : ToJToken(edits),
            ["elevationToleranceMm"] = elevationToleranceMm ?? -1.0,
            ["allowEquivalentLevels"] = allowEquivalentLevels,
            ["confirmHighImpact"] = confirmHighImpact,
            ["mode"] = string.IsNullOrWhiteSpace(mode) ? "allOrNothing" : mode,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

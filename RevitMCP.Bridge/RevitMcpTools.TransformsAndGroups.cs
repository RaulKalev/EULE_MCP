using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Transforms and groups (#108): mirror, array, list/create/ungroup/place groups (docs/transforms-and-groups.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string MirrorPlaneDescription =
        "The mirror plane, one of: {viewId?, fromMm {x,y}, toMm {x,y}} — a line drawn in a view (plan when viewId is omitted); " +
        "the plane contains the line and the view direction, so in a plan it is the vertical plane through the line, as Revit's " +
        "Mirror – Draw Axis; {axis: x|y|z, throughMm {x,y,z}} — the plane perpendicular to that model axis through the point " +
        "(axis x flips X); {pointMm {x,y,z}, normal {x,y,z}}. Model mm.";

    // ── Mirror ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_preview_mirror_elements", ReadOnly = true),
     Description("Previews mirroring elements across a plane, WITHOUT changing the model. Same arguments as revit_mirror_elements. Returns the resolved plane (origin, normal, vertical), per element whether Revit can mirror it (CanMirrorElement) and where its insertion point lands (fromMm → toMm), and — from a trial run that is rolled back — how many elements Revit would create.")]
    public async Task<string> PreviewMirrorElements(
        [Description("Elements to mirror (up to 2000).")] long[] elementIds,
        [Description(MirrorPlaneDescription)] object? plane = null,
        [Description("true (default): create mirrored copies and keep the originals. false: mirror the elements in place.")] bool copy = true,
        [Description("Allow a plane that is not vertical (it turns elements upside down). Default false.")] bool allowHorizontalPlane = false,
        [Description("true (default): each set is mirrored together and any refusal undoes everything. false: element by element.")] bool atomic = true,
        [Description("copy=false only: skip pinned elements instead of failing on them (default true).")] bool skipPinned = true,
        [Description("The view the mirror line is drawn in, when plane has no viewId.")] long viewId = 0,
        [Description("Let Revit try the mirror in a transaction that is rolled back (default true).")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = MirrorArgs(elementIds, plane, copy, allowHorizontalPlane, atomic, skipPinned, viewId, document);
        args["dryRun"] = dryRun;
        return FormatResult(await pipeClient.SendAsync("revit_preview_mirror_elements", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_mirror_elements"),
     Description("Mirrors elements across a plane, as Revit's Mirror – Draw Axis / Pick Axis. Requires approval. plane: a line drawn in a view (fromMm/toMm; in a plan the vertical plane through the line), a model axis through a point, or a point and normal. copy=true (default) creates mirrored copies; copy=false flips the elements in place. A plane that is not vertical needs allowHorizontalPlane=true. View-specific elements are mirrored in their own view (draw the line there). A group instance id mirrors the whole group. Elements Revit cannot mirror are listed, never silently dropped: with atomic=true (default) the request is refused with status mirror_not_allowed and data.notAllowed. Returns newIds and pairs {sourceId, newId, fromMm, toMm}. One transaction, one Revit undo. Run revit_preview_mirror_elements first.")]
    public async Task<string> MirrorElements(
        [Description("Elements to mirror (up to 2000).")] long[] elementIds,
        [Description(MirrorPlaneDescription)] object? plane = null,
        [Description("true (default): create mirrored copies and keep the originals. false: mirror the elements in place.")] bool copy = true,
        [Description("Allow a plane that is not vertical (it turns elements upside down). Default false.")] bool allowHorizontalPlane = false,
        [Description("true (default): each set is mirrored together and any refusal undoes everything. false: element by element.")] bool atomic = true,
        [Description("copy=false only: skip pinned elements instead of failing on them (default true).")] bool skipPinned = true,
        [Description("The view the mirror line is drawn in, when plane has no viewId.")] long viewId = 0,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = MirrorArgs(elementIds, plane, copy, allowHorizontalPlane, atomic, skipPinned, viewId, document);
        return FormatResult(await pipeClient.SendAsync("revit_mirror_elements", args, cancellationToken));
    }

    private static Dictionary<string, object?> MirrorArgs(
        long[] elementIds, object? plane, bool copy, bool allowHorizontalPlane, bool atomic, bool skipPinned, long viewId, string? document) => new()
    {
        ["elementIds"] = elementIds ?? [],
        ["plane"] = ToJToken(plane),
        ["copy"] = copy,
        ["allowHorizontalPlane"] = allowHorizontalPlane,
        ["atomic"] = atomic,
        ["skipPinned"] = skipPinned,
        ["viewId"] = viewId,
        ["document"] = document ?? string.Empty
    };

    // ── Array ────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_preview_array_elements", ReadOnly = true),
     Description("Previews a linear or radial array, WITHOUT changing the model. Same arguments as revit_array_elements. Returns the steps (translation or angle per copy), copies per element, per element whether it can be copied, and where the first elements land in every step. Static checks only.")]
    public async Task<string> PreviewArrayElements(
        [Description("Elements to array.")] long[] elementIds,
        [Description("linear (default) or radial.")] string kind = "linear",
        [Description("Members of the array, 2-200 (radial asGroup: 3-200). With includeOriginal=true the original is one of them.")] int count = 2,
        [Description("Linear: {x,y,z} mm from one member to the next, model axes.")] object? spacingMm = null,
        [Description("Linear: {x,y,z} mm from the original to the last copy.")] object? totalMm = null,
        [Description("Linear: mm per step along the view's right (viewId or the elements' owner view).")] double? deltaRightMm = null,
        [Description("Linear: mm per step along the view's up.")] double? deltaUpMm = null,
        [Description("Radial: {x,y,z} mm, the point the axis passes through.")] object? centerMm = null,
        [Description("Radial: axis x, y or z (default z).")] string axis = "z",
        [Description("Radial: degrees per step, counter-clockwise.")] double? angleDeg = null,
        [Description("Radial: degrees from the original to the last copy; 360 spreads the members evenly round a full circle.")] double? totalAngleDeg = null,
        [Description("Radial: turn each copy with the array (default true); false keeps its orientation.")] bool rotateElements = true,
        [Description("false (default): independent copies. true: a Revit array group (LinearArray / RadialArray).")] bool asGroup = false,
        [Description("true (default): count includes the original. false: count new copies.")] bool includeOriginal = true,
        [Description("true (default): any failure undoes the whole array.")] bool atomic = true,
        [Description("The view for deltaRightMm/deltaUpMm, and the view a Revit array of model elements is created in.")] long viewId = 0,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ArrayArgs(elementIds, kind, count, spacingMm, totalMm, deltaRightMm, deltaUpMm, centerMm, axis, angleDeg,
            totalAngleDeg, rotateElements, asGroup, includeOriginal, atomic, viewId, document);
        return FormatResult(await pipeClient.SendAsync("revit_preview_array_elements", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_array_elements"),
     Description("Arrays elements: linear (spacingMm, totalMm or deltaRightMm/deltaUpMm) or radial (centerMm, axis, angleDeg or totalAngleDeg, rotateElements). Requires approval. count is the number of members, 2-200, including the original unless includeOriginal=false. asGroup=false (default) makes independent copies with Revit's copy per step (model or view path as revit_copy_elements); asGroup=true creates a Revit array (LinearArray / RadialArray) whose count parameter equals count. Collisions with existing elements are not checked. Returns the new ids per step with source-to-copy pairs. Status array_count_out_of_range for a bad count. One transaction, one Revit undo. Run revit_preview_array_elements first.")]
    public async Task<string> ArrayElements(
        [Description("Elements to array.")] long[] elementIds,
        [Description("linear (default) or radial.")] string kind = "linear",
        [Description("Members of the array, 2-200 (radial asGroup: 3-200). With includeOriginal=true the original is one of them.")] int count = 2,
        [Description("Linear: {x,y,z} mm from one member to the next, model axes.")] object? spacingMm = null,
        [Description("Linear: {x,y,z} mm from the original to the last copy.")] object? totalMm = null,
        [Description("Linear: mm per step along the view's right (viewId or the elements' owner view).")] double? deltaRightMm = null,
        [Description("Linear: mm per step along the view's up.")] double? deltaUpMm = null,
        [Description("Radial: {x,y,z} mm, the point the axis passes through.")] object? centerMm = null,
        [Description("Radial: axis x, y or z (default z).")] string axis = "z",
        [Description("Radial: degrees per step, counter-clockwise.")] double? angleDeg = null,
        [Description("Radial: degrees from the original to the last copy; 360 spreads the members evenly round a full circle.")] double? totalAngleDeg = null,
        [Description("Radial: turn each copy with the array (default true); false keeps its orientation.")] bool rotateElements = true,
        [Description("false (default): independent copies. true: a Revit array group (LinearArray / RadialArray).")] bool asGroup = false,
        [Description("true (default): count includes the original. false: count new copies.")] bool includeOriginal = true,
        [Description("true (default): any failure undoes the whole array.")] bool atomic = true,
        [Description("The view for deltaRightMm/deltaUpMm, and the view a Revit array of model elements is created in.")] long viewId = 0,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ArrayArgs(elementIds, kind, count, spacingMm, totalMm, deltaRightMm, deltaUpMm, centerMm, axis, angleDeg,
            totalAngleDeg, rotateElements, asGroup, includeOriginal, atomic, viewId, document);
        return FormatResult(await pipeClient.SendAsync("revit_array_elements", args, cancellationToken));
    }

    private static Dictionary<string, object?> ArrayArgs(
        long[] elementIds, string kind, int count, object? spacingMm, object? totalMm, double? deltaRightMm, double? deltaUpMm,
        object? centerMm, string axis, double? angleDeg, double? totalAngleDeg, bool rotateElements, bool asGroup, bool includeOriginal,
        bool atomic, long viewId, string? document)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["kind"] = kind,
            ["count"] = count,
            ["axis"] = axis,
            ["rotateElements"] = rotateElements,
            ["asGroup"] = asGroup,
            ["includeOriginal"] = includeOriginal,
            ["atomic"] = atomic,
            ["viewId"] = viewId,
            ["document"] = document ?? string.Empty
        };
        // Optional values are sent only when given: which of them is present decides how the spacing or angle is read.
        if (spacingMm != null) args["spacingMm"] = ToJToken(spacingMm);
        if (totalMm != null) args["totalMm"] = ToJToken(totalMm);
        if (deltaRightMm.HasValue) args["deltaRightMm"] = deltaRightMm.Value;
        if (deltaUpMm.HasValue) args["deltaUpMm"] = deltaUpMm.Value;
        if (centerMm != null) args["centerMm"] = ToJToken(centerMm);
        if (angleDeg.HasValue) args["angleDeg"] = angleDeg.Value;
        if (totalAngleDeg.HasValue) args["totalAngleDeg"] = totalAngleDeg.Value;
        return args;
    }

    // ── Groups ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_list_groups", ReadOnly = true),
     Description("Lists group types — model, detail and attached detail groups — with groupTypeId, name, kind, instanceCount and instance ids, memberCount and memberCategories (from the first instance), and hasMixedInstances (instances edited in place). includeMembers names one group type whose first instance's member ids are listed too.")]
    public async Task<string> ListGroups(
        [Description("Only group types whose name contains this text.")] string? nameContains = null,
        [Description("Only this kind: model | detail | attachedDetail.")] string? kind = null,
        [Description("A group type name or id: also list the member ids and categories of its first instance.")] string? includeMembers = null,
        [Description("At most this many group types (default 200).")] int limit = 200,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["nameContains"] = nameContains ?? string.Empty,
            ["kind"] = kind ?? string.Empty,
            ["includeMembers"] = includeMembers ?? string.Empty,
            ["limit"] = limit,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_groups", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_group", ReadOnly = true),
     Description("Previews creating a group, WITHOUT changing the model. Same arguments as revit_create_group. Returns the detected kind (model, or detail for elements of one view), the unique name that would be used, members per category and the blockers — mixed model and view-specific elements come back as group_members_mixed with the offending ids.")]
    public async Task<string> PreviewCreateGroup(
        [Description("Elements to group (up to 2000).")] long[] elementIds,
        [Description("Group type name; made unique against existing group types ('Name 2', …). Default 'MCP Group'.")] string? name = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["name"] = name ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_preview_create_group", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_create_group"),
     Description("Creates a group from elements (doc.Create.NewGroup) and names its type. Requires approval. The kind is detected: all model elements → model group; all view-specific elements of one view → detail group; a mix is refused with status group_members_mixed naming the offending ids (attached detail groups are made only in Revit's UI). Pinned and already grouped elements block. Returns groupId and groupTypeId. One transaction, one Revit undo. Run revit_preview_create_group first.")]
    public async Task<string> CreateGroup(
        [Description("Elements to group (up to 2000).")] long[] elementIds,
        [Description("Group type name; made unique against existing group types ('Name 2', …). Default 'MCP Group'.")] string? name = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIds"] = elementIds ?? [],
            ["name"] = name ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_create_group", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_ungroup"),
     Description("Ungroups group instances (Group.UngroupMembers): the members stay in the model as ordinary elements and the group type stays in the project. Requires approval. Give groupIds (instance ids) or groupType (name or id: every instance is ungrouped). Returns the member ids per instance. To also delete the emptied group types use revit_ungroup_and_delete_type. One transaction, one Revit undo.")]
    public async Task<string> Ungroup(
        [Description("Group instance ids to ungroup.")] long[]? groupIds = null,
        [Description("A group type name or id: ungroup every instance of it. Do not combine with groupIds.")] string? groupType = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = UngroupArgs(groupIds, groupType, document);
        return FormatResult(await pipeClient.SendAsync("revit_ungroup", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_ungroup_and_delete_type", Destructive = true),
     Description("DESTRUCTIVE: ungroups group instances (as revit_ungroup) and then permanently deletes every group type of those instances left without instances. Always requires manual approval in Revit — never bypassed by Direct Edit or covered by a plan. The members are kept. Give groupIds or groupType. One transaction, one Revit undo.")]
    public async Task<string> UngroupAndDeleteType(
        [Description("Group instance ids to ungroup.")] long[]? groupIds = null,
        [Description("A group type name or id: ungroup every instance of it, then delete the type. Do not combine with groupIds.")] string? groupType = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = UngroupArgs(groupIds, groupType, document);
        return FormatResult(await pipeClient.SendAsync("revit_ungroup_and_delete_type", args, cancellationToken));
    }

    private static Dictionary<string, object?> UngroupArgs(long[]? groupIds, string? groupType, string? document) => new()
    {
        ["groupIds"] = groupIds ?? [],
        ["groupType"] = groupType ?? string.Empty,
        ["document"] = document ?? string.Empty
    };

    [McpServerTool(Name = "revit_preview_place_group_instances", ReadOnly = true),
     Description("Previews placing group instances, WITHOUT changing the model. Same arguments as revit_place_group_instances. Returns the resolved group type and kind, the placement path per point (placeGroup for model groups, copiedFromInstance for detail groups) and the nearest level per point.")]
    public async Task<string> PreviewPlaceGroupInstances(
        [Description("Group type name or id (revit_list_groups).")] string groupType,
        [Description("JSON array (up to 500) of {pointMm {x, y, z?}, rotationDeg?, viewId? (detail groups), levelOverride? (level name or id)}.")] object? placements = null,
        [Description("true (default): any failed placement undoes them all.")] bool atomic = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = PlaceGroupArgs(groupType, placements, atomic, document);
        return FormatResult(await pipeClient.SendAsync("revit_preview_place_group_instances", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_place_group_instances"),
     Description("Places instances of a group type at points. Requires approval. Model groups: doc.Create.PlaceGroup at the point, rotated about Z there by rotationDeg; a point without z sits on the nearest level (or levelOverride). Detail groups: PlaceGroup is model-only, so an existing instance is copied (in its view, or into viewId) and moved onto the point — each placement says which path was used. Returns newGroupIds and the resulting point and level per placement. One transaction, one Revit undo. Run revit_preview_place_group_instances first.")]
    public async Task<string> PlaceGroupInstances(
        [Description("Group type name or id (revit_list_groups).")] string groupType,
        [Description("JSON array (up to 500) of {pointMm {x, y, z?}, rotationDeg?, viewId? (detail groups), levelOverride? (level name or id)}.")] object? placements = null,
        [Description("true (default): any failed placement undoes them all.")] bool atomic = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = PlaceGroupArgs(groupType, placements, atomic, document);
        return FormatResult(await pipeClient.SendAsync("revit_place_group_instances", args, cancellationToken));
    }

    private static Dictionary<string, object?> PlaceGroupArgs(string groupType, object? placements, bool atomic, string? document) => new()
    {
        ["groupType"] = groupType ?? string.Empty,
        ["placements"] = ToJToken(placements),
        ["atomic"] = atomic,
        ["document"] = document ?? string.Empty
    };
}

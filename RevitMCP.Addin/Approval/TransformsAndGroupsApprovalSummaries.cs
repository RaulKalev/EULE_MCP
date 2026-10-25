using System.Globalization;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Approval;

/// <summary>
/// Approval summaries of the transform and group tools (#108). <see cref="TryBuild"/> works from the arguments alone
/// (chained into <see cref="ApprovalSummaryBuilder"/>); the mirror and ungroup tools also build a live one
/// (IApprovalSummaryTool) that names the view and counts the members.
/// </summary>
public static class TransformsAndGroupsApprovalSummaries
{
    public static string? TryBuild(McpToolRequest request) => request.ToolName switch
    {
        "revit_mirror_elements" => MirrorFromArguments(request),
        "revit_array_elements" => ArraySummary(request),
        "revit_create_group" => CreateGroup(request),
        "revit_ungroup" => UngroupFromArguments(request, deleteTypes: false),
        "revit_ungroup_and_delete_type" => UngroupFromArguments(request, deleteTypes: true),
        "revit_place_group_instances" => PlaceGroups(request),
        _ => null
    };

    /// <summary>"Mirror-copy 24 elements across a vertical plane through (…)–(…) in view 'L2'."</summary>
    public static string Mirror(McpToolRequest request, string planeDescription)
    {
        var count = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().Count();
        var copy = ToolArguments.GetBool(request.Arguments, "copy", true);
        var atomic = ToolArguments.GetBool(request.Arguments, "atomic", true);
        return $"{(copy ? "Mirror-copy" : "Mirror in place")} {count} element{(count == 1 ? "" : "s")} across {planeDescription}." +
               (copy ? " The originals are not changed." : " The elements themselves are flipped.") +
               (atomic ? " Atomic: any refusal undoes the whole mirror." : " Non-atomic: element by element, keeping what succeeds.");
    }

    private static string MirrorFromArguments(McpToolRequest request)
    {
        request.Arguments.TryGetValue("plane", out var raw);
        var planeRequest = MirrorMath.ParsePlane(raw, ToolArguments.GetLong(request.Arguments, "viewId"), out _);
        if (planeRequest == null)
            return "Mirror elements (the plane could not be read — the request will be rejected).";
        var plane = MirrorMath.Resolve(planeRequest, null, planeRequest.ViewId > 0 ? $"view {planeRequest.ViewId}" : null, out _);
        return Mirror(request, plane?.Description ?? "the given plane");
    }

    private static string ArraySummary(McpToolRequest request)
    {
        var array = ArrayMath.Parse(request.Arguments, out _, out _);
        if (array == null)
            return "Array elements (the request could not be read — it will be rejected).";

        var what = $"{array.ElementIds.Count} element{(array.ElementIds.Count == 1 ? "" : "s")}";
        var members = $"{array.Count} members {(array.IncludeOriginal ? "including" : "plus")} the original";
        var how = array.Kind == ArrayKind.Linear
            ? array.SpacingMm.HasValue ? $"every {Vector(array.SpacingMm.Value)} mm"
            : array.TotalMm.HasValue ? $"spread over {Vector(array.TotalMm.Value)} mm"
            : $"every {Num(array.DeltaRightMm ?? 0)} mm right, {Num(array.DeltaUpMm ?? 0)} mm up in the view"
            : $"round axis {array.Axis} through {Vector(array.CenterMm ?? new PointMm(0, 0, 0))} mm, " +
              (array.AngleDeg.HasValue ? $"{Num(array.AngleDeg.Value)}° per step" : $"{Num(array.TotalAngleDeg ?? 0)}° in total") +
              (array.RotateElements ? string.Empty : ", keeping orientation");
        return $"{(array.Kind == ArrayKind.Linear ? "Linear" : "Radial")} array of {what}: {members}, {how}" +
               (array.AsGroup ? ", as a Revit array group." : $", as {array.Copies * array.ElementIds.Count} independent copies.") +
               " Collisions with existing elements are not checked.";
    }

    private static string CreateGroup(McpToolRequest request)
    {
        var count = ToolArguments.GetLongArray(request.Arguments, "elementIds").Distinct().Count();
        var name = ToolArguments.GetString(request.Arguments, "name");
        return $"Create a group{(string.IsNullOrWhiteSpace(name) ? string.Empty : $" '{name.Trim()}'")} from {count} element{(count == 1 ? "" : "s")} " +
               "(model or detail group, detected from the elements; the name is made unique).";
    }

    /// <summary>The live ungroup summary: how many instances and members, which types, and whether types are deleted.</summary>
    public static string Ungroup(int instanceCount, int memberCount, IReadOnlyCollection<string> typeNames, bool deleteTypes)
    {
        var types = typeNames.Count == 0 ? string.Empty
            : $" of {(typeNames.Count == 1 ? "group type" : "group types")} {string.Join(", ", typeNames.Take(5).Select(n => $"'{n}'"))}" +
              (typeNames.Count > 5 ? $" and {typeNames.Count - 5} more" : string.Empty);
        return $"Ungroup {instanceCount} group instance{(instanceCount == 1 ? "" : "s")}{types}: {memberCount} member element(s) stay in the model." +
               (deleteTypes ? " Then DELETE every one of those group types left without instances." : " The group types stay in the project.");
    }

    private static string UngroupFromArguments(McpToolRequest request, bool deleteTypes)
    {
        var ids = ToolArguments.GetLongArray(request.Arguments, "groupIds").Distinct().Count();
        var type = ToolArguments.GetString(request.Arguments, "groupType");
        var what = ids > 0 ? $"{ids} group instance{(ids == 1 ? "" : "s")}" : $"every instance of group type '{type}'";
        return $"Ungroup {what}; the members stay in the model." +
               (deleteTypes ? " Then DELETE the group types left without instances." : " The group types stay in the project.");
    }

    private static string PlaceGroups(McpToolRequest request)
    {
        request.Arguments.TryGetValue("placements", out var raw);
        var placements = GroupSupport.ParsePlacements(raw, out _);
        var count = placements?.Count ?? (raw is JArray array ? array.Count : 0);
        var rotated = placements?.Count(p => Math.Abs(p.RotationDeg) > 1e-9) ?? 0;
        return $"Place {count} instance{(count == 1 ? "" : "s")} of group type '{ToolArguments.GetString(request.Arguments, "groupType")}'" +
               (rotated > 0 ? $", {rotated} of them rotated" : string.Empty) +
               ". Model groups use PlaceGroup; detail groups are copied from an existing instance.";
    }

    private static string Vector(PointMm v) => $"({Num(v.X)}, {Num(v.Y)}, {Num(v.Z)})";

    private static string Num(double value) => Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
}

using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;

namespace RevitMCP.Addin.Placement;

/// <summary>Model group, detail group, or attached detail group (#108).</summary>
public static class GroupKind
{
    public const string Model = "model";
    public const string Detail = "detail";
    public const string AttachedDetail = "attachedDetail";

    /// <summary>The kind of a group type from its category's BuiltInCategory name; null for anything else.</summary>
    public static string? FromCategory(string? builtInCategory) => builtInCategory switch
    {
        "OST_IOSModelGroups" => Model,
        "OST_IOSDetailGroups" => Detail,
        "OST_IOSAttachedDetailGroups" => AttachedDetail,
        _ => null
    };
}

/// <summary>What is known about one would-be group member, free of Revit types.</summary>
public sealed class GroupMemberInfo
{
    public long Id { get; init; }
    public bool Exists { get; init; } = true;
    public string Category { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;

    /// <summary>The owner view of a view-specific element; null for a model element.</summary>
    public long? OwnerViewId { get; init; }

    /// <summary>The group the element already belongs to.</summary>
    public long? GroupId { get; init; }

    public bool Pinned { get; init; }

    /// <summary>A view, sheet, type or other element Revit does not put in groups.</summary>
    public bool NotGroupable { get; init; }
}

/// <summary>The outcome of <see cref="GroupSupport.DetectKind"/>.</summary>
public sealed class GroupKindResult
{
    /// <summary>model or detail; null when the set cannot be grouped.</summary>
    public string? Kind { get; set; }

    /// <summary>The view of a detail group.</summary>
    public long? ViewId { get; set; }

    public string? Error { get; set; }

    /// <summary>group_members_mixed when model and view-specific elements (or several views) are mixed.</summary>
    public string? Status { get; set; }

    public List<long> ModelIds { get; } = new();
    public Dictionary<long, List<long>> ViewSpecificIds { get; } = new();

    /// <summary>Elements that block the group: missing, pinned, already grouped, not groupable — with the reason.</summary>
    public List<(long Id, string Reason)> Blockers { get; } = new();

    /// <summary>The ids named in the refusal: the smaller side of a model/detail mix, or the blockers.</summary>
    public List<long> Offenders { get; } = new();
}

/// <summary>One requested group placement.</summary>
public sealed class GroupPlacementRequest
{
    public int Index { get; init; }
    public PointMm PointMm { get; init; }

    /// <summary>False when pointMm had no z: the elevation then comes from the level.</summary>
    public bool HasZ { get; init; }

    public double RotationDeg { get; init; }

    /// <summary>The view a detail group is placed in. 0 = the view of the instance it is copied from.</summary>
    public long ViewId { get; init; }

    /// <summary>A level name or id that replaces the nearest-level lookup.</summary>
    public string? LevelOverride { get; init; }
}

/// <summary>
/// The Revit-free part of the group tools (#108): what kind of group a set of elements would make (and why not),
/// unique group names, mixed-instance detection, and the placement and group-type arguments.
/// </summary>
public static class GroupSupport
{
    public const int MaxMembers = 2000;
    public const int MaxPlacements = 500;

    public const string MembersMixedStatus = "group_members_mixed";
    public const string TypeNotFoundStatus = "group_type_not_found";

    /// <summary>The name used when the caller gives none.</summary>
    public const string DefaultName = "MCP Group";

    /// <summary>
    /// Decides the group kind from the members: all model elements → a model group; all view-specific and in one view
    /// → a detail group in that view; anything else → refused, naming the offending ids. Revit makes attached detail
    /// groups only through its UI, so a mixed set is not turned into one.
    /// </summary>
    public static GroupKindResult DetectKind(IReadOnlyList<GroupMemberInfo> members)
    {
        var result = new GroupKindResult();
        foreach (var member in members)
        {
            if (!member.Exists) { result.Blockers.Add((member.Id, "No element with this id exists in the document.")); continue; }
            if (member.NotGroupable) { result.Blockers.Add((member.Id, $"A {Describe(member)} cannot be a group member.")); continue; }
            if (member.GroupId.HasValue)
            {
                result.Blockers.Add((member.Id, $"Already a member of group {member.GroupId.Value}. Revit does not regroup members " +
                                                "outside group edit mode — ungroup that group first, or leave this element out."));
                continue;
            }
            if (member.Pinned)
            {
                result.Blockers.Add((member.Id, "Pinned. Unpin it in Revit first — this tool never unpins elements."));
                continue;
            }

            if (member.OwnerViewId.HasValue)
            {
                if (!result.ViewSpecificIds.TryGetValue(member.OwnerViewId.Value, out var list))
                    result.ViewSpecificIds[member.OwnerViewId.Value] = list = new List<long>();
                list.Add(member.Id);
            }
            else
            {
                result.ModelIds.Add(member.Id);
            }
        }

        if (result.Blockers.Count > 0)
        {
            result.Offenders.AddRange(result.Blockers.Select(b => b.Id));
            result.Error = $"{result.Blockers.Count} element(s) cannot be grouped: " +
                           string.Join("; ", result.Blockers.Take(10).Select(b => $"{b.Id}: {b.Reason}")) +
                           (result.Blockers.Count > 10 ? $"; and {result.Blockers.Count - 10} more" : string.Empty);
            return result;
        }

        var viewSpecificCount = result.ViewSpecificIds.Values.Sum(v => v.Count);
        if (result.ModelIds.Count == 0 && viewSpecificCount == 0)
        {
            result.Error = "No elements to group.";
            return result;
        }

        if (result.ModelIds.Count > 0 && viewSpecificCount > 0)
        {
            result.Status = MembersMixedStatus;
            var viewIds = result.ViewSpecificIds.Values.SelectMany(v => v).ToList();
            result.Offenders.AddRange(viewSpecificCount <= result.ModelIds.Count ? viewIds : result.ModelIds);
            result.Error = $"The set mixes {result.ModelIds.Count} model element(s) with {viewSpecificCount} view-specific element(s) " +
                           $"(view-specific: {Ids(viewIds)}; model: {Ids(result.ModelIds)}). A model group holds model elements and " +
                           "a detail group the elements of one view; Revit makes the combination (an attached detail group) only " +
                           "in its UI. Group them separately.";
            return result;
        }

        if (result.ViewSpecificIds.Count > 1)
        {
            result.Status = MembersMixedStatus;
            var largest = result.ViewSpecificIds.OrderByDescending(v => v.Value.Count).First().Key;
            result.Offenders.AddRange(result.ViewSpecificIds.Where(v => v.Key != largest).SelectMany(v => v.Value));
            result.Error = "A detail group holds the elements of one view, and these belong to " +
                           $"{result.ViewSpecificIds.Count} views: " +
                           string.Join("; ", result.ViewSpecificIds.Select(v => $"view {v.Key}: {Ids(v.Value)}")) + ".";
            return result;
        }

        if (result.ModelIds.Count > 0)
        {
            result.Kind = GroupKind.Model;
        }
        else
        {
            result.Kind = GroupKind.Detail;
            result.ViewId = result.ViewSpecificIds.Keys.First();
        }
        return result;
    }

    /// <summary>
    /// A group type name not yet taken (case-insensitive, as Revit compares names): the requested name, or
    /// "name 2", "name 3", … Empty input uses <see cref="DefaultName"/>.
    /// </summary>
    public static string ResolveUniqueName(string? requested, IEnumerable<string> existingNames)
    {
        var taken = new HashSet<string>(existingNames.Where(n => n != null), StringComparer.OrdinalIgnoreCase);
        var name = string.IsNullOrWhiteSpace(requested) ? DefaultName : requested!.Trim();
        return FamilyTypeNamePlanner.ResolveUniqueName(name, taken);
    }

    /// <summary>Whether a name is one Revit accepts for a group type.</summary>
    public static bool IsValidName(string? name, out string error) => FamilyTypeNamePlanner.IsValidTypeName(name, out error);

    /// <summary>
    /// Instances of one group type that do not all have the same member count: some were edited (members excluded) in
    /// place. Revit reports such a type with mixed instances.
    /// </summary>
    public static bool HasMixedInstances(IEnumerable<int> memberCounts) => memberCounts.Distinct().Skip(1).Any();

    /// <summary>
    /// Reads <c>placements</c>: [{pointMm {x,y,z?}, rotationDeg?, viewId?, levelOverride?}], at most
    /// <see cref="MaxPlacements"/>.
    /// </summary>
    public static List<GroupPlacementRequest>? ParsePlacements(object? raw, out string? error)
    {
        error = null;
        JArray? array = raw switch
        {
            JArray a => a,
            string text when !string.IsNullOrWhiteSpace(text) => TryParseArray(text),
            JValue { Type: JTokenType.String } jv => TryParseArray(jv.Value<string>() ?? string.Empty),
            _ => null
        };
        if (array == null || array.Count == 0)
        {
            error = "Provide placements: [{pointMm {x, y, z?}, rotationDeg?, viewId?, levelOverride?}].";
            return null;
        }
        if (array.Count > MaxPlacements)
        {
            error = $"placements holds {array.Count} entries; the limit is {MaxPlacements} per request.";
            return null;
        }

        var result = new List<GroupPlacementRequest>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            var fields = MirrorMath.ToFieldMap(array[i]);
            if (fields == null)
            {
                error = $"placements[{i}] is not a JSON object.";
                return null;
            }

            JToken? pointToken = null;
            foreach (var name in new[] { "pointMm", "point", "locationMm" })
                if (fields.TryGetValue(name, out var token) && token.Type != JTokenType.Null) { pointToken = token; break; }
            PointMm? point = null;
            var hasZ = false;
            if (pointToken != null)
                point = MirrorMath.PointFromToken(pointToken, out hasZ);
            if (point == null)
            {
                // Flat form: {x, y, z?}.
                var x = MirrorMath.ReadDouble(fields, "x", "xMm");
                var y = MirrorMath.ReadDouble(fields, "y", "yMm");
                var z = MirrorMath.ReadDouble(fields, "z", "zMm");
                if (x.HasValue && y.HasValue)
                {
                    point = new PointMm(x.Value, y.Value, z ?? 0);
                    hasZ = z.HasValue;
                }
            }
            if (point == null)
            {
                error = $"placements[{i}] has no pointMm {{x, y, z?}}.";
                return null;
            }

            result.Add(new GroupPlacementRequest
            {
                Index = i,
                PointMm = point.Value,
                HasZ = hasZ,
                RotationDeg = MirrorMath.ReadDouble(fields, "rotationDeg", "rotation") ?? 0,
                ViewId = MirrorMath.ReadLong(fields, "viewId"),
                LevelOverride = MirrorMath.ReadString(fields, "levelOverride") is { Length: > 0 } level ? level.Trim() : null
            });
        }
        return result;
    }

    /// <summary>
    /// Finds a group type by id or name among (id, name, kind) candidates: the id, else an exact name (case-insensitive),
    /// else a unique name containing the text. Returns the index of the match or -1 with the refusal in
    /// <paramref name="error"/> (status <see cref="TypeNotFoundStatus"/>), including close names.
    /// </summary>
    public static int MatchGroupType(
        string? reference, IReadOnlyList<(long Id, string Name, string Kind)> candidates, out string? error)
    {
        error = null;
        var text = (reference ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Provide groupType: the group type's name or id (revit_list_groups lists them).";
            return -1;
        }

        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            for (var i = 0; i < candidates.Count; i++)
                if (candidates[i].Id == id) return i;
        }

        for (var i = 0; i < candidates.Count; i++)
            if (string.Equals(candidates[i].Name, text, StringComparison.OrdinalIgnoreCase)) return i;

        var containing = Enumerable.Range(0, candidates.Count)
            .Where(i => candidates[i].Name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (containing.Count == 1) return containing[0];

        var suggestions = (containing.Count > 1 ? containing : Enumerable.Range(0, candidates.Count)
                .Where(i => SharesWord(candidates[i].Name, text)).ToList())
            .Take(8).Select(i => $"'{candidates[i].Name}' ({candidates[i].Kind}, id {candidates[i].Id})").ToList();
        error = containing.Count > 1
            ? $"groupType '{text}' matches {containing.Count} group types: {string.Join(", ", suggestions)}. Give the exact name or the id."
            : $"No group type named '{text}'." + (suggestions.Count > 0 ? $" Close names: {string.Join(", ", suggestions)}." : " revit_list_groups lists them.");
        return -1;
    }

    private static bool SharesWord(string name, string text)
    {
        var words = text.Split(new[] { ' ', '_', '-', '.' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3);
        return words.Any(w => name.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    private static string Describe(GroupMemberInfo member) =>
        string.IsNullOrEmpty(member.Category) ? member.ClassName : $"{member.Category} ({member.ClassName})";

    private static string Ids(IEnumerable<long> ids)
    {
        var list = ids.ToList();
        return string.Join(", ", list.Take(20)) + (list.Count > 20 ? $" and {list.Count - 20} more" : string.Empty);
    }

    private static JArray? TryParseArray(string text)
    {
        try { return JToken.Parse(text) as JArray; }
        catch { return null; }
    }
}

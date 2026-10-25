using Autodesk.Revit.DB;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Placement;

/// <summary>A group type as revit_list_groups reports it.</summary>
internal sealed class GroupTypeInfo
{
    public GroupType Type { get; init; } = null!;
    public long Id => Type.Id.Value;
    public string Name { get; init; } = string.Empty;
    public string Kind { get; init; } = GroupKind.Model;
    public List<Group> Instances { get; init; } = new();
}

/// <summary>
/// The Revit side of the group tools (#108): reading group types and instances, resolving a group type by name or
/// id, and what a set of elements is as group members.
/// </summary>
internal static class GroupService
{
    /// <summary>Every group type in the document, with its kind and placed instances.</summary>
    public static List<GroupTypeInfo> GroupTypes(Document doc)
    {
        var result = new List<GroupTypeInfo>();
        foreach (var type in new FilteredElementCollector(doc).OfClass(typeof(GroupType)).Cast<GroupType>())
        {
            string? kind;
            try { kind = GroupKind.FromCategory(type.Category?.BuiltInCategory.ToString()); }
            catch { kind = null; }
            if (kind == null) continue;

            var instances = new List<Group>();
            try
            {
                foreach (Group group in type.Groups) instances.Add(group);
            }
            catch
            {
                // a type whose instances cannot be read is listed with none
            }

            string name;
            try { name = type.Name ?? string.Empty; }
            catch { name = string.Empty; }
            result.Add(new GroupTypeInfo { Type = type, Name = name, Kind = kind, Instances = instances });
        }
        return result.OrderBy(t => t.Kind).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Finds the group type named or numbered by <paramref name="reference"/>; null with the refusal otherwise.</summary>
    public static GroupTypeInfo? Resolve(List<GroupTypeInfo> types, string? reference, out string? error)
    {
        var index = GroupSupport.MatchGroupType(reference, types.Select(t => (t.Id, t.Name, t.Kind)).ToList(), out error);
        return index < 0 ? null : types[index];
    }

    /// <summary>Member ids of a group instance.</summary>
    public static List<ElementId> MemberIds(Group group)
    {
        try { return group.GetMemberIds()?.ToList() ?? new List<ElementId>(); }
        catch { return new List<ElementId>(); }
    }

    /// <summary>Member counts per category, largest first.</summary>
    public static Dictionary<string, int> CategoriesOf(Document doc, IEnumerable<ElementId> ids) =>
        ids.Select(doc.GetElement)
            .Where(e => e != null)
            .GroupBy(e => SafeCategory(e!))
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

    /// <summary>What each element is as a would-be group member, for <see cref="GroupSupport.DetectKind"/>.</summary>
    public static List<GroupMemberInfo> Members(Document doc, IEnumerable<long> ids)
    {
        var result = new List<GroupMemberInfo>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(MoveElementsService.ToElementId(id));
            if (element == null)
            {
                result.Add(new GroupMemberInfo { Id = id, Exists = false });
                continue;
            }

            var ownerView = MoveElementsService.OwnerViewOf(doc, element);
            bool pinned;
            try { pinned = element.Pinned; }
            catch { pinned = false; }
            result.Add(new GroupMemberInfo
            {
                Id = id,
                Category = SafeCategory(element),
                ClassName = element.GetType().Name,
                OwnerViewId = ownerView?.Id.Value,
                GroupId = element.GroupId == ElementId.InvalidElementId ? null : element.GroupId.Value,
                Pinned = pinned,
                NotGroupable = element is View or ElementType or Level || element.Category == null
            });
        }
        return result;
    }

    public static string SafeCategory(Element element)
    {
        try { return element.Category?.Name ?? $"({element.GetType().Name})"; }
        catch { return $"({element.GetType().Name})"; }
    }

    /// <summary>The group instances an ungroup request names: groupIds, or every instance of groupType.</summary>
    public static (List<Group> Groups, GroupTypeInfo? Type, string? Error, string? Status) UngroupTargets(Document doc, Dictionary<string, object?> arguments)
    {
        var groupIds = ToolArguments.GetLongArray(arguments, "groupIds");
        var typeReference = ToolArguments.GetString(arguments, "groupType");
        if (groupIds.Length > 0 && !string.IsNullOrWhiteSpace(typeReference))
            return (new List<Group>(), null, "Give groupIds or groupType, not both.", "validation_failed");

        if (groupIds.Length == 0 && string.IsNullOrWhiteSpace(typeReference))
            return (new List<Group>(), null, "Provide groupIds (group instance ids) or groupType (a group type name or id: every instance is ungrouped).", "validation_failed");

        if (groupIds.Length > 0)
        {
            var groups = new List<Group>();
            var problems = new List<string>();
            foreach (var id in groupIds.Distinct())
            {
                var element = doc.GetElement(MoveElementsService.ToElementId(id));
                if (element is Group group) groups.Add(group);
                else problems.Add(element == null ? $"{id} does not exist" : $"{id} is a {element.GetType().Name}, not a group instance");
            }
            return problems.Count > 0
                ? (groups, null, "These are not group instances: " + string.Join("; ", problems) +
                                 ". revit_list_groups lists group types; their instances are ungrouped with groupType.", "validation_failed")
                : (groups, null, null, null);
        }

        var types = GroupTypes(doc);
        var type = Resolve(types, typeReference, out var error);
        if (type == null) return (new List<Group>(), null, error, GroupSupport.TypeNotFoundStatus);
        if (type.Instances.Count == 0)
            return (new List<Group>(), type, $"Group type '{type.Name}' has no placed instances: there is nothing to ungroup.", "validation_failed");
        return (type.Instances, type, null, null);
    }
}

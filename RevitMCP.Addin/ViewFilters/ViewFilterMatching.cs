using System.Globalization;

namespace RevitMCP.Addin.ViewFilters;

/// <summary>A named model element a reference can point at (a view, a filter, a parameter, a category), for matching without Revit.</summary>
public sealed class NamedRef
{
    public NamedRef(long id, string name, string? builtInName = null, string? guid = null)
    {
        Id = id;
        Name = name;
        BuiltInName = builtInName;
        Guid = guid;
    }

    public long Id { get; }
    public string Name { get; }

    /// <summary>The BuiltInParameter or BuiltInCategory name, when it has one.</summary>
    public string? BuiltInName { get; }

    /// <summary>The shared parameter GUID, when it has one.</summary>
    public string? Guid { get; }
}

public enum RefMatchKind
{
    Found,
    NotFound,
    Ambiguous
}

public sealed class RefMatch
{
    public RefMatch(RefMatchKind kind, NamedRef? found, IReadOnlyList<NamedRef> candidates)
    {
        Kind = kind;
        Found = found;
        Candidates = candidates;
    }

    public RefMatchKind Kind { get; }
    public NamedRef? Found { get; }
    public IReadOnlyList<NamedRef> Candidates { get; }
}

/// <summary>
/// Finds what a reference means, in order: element id, shared parameter GUID, built-in name, then the display name
/// (exact, case-insensitive). Two elements with the same display name are ambiguous, never a guess. Pure.
/// </summary>
public static class ViewFilterRefMatcher
{
    public static RefMatch Match(string? reference, IReadOnlyCollection<NamedRef> items)
    {
        var text = (reference ?? string.Empty).Trim();
        if (text.Length == 0) return new RefMatch(RefMatchKind.NotFound, null, Array.Empty<NamedRef>());

        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
        {
            var byId = items.FirstOrDefault(i => i.Id == id);
            if (byId != null) return new RefMatch(RefMatchKind.Found, byId, new[] { byId });
        }

        var byGuid = items.FirstOrDefault(i => i.Guid != null && string.Equals(i.Guid, text, StringComparison.OrdinalIgnoreCase));
        if (byGuid != null) return new RefMatch(RefMatchKind.Found, byGuid, new[] { byGuid });

        var byBuiltIn = items.FirstOrDefault(i => i.BuiltInName != null && string.Equals(i.BuiltInName, text, StringComparison.OrdinalIgnoreCase));
        if (byBuiltIn != null) return new RefMatch(RefMatchKind.Found, byBuiltIn, new[] { byBuiltIn });

        var byName = items.Where(i => string.Equals(i.Name, text, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count switch
        {
            0 => new RefMatch(RefMatchKind.NotFound, null, Array.Empty<NamedRef>()),
            1 => new RefMatch(RefMatchKind.Found, byName[0], byName),
            _ => new RefMatch(RefMatchKind.Ambiguous, null, byName)
        };
    }

    /// <summary>"'Name' (id 5), 'Name' (id 7)", for a message about an ambiguous reference.</summary>
    public static string List(IEnumerable<NamedRef> items, int max = 10)
    {
        var list = items.ToList();
        var text = string.Join(", ", list.Take(max).Select(i => $"'{i.Name}' (id {i.Id.ToString(CultureInfo.InvariantCulture)})"));
        return list.Count > max ? $"{text} and {list.Count - max} more" : text;
    }
}

public static class ViewFilterCopyActions
{
    public const string Create = "create";
    public const string Reuse = "reuse";
    public const string Overwrite = "overwrite";
    public const string Skip = "skip";
    public const string Fail = "fail";
}

/// <summary>What a copy does with a source filter given the names in the target: filters are matched by name. Pure.</summary>
public static class ViewFilterCopyMatcher
{
    public static string Decide(string sourceName, IReadOnlyCollection<string> targetNames, string ifExists, bool sameDocument)
    {
        if (sameDocument) return ViewFilterCopyActions.Reuse;
        if (!targetNames.Contains(sourceName, StringComparer.OrdinalIgnoreCase)) return ViewFilterCopyActions.Create;
        return ifExists switch
        {
            ViewFilterIfExists.Overwrite => ViewFilterCopyActions.Overwrite,
            ViewFilterIfExists.Skip => ViewFilterCopyActions.Skip,
            ViewFilterIfExists.Fail => ViewFilterCopyActions.Fail,
            _ => ViewFilterCopyActions.Reuse
        };
    }
}

/// <summary>Filter order in a view: the named filters first in the given order, the others after them as they were. Pure.</summary>
public static class ViewFilterOrderPlanner
{
    /// <summary>The new order; <paramref name="missing"/> holds the requested ids the view does not have.</summary>
    public static List<long> Merge(IReadOnlyList<long> current, IReadOnlyList<long> requested, out List<long> missing)
    {
        missing = requested.Where(id => !current.Contains(id)).Distinct().ToList();
        var result = new List<long>();
        foreach (var id in requested)
            if (current.Contains(id) && !result.Contains(id)) result.Add(id);
        foreach (var id in current)
            if (!result.Contains(id)) result.Add(id);
        return result;
    }

    /// <summary>What a view's filters will be after the additions and removals of one call, in Revit's append order.</summary>
    public static List<long> AfterChanges(IReadOnlyList<long> current, IEnumerable<long> added, IEnumerable<long> removed)
    {
        var removedSet = new HashSet<long>(removed);
        var result = current.Where(id => !removedSet.Contains(id)).ToList();
        foreach (var id in added)
            if (!result.Contains(id)) result.Add(id);
        return result;
    }
}

/// <summary>A view as the routing rule needs to see it.</summary>
public sealed class ViewRouteInput
{
    public long ViewId { get; set; }
    public string ViewName { get; set; } = string.Empty;
    public bool IsTemplate { get; set; }

    /// <summary>The view's template; 0 or less when it has none.</summary>
    public long TemplateId { get; set; }
    public string? TemplateName { get; set; }
    public bool TemplateControlsFilters { get; set; }
}

public sealed class ViewRoute
{
    public long RequestedViewId { get; set; }
    public string RequestedViewName { get; set; } = string.Empty;
    public long TargetId { get; set; }
    public string TargetName { get; set; } = string.Empty;
    public bool TargetIsTemplate { get; set; }

    /// <summary>True when the view asked for is controlled by its template, so the change lands on the template.</summary>
    public bool RoutedToTemplate { get; set; }
}

/// <summary>
/// Where a filter change goes (#134): a template is its own target; a view whose filters its template controls sends
/// the change to the template; any other view takes it itself. Pure.
/// </summary>
public static class ViewFilterRouting
{
    public static ViewRoute Route(ViewRouteInput view)
    {
        var toTemplate = !view.IsTemplate && view.TemplateId > 0 && view.TemplateControlsFilters;
        return new ViewRoute
        {
            RequestedViewId = view.ViewId,
            RequestedViewName = view.ViewName,
            TargetId = toTemplate ? view.TemplateId : view.ViewId,
            TargetName = toTemplate ? view.TemplateName ?? string.Empty : view.ViewName,
            TargetIsTemplate = toTemplate || view.IsTemplate,
            RoutedToTemplate = toTemplate
        };
    }

    /// <summary>Does a template control filters? It must list the V/G filters setting, and not have it switched off.</summary>
    public static bool TemplateControlsFilters(IEnumerable<long> templateParameterIds, IEnumerable<long> nonControlledIds, long filtersParameterId)
        => templateParameterIds.Contains(filtersParameterId) && !nonControlledIds.Contains(filtersParameterId);
}

/// <summary>Approval text of the view filter write tools from the request alone. Pure.</summary>
public static class ViewFilterSummaries
{
    public static string Create(ViewFilterCreateRequest request)
    {
        var parts = request.Filters.Take(10).Select(f => f.Kind == ViewFilterKinds.Selection
            ? $"'{f.Name}' (selection, {f.ElementIds.Count} element(s))"
            : $"'{f.Name}' ({string.Join(", ", f.Categories.Take(5))}{(f.Categories.Count > 5 ? ", ..." : string.Empty)}: {ViewFilterRuleParser.CountRules(f.Rules)} rule(s))");
        return $"Create {request.Filters.Count} view filter(s): {string.Join("; ", parts)}{More(request.Filters.Count, 10)}. Not added to any view.";
    }

    public static string Edit(ViewFilterEditRequest request)
    {
        var parts = request.Edits.Take(10).Select(e =>
        {
            var changes = new List<string>();
            if (e.NewName != null) changes.Add($"rename to '{e.NewName}'");
            if (e.Categories != null) changes.Add($"categories {string.Join(", ", e.Categories.Take(5))}");
            if (e.HasRules) changes.Add($"{ViewFilterRuleParser.CountRules(e.Rules)} new rule(s)");
            if (e.ClearRules) changes.Add("remove the rules");
            if (e.ElementIds != null) changes.Add($"replace members with {e.ElementIds.Count} element(s)");
            if (e.AddElementIds != null) changes.Add($"add {e.AddElementIds.Count} element(s)");
            if (e.RemoveElementIds != null) changes.Add($"remove {e.RemoveElementIds.Count} element(s)");
            return $"'{e.Filter}': {string.Join(", ", changes)}";
        });
        return $"Edit {request.Edits.Count} view filter(s): {string.Join("; ", parts)}{More(request.Edits.Count, 10)}. Every view and template using them changes with them.";
    }

    public static string Delete(IReadOnlyCollection<string> filters) =>
        $"DESTRUCTIVE: Delete {filters.Count} view filter(s): {string.Join(", ", filters.Take(10).Select(f => $"'{f}'"))}{More(filters.Count, 10)}. " +
        "They disappear from every view and template using them. Manual approval required.";

    public static string Apply(ViewFilterApplyRequest request)
    {
        var parts = new List<string>();
        if (request.Add.Count > 0)
        {
            var withSettings = request.Add.Count(a => a.HasSettings);
            parts.Add($"add {string.Join(", ", request.Add.Take(10).Select(a => $"'{a.Filter}'"))}{More(request.Add.Count, 10)}" +
                      (withSettings > 0 ? $" ({withSettings} with settings)" : " (no graphics)"));
        }
        if (request.Remove.Count > 0) parts.Add($"remove {string.Join(", ", request.Remove.Take(10).Select(r => $"'{r}'"))}{More(request.Remove.Count, 10)}");
        if (request.Order.Count > 0) parts.Add($"set the filter order ({request.Order.Count} named)");
        return $"View filters on {request.Views.Count} view(s) or template(s): {string.Join("; ", parts)}.";
    }

    public static string Copy(ViewFilterCopyRequest request)
    {
        var what = request.Filters.Count > 0 ? $"{request.Filters.Count} view filter(s)" : "the view filters";
        var from = request.SourceView.Length > 0 ? $" of view '{request.SourceView}'" : string.Empty;
        var doc = request.SourceDocument.Length > 0 ? $" from document '{request.SourceDocument}'" : string.Empty;
        var to = request.TargetViews.Count > 0 ? $" and add them to {request.TargetViews.Count} view(s) or template(s)" : string.Empty;
        return $"Copy {what}{from}{doc}{to} (a filter that exists by name: {request.IfExists}).";
    }

    private static string More(int count, int shown) => count > shown ? $" and {count - shown} more" : string.Empty;
}

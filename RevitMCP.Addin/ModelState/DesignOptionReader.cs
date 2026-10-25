using Autodesk.Revit.DB;

namespace RevitMCP.Addin.ModelState;

public sealed class DesignOptionRow
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsPrimary { get; set; }
    public long? ElementCount { get; set; }
}

public sealed class DesignOptionSetRow
{
    public long SetId { get; set; }
    public string SetName { get; set; } = string.Empty;
    public List<DesignOptionRow> Options { get; set; } = new();
}

/// <summary>
/// Design option sets and their options (#109): primary flag and, on request, element counts per option
/// (<c>ElementDesignOptionFilter</c>). Which views show which option is not reported: the API has no reliable read of
/// a view's design option visibility. Option membership can only be changed in the Revit UI. API thread only.
/// </summary>
public static class DesignOptionReader
{
    /// <summary>Status code of the informational note that options can be read but not assigned (#109).</summary>
    public const string SetOnlyStatus = "design_option_set_only";

    public const string SetOnlyNote =
        "Design options are read-only here: the Revit API cannot move an element into or out of an option (elements are " +
        "created in the active option in the Revit UI), and which views show which option is not reported.";

    public static List<DesignOptionSetRow> Read(Document doc, bool countElements, CancellationToken cancellationToken)
    {
        var sets = new Dictionary<long, DesignOptionSetRow>();
        foreach (var set in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_DesignOptionSets).WhereElementIsNotElementType())
        {
            sets[set.Id.Value] = new DesignOptionSetRow { SetId = set.Id.Value, SetName = SafeName(set) };
        }

        foreach (var option in new FilteredElementCollector(doc).OfClass(typeof(DesignOption)).Cast<DesignOption>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long setId = -1;
            try
            {
                var p = option.get_Parameter(BuiltInParameter.OPTION_SET_ID);
                if (p != null && p.StorageType == StorageType.ElementId) setId = p.AsElementId()?.Value ?? -1;
            }
            catch { }

            if (!sets.TryGetValue(setId, out var set))
            {
                set = new DesignOptionSetRow { SetId = setId, SetName = setId >= 0 ? SafeName(doc.GetElement(new ElementId(setId))) : "(unknown set)" };
                sets[setId] = set;
            }

            var row = new DesignOptionRow { Id = option.Id.Value, Name = SafeName(option) };
            try { row.IsPrimary = option.IsPrimary; } catch { }
            if (countElements)
            {
                try
                {
                    row.ElementCount = new FilteredElementCollector(doc)
                        .WherePasses(new ElementDesignOptionFilter(option.Id))
                        .WhereElementIsNotElementType()
                        .GetElementCount();
                }
                catch
                {
                    row.ElementCount = null;
                }
            }
            set.Options.Add(row);
        }

        foreach (var set in sets.Values)
            set.Options = set.Options.OrderByDescending(o => o.IsPrimary).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        return sets.Values.OrderBy(s => s.SetName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string SafeName(Element? element)
    {
        if (element == null) return string.Empty;
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}

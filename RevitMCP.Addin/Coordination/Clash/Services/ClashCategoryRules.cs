namespace RevitMCP.Addin.Coordination.Clash.Services;

/// <summary>
/// What the discovery service knows about one category of a document, read from the Revit API.
/// Pure data, so the clashable rule can be unit-tested without Revit.
/// </summary>
public class ClashCategoryFacts
{
    /// <summary>Category name as the document shows it (may be localized).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>BuiltInCategory name (e.g. "OST_Doors"); empty when the category is not built in (import or link file categories).</summary>
    public string BuiltInName { get; set; } = string.Empty;

    /// <summary>Revit <c>CategoryType</c> name: "Model", "Annotation", "Internal", "AnalyticalModel".</summary>
    public string CategoryType { get; set; } = string.Empty;

    public bool IsTagCategory { get; set; }
    public bool IsSubcategory { get; set; }

    /// <summary>Number of view-independent instances. Null when not counted (name resolution only).</summary>
    public int? ElementCount { get; set; }

    /// <summary>Whether a sampled instance has a solid with volume. Null when not checked (name resolution only).</summary>
    public bool? HasSolidGeometry { get; set; }
}

public class ClashCategoryVerdict
{
    public bool Clashable { get; set; }
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// Decides which categories clash detection accepts: any built-in, top-level model category that is
/// physical (not rooms, spaces, systems, groups, lines, analytical, …) and, when counted, has instances
/// with solid geometry. The curated list below is only a preference order and a set of English aliases.
/// </summary>
public static class ClashCategoryRules
{
    /// <summary>The pseudo-category for DWG/DXF/IFC import instances; handled separately from model categories.</summary>
    public const string ImportedGeometry = "ImportedGeometry";

    /// <summary>Common clash categories, listed first in this order; everything else follows by name.</summary>
    private static readonly (string BuiltInName, string Alias)[] _preferred =
    [
        ("OST_CableTray",            "Cable Trays"),
        ("OST_Conduit",              "Conduits"),
        ("OST_DuctCurves",           "Ducts"),
        ("OST_PipeCurves",           "Pipes"),
        ("OST_Walls",                "Walls"),
        ("OST_Floors",               "Floors"),
        ("OST_Ceilings",             "Ceilings"),
        ("OST_StructuralFraming",    "Structural Framing"),
        ("OST_StructuralColumns",    "Structural Columns"),
        ("OST_StructuralFoundation", "Structural Foundations"),
        ("OST_GenericModel",         "Generic Models"),
        ("OST_MechanicalEquipment",  "Mechanical Equipment"),
        ("OST_ElectricalFixtures",   "Electrical Fixtures"),
        ("OST_FireAlarmDevices",     "Fire Alarm Devices"),
    ];

    /// <summary>
    /// Model categories with no physical body of their own: spatial elements, systems and circuits, groups and
    /// assemblies (their members are collected in their own categories), lines, view-owned detail items, points.
    /// </summary>
    private static readonly HashSet<string> _nonPhysical = new(StringComparer.OrdinalIgnoreCase)
    {
        "OST_Rooms", "OST_Areas", "OST_MEPSpaces", "OST_HVAC_Zones",
        "OST_RvtLinks", "OST_Coordination_Model", "OST_PointClouds", "OST_Cameras",
        "OST_Lines", "OST_SketchLines", "OST_RoomSeparationLines", "OST_MEPSpaceSeparationLines", "OST_AreaSchemeLines",
        "OST_Wire", "OST_ElectricalCircuit", "OST_SwitchSystem", "OST_ElectricalLoadClassifications",
        "OST_DuctSystem", "OST_PipingSystem", "OST_CableTrayRun", "OST_ConduitRun", "OST_PipeSegments",
        "OST_IOSModelGroups", "OST_IOSDetailGroups", "OST_Assemblies",
        "OST_DetailComponents",
        "OST_SharedBasePoint", "OST_ProjectBasePoint", "OST_SitePoint", "OST_SiteProperty",
        "OST_Materials", "OST_ProjectInformation",
    };

    /// <summary>
    /// Applies the clashable rule. Count and geometry are only checked when set, so the same rule serves
    /// discovery (all facts) and name resolution (kind only).
    /// </summary>
    public static ClashCategoryVerdict Evaluate(ClashCategoryFacts facts)
    {
        if (!string.Equals(facts.CategoryType, "Model", StringComparison.OrdinalIgnoreCase) || facts.IsTagCategory)
            return Reject($"not a model category ({(facts.IsTagCategory ? "tag" : facts.CategoryType)})");
        if (facts.IsSubcategory)
            return Reject("a subcategory; use its parent category");
        if (string.IsNullOrWhiteSpace(facts.BuiltInName))
            return Reject($"not a built-in model category (import or link categories go through '{ImportedGeometry}')");
        if (facts.BuiltInName.IndexOf("Analytical", StringComparison.OrdinalIgnoreCase) >= 0)
            return Reject("an analytical category");
        if (_nonPhysical.Contains(facts.BuiltInName))
            return Reject("has no physical geometry (spatial, system, group, line or point category)");
        if (facts.ElementCount is <= 0)
            return Reject("has no elements");
        if (facts.HasSolidGeometry == false)
            return Reject("no sampled element has solid geometry");
        return new ClashCategoryVerdict { Clashable = true };
    }

    /// <summary>
    /// Maps a curated English alias ("Cable Trays") or a BuiltInCategory name ("OST_Doors", case-insensitive)
    /// to the BuiltInCategory name. Returns null for anything else; the caller then resolves the name against
    /// the document's categories.
    /// </summary>
    public static string? TryResolveAlias(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        foreach (var (bic, alias) in _preferred)
            if (string.Equals(alias, trimmed, StringComparison.OrdinalIgnoreCase)) return bic;
        if (trimmed.StartsWith("OST_", StringComparison.OrdinalIgnoreCase))
            return "OST_" + trimmed.Substring(4);
        return null;
    }

    /// <summary>Position in the preferred list, or <see cref="int.MaxValue"/> for every other category.</summary>
    public static int PreferenceRank(string builtInName)
    {
        for (var i = 0; i < _preferred.Length; i++)
            if (string.Equals(_preferred[i].BuiltInName, builtInName, StringComparison.OrdinalIgnoreCase)) return i;
        return int.MaxValue;
    }

    /// <summary>Orders categories: preferred ones first in curated order, then the rest by name.</summary>
    public static List<T> Order<T>(IEnumerable<T> items, Func<T, string> builtInName, Func<T, string> name) =>
        items.OrderBy(i => PreferenceRank(builtInName(i)))
             .ThenBy(i => name(i), StringComparer.OrdinalIgnoreCase)
             .ToList();

    private static ClashCategoryVerdict Reject(string reason) => new() { Clashable = false, Reason = reason };
}

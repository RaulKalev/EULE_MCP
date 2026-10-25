using RevitMCP.Addin.Placement;

namespace RevitMCP.Addin.Geometry;

/// <summary>
/// Classifies what a ray hit as a wall, ceiling, floor, roof or other surface (#107). The orientation of the face
/// decides first (<see cref="AlignmentMath.ClassifySurface"/>: linked IFC geometry often has the wrong category);
/// the category refines it: anything in the Roofs category is a roof, and a sloped face (orientation "other") takes
/// the kind of its category (a sloped wall, a ramped floor). Pure; unit tested.
/// </summary>
public static class SurfaceClassifier
{
    public const string Wall = AlignmentMath.SurfaceWall;
    public const string Ceiling = AlignmentMath.SurfaceCeiling;
    public const string Floor = AlignmentMath.SurfaceFloor;
    public const string Roof = "roof";
    public const string Other = AlignmentMath.SurfaceOther;
    public const string Any = "any";

    /// <summary>Surface kinds a caller may ask for.</summary>
    public static readonly string[] Requestable = { Wall, Ceiling, Floor, Roof, Any };

    public static bool IsRequestable(string kind) => Requestable.Contains(kind);

    /// <summary>
    /// The kind of a hit surface. <paramref name="normalTowardCaster"/> is the face normal turned back towards the ray
    /// origin (a ceiling seen from below points down). <paramref name="builtInCategory"/> is the category's
    /// BuiltInCategory name (<c>OST_Roofs</c>), <paramref name="categoryName"/> its display name; either may be empty.
    /// </summary>
    public static string Classify(Vec3 normalTowardCaster, string? builtInCategory, string? categoryName, double angleToleranceDegrees)
    {
        var byCategory = KindOfCategory(builtInCategory, categoryName);
        if (byCategory == Roof) return Roof;

        var byOrientation = AlignmentMath.ClassifySurface(normalTowardCaster, angleToleranceDegrees);
        if (byOrientation != Other) return byOrientation;
        return byCategory ?? Other;
    }

    /// <summary>True when a classified hit satisfies a requested kind; <c>any</c> accepts everything.</summary>
    public static bool Satisfies(string requested, string classified) =>
        requested == Any || requested == classified;

    /// <summary>The surface kind a category stands for, or null when it says nothing (Generic Models, IFC proxies, …).</summary>
    public static string? KindOfCategory(string? builtInCategory, string? categoryName)
    {
        switch (builtInCategory)
        {
            case "OST_Roofs": case "OST_RoofSoffit": case "OST_Fascia": case "OST_Gutter": return Roof;
            case "OST_Walls": case "OST_CurtainWallPanels": case "OST_CurtainWallMullions": case "OST_Cornices": return Wall;
            case "OST_Ceilings": return Ceiling;
            case "OST_Floors": case "OST_StructuralFoundation": case "OST_Ramps": return Floor;
        }

        switch ((categoryName ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "roofs": case "roof soffits": case "fascias": case "gutters": return Roof;
            case "walls": case "curtain panels": case "curtain wall mullions": return Wall;
            case "ceilings": return Ceiling;
            case "floors": case "structural foundations": case "ramps": return Floor;
            default: return null;
        }
    }
}

/// <summary>
/// Category matching of the ray tools (#107): the default blockers of a line of sight and the match of a caller's
/// category names against a hit's BuiltInCategory or display name. Pure; unit tested.
/// </summary>
public static class GeometryCategories
{
    /// <summary>What blocks a line of sight unless the caller names other categories.</summary>
    public static readonly string[] DefaultBlockingBuiltIns =
    {
        "OST_Walls", "OST_Floors", "OST_Ceilings", "OST_Roofs", "OST_Doors", "OST_StructuralColumns",
        "OST_StructuralFraming", "OST_CurtainWallPanels", "OST_GenericModel"
    };

    /// <summary>Display names of the defaults (English UI), for the result and for links whose categories resolve by name only.</summary>
    public static readonly string[] DefaultBlockingNames =
    {
        "Walls", "Floors", "Ceilings", "Roofs", "Doors", "Structural Columns", "Structural Framing", "Curtain Panels", "Generic Models"
    };

    /// <summary>
    /// True when a hit's category is in <paramref name="wanted"/>. An entry matches the BuiltInCategory name
    /// (<c>OST_Walls</c>, with or without the <c>OST_</c> prefix) or the display name, case-insensitive, ignoring
    /// spaces. An empty set matches everything.
    /// </summary>
    public static bool Matches(ICollection<string> wanted, string? builtInCategory, string? categoryName)
    {
        if (wanted.Count == 0) return true;
        var bic = Normalize(builtInCategory);
        var bicShort = bic.StartsWith("ost_", StringComparison.Ordinal) ? bic.Substring(4) : bic;
        var name = Normalize(categoryName);
        foreach (var entry in wanted)
        {
            var w = Normalize(entry);
            if (w.Length == 0) continue;
            if (w == bic || w == bicShort || w == name) return true;
            if (w.StartsWith("ost_", StringComparison.Ordinal) && w.Substring(4) == bicShort) return true;
        }
        return false;
    }

    /// <summary>The default blockers as one set (BuiltInCategory names and English display names).</summary>
    public static HashSet<string> DefaultBlocking() =>
        new(DefaultBlockingBuiltIns.Concat(DefaultBlockingNames), StringComparer.OrdinalIgnoreCase);

    private static string Normalize(string? value) =>
        new string((value ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
}

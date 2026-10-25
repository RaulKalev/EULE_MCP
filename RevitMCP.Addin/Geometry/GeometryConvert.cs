using Autodesk.Revit.DB;
using RevitMCP.Addin.Placement;

namespace RevitMCP.Addin.Geometry;

/// <summary>
/// Converts Revit geometry to the shared geometry DTOs (#107): <see cref="XYZ"/> in internal feet to
/// <see cref="PointMmDto"/>, <see cref="BoundingBoxXYZ"/> (with its own transform and an optional link transform) to
/// <see cref="BoxMmDto"/>, and back for collectors (<see cref="Outline"/>).
/// </summary>
internal static class GeometryConvert
{
    public static Vec3 ToVec(XYZ p) => new(p.X, p.Y, p.Z);

    public static XYZ ToXyz(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>A point in feet as mm.</summary>
    public static Vec3 ToMm(XYZ p) => new(GeometryUnits.FtToMm(p.X), GeometryUnits.FtToMm(p.Y), GeometryUnits.FtToMm(p.Z));

    /// <summary>A point in mm as feet.</summary>
    public static XYZ FromMm(Vec3 mm) => new(GeometryUnits.MmToFt(mm.X), GeometryUnits.MmToFt(mm.Y), GeometryUnits.MmToFt(mm.Z));

    public static PointMmDto Point(XYZ p) => PointMmDto.FromFeet(p.X, p.Y, p.Z);

    /// <summary>A point in host coordinates: <paramref name="toHost"/> maps link coordinates to the host (null = identity).</summary>
    public static PointMmDto Point(XYZ p, Transform? toHost) => Point(toHost == null ? p : toHost.OfPoint(p));

    public static DirectionDto? Direction(XYZ? v, Transform? toHost = null)
    {
        if (v == null || v.IsZeroLength()) return null;
        var host = toHost == null ? v : toHost.OfVector(v);
        return DirectionDto.From(host.X, host.Y, host.Z);
    }

    /// <summary>
    /// The host-coordinate box (mm) of a Revit bounding box: its eight corners through the box's own transform and
    /// <paramref name="toHost"/>. For a rotated link the result is the box around the rotated box.
    /// </summary>
    public static BoxMm HostBox(BoundingBoxXYZ box, Transform? toHost = null)
    {
        var own = box.Transform ?? Transform.Identity;
        var corners = new List<Vec3>(8);
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
        {
            var p = own.OfPoint(new XYZ(x, y, z));
            if (toHost != null) p = toHost.OfPoint(p);
            corners.Add(ToMm(p));
        }
        return BoxMath.Enclosing(corners);
    }

    public static BoxMmDto Box(BoundingBoxXYZ box, Transform? toHost = null) => BoxMmDto.FromMm(HostBox(box, toHost));

    /// <summary>
    /// A host box (mm) expressed in a link's coordinates, for a collector in the link document: the eight corners
    /// through the inverse of the link transform, then the box around them (conservative for rotated links — the
    /// caller re-checks each element in host coordinates).
    /// </summary>
    public static BoxMm ToLinkBoxMm(BoxMm hostBox, Transform toHost)
    {
        var inverse = toHost.Inverse;
        var corners = BoxMath.Corners(hostBox).Select(c => ToMm(inverse.OfPoint(FromMm(c))));
        return BoxMath.Enclosing(corners);
    }

    /// <summary>The Revit outline (feet) of a box in mm.</summary>
    public static Outline Outline(BoxMm box) => new(FromMm(box.Min), FromMm(box.Max));

    /// <summary>The bounding box an element is measured by: the model box, or the owner view's box for view-specific elements.</summary>
    public static BoundingBoxXYZ? ElementBox(Element element, out string source)
    {
        source = "model";
        try
        {
            var box = element.get_BoundingBox(null);
            if (box != null) return box;
            if (element.ViewSpecific && element.OwnerViewId != ElementId.InvalidElementId &&
                element.Document.GetElement(element.OwnerViewId) is View view)
            {
                source = "ownerView";
                return element.get_BoundingBox(view);
            }
        }
        catch { }
        return null;
    }

    /// <summary>The element's representative point in feet: its location point, else its bounding box centre.</summary>
    public static XYZ? RepresentativePoint(Element element)
    {
        try
        {
            if (element.Location is LocationPoint lp) return lp.Point;
            var box = ElementBox(element, out _);
            if (box != null)
            {
                var t = box.Transform ?? Transform.Identity;
                return t.OfPoint((box.Min + box.Max) * 0.5);
            }
        }
        catch { }
        return null;
    }

    public static string SafeName(Element element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>The BuiltInCategory name of a category (<c>OST_Walls</c>), or empty.</summary>
    public static string BuiltInName(Category? category)
    {
        if (category == null) return string.Empty;
        try
        {
            var value = category.Id.Value;
            return value < 0 && Enum.IsDefined(typeof(BuiltInCategory), (int)value) ? ((BuiltInCategory)(int)value).ToString() : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>A link transform (total, else the instance transform), or null when the link is not usable.</summary>
    public static Transform? LinkTransform(RevitLinkInstance link)
    {
        try { return link.GetTotalTransform(); }
        catch
        {
            try { return link.GetTransform(); }
            catch { return null; }
        }
    }
}

/// <summary>Reads an element's level for <see cref="LevelResolver"/> through the Revit API.</summary>
internal sealed class RevitLevelReader : ILevelReader
{
    private static readonly Dictionary<string, BuiltInParameter> Parameters = new(StringComparer.Ordinal)
    {
        ["FAMILY_LEVEL_PARAM"] = BuiltInParameter.FAMILY_LEVEL_PARAM,
        ["RBS_START_LEVEL_PARAM"] = BuiltInParameter.RBS_START_LEVEL_PARAM,
        ["SCHEDULE_LEVEL_PARAM"] = BuiltInParameter.SCHEDULE_LEVEL_PARAM,
        ["LEVEL_PARAM"] = BuiltInParameter.LEVEL_PARAM
    };

    private readonly Element _element;

    public RevitLevelReader(Element element) => _element = element;

    public static LevelResolution Resolve(Element element) => LevelResolver.Resolve(new RevitLevelReader(element));

    public long? LevelId()
    {
        var id = _element.LevelId;
        return id == null || id == ElementId.InvalidElementId ? null : id.Value;
    }

    public long? ParameterLevelId(string builtInParameter)
    {
        if (!Parameters.TryGetValue(builtInParameter, out var bip)) return null;
        var p = _element.get_Parameter(bip);
        if (p == null || p.StorageType != StorageType.ElementId) return null;
        var id = p.AsElementId();
        return id == null || id == ElementId.InvalidElementId ? null : id.Value;
    }

    public string? LevelName(long levelId) =>
        (_element.Document.GetElement(new ElementId(levelId)) as Level)?.Name;
}

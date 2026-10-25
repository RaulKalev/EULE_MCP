using Newtonsoft.Json;
using RevitMCP.Addin.Placement;

namespace RevitMCP.Addin.Geometry;

/// <summary>
/// Units and rounding shared by every geometry payload (#107): millimetres in host internal coordinates, rounded to
/// 0.1 mm; unit vectors rounded to 4 decimals. Pure — no Revit API, unit tested.
/// </summary>
public static class GeometryUnits
{
    /// <summary>Millimetres per Revit internal foot (the same constant the move and copy tools use).</summary>
    public const double MmPerFoot = MoveElementsMath.MmPerFoot;

    public static double FtToMm(double ft) => ft * MmPerFoot;

    public static double MmToFt(double mm) => mm / MmPerFoot;

    /// <summary>Rounds a length in mm to 0.1 mm, turning -0 into 0 so payloads do not show "-0.0".</summary>
    public static double RoundMm(double mm)
    {
        var rounded = Math.Round(mm, 1, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    /// <summary>Rounds a unit-vector component to 4 decimals (about 0.006 degrees).</summary>
    public static double RoundUnit(double value)
    {
        var rounded = Math.Round(value, 4, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }

    /// <summary>Rounds a volume in mm³ to whole cubic millimetres.</summary>
    public static double RoundVolume(double mm3) => Math.Round(mm3, 0, MidpointRounding.AwayFromZero);
}

/// <summary>A point in host internal coordinates, mm, rounded to 0.1 mm. Serialises as <c>{x, y, z}</c>.</summary>
public sealed class PointMmDto
{
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("z")] public double Z { get; set; }

    public static PointMmDto FromMm(double x, double y, double z) => new()
    {
        X = GeometryUnits.RoundMm(x),
        Y = GeometryUnits.RoundMm(y),
        Z = GeometryUnits.RoundMm(z)
    };

    public static PointMmDto FromMm(Vec3 mm) => FromMm(mm.X, mm.Y, mm.Z);

    public static PointMmDto FromFeet(double x, double y, double z) =>
        FromMm(GeometryUnits.FtToMm(x), GeometryUnits.FtToMm(y), GeometryUnits.FtToMm(z));

    public static PointMmDto FromFeet(Vec3 ft) => FromFeet(ft.X, ft.Y, ft.Z);

    public override string ToString() => $"({X:0.0}, {Y:0.0}, {Z:0.0}) mm";
}

/// <summary>A unit direction. Serialises as <c>{x, y, z}</c>, normalised and rounded to 4 decimals.</summary>
public sealed class DirectionDto
{
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("z")] public double Z { get; set; }

    /// <summary>The normalised direction, or null for a zero-length vector.</summary>
    public static DirectionDto? From(Vec3 v)
    {
        var unit = v.Normalized();
        if (unit.Length < 0.5) return null;
        return new DirectionDto
        {
            X = GeometryUnits.RoundUnit(unit.X),
            Y = GeometryUnits.RoundUnit(unit.Y),
            Z = GeometryUnits.RoundUnit(unit.Z)
        };
    }

    public static DirectionDto? From(double x, double y, double z) => From(new Vec3(x, y, z));
}

/// <summary>
/// An axis-aligned box in host internal coordinates, mm. <c>min</c>/<c>max</c> are the corners, <c>sizeMm</c> the
/// extents and <c>centerMm</c> the centre. The flat <c>minX … maxZ</c> fields are the pre-#107 shape of
/// <c>revit_get_linked_elements_in_room</c>; they are filled only by <see cref="WithLegacyFields"/> (the
/// <c>legacy=true</c> default, kept for one release) and are omitted otherwise.
/// </summary>
public sealed class BoxMmDto
{
    [JsonProperty("min")] public PointMmDto Min { get; set; } = new();
    [JsonProperty("max")] public PointMmDto Max { get; set; } = new();
    [JsonProperty("sizeMm")] public PointMmDto SizeMm { get; set; } = new();
    [JsonProperty("centerMm")] public PointMmDto CenterMm { get; set; } = new();

    [JsonProperty("minX", NullValueHandling = NullValueHandling.Ignore)] public double? MinX { get; set; }
    [JsonProperty("minY", NullValueHandling = NullValueHandling.Ignore)] public double? MinY { get; set; }
    [JsonProperty("minZ", NullValueHandling = NullValueHandling.Ignore)] public double? MinZ { get; set; }
    [JsonProperty("maxX", NullValueHandling = NullValueHandling.Ignore)] public double? MaxX { get; set; }
    [JsonProperty("maxY", NullValueHandling = NullValueHandling.Ignore)] public double? MaxY { get; set; }
    [JsonProperty("maxZ", NullValueHandling = NullValueHandling.Ignore)] public double? MaxZ { get; set; }

    /// <summary>A box from two corners in mm, in any order.</summary>
    public static BoxMmDto FromMm(BoxMm box) => FromMm(box.Min, box.Max);

    public static BoxMmDto FromMm(Vec3 a, Vec3 b)
    {
        var box = BoxMath.FromCorners(a, b);
        return new BoxMmDto
        {
            Min = PointMmDto.FromMm(box.Min),
            Max = PointMmDto.FromMm(box.Max),
            SizeMm = PointMmDto.FromMm(box.Size),
            CenterMm = PointMmDto.FromMm(box.Center)
        };
    }

    public static BoxMmDto FromFeet(Vec3 a, Vec3 b) =>
        FromMm(a.Scaled(GeometryUnits.MmPerFoot), b.Scaled(GeometryUnits.MmPerFoot));

    /// <summary>Adds the flat pre-#107 fields (<c>minX … maxZ</c>) alongside the structured ones.</summary>
    public BoxMmDto WithLegacyFields()
    {
        MinX = Min.X; MinY = Min.Y; MinZ = Min.Z;
        MaxX = Max.X; MaxY = Max.Y; MaxZ = Max.Z;
        return this;
    }

    /// <summary>The box as mm values again (rounded), for distance maths on a DTO.</summary>
    public BoxMm ToBox() => new(new Vec3(Min.X, Min.Y, Min.Z), new Vec3(Max.X, Max.Y, Max.Z));
}

/// <summary>
/// What a ray hit (#107). <c>pointMm</c> is in host coordinates, <c>normal</c> points back towards the ray origin,
/// <c>distanceMm</c> is measured along the ray unless the tool says otherwise. Elements inside a link carry
/// <c>linkInstanceId</c>; their <c>elementId</c> is the id in the link document.
/// </summary>
public sealed class HitDto
{
    [JsonProperty("elementId")] public long ElementId { get; set; }
    [JsonProperty("category")] public string Category { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("documentTitle")] public string DocumentTitle { get; set; } = string.Empty;

    [JsonProperty("linkInstanceId", NullValueHandling = NullValueHandling.Ignore)]
    public long? LinkInstanceId { get; set; }

    [JsonProperty("pointMm")] public PointMmDto PointMm { get; set; } = new();

    [JsonProperty("normal", NullValueHandling = NullValueHandling.Ignore)]
    public DirectionDto? Normal { get; set; }

    [JsonProperty("distanceMm")] public double DistanceMm { get; set; }

    /// <summary>wall | ceiling | floor | roof | other, when the tool classified the surface.</summary>
    [JsonProperty("surfaceKind", NullValueHandling = NullValueHandling.Ignore)]
    public string? SurfaceKind { get; set; }

    /// <summary>How the normal was found: geometry (the face itself), measured (three rays) or ray (inferred).</summary>
    [JsonProperty("normalSource", NullValueHandling = NullValueHandling.Ignore)]
    public string? NormalSource { get; set; }
}

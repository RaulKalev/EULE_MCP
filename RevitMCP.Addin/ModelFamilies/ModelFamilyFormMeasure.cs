namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// Compares what Revit built with what was asked (#171): an extrusion's bounding box against its profile's width and
/// height on the plane and its depth along the normal. Pure, millimetres.
/// </summary>
public static class ModelFamilyFormMeasure
{
    /// <summary>A size differing by more than this is reported, mm.</summary>
    public const double ToleranceMm = 0.1;

    public sealed class Mismatch
    {
        public string Name { get; init; } = string.Empty;
        public double Requested { get; init; }
        public double Measured { get; init; }
        public override string ToString() =>
            $"{Name} is {ModelFamilyFormGeometry.Mm(Measured)} mm, requested {ModelFamilyFormGeometry.Mm(Requested)} mm";
    }

    /// <summary>
    /// The family axis (0 = x, 1 = y, 2 = z) a unit direction runs along, or -1 when it is not parallel to one. Only
    /// then does an axis-aligned bounding box measure the form's size along it.
    /// </summary>
    public static int AxisOf(double[] direction)
    {
        for (var i = 0; i < 3; i++)
            if (Math.Abs(Math.Abs(direction[i]) - 1) < 1e-6) return i;
        return -1;
    }

    /// <summary>
    /// The sizes of an extrusion that differ from the request: width (plane x), height (plane y) and depth
    /// (|end − start|) against the bounding box. Null when the plane is not aligned with the family axes, so the box
    /// cannot measure it.
    /// </summary>
    public static List<Mismatch>? CompareExtrusion(ModelFamilyFormFrame frame, double width, double height, double depth,
        double[] boxMin, double[] boxMax, double tolerance = ToleranceMm)
    {
        var axes = new[] { AxisOf(frame.XAxis), AxisOf(frame.YAxis), AxisOf(frame.Normal) };
        if (axes.Any(a => a < 0) || axes.Distinct().Count() != 3) return null;
        var requested = new[] { width, height, Math.Abs(depth) };
        var names = new[] { "width (work plane x)", "height (work plane y)", "depth (along the normal)" };
        var result = new List<Mismatch>();
        for (var i = 0; i < 3; i++)
        {
            var measured = boxMax[axes[i]] - boxMin[axes[i]];
            if (Math.Abs(measured - requested[i]) > tolerance)
                result.Add(new Mismatch { Name = names[i], Requested = Math.Round(requested[i], 3), Measured = Math.Round(measured, 3) });
        }
        return result;
    }
}

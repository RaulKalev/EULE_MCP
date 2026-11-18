namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// Names a planar face of a form by its outward normal (#171), so other tools can say <c>{"element": id, "face": "top"}</c>
/// instead of a stable reference string. Pure; the Revit side is <c>ModelFamilyFormFaces</c>.
/// </summary>
public static class ModelFamilyFormFaceLabels
{
    /// <summary>The labels, in the order of the axes they stand for: −X, +X, −Y, +Y, +Z, −Z.</summary>
    public static readonly string[] Labels = { "left", "right", "front", "back", "top", "bottom" };

    /// <summary>A normal within this many degrees of an axis gets that axis's label.</summary>
    public const double ToleranceDegrees = 1.0;

    private static readonly double[][] Axes =
    {
        new[] { -1.0, 0, 0 }, new[] { 1.0, 0, 0 }, new[] { 0, -1.0, 0 }, new[] { 0, 1.0, 0 }, new[] { 0, 0, 1.0 }, new[] { 0, 0, -1.0 }
    };

    /// <summary>
    /// <c>left|right|front|back|top|bottom</c> when the outward normal is within <see cref="ToleranceDegrees"/> of −X, +X,
    /// −Y, +Y, +Z or −Z (family coordinates: front is −Y, the side the Front elevation looks at), else null.
    /// </summary>
    public static string? LabelOf(double nx, double ny, double nz)
    {
        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (length < 1e-12) return null;
        var cosTolerance = Math.Cos(ToleranceDegrees * Math.PI / 180);
        for (var i = 0; i < Axes.Length; i++)
            if ((nx * Axes[i][0] + ny * Axes[i][1] + nz * Axes[i][2]) / length >= cosTolerance) return Labels[i];
        return null;
    }

    public static string? LabelOf(double[] normal) => LabelOf(normal[0], normal[1], normal[2]);

    /// <summary>Whether <paramref name="label"/> is one of <see cref="Labels"/> (case-insensitive).</summary>
    public static bool IsLabel(string label) => Labels.Contains(label.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The unit normal a label stands for.</summary>
    public static double[] NormalOf(string label)
    {
        var index = Array.FindIndex(Labels, l => l.Equals(label.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0) throw new ArgumentException($"'{label}' is not a face label.", nameof(label));
        return (double[])Axes[index].Clone();
    }
}

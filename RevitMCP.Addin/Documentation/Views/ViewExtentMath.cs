namespace RevitMCP.Addin.Documentation.Views;

/// <summary>An orthonormal frame in model mm: a view's crop box transform, or a parent view's right/up/direction.</summary>
public sealed class FrameMm
{
    public FrameMm(Vec3 origin, Vec3 basisX, Vec3 basisY, Vec3 basisZ)
    {
        Origin = origin;
        BasisX = basisX;
        BasisY = basisY;
        BasisZ = basisZ;
    }

    public Vec3 Origin { get; }
    public Vec3 BasisX { get; }
    public Vec3 BasisY { get; }
    public Vec3 BasisZ { get; }

    public static readonly FrameMm Identity = new(Vec3.Zero, new Vec3(1, 0, 0), new Vec3(0, 1, 0), Vec3.UnitZ);

    public Vec3 ToLocal(Vec3 model)
    {
        var d = model.Minus(Origin);
        return new Vec3(d.Dot(BasisX), d.Dot(BasisY), d.Dot(BasisZ));
    }

    public Vec3 ToModel(Vec3 local) =>
        Origin.Plus(BasisX.Scaled(local.X)).Plus(BasisY.Scaled(local.Y)).Plus(BasisZ.Scaled(local.Z));
}

/// <summary>
/// The Revit-free part of <c>ViewExtentService</c> (#104): box union and padding, a box seen in a view's frame (crop
/// regions and callout corners), and 3D orientations. All values in mm.
/// </summary>
public static class ViewExtentMath
{
    public const double DefaultPaddingMm = 500;

    /// <summary>The smallest box holding every input box; null when there is none.</summary>
    public static BoxMm? Union(IEnumerable<BoxMm?> boxes)
    {
        BoxMm? result = null;
        foreach (var box in boxes)
        {
            if (box == null) continue;
            result = result == null
                ? box
                : new BoxMm(
                    new Vec3(Math.Min(result.Min.X, box.Min.X), Math.Min(result.Min.Y, box.Min.Y), Math.Min(result.Min.Z, box.Min.Z)),
                    new Vec3(Math.Max(result.Max.X, box.Max.X), Math.Max(result.Max.Y, box.Max.Y), Math.Max(result.Max.Z, box.Max.Z)));
        }
        return result;
    }

    /// <summary>The smallest box holding the points; null when there is none.</summary>
    public static BoxMm? Bounds(IEnumerable<Vec3> points)
    {
        var list = points.ToList();
        if (list.Count == 0) return null;
        return new BoxMm(
            new Vec3(list.Min(p => p.X), list.Min(p => p.Y), list.Min(p => p.Z)),
            new Vec3(list.Max(p => p.X), list.Max(p => p.Y), list.Max(p => p.Z)));
    }

    /// <summary>The box grown by <paramref name="paddingMm"/> on every side (a negative padding shrinks it, never past its centre).</summary>
    public static BoxMm Pad(BoxMm box, double paddingMm)
    {
        var c = box.Center;
        double Lo(double min, double center) => Math.Min(min - paddingMm, center);
        double Hi(double max, double center) => Math.Max(max + paddingMm, center);
        return new BoxMm(
            new Vec3(Lo(box.Min.X, c.X), Lo(box.Min.Y, c.Y), Lo(box.Min.Z, c.Z)),
            new Vec3(Hi(box.Max.X, c.X), Hi(box.Max.Y, c.Y), Hi(box.Max.Z, c.Z)));
    }

    /// <summary>The eight corners of a box.</summary>
    public static IEnumerable<Vec3> Corners(BoxMm box)
    {
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
            yield return new Vec3(x, y, z);
    }

    /// <summary>The model box seen in a frame: local min/max of its corners (a crop region in view coordinates).</summary>
    public static BoxMm InFrame(BoxMm modelBox, FrameMm frame) =>
        Bounds(Corners(modelBox).Select(frame.ToLocal))!;

    /// <summary>
    /// Two opposite corners of the rectangle the model box covers in a view whose right and up directions are
    /// <paramref name="frame"/>'s BasisX and BasisY. Revit's CreateCallout projects them onto the view plane, so the
    /// depth along the view direction does not matter; they are placed on the frame's plane.
    /// </summary>
    public static (Vec3 First, Vec3 Second) CalloutCorners(BoxMm modelBox, FrameMm frame)
    {
        var local = InFrame(modelBox, frame);
        return (frame.ToModel(new Vec3(local.Min.X, local.Min.Y, 0)), frame.ToModel(new Vec3(local.Max.X, local.Max.Y, 0)));
    }

    /// <summary>True when the box spans at least <paramref name="minMm"/> in x and y (a crop region needs an area).</summary>
    public static bool HasPlanArea(BoxMm box, double minMm = 1.0) =>
        box.Max.X - box.Min.X >= minMm && box.Max.Y - box.Min.Y >= minMm;

    /// <summary>True when the box spans at least <paramref name="minMm"/> along every axis (a section box needs a volume).</summary>
    public static bool HasVolume(BoxMm box, double minMm = 1.0) =>
        HasPlanArea(box, minMm) && box.Max.Z - box.Min.Z >= minMm;
}

/// <summary>3D view orientations: presets and eye/forward/up (#104). Pure.</summary>
public static class ViewOrientationMath
{
    public static readonly string[] Presets = { "iso-ne", "iso-nw", "iso-se", "iso-sw", "top" };

    public static bool IsPreset(string? name) =>
        name != null && Presets.Contains(name.Trim().ToLowerInvariant());

    public sealed class Orientation
    {
        public Vec3 EyeMm { get; init; }
        public Vec3 Forward { get; init; }
        public Vec3 Up { get; init; }

        public object Describe() => new { eyeMm = EyeMm.ToMm(), forward = Forward.ToUnit(), up = Up.ToUnit() };
    }

    /// <summary>
    /// The forward direction of a preset: iso-se looks from the south-east corner (+x, −y, above) towards the target,
    /// like Revit's default {3D}; iso-ne from (+x, +y), iso-nw from (−x, +y), iso-sw from (−x, −y); top looks straight
    /// down with +y up on screen.
    /// </summary>
    public static Vec3 PresetForward(string preset) => preset.Trim().ToLowerInvariant() switch
    {
        "iso-ne" => new Vec3(-1, -1, -1).Normalized(),
        "iso-nw" => new Vec3(1, -1, -1).Normalized(),
        "iso-sw" => new Vec3(1, 1, -1).Normalized(),
        "top" => new Vec3(0, 0, -1),
        _ => new Vec3(-1, 1, -1).Normalized()   // iso-se
    };

    /// <summary>
    /// A complete orientation: the eye <paramref name="distanceMm"/> back from <paramref name="target"/> along the
    /// forward direction unless given, and an up direction perpendicular to forward (model +Z made perpendicular, or +Y
    /// for a view straight down or up).
    /// </summary>
    public static Orientation? Resolve(OrientationSpec? spec, Vec3 target, double distanceMm, out string? error)
    {
        error = null;
        var forward = spec?.Forward ?? PresetForward(spec?.Preset ?? "iso-se");
        if (forward.Length < 1e-9)
        {
            error = "The orientation's forward direction has no length.";
            return null;
        }
        forward = forward.Normalized();

        var up = spec?.Up ?? PerpendicularUp(forward);
        // Make up exactly perpendicular to forward (Gram-Schmidt); Revit requires it.
        up = up.Minus(forward.Scaled(up.Dot(forward)));
        if (up.Length < 1e-9)
        {
            error = "The orientation's up direction is parallel to forward.";
            return null;
        }
        up = up.Normalized();

        var eye = spec?.EyeMm ?? target.Minus(forward.Scaled(Math.Max(distanceMm, 1000)));
        return new Orientation { EyeMm = eye, Forward = forward, Up = up };
    }

    private static Vec3 PerpendicularUp(Vec3 forward)
    {
        var z = Vec3.UnitZ;
        var up = z.Minus(forward.Scaled(z.Dot(forward)));
        return up.Length < 1e-6 ? new Vec3(0, 1, 0) : up.Normalized();
    }
}

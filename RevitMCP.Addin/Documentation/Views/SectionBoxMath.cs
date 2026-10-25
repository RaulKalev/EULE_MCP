namespace RevitMCP.Addin.Documentation.Views;

/// <summary>
/// The section box of a section drawn in plan from <c>from</c> to <c>to</c>, in model mm (#104). Pure, so the
/// convention is unit tested.
/// <para>
/// Convention: walking from <c>from</c> towards <c>to</c>, the section LOOKS to the LEFT of the line (<c>flip</c>: to the
/// RIGHT). Revit looks along the box's +BasisZ from its Min.Z face (the cut plane) towards its Max.Z face (the far
/// clip), and reports View.ViewDirection = −BasisZ (towards the viewer); this was measured in Revit 2026. So BasisZ is
/// the look direction, BasisY is up (model +Z) and BasisX = BasisY × BasisZ (right-handed): against the drawn line
/// without flip, along it with flip. BasisX is the section view's right direction, so <c>from</c> is on the right
/// without flip and <c>to</c> with flip.
/// </para>
/// <para>
/// The box origin is the midpoint of the line at elevation 0, so local Y is the model elevation. Min =
/// (−length/2, bottom, 0), Max = (+length/2, top, depth): the cut plane (local z = 0) runs through the drawn line and
/// the far clip lies <c>depthMm</c> beyond it, in the looking direction.
/// </para>
/// </summary>
public static class SectionBoxMath
{
    public const double DefaultDepthMm = 3000;

    /// <summary>Used as the height when no level lies above the bottom.</summary>
    public const double DefaultHeightMm = 4000;

    /// <summary>Shorter plan lines than this are treated as vertical (no direction in plan).</summary>
    public const double MinLengthMm = 1.0;

    public sealed class Result
    {
        public Vec3 Origin { get; init; }
        public Vec3 BasisX { get; init; }
        public Vec3 BasisY { get; init; }
        public Vec3 BasisZ { get; init; }

        /// <summary>Local (box) coordinates of the near-bottom-left corner, mm.</summary>
        public Vec3 Min { get; init; }

        /// <summary>Local (box) coordinates of the far-top-right corner, mm.</summary>
        public Vec3 Max { get; init; }

        public double LengthMm { get; init; }
        public double BottomMm { get; init; }
        public double TopMm { get; init; }
        public double DepthMm { get; init; }
        public bool Flip { get; init; }

        /// <summary>The direction the section looks in (+BasisZ), in plan.</summary>
        public Vec3 LookDirection => BasisZ;

        /// <summary>The View.ViewDirection Revit reports for this box (towards the viewer, −BasisZ).</summary>
        public Vec3 ExpectedViewDirection => BasisZ.Negated();

        /// <summary>A model point of the box from local coordinates.</summary>
        public Vec3 ToModel(Vec3 local) =>
            Origin.Plus(BasisX.Scaled(local.X)).Plus(BasisY.Scaled(local.Y)).Plus(BasisZ.Scaled(local.Z));

        /// <summary>The eight corners in model mm.</summary>
        public IEnumerable<Vec3> ModelCorners()
        {
            foreach (var x in new[] { Min.X, Max.X })
            foreach (var y in new[] { Min.Y, Max.Y })
            foreach (var z in new[] { Min.Z, Max.Z })
                yield return ToModel(new Vec3(x, y, z));
        }

        /// <summary>A description for the preview, in mm and unit vectors.</summary>
        public object Describe() => new
        {
            convention = "BasisZ = the look direction (left of from->to; right with flip), BasisY up, BasisX = BasisY x BasisZ " +
                         "(the view's right). Revit reports View.ViewDirection = -BasisZ. Cut plane on the line (local z 0), far clip depthMm beyond it.",
            originMm = Origin.ToMm(),
            basisX = BasisX.ToUnit(),
            basisY = BasisY.ToUnit(),
            basisZ = BasisZ.ToUnit(),
            lookDirection = LookDirection.ToUnit(),
            looks = Flip ? "right of the line (flip=true)" : "left of the line",
            minLocalMm = Min.ToMm(),
            maxLocalMm = Max.ToMm(),
            lengthMm = Math.Round(LengthMm, 1),
            bottomMm = Math.Round(BottomMm, 1),
            topMm = Math.Round(TopMm, 1),
            depthMm = Math.Round(DepthMm, 1),
            flip = Flip
        };
    }

    /// <summary>
    /// The box for a line from <paramref name="from"/> to <paramref name="to"/> (their z is ignored for the direction),
    /// between the absolute elevations <paramref name="bottomMm"/> and <paramref name="topMm"/>.
    /// </summary>
    public static Result? Compute(Vec3 from, Vec3 to, double bottomMm, double topMm, double depthMm, bool flip, out string? error)
    {
        error = null;
        var plan = new Vec3(to.X - from.X, to.Y - from.Y, 0);
        var length = plan.Length;
        if (length < MinLengthMm)
        {
            error = "The section line is vertical or has no length in plan: 'from' and 'to' must differ in x or y.";
            return null;
        }
        if (!(depthMm > 0))
        {
            error = $"depthMm must be positive (got {depthMm}).";
            return null;
        }
        if (!(topMm - bottomMm >= MinLengthMm))
        {
            error = $"The section's top ({topMm:0.#} mm) must lie above its bottom ({bottomMm:0.#} mm).";
            return null;
        }

        var along = plan.Scaled(1.0 / length);
        var basisY = Vec3.UnitZ;
        var left = basisY.Cross(along);                 // up × along: to the left of from → to
        var basisZ = flip ? left.Negated() : left;      // the look direction
        var basisX = basisY.Cross(basisZ);              // right-handed: X = Y × Z, so X × Y = Z
        var mid = new Vec3((from.X + to.X) / 2, (from.Y + to.Y) / 2, 0);

        return new Result
        {
            Origin = mid,
            BasisX = basisX,
            BasisY = basisY,
            BasisZ = basisZ,
            Min = new Vec3(-length / 2, bottomMm, 0),
            Max = new Vec3(length / 2, topMm, depthMm),
            LengthMm = length,
            BottomMm = bottomMm,
            TopMm = topMm,
            DepthMm = depthMm,
            Flip = flip
        };
    }

    /// <summary>
    /// The default vertical range: from the level at or just below the lower point to the next level above (or
    /// <see cref="DefaultHeightMm"/> when there is none). <paramref name="bottomMm"/> and <paramref name="heightMm"/>
    /// override either end. Levels are elevations in mm, any order.
    /// </summary>
    public static (double Bottom, double Top) VerticalRange(
        Vec3 from, Vec3 to, IReadOnlyCollection<double> levelElevationsMm, double? bottomMm, double? heightMm)
    {
        var z = Math.Min(from.Z, to.Z);
        var sorted = levelElevationsMm.OrderBy(e => e).ToList();
        double bottom;
        if (bottomMm.HasValue) bottom = bottomMm.Value;
        else if (sorted.Count == 0) bottom = z;
        else
        {
            const double toleranceMm = 150;
            var below = sorted.Where(e => e <= z + toleranceMm).ToList();
            bottom = below.Count > 0 ? below.Last() : sorted.First();
        }

        double top;
        if (heightMm.HasValue) top = bottom + heightMm.Value;
        else
        {
            var above = sorted.Where(e => e > bottom + MinLengthMm).ToList();
            top = above.Count > 0 ? above.First() : bottom + DefaultHeightMm;
        }
        return (bottom, top);
    }

    /// <summary>True when the three basis vectors are unit length and mutually perpendicular.</summary>
    public static bool IsOrthonormal(Result box, double tolerance = 1e-9) =>
        Math.Abs(box.BasisX.Length - 1) < tolerance && Math.Abs(box.BasisY.Length - 1) < tolerance &&
        Math.Abs(box.BasisZ.Length - 1) < tolerance &&
        Math.Abs(box.BasisX.Dot(box.BasisY)) < tolerance && Math.Abs(box.BasisX.Dot(box.BasisZ)) < tolerance &&
        Math.Abs(box.BasisY.Dot(box.BasisZ)) < tolerance;
}

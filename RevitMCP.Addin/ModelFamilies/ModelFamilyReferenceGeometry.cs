namespace RevitMCP.Addin.ModelFamilies;

/// <summary>
/// Vector maths of the reference tools (#170) on plain <c>double[3]</c> vectors, so it can be tested without Revit:
/// reference plane ends from origin/direction/normal, offset planes, the view a plane or dimension is drawn in, and the
/// arc of an angular dimension. Points are millimetres; directions are unit-less.
/// </summary>
public static class ModelFamilyReferenceGeometry
{
    /// <summary>Two directions closer than this (as |cos| from 1, about 0.08°) count as parallel.</summary>
    public const double ParallelTolerance = 1e-6;

    /// <summary>The default length of a new reference plane in millimetres, centred on its origin.</summary>
    public const double DefaultPlaneLengthMm = 1200;

    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    public static double[] Cross(double[] a, double[] b) => new[]
    {
        a[1] * b[2] - a[2] * b[1],
        a[2] * b[0] - a[0] * b[2],
        a[0] * b[1] - a[1] * b[0]
    };

    public static double Length(double[] a) => Math.Sqrt(Dot(a, a));
    public static double[] Add(double[] a, double[] b) => new[] { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
    public static double[] Subtract(double[] a, double[] b) => new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] };
    public static double[] Scale(double[] a, double factor) => new[] { a[0] * factor, a[1] * factor, a[2] * factor };

    /// <summary>The unit vector of <paramref name="a"/>, or null when it is (nearly) zero.</summary>
    public static double[]? Normalize(double[] a)
    {
        var length = Length(a);
        return length < 1e-9 ? null : Scale(a, 1 / length);
    }

    /// <summary>True when two unit vectors point the same way or opposite ways.</summary>
    public static bool Parallel(double[] a, double[] b) => Math.Abs(Math.Abs(Dot(a, b)) - 1) < ParallelTolerance;

    /// <summary>True when two unit vectors are at right angles.</summary>
    public static bool Perpendicular(double[] a, double[] b) => Math.Abs(Dot(a, b)) < 1e-6;

    /// <summary>The angle between two unit vectors in degrees, 0–180.</summary>
    public static double AngleDegrees(double[] a, double[] b) => Math.Acos(Math.Max(-1, Math.Min(1, Dot(a, b)))) * 180 / Math.PI;

    /// <summary>The ends and cut vector Revit's NewReferencePlane takes, and the plane's normal.</summary>
    public sealed class PlaneEnds
    {
        public double[] BubbleEnd { get; set; } = new double[3];
        public double[] FreeEnd { get; set; } = new double[3];
        /// <summary>In the plane, at right angles to the bubble–free line.</summary>
        public double[] CutVector { get; set; } = new double[3];
        /// <summary>The normal the caller asked for (direction × cut vector when only a cut vector was given).</summary>
        public double[] Normal { get; set; } = new double[3];
    }

    /// <summary>
    /// A reference plane through <paramref name="originMm"/> drawn along <paramref name="direction"/> (bubble end at
    /// origin − direction·length/2, free end at origin + direction·length/2). The plane is fixed by
    /// <paramref name="normal"/> (at right angles to the direction) or by <paramref name="cutVector"/> (a second
    /// in-plane direction at right angles to it). With neither, a direction that is not vertical gives a vertical plane
    /// (cut vector +Z); a vertical direction needs one of them.
    /// </summary>
    public static PlaneEnds? Ends(double[] originMm, double[] direction, double[]? normal, double[]? cutVector, double lengthMm,
        string source, List<string> problems)
    {
        var d = Normalize(direction);
        if (d == null)
        {
            problems.Add($"{source}.direction: must not be zero.");
            return null;
        }
        if (!(lengthMm > 0))
        {
            problems.Add($"{source}.length: must be more than 0 mm.");
            return null;
        }
        if (normal != null && cutVector != null)
        {
            problems.Add($"{source}: give normal or cutVector, not both.");
            return null;
        }

        double[] cut;
        double[] n;
        if (normal != null)
        {
            var unit = Normalize(normal);
            if (unit == null)
            {
                problems.Add($"{source}.normal: must not be zero.");
                return null;
            }
            if (!Perpendicular(unit, d))
            {
                problems.Add($"{source}.normal: must be at right angles to direction (they are {AngleDegrees(unit, d):0.###}° apart); the plane contains its direction.");
                return null;
            }
            n = unit;
            cut = Normalize(Cross(n, d))!;
        }
        else
        {
            var given = cutVector;
            if (given == null)
            {
                if (Parallel(d, new[] { 0, 0, 1.0 }))
                {
                    problems.Add($"{source}: a vertical direction needs normal (or cutVector) to fix the plane, e.g. normal [1, 0, 0].");
                    return null;
                }
                given = new[] { 0, 0, 1.0 };
            }
            var unit = Normalize(given);
            if (unit == null)
            {
                problems.Add($"{source}.cutVector: must not be zero.");
                return null;
            }
            if (!Perpendicular(unit, d))
            {
                if (cutVector != null)
                {
                    problems.Add($"{source}.cutVector: must be at right angles to direction (they are {AngleDegrees(unit, d):0.###}° apart).");
                    return null;
                }
                // The default +Z against a sloping direction: keep the plane vertical through that direction.
                unit = Normalize(Subtract(unit, Scale(d, Dot(unit, d))))!;
            }
            cut = unit;
            n = Normalize(Cross(d, cut))!;
        }

        var half = lengthMm / 2;
        return new PlaneEnds
        {
            BubbleEnd = Subtract(originMm, Scale(d, half)),
            FreeEnd = Add(originMm, Scale(d, half)),
            CutVector = cut,
            Normal = n
        };
    }

    /// <summary>
    /// The plane parallel to one with ends <paramref name="bubbleMm"/>/<paramref name="freeMm"/> and normal
    /// <paramref name="normal"/>, moved <paramref name="distanceMm"/> along the normal (negative: the other side).
    /// </summary>
    public static PlaneEnds Offset(double[] bubbleMm, double[] freeMm, double[] normal, double distanceMm)
    {
        var n = Normalize(normal) ?? new[] { 0, 0, 1.0 };
        var shift = Scale(n, distanceMm);
        var along = Normalize(Subtract(freeMm, bubbleMm)) ?? Normalize(Cross(n, new[] { 0, 0, 1.0 })) ?? new[] { 1.0, 0, 0 };
        return new PlaneEnds
        {
            BubbleEnd = Add(bubbleMm, shift),
            FreeEnd = Add(freeMm, shift),
            CutVector = Normalize(Cross(n, along)) ?? new[] { 0, 0, 1.0 },
            Normal = n
        };
    }

    /// <summary>The signed distance of <paramref name="pointMm"/> from the plane through <paramref name="planePointMm"/> with unit normal <paramref name="normal"/>.</summary>
    public static double SignedDistance(double[] pointMm, double[] planePointMm, double[] normal) =>
        Dot(Subtract(pointMm, planePointMm), normal);

    /// <summary>
    /// The direction (towards the viewer) of the view kinds <c>ModelFamilyLookup.View</c> knows: the plan looks down,
    /// the Front elevation looks from −Y, Back from +Y, Left from −X, Right from +X.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, double[]> ViewDirections = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["plan"] = new[] { 0, 0, 1.0 },
        ["front"] = new[] { 0, -1.0, 0 },
        ["back"] = new[] { 0, 1.0, 0 },
        ["left"] = new[] { -1.0, 0, 0 },
        ["right"] = new[] { 1.0, 0, 0 }
    };

    /// <summary>
    /// The views a new reference plane is created in by default, best first: the plan for a vertical plane (it shows as
    /// a line there), the Front then Left elevation for a horizontal one, and for a sloping plane the elevations it is
    /// seen edge-on in.
    /// </summary>
    public static List<string> PlaneViewKinds(double[] normal)
    {
        var n = Normalize(normal) ?? new[] { 0, 0, 1.0 };
        if (Math.Abs(n[2]) < 1e-6) return new List<string> { "plan", "front", "left" };
        if (Parallel(n, new[] { 0, 0, 1.0 })) return new List<string> { "front", "left", "back", "right" };
        var edgeOn = new[] { "front", "back", "left", "right" }.Where(k => Perpendicular(n, ViewDirections[k])).ToList();
        edgeOn.AddRange(new[] { "front", "left", "plan" }.Where(k => !edgeOn.Contains(k)));
        return edgeOn;
    }

    /// <summary>
    /// The views a linear dimension (or alignment) between references with normal <paramref name="normal"/> can be drawn
    /// in, best first: the dimension runs along the normal, so the view must contain it. Horizontal normals prefer the
    /// plan, vertical ones the Front elevation. Empty when no standard view contains the normal.
    /// </summary>
    public static List<string> DimensionViewKinds(double[] normal)
    {
        var n = Normalize(normal) ?? new[] { 0, 0, 1.0 };
        var order = Math.Abs(n[2]) < 1e-6
            ? new[] { "plan", "front", "back", "left", "right" }
            : new[] { "front", "left", "back", "right", "plan" };
        return order.Where(k => Perpendicular(n, ViewDirections[k])).ToList();
    }

    /// <summary>The views an angular dimension between two planes can be drawn in: their intersection line must point at the viewer.</summary>
    public static List<string> AngularViewKinds(double[] axis)
    {
        var a = Normalize(axis) ?? new[] { 0, 0, 1.0 };
        return new[] { "plan", "front", "left", "back", "right" }.Where(k => Parallel(a, ViewDirections[k])).ToList();
    }

    /// <summary>
    /// A point on the line where two planes meet (n·x = n·p for each), the one closest to the origin, or null when the
    /// planes are parallel. Millimetres.
    /// </summary>
    public static double[]? Intersection(double[] n1, double[] p1, double[] n2, double[] p2)
    {
        var d1 = Dot(n1, p1);
        var d2 = Dot(n2, p2);
        var n11 = Dot(n1, n1);
        var n22 = Dot(n2, n2);
        var n12 = Dot(n1, n2);
        var determinant = n11 * n22 - n12 * n12;
        if (Math.Abs(determinant) < 1e-12) return null;
        var c1 = (d1 * n22 - d2 * n12) / determinant;
        var c2 = (d2 * n11 - d1 * n12) / determinant;
        return Add(Scale(n1, c1), Scale(n2, c2));
    }

    /// <summary>The arc an angular dimension is drawn on: its axes in the view plane and its sweep (0–180°, radians).</summary>
    public sealed class AngularArc
    {
        public double[] XAxis { get; set; } = new double[3];
        public double[] YAxis { get; set; } = new double[3];
        public double EndAngle { get; set; }
    }

    /// <summary>
    /// The arc between two planes seen along <paramref name="viewDirection"/>: it sweeps counter-clockwise over the
    /// acute (or right) angle between them, from one plane to the other. Null when the planes are parallel or not seen
    /// edge-on.
    /// </summary>
    public static AngularArc? Arc(double[] n1, double[] n2, double[] viewDirection)
    {
        var v = Normalize(viewDirection);
        var a = Normalize(n1);
        var b = Normalize(n2);
        if (v == null || a == null || b == null || Parallel(a, b) || !Perpendicular(a, v) || !Perpendicular(b, v)) return null;
        var u1 = Normalize(Cross(v, a))!;
        var u2 = Normalize(Cross(v, b))!;
        var y = Normalize(Cross(v, u1))!;
        var angle = Math.Atan2(Dot(u2, y), Dot(u2, u1));
        if (angle < 0) angle += Math.PI;   // the other ray of the second plane: the sweep stays under 180°
        if (angle < 1e-9) return null;
        if (angle <= Math.PI / 2 + 1e-9) return new AngularArc { XAxis = u1, YAxis = y, EndAngle = angle };
        // Obtuse: measure the acute angle instead, from the second plane's ray round to the first plane's other ray.
        var start = Add(Scale(u1, Math.Cos(angle)), Scale(y, Math.Sin(angle)));
        return new AngularArc { XAxis = start, YAxis = Normalize(Cross(v, start))!, EndAngle = Math.PI - angle };
    }
}

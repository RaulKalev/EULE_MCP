namespace RevitMCP.Addin.Mep;

/// <summary>An open end of a tray or conduit: where it is and which way it points out of its curve (mm, unit vector).</summary>
public readonly struct MepEnd
{
    public MepEnd(MepPoint origin, MepPoint direction)
    {
        Origin = origin;
        Direction = direction.Normalize();
    }

    public MepPoint Origin { get; }

    /// <summary>Points away from the curve, as a Revit end connector's BasisZ does.</summary>
    public MepPoint Direction { get; }
}

/// <summary>How two open ends lie relative to each other.</summary>
public static class MepRelation
{
    /// <summary>The ends touch and face each other: a straight joint.</summary>
    public const string Coincident = "coincident";

    /// <summary>The ends touch but the curves meet at an angle.</summary>
    public const string CoincidentAngled = "coincident_angled";

    /// <summary>The ends face each other on one line, with a gap.</summary>
    public const string Collinear = "collinear";

    /// <summary>The two lines cross: an elbow joins them.</summary>
    public const string Angled = "angled";

    /// <summary>Parallel but side by side, or pointing the same way: no single fitting joins them.</summary>
    public const string ParallelOffset = "parallel_offset";

    /// <summary>The lines pass each other without crossing.</summary>
    public const string Skew = "skew";
}

/// <summary>The geometry between two open ends, computed by <see cref="MepConnectMath.Analyze"/>.</summary>
public sealed class MepConnectGeometry
{
    public string Relation { get; init; } = MepRelation.Skew;

    /// <summary>Distance between the two end points (mm).</summary>
    public double GapMm { get; init; }

    /// <summary>Deflection between the two curves: 0 = straight on, 90 = a right angle.</summary>
    public double AngleDeg { get; init; }

    /// <summary>Where the two lines cross (angled), or null.</summary>
    public MepPoint? Intersection { get; init; }

    /// <summary>How far each end would have to grow (positive) or shrink (negative) to reach the crossing.</summary>
    public double ExtendAMm { get; init; }
    public double ExtendBMm { get; init; }

    /// <summary>Shortest distance between the two lines (mm), 0 when they cross.</summary>
    public double LineDistanceMm { get; init; }

    /// <summary>The longest distance an end has to travel to meet the other: the gap, or the way to the crossing.</summary>
    public double ReachMm => Relation == MepRelation.Angled ? Math.Max(Math.Abs(ExtendAMm), Math.Abs(ExtendBMm)) : GapMm;
}

/// <summary>What revit_connect_mep_curves will do for a pair of ends.</summary>
public sealed class MepConnectDecision
{
    /// <summary>connect_to, union, transition, elbow, extend, or null when the ends cannot be joined.</summary>
    public string? Action { get; init; }

    /// <summary>The status when Action is null: skew_lines, too_far, …</summary>
    public string? RefusedStatus { get; init; }

    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Pure geometry for joining two open MEP curve ends (#106): coincident, collinear, angled or skew, the gap, and which
/// Revit call joins them. No Revit API.
/// </summary>
public static class MepConnectMath
{
    public const double DefaultToleranceMm = 1.0;
    public const double DefaultMaxGapMm = 5000;

    public static MepConnectGeometry Analyze(MepEnd a, MepEnd b, double toleranceMm = DefaultToleranceMm)
    {
        var gap = a.Origin.DistanceTo(b.Origin);
        // Deflection between the curves: two ends that face each other (opposite directions) are a straight joint.
        var facing = MepMath.AngleDeg(a.Direction, b.Direction * -1);
        var parallel = facing < MepMath.StraightToleranceDeg || facing > 180 - MepMath.StraightToleranceDeg;

        if (gap <= toleranceMm)
            return new MepConnectGeometry
            {
                Relation = facing < MepMath.StraightToleranceDeg ? MepRelation.Coincident : MepRelation.CoincidentAngled,
                GapMm = gap,
                AngleDeg = Math.Round(facing, 2),
                Intersection = a.Origin
            };

        if (parallel)
        {
            // Distance of B's end from A's line.
            var offset = PerpendicularDistance(b.Origin, a.Origin, a.Direction);
            var ahead = (b.Origin - a.Origin).Dot(a.Direction) > 0;
            if (offset <= toleranceMm && facing < MepMath.StraightToleranceDeg && ahead)
                return new MepConnectGeometry { Relation = MepRelation.Collinear, GapMm = gap, AngleDeg = Math.Round(facing, 2) };
            return new MepConnectGeometry
            {
                Relation = MepRelation.ParallelOffset,
                GapMm = gap,
                AngleDeg = Math.Round(facing, 2),
                LineDistanceMm = offset
            };
        }

        // Closest points of the two lines p = a + s*da, q = b + t*db.
        var w = a.Origin - b.Origin;
        var da = a.Direction;
        var db = b.Direction;
        var bb = da.Dot(db);
        var d = da.Dot(w);
        var e = db.Dot(w);
        var denominator = 1 - bb * bb;
        var s = (bb * e - d) / denominator;
        var t = (e - bb * d) / denominator;
        var p = a.Origin + da * s;
        var q = b.Origin + db * t;
        var distance = p.DistanceTo(q);

        if (distance > toleranceMm)
            return new MepConnectGeometry
            {
                Relation = MepRelation.Skew,
                GapMm = gap,
                AngleDeg = Math.Round(facing, 2),
                LineDistanceMm = distance
            };

        return new MepConnectGeometry
        {
            Relation = MepRelation.Angled,
            GapMm = gap,
            AngleDeg = Math.Round(facing, 2),
            Intersection = (p + q) / 2,
            ExtendAMm = s,
            ExtendBMm = t,
            LineDistanceMm = distance
        };
    }

    /// <summary>
    /// The Revit call for a pair of ends. mode: auto (the matching call), elbow, union or extend (only when collinear
    /// within 1°). <paramref name="sameSize"/> decides between a straight joint and a transition.
    /// </summary>
    public static MepConnectDecision Decide(MepConnectGeometry geometry, string mode, bool sameSize, double maxGapMm = DefaultMaxGapMm)
    {
        if (geometry.ReachMm > maxGapMm)
            return Refuse(MepStatus.TooFar,
                $"The ends are {MepMath.FormatMm(geometry.ReachMm)} mm apart, more than maxGapMm ({MepMath.FormatMm(maxGapMm)} mm).");

        switch (geometry.Relation)
        {
            case MepRelation.Skew:
                return Refuse(MepStatus.SkewLines,
                    $"The two curves pass each other {MepMath.FormatMm(geometry.LineDistanceMm)} mm apart without crossing: no single " +
                    "fitting joins them. Move one so their lines cross, or draw a connecting run.");
            case MepRelation.ParallelOffset:
                return Refuse(MepStatus.SkewLines,
                    $"The two curves are parallel but {MepMath.FormatMm(geometry.LineDistanceMm)} mm apart side by side (or point the same " +
                    "way): joining them takes two bends. Draw a connecting run with revit_create_mep_run.");
        }

        switch (mode)
        {
            case "elbow":
                if (geometry.Relation is MepRelation.Angled or MepRelation.CoincidentAngled)
                    return Act("elbow", $"An elbow joins curves meeting at {MepMath.FormatMm(geometry.AngleDeg)}°.");
                return Refuse(MepStatus.SkewLines, "mode=elbow needs curves that meet at an angle; these are in line. Use mode=union or auto.");

            case "union":
            case "extend":
                if (geometry.Relation is MepRelation.Coincident or MepRelation.Collinear)
                {
                    if (!sameSize)
                        return Act("transition", "The ends are in line but differ in size: a transition joins them.");
                    return mode == "union"
                        ? Act("union", geometry.Relation == MepRelation.Coincident
                            ? "The ends touch in line: a union joins them."
                            : $"The ends are in line {MepMath.FormatMm(geometry.GapMm)} mm apart: A is extended to B and a union joins them.")
                        : Act("extend", geometry.Relation == MepRelation.Coincident
                            ? "The ends already touch in line: they are connected directly."
                            : $"A is extended {MepMath.FormatMm(geometry.GapMm)} mm to B's end and the two are connected.");
                }
                return Refuse(MepStatus.SkewLines,
                    $"mode={mode} needs curves in line (within 1°); these meet at {MepMath.FormatMm(geometry.AngleDeg)}°. Use mode=elbow or auto.");

            default:
                if (geometry.Relation is MepRelation.Angled or MepRelation.CoincidentAngled)
                    return Act("elbow", $"The curves meet at {MepMath.FormatMm(geometry.AngleDeg)}°: an elbow joins them.");
                if (!sameSize)
                    return Act("transition", "The ends are in line but differ in size: a transition joins them.");
                return geometry.Relation == MepRelation.Coincident
                    ? Act("connect_to", "The ends touch in line: they are connected directly.")
                    : Act("extend", $"The ends are in line {MepMath.FormatMm(geometry.GapMm)} mm apart: A is extended to B and the two are connected.");
        }
    }

    /// <summary>The distance of <paramref name="point"/> from the line through <paramref name="origin"/> along <paramref name="direction"/>.</summary>
    public static double PerpendicularDistance(MepPoint point, MepPoint origin, MepPoint direction)
    {
        var unit = direction.Normalize();
        var relative = point - origin;
        return (relative - unit * relative.Dot(unit)).Length;
    }

    /// <summary>The index of the candidate end nearest <paramref name="point"/> within <paramref name="snapMm"/>, or -1.</summary>
    public static int NearestWithin(MepPoint point, IReadOnlyList<MepPoint> candidates, double snapMm)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < candidates.Count; i++)
        {
            var distance = candidates[i].DistanceTo(point);
            if (distance > snapMm || distance >= bestDistance) continue;
            best = i;
            bestDistance = distance;
        }
        return best;
    }

    private static MepConnectDecision Act(string action, string reason) => new() { Action = action, Reason = reason };
    private static MepConnectDecision Refuse(string status, string reason) => new() { RefusedStatus = status, Reason = reason };
}

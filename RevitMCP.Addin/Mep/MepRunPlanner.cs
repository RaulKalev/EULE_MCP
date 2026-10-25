using System.Globalization;

namespace RevitMCP.Addin.Mep;

/// <summary>One straight piece of a planned run: one CableTray or Conduit element.</summary>
public sealed class MepPlannedSegment
{
    public int Index { get; init; }
    public MepPoint From { get; init; }
    public MepPoint To { get; init; }
    public double LengthMm => From.DistanceTo(To);
    public MepPoint Direction => (To - From).Normalize();

    /// <summary>horizontal, vertical (a riser) or sloped.</summary>
    public string Orientation { get; init; } = "horizontal";
}

/// <summary>A change of direction between two segments, where an elbow goes.</summary>
public sealed class MepPlannedBend
{
    /// <summary>Index of the bend along the run (0 = between segment 0 and 1).</summary>
    public int Index { get; init; }

    public MepPoint At { get; init; }

    /// <summary>The deflection: 0 = straight on, 90 = a right angle, towards 180 = doubling back.</summary>
    public double AngleDeg { get; init; }

    /// <summary>horizontal (both segments level: a horizontal bend) or vertical (any bend involving a rise or fall).</summary>
    public string Plane { get; init; } = "horizontal";

    public int SegmentBefore => Index;
    public int SegmentAfter => Index + 1;

    /// <summary>"elbow" when fittings=auto, null when no fitting is placed.</summary>
    public string? Fitting { get; init; }
}

/// <summary>What the planner made of a polyline: the run the tools draw, and what it changed on the way.</summary>
public sealed class MepRunPlan
{
    public List<MepPoint> Points { get; } = new();
    public List<MepPlannedSegment> Segments { get; } = new();
    public List<MepPlannedBend> Bends { get; } = new();

    /// <summary>What was merged or dropped from the input, one line each.</summary>
    public List<string> Changes { get; } = new();

    public List<string> Warnings { get; } = new();
    public List<string> Errors { get; } = new();

    public bool IsValid => Errors.Count == 0 && Segments.Count > 0;

    public double TotalLengthMm => Segments.Sum(s => s.LengthMm);
    public int ElbowCount => Bends.Count(b => b.Fitting == "elbow");
    public bool HasVerticalBends => Bends.Any(b => b.Plane == "vertical");
    public bool HasHorizontalBends => Bends.Any(b => b.Plane == "horizontal");
}

/// <summary>
/// Pure planning of a tray or conduit run from a polyline (#106): drops repeated points, merges collinear points and
/// segments shorter than minSegmentMm, measures the bends and decides where elbows go. No Revit API.
/// </summary>
public static class MepRunPlanner
{
    /// <summary>A bend sharper than this (deflection) points back on itself: the polyline is rejected.</summary>
    public const double DoublingBackDeg = 179.0;

    public static MepRunPlan Plan(IReadOnlyList<MepPoint> input, double minSegmentMm, bool autoFittings)
    {
        var plan = new MepRunPlan();
        if (input.Count < 2)
        {
            plan.Errors.Add($"A run needs at least 2 points (got {input.Count}).");
            return plan;
        }

        var points = new List<MepPoint>(input);
        var startPoint = points[0];
        var endPoint = points[points.Count - 1];

        DropRepeatedPoints(points, plan);
        if (points.Count < 2)
        {
            plan.Errors.Add("All points are the same point: the run has no length.");
            return plan;
        }

        // Merge until nothing changes: removing a short jog can make its neighbours collinear.
        for (var pass = 0; pass < input.Count + 2; pass++)
        {
            var changed = MergeCollinear(points, plan);
            changed |= MergeShortSegments(points, minSegmentMm, plan);
            changed |= DropRepeatedPoints(points, plan);
            if (!changed) break;
        }

        // A run that doubles back cannot be built with elbows.
        for (var i = 1; i < points.Count - 1; i++)
        {
            var angle = MepMath.AngleDeg(points[i] - points[i - 1], points[i + 1] - points[i]);
            if (angle >= DoublingBackDeg)
                plan.Errors.Add($"The polyline doubles back on itself at point {points[i]} (a {Format(angle)}° turn). " +
                                "Split it into two runs or add a point to go round.");
        }
        if (plan.Errors.Count > 0) return plan;

        plan.Points.AddRange(points);
        for (var i = 0; i < points.Count - 1; i++)
        {
            var direction = points[i + 1] - points[i];
            plan.Segments.Add(new MepPlannedSegment
            {
                Index = i,
                From = points[i],
                To = points[i + 1],
                Orientation = MepMath.IsHorizontal(direction) ? "horizontal" : MepMath.IsVertical(direction) ? "vertical" : "sloped"
            });
        }

        for (var i = 0; i < plan.Segments.Count - 1; i++)
        {
            var before = plan.Segments[i];
            var after = plan.Segments[i + 1];
            var angle = MepMath.AngleDeg(before.Direction, after.Direction);
            var plane = before.Orientation == "horizontal" && after.Orientation == "horizontal" ? "horizontal" : "vertical";
            plan.Bends.Add(new MepPlannedBend
            {
                Index = i,
                At = before.To,
                AngleDeg = Math.Round(angle, 2, MidpointRounding.AwayFromZero),
                Plane = plane,
                Fitting = autoFittings ? "elbow" : null
            });
        }

        Warn(plan, minSegmentMm, startPoint, endPoint);
        return plan;
    }

    private static void Warn(MepRunPlan plan, double minSegmentMm, MepPoint startPoint, MepPoint endPoint)
    {
        foreach (var bend in plan.Bends.Where(b => b.AngleDeg > 90 + MepMath.StraightToleranceDeg))
            plan.Warnings.Add($"Bend {bend.Index} at {bend.At} turns {Format(bend.AngleDeg)}°, more than a right angle: most elbow " +
                              "families stop at 90°, so this fitting may fail. Add a point to split it into two bends.");

        var risers = plan.Segments.Where(s => s.Orientation == "vertical").Select(s => s.Index).ToList();
        if (risers.Count > 0)
            plan.Warnings.Add($"Segment(s) {string.Join(", ", risers)} are vertical (risers). That is fine; the bends into and out of " +
                              "them are vertical elbows, which need vertical bend fittings on the type.");

        var sloped = plan.Segments.Where(s => s.Orientation == "sloped").Select(s => s.Index).ToList();
        if (sloped.Count > 0)
            plan.Warnings.Add($"Segment(s) {string.Join(", ", sloped)} are sloped (neither level nor vertical). Slope is not supported " +
                              "on purpose: the segments are drawn as given, but the elbows at their ends may fail.");

        if (plan.Segments.Count == 1 && minSegmentMm > 0 && plan.Segments[0].LengthMm < minSegmentMm)
            plan.Warnings.Add($"The run is a single segment of {Format(plan.Segments[0].LengthMm)} mm, shorter than minSegmentMm " +
                              $"({Format(minSegmentMm)} mm). It is drawn as given.");

        if (plan.TotalLengthMm > MepMath.LongRunWarningMm)
            plan.Warnings.Add($"The run is {Format(plan.TotalLengthMm / 1000.0)} m long, more than 500 m: check the units — " +
                              "points are millimetres in model coordinates.");

        if (plan.Points[0].DistanceTo(startPoint) > MepMath.CoincidentToleranceMm ||
            plan.Points[plan.Points.Count - 1].DistanceTo(endPoint) > MepMath.CoincidentToleranceMm)
            plan.Warnings.Add("Merging moved a run end. The ends are kept where possible; check the normalized polyline.");
    }

    /// <summary>Removes points that repeat the previous point (zero-length segments). True when something was removed.</summary>
    private static bool DropRepeatedPoints(List<MepPoint> points, MepRunPlan plan)
    {
        var changed = false;
        for (var i = points.Count - 1; i >= 1; i--)
        {
            if (points[i].DistanceTo(points[i - 1]) >= MepMath.CoincidentToleranceMm) continue;
            plan.Changes.Add($"Dropped a zero-length segment at {points[i]}.");
            // Keep the last point of the run as given: remove the earlier copy.
            points.RemoveAt(i == points.Count - 1 ? i - 1 : i);
            changed = true;
        }
        return changed;
    }

    /// <summary>Removes interior points where the run goes straight on (deflection below 1°).</summary>
    private static bool MergeCollinear(List<MepPoint> points, MepRunPlan plan)
    {
        var changed = false;
        for (var i = points.Count - 2; i >= 1; i--)
        {
            var angle = MepMath.AngleDeg(points[i] - points[i - 1], points[i + 1] - points[i]);
            if (angle >= MepMath.StraightToleranceDeg) continue;
            plan.Changes.Add(angle < 0.01
                ? $"Merged collinear point {points[i]}."
                : $"Merged point {points[i]}: its {Format(angle)}° bend is below 1° and treated as straight.");
            points.RemoveAt(i);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Segments shorter than minSegmentMm leave no room for elbows. An inner one collapses to its midpoint; the first
    /// and the last keep the run end and lose their inner point instead. True when something was merged.
    /// </summary>
    private static bool MergeShortSegments(List<MepPoint> points, double minSegmentMm, MepRunPlan plan)
    {
        if (minSegmentMm <= 0) return false;
        for (var i = 0; i < points.Count - 1; i++)
        {
            var length = points[i].DistanceTo(points[i + 1]);
            if (length >= minSegmentMm || points.Count <= 2) continue;

            if (i == 0)
            {
                plan.Changes.Add($"Merged the first segment ({Format(length)} mm, shorter than minSegmentMm) into the next one: " +
                                 $"removed point {points[1]}.");
                points.RemoveAt(1);
            }
            else if (i == points.Count - 2)
            {
                plan.Changes.Add($"Merged the last segment ({Format(length)} mm, shorter than minSegmentMm) into the previous one: " +
                                 $"removed point {points[i]}.");
                points.RemoveAt(i);
            }
            else
            {
                var middle = (points[i] + points[i + 1]) / 2;
                plan.Changes.Add($"Merged segment {i} ({Format(length)} mm, shorter than minSegmentMm): points {points[i]} and " +
                                 $"{points[i + 1]} became {middle}.");
                points[i] = middle;
                points.RemoveAt(i + 1);
            }
            return true;
        }
        return false;
    }

    private static string Format(double value) => MepMath.Round(value).ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>Standard size checks (#106): a conduit diameter must be one of the standard's sizes.</summary>
public static class MepSizeMath
{
    /// <summary>A size within this of a standard size counts as that size (mm).</summary>
    public const double ToleranceMm = 0.05;

    public static bool IsStandard(double sizeMm, IEnumerable<double> standardMm) =>
        standardMm.Any(s => Math.Abs(s - sizeMm) <= ToleranceMm);

    /// <summary>The standard sizes nearest to <paramref name="sizeMm"/>, nearest first.</summary>
    public static List<double> Nearest(double sizeMm, IEnumerable<double> standardMm, int count = 3) =>
        standardMm.Distinct().OrderBy(s => Math.Abs(s - sizeMm)).ThenBy(s => s).Take(count).Select(MepMath.Round).ToList();
}

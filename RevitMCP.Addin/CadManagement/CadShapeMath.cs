namespace RevitMCP.Addin.CadManagement;

/// <summary>
/// One straight piece of CAD geometry, in millimetres, in host coordinates. Arcs and circles arrive
/// tessellated into several of these, flagged so a round symbol can still be told from a boxy one.
/// </summary>
public sealed class CadSegment
{
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }

    /// <summary>Drawing height of the piece, carried through so <c>elevationMode=dwg</c> still works.</summary>
    public double Zmm { get; set; }

    public string Layer { get; set; } = string.Empty;

    /// <summary>True when this came from an arc or a circle rather than a straight line.</summary>
    public bool FromArc { get; set; }
}

/// <summary>
/// A fixture reconstructed from loose geometry: the lines that touch each other, boxed and measured.
/// </summary>
public sealed class CadShape
{
    public string Layer { get; set; } = string.Empty;

    /// <summary>Centre of the minimum-area box around the cluster — the insertion point.</summary>
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Zmm { get; set; }

    /// <summary>Long side of the minimum-area box.</summary>
    public double LengthMm { get; set; }

    /// <summary>Short side of the minimum-area box.</summary>
    public double WidthMm { get; set; }

    /// <summary>
    /// Direction of the long side, in degrees, in [0, 180). A drawn rectangle is symmetric, so its
    /// orientation is only ever known to half a turn; which end is "front" is not in the geometry.
    /// </summary>
    public double RotationDegrees { get; set; }

    public int SegmentCount { get; set; }
    public int ArcSegmentCount { get; set; }

    /// <summary>What the cluster looks like: <c>rectangle</c>, <c>circle</c>, <c>line</c> or <c>other</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Kind plus bucketed size — the key a family type is mapped to.</summary>
    public string Signature { get; set; } = string.Empty;

    /// <summary>
    /// Set when the cluster is larger than any fixture should be. Almost always a drawing line that
    /// happens to touch a symbol, dragging half the plan into one cluster.
    /// </summary>
    public bool Oversize { get; set; }
}

/// <summary>
/// The Revit-free half of reading fixtures out of loose DWG geometry.
///
/// A DWG that draws its luminaires as bare lines carries no blocks, no insertion points and no
/// rotation. What it does carry is that the lines of one symbol touch each other and nothing else,
/// so the symbols fall out as connected components, and the minimum-area box around a component
/// gives back the centre and the angle the draughtsman drew it at.
/// </summary>
public static class CadShapeMath
{
    public const string KindRectangle = "rectangle";
    public const string KindCircle = "circle";
    public const string KindLine = "line";
    public const string KindOther = "other";

    public const double DefaultJoinToleranceMm = 2.0;
    public const double DefaultSignatureBucketMm = 10.0;
    public const double DefaultMaxShapeSizeMm = 3000.0;

    /// <summary>Below this a box has no measurable second dimension and the cluster is just a line.</summary>
    private const double FlatToleranceMm = 0.5;

    public static bool IsKnownKind(string kind) =>
        kind is KindRectangle or KindCircle or KindLine or KindOther;

    /// <summary>
    /// Groups touching segments into fixtures and measures each one.
    ///
    /// Segments join when an endpoint of one lands within <paramref name="joinToleranceMm"/> of an
    /// endpoint of the other, never across layers. Clusters whose long side exceeds
    /// <paramref name="maxShapeSizeMm"/> are returned flagged rather than dropped: silently losing
    /// geometry is worse than reporting that a wall line got caught in the net.
    /// </summary>
    public static List<CadShape> Cluster(
        IReadOnlyList<CadSegment> segments,
        double joinToleranceMm = DefaultJoinToleranceMm,
        double signatureBucketMm = DefaultSignatureBucketMm,
        double maxShapeSizeMm = DefaultMaxShapeSizeMm)
    {
        var shapes = new List<CadShape>();
        if (segments.Count == 0)
            return shapes;

        var tolerance = joinToleranceMm > 0 ? joinToleranceMm : 0.0;
        var groups = ConnectedComponents(segments, tolerance);

        foreach (var group in groups)
        {
            var shape = Measure(segments, group, signatureBucketMm);
            if (shape == null)
                continue;

            shape.Oversize = maxShapeSizeMm > 0 && shape.LengthMm > maxShapeSizeMm;
            shapes.Add(shape);
        }

        // Stable order so a preview and the write that follows list the same fixture in the same
        // place, and so the caller can page through them.
        return shapes
            .OrderBy(s => s.Layer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => Math.Round(s.CenterY, 3))
            .ThenBy(s => Math.Round(s.CenterX, 3))
            .ToList();
    }

    /// <summary>Counts how many fixtures share each signature — what to show before mapping types.</summary>
    public static List<(string Signature, string Kind, int Count, double LengthMm, double WidthMm)>
        SummariseSignatures(IReadOnlyList<CadShape> shapes)
    {
        return shapes
            .GroupBy(s => s.Signature, StringComparer.OrdinalIgnoreCase)
            .Select(g => (
                Signature: g.Key,
                Kind: g.First().Kind,
                Count: g.Count(),
                LengthMm: g.Average(s => s.LengthMm),
                WidthMm: g.Average(s => s.WidthMm)))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Signature, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Union-find over segment endpoints, with a grid so only nearby endpoints are ever compared.
    /// Returns one list of segment indices per connected component.
    /// </summary>
    private static List<List<int>> ConnectedComponents(
        IReadOnlyList<CadSegment> segments,
        double toleranceMm)
    {
        var parent = new int[segments.Count];
        for (var i = 0; i < parent.Length; i++)
            parent[i] = i;

        if (toleranceMm > 0)
        {
            // Cell size is the tolerance, so a match can only ever be in the 9 cells around a point.
            var cells = new Dictionary<(string Layer, long X, long Y), List<(int Index, double X, double Y)>>();

            for (var index = 0; index < segments.Count; index++)
            {
                var segment = segments[index];
                Add(cells, segment.Layer, index, segment.X1, segment.Y1, toleranceMm);
                Add(cells, segment.Layer, index, segment.X2, segment.Y2, toleranceMm);
            }

            var toleranceSquared = toleranceMm * toleranceMm;

            foreach (var cell in cells)
            {
                foreach (var endpoint in cell.Value)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        var key = (cell.Key.Layer, cell.Key.X + dx, cell.Key.Y + dy);
                        if (!cells.TryGetValue(key, out var neighbours))
                            continue;

                        foreach (var other in neighbours)
                        {
                            if (other.Index == endpoint.Index)
                                continue;

                            var ex = other.X - endpoint.X;
                            var ey = other.Y - endpoint.Y;
                            if (ex * ex + ey * ey <= toleranceSquared)
                                Union(parent, endpoint.Index, other.Index);
                        }
                    }
                }
            }
        }

        var components = new Dictionary<int, List<int>>();
        for (var index = 0; index < segments.Count; index++)
        {
            var root = Find(parent, index);
            if (!components.TryGetValue(root, out var bucket))
            {
                bucket = new List<int>();
                components[root] = bucket;
            }
            bucket.Add(index);
        }

        return components.Values.ToList();
    }

    private static void Add(
        Dictionary<(string, long, long), List<(int, double, double)>> cells,
        string layer,
        int index,
        double x,
        double y,
        double cellSize)
    {
        var key = (layer, (long)Math.Floor(x / cellSize), (long)Math.Floor(y / cellSize));
        if (!cells.TryGetValue(key, out var bucket))
        {
            bucket = new List<(int, double, double)>();
            cells[key] = bucket;
        }
        bucket.Add((index, x, y));
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }
        return index;
    }

    private static void Union(int[] parent, int a, int b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);
        if (rootA != rootB)
            parent[rootB] = rootA;
    }

    /// <summary>Boxes one cluster and works out what it is.</summary>
    private static CadShape? Measure(
        IReadOnlyList<CadSegment> segments,
        List<int> group,
        double signatureBucketMm)
    {
        if (group.Count == 0)
            return null;

        var points = new List<(double X, double Y)>(group.Count * 2);
        var arcSegments = 0;
        var zSum = 0.0;

        foreach (var index in group)
        {
            var segment = segments[index];
            points.Add((segment.X1, segment.Y1));
            points.Add((segment.X2, segment.Y2));
            if (segment.FromArc)
                arcSegments++;
            zSum += segment.Zmm;
        }

        var box = MinAreaBox(points);
        if (box == null)
            return null;

        var (centerX, centerY, longSide, shortSide, angle, hullArea) = box.Value;
        var kind = Classify(longSide, shortSide, hullArea, arcSegments);

        var shape = new CadShape
        {
            Layer = segments[group[0]].Layer,
            CenterX = centerX,
            CenterY = centerY,
            Zmm = zSum / group.Count,
            LengthMm = longSide,
            WidthMm = shortSide,
            // A circle reads as square, so its box angle is whatever the tessellation happened to
            // land on. Reporting that as a rotation would spin every downlight at random.
            RotationDegrees = kind == KindCircle ? 0.0 : NormalizeHalfTurn(angle),
            SegmentCount = group.Count,
            ArcSegmentCount = arcSegments,
            Kind = kind
        };

        shape.Signature = BuildSignature(shape, signatureBucketMm);
        return shape;
    }

    /// <summary>
    /// Smallest box that contains every point, at any angle. Found by rotating calipers: the
    /// minimum-area box always shares an edge with the convex hull, so trying each hull edge as the
    /// box direction and keeping the smallest is exact, not an approximation.
    /// </summary>
    public static (double CenterX, double CenterY, double LongSide, double ShortSide,
        double AngleDegrees, double HullArea)? MinAreaBox(IReadOnlyList<(double X, double Y)> points)
    {
        var hull = ConvexHull(points);
        if (hull.Count == 0)
            return null;

        if (hull.Count == 1)
            return (hull[0].X, hull[0].Y, 0.0, 0.0, 0.0, 0.0);

        var hullArea = PolygonArea(hull);

        var bestArea = double.MaxValue;
        double bestCenterX = 0, bestCenterY = 0, bestWidth = 0, bestHeight = 0, bestAngle = 0;

        for (var i = 0; i < hull.Count; i++)
        {
            var a = hull[i];
            var b = hull[(i + 1) % hull.Count];

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var edgeLength = Math.Sqrt(dx * dx + dy * dy);
            if (edgeLength < 1e-12)
                continue;

            // Project every hull point into the frame aligned with this edge.
            var cos = dx / edgeLength;
            var sin = dy / edgeLength;

            double minU = double.MaxValue, maxU = double.MinValue;
            double minV = double.MaxValue, maxV = double.MinValue;

            foreach (var point in hull)
            {
                var u = point.X * cos + point.Y * sin;
                var v = -point.X * sin + point.Y * cos;
                if (u < minU) minU = u;
                if (u > maxU) maxU = u;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            var width = maxU - minU;
            var height = maxV - minV;

            // A degenerate (collinear) hull has zero area in every direction; comparing on area
            // alone would keep an arbitrary one, so fall back to the extent across the line.
            var area = width * height;
            var score = area > 1e-12 ? area : width;

            if (score >= bestArea)
                continue;

            bestArea = score;
            bestWidth = width;
            bestHeight = height;
            bestAngle = Math.Atan2(sin, cos) * 180.0 / Math.PI;

            // Centre back out of the rotated frame.
            var centerU = (minU + maxU) / 2.0;
            var centerV = (minV + maxV) / 2.0;
            bestCenterX = centerU * cos - centerV * sin;
            bestCenterY = centerU * sin + centerV * cos;
        }

        if (bestArea == double.MaxValue)
            return null;

        // Report the long side first, and give the angle of that long side.
        var longSide = Math.Max(bestWidth, bestHeight);
        var shortSide = Math.Min(bestWidth, bestHeight);
        var angle = bestHeight > bestWidth ? bestAngle + 90.0 : bestAngle;

        return (bestCenterX, bestCenterY, longSide, shortSide, angle, hullArea);
    }

    /// <summary>Convex hull by Andrew's monotone chain, counter-clockwise, without repeating the first point.</summary>
    public static List<(double X, double Y)> ConvexHull(IReadOnlyList<(double X, double Y)> points)
    {
        var sorted = points
            .Select(p => (X: p.X, Y: p.Y))
            .Distinct()
            .OrderBy(p => p.X)
            .ThenBy(p => p.Y)
            .ToList();

        if (sorted.Count <= 2)
            return sorted;

        var hull = new List<(double X, double Y)>(sorted.Count * 2);

        // Lower hull, then upper hull; each drops points that would make a clockwise turn.
        foreach (var point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }

        var lowerCount = hull.Count + 1;
        for (var i = sorted.Count - 2; i >= 0; i--)
        {
            var point = sorted[i];
            while (hull.Count >= lowerCount && Cross(hull[hull.Count - 2], hull[hull.Count - 1], point) <= 0)
                hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }

        // The last point repeats the first.
        hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) =>
        (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static double PolygonArea(IReadOnlyList<(double X, double Y)> polygon)
    {
        if (polygon.Count < 3)
            return 0.0;

        var sum = 0.0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum) / 2.0;
    }

    /// <summary>
    /// What the cluster is, from how much of its box it fills. A rectangle fills all of it, a circle
    /// fills pi/4 of it, and something with no second dimension is a bare line.
    /// </summary>
    private static string Classify(double longSide, double shortSide, double hullArea, int arcSegments)
    {
        if (shortSide < FlatToleranceMm || longSide < FlatToleranceMm)
            return KindLine;

        var boxArea = longSide * shortSide;
        if (boxArea <= 1e-9)
            return KindLine;

        var fill = hullArea / boxArea;
        var aspect = shortSide / longSide;

        // Round symbols come in as arc pieces and box up square; pi/4 is 0.785.
        if (arcSegments > 0 && aspect > 0.9 && fill is > 0.70 and < 0.88)
            return KindCircle;

        if (fill > 0.90)
            return KindRectangle;

        return KindOther;
    }

    /// <summary>
    /// The key a family type is mapped to. Sizes are bucketed because no two drawn symbols measure
    /// exactly alike, and an unbucketed key would give every fixture a signature of its own.
    /// </summary>
    public static string BuildSignature(CadShape shape, double bucketMm)
    {
        var bucket = bucketMm > 0 ? bucketMm : DefaultSignatureBucketMm;

        return shape.Kind switch
        {
            KindCircle => $"circle d{Bucket(shape.LengthMm, bucket)}",
            KindLine => $"line {Bucket(shape.LengthMm, bucket)}",
            _ => $"{shape.Kind} {Bucket(shape.LengthMm, bucket)}x{Bucket(shape.WidthMm, bucket)}"
        };
    }

    private static long Bucket(double valueMm, double bucketMm) =>
        (long)Math.Round(valueMm / bucketMm, MidpointRounding.AwayFromZero) * (long)bucketMm;

    /// <summary>
    /// Wraps an angle into [0, 180). A drawn rectangle looks the same rotated half a turn, so 190
    /// degrees and 10 degrees are the same fixture and must not signature differently.
    /// </summary>
    public static double NormalizeHalfTurn(double degrees)
    {
        var wrapped = degrees % 180.0;
        if (wrapped < 0) wrapped += 180.0;
        return Math.Abs(wrapped - 180.0) < 1e-9 ? 0.0 : wrapped;
    }
}

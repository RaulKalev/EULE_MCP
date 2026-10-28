namespace RevitMCP.Addin.Coordination.ClashFix;

/// <summary>A point or direction for the clash-fix math (any length unit; the callers use millimetres).</summary>
public readonly struct FixVector
{
    public FixVector(double x, double y, double z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static readonly FixVector Up = new(0, 0, 1);

    public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
    public double Dot(FixVector o) => X * o.X + Y * o.Y + Z * o.Z;
    public FixVector Cross(FixVector o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    public FixVector Scale(double f) => new(X * f, Y * f, Z * f);
    public FixVector Normalize() => Length < 1e-12 ? this : Scale(1 / Length);
    public static FixVector operator -(FixVector v) => new(-v.X, -v.Y, -v.Z);
}

/// <summary>One way to push the moving element out of the obstacle.</summary>
public sealed class FixDirection
{
    public FixDirection(string name, FixVector vector)
    {
        Name = name;
        Vector = vector.Normalize();
    }

    /// <summary>up, down, +x, -x, +y, -y, or side+ / side- (horizontal, square to a run).</summary>
    public string Name { get; }

    public FixVector Vector { get; }

    /// <summary>True for the horizontal directions (every name but up and down).</summary>
    public bool IsHorizontal => Name != "up" && Name != "down";
}

/// <summary>What kind of element moves: a straight MEP run segment, or a family instance placed at a point.</summary>
public enum MoverShape
{
    LinearRun,
    PointInstance
}

/// <summary>A candidate for the element that moves: what a clash fix ranks between the two clashing elements.</summary>
public sealed class MoverCandidate
{
    public long ElementId { get; set; }

    /// <summary>Null when it can move; else why it is not moved automatically.</summary>
    public string? Blocked { get; set; }

    /// <summary>End connectors joined to other elements (a free element is the cheaper one to move).</summary>
    public int ConnectedEnds { get; set; }

    /// <summary>Bounding-box volume, any unit: the smaller element moves first.</summary>
    public double Volume { get; set; }
}

/// <summary>
/// Pure geometry behind clash fixes (#bcf): which directions an element may move in, how far it must travel to leave an
/// obstacle, which element of a pair moves, and whether a connected run segment can move without sloping its
/// neighbours. The Revit side (ClashFixService) supplies the points and confirms each distance with real solids.
/// </summary>
public static class ClashFixMath
{
    /// <summary>Directions whose names <c>directions</c> accepts besides the exact ones.</summary>
    public const string VerticalGroup = "vertical";
    public const string HorizontalGroup = "horizontal";

    /// <summary>
    /// Candidate directions. A horizontal run moves up, down or sideways (square to itself, never along itself, which
    /// does not clear anything it crosses); a vertical run moves along the model X and Y axes; a sloped run moves up,
    /// down or sideways. A point instance moves up, down and along X and Y; a face-hosted one only within its host's
    /// plane (no component along <paramref name="hostNormal"/>). <paramref name="filter"/> keeps the named directions
    /// (exact names, or "vertical" / "horizontal"); null or empty keeps all.
    /// </summary>
    public static List<FixDirection> Directions(
        MoverShape shape,
        FixVector? runAxis,
        FixVector? hostNormal,
        IReadOnlyCollection<string>? filter)
    {
        var result = new List<FixDirection>();
        if (shape == MoverShape.LinearRun && runAxis is { } axisRaw)
        {
            var axis = axisRaw.Normalize();
            var vertical = Math.Abs(axis.Z) > 0.9;
            if (!vertical)
            {
                result.Add(new FixDirection("up", FixVector.Up));
                result.Add(new FixDirection("down", -FixVector.Up));
                var side = axis.Cross(FixVector.Up).Normalize();
                result.Add(new FixDirection("side+", side));
                result.Add(new FixDirection("side-", -side));
            }
            else
            {
                AddAxes(result);
            }
        }
        else
        {
            result.Add(new FixDirection("up", FixVector.Up));
            result.Add(new FixDirection("down", -FixVector.Up));
            AddAxes(result);
        }

        if (hostNormal is { } normalRaw && normalRaw.Length > 1e-9)
        {
            var normal = normalRaw.Normalize();
            result.RemoveAll(d => Math.Abs(d.Vector.Dot(normal)) > 0.1);
        }

        if (filter != null && filter.Count > 0)
        {
            var names = new HashSet<string>(filter.Select(f => f.Trim().ToLowerInvariant()));
            result.RemoveAll(d => !(names.Contains(d.Name) ||
                                    (names.Contains(VerticalGroup) && !d.IsHorizontal) ||
                                    (names.Contains(HorizontalGroup) && d.IsHorizontal)));
        }

        return result;
    }

    private static void AddAxes(List<FixDirection> result)
    {
        result.Add(new FixDirection("+x", new FixVector(1, 0, 0)));
        result.Add(new FixDirection("-x", new FixVector(-1, 0, 0)));
        result.Add(new FixDirection("+y", new FixVector(0, 1, 0)));
        result.Add(new FixDirection("-y", new FixVector(0, -1, 0)));
    }

    /// <summary>
    /// How far the mover must travel along <paramref name="direction"/> until every one of its points lies past every
    /// obstacle point: an upper bound for the travel that clears the obstacle (exact for boxes along their axes). Null
    /// when either point set is empty.
    /// </summary>
    public static double? TravelToPass(IEnumerable<FixVector> mover, IEnumerable<FixVector> obstacle, FixVector direction)
    {
        var u = direction.Normalize();
        double? moverMin = null, obstacleMax = null;
        foreach (var p in mover)
        {
            var d = p.Dot(u);
            if (moverMin == null || d < moverMin) moverMin = d;
        }
        foreach (var p in obstacle)
        {
            var d = p.Dot(u);
            if (obstacleMax == null || d > obstacleMax) obstacleMax = d;
        }
        if (moverMin == null || obstacleMax == null) return null;
        return Math.Max(0, obstacleMax.Value - moverMin.Value);
    }

    /// <summary>
    /// The smallest travel in [0, <paramref name="upperBound"/>] at which <paramref name="clears"/> holds, by bisection to
    /// <paramref name="precision"/>. Assumes that once the mover is clear it stays clear further out; the caller checks
    /// the final position again. Returns null when even the upper bound does not clear.
    /// </summary>
    public static double? MinimalClearingTravel(double upperBound, Func<double, bool> clears, double precision, int maxSteps = 16)
    {
        if (!clears(upperBound)) return null;
        double low = 0, high = upperBound;
        for (var step = 0; step < maxSteps && high - low > precision; step++)
        {
            var mid = (low + high) / 2;
            if (clears(mid)) high = mid;
            else low = mid;
        }
        return high;
    }

    /// <summary>The travel to propose: the clearing travel plus the clearance, rounded up to a multiple of <paramref name="step"/>.</summary>
    public static double ProposedTravel(double clearingTravel, double clearance, double step)
    {
        var raw = Math.Max(0, clearingTravel) + Math.Max(0, clearance);
        if (step <= 0) return Math.Round(raw, 1);
        return Math.Ceiling(raw / step - 1e-9) * step;
    }

    /// <summary>True when the two directions are parallel or anti-parallel within <paramref name="cosTolerance"/>.</summary>
    public static bool IsParallel(FixVector a, FixVector b, double cosTolerance = 0.9995) =>
        a.Length > 1e-12 && b.Length > 1e-12 && Math.Abs(a.Normalize().Dot(b.Normalize())) >= cosTolerance;

    /// <summary>
    /// Whether a run segment joined at its ends can move along <paramref name="direction"/>: only when every neighbouring
    /// run it is joined to (through a fitting) lies along the move, so those neighbours stretch or shorten and keep their
    /// slope. Null when it can; otherwise the reason.
    /// </summary>
    public static string? ConnectedMoveBlocker(FixVector direction, IReadOnlyList<FixVector> neighbourAxes, int connectedToEquipment)
    {
        if (connectedToEquipment > 0)
            return "joined directly to equipment or a device: moving it would pull the connection apart";
        var crossing = neighbourAxes.Count(axis => !IsParallel(axis, direction));
        return crossing == 0
            ? null
            : $"{crossing} joined neighbour run(s) do not lie along this move and would be sloped: needs a reroute (offset with fittings) by hand";
    }

    /// <summary>The movable candidates, best first: unblocked, then the fewest joined ends, then the smallest.</summary>
    public static List<MoverCandidate> RankMovers(IEnumerable<MoverCandidate> candidates) =>
        candidates
            .Where(c => c.Blocked == null)
            .OrderBy(c => c.ConnectedEnds)
            .ThenBy(c => c.Volume)
            .ThenBy(c => c.ElementId)
            .ToList();
}

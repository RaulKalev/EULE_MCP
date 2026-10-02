namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>A plan segment in feet (link coordinates, Z dropped).</summary>
public readonly struct PlanSegment
{
    public PlanSegment(double x1, double y1, double x2, double y2) { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
    public double X1 { get; }
    public double Y1 { get; }
    public double X2 { get; }
    public double Y2 { get; }
    public double Length => Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1));
}

/// <summary>
/// Joins loose plan segments into closed outlines. IFC spaces whose 3D body fails to import still
/// carry their footprint as 2D curves (the IFC "FootPrint" representation); chaining those curves
/// recovers the outline (#68). No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class FootprintCurveChainer
{
    /// <summary>
    /// Chains segments end to end (endpoints within <paramref name="toleranceFt"/>, either direction)
    /// into closed loops. Duplicate and zero-length segments are ignored; open chains are dropped.
    /// Each loop is returned as its vertex list (the closing vertex is not repeated).
    /// </summary>
    public static List<List<(double X, double Y)>> ClosedLoops(IEnumerable<PlanSegment> segments, double toleranceFt)
    {
        var pool = new List<PlanSegment>();
        foreach (var s in segments)
        {
            if (s.Length <= toleranceFt) continue;
            if (pool.Any(p => SameSegment(p, s, toleranceFt))) continue;
            pool.Add(s);
        }

        var loops = new List<List<(double X, double Y)>>();
        var used = new bool[pool.Count];
        for (int start = 0; start < pool.Count; start++)
        {
            if (used[start]) continue;
            used[start] = true;
            var first = (pool[start].X1, pool[start].Y1);
            var current = (pool[start].X2, pool[start].Y2);
            var loop = new List<(double X, double Y)> { first };
            var closed = false;

            for (int guard = 0; guard < pool.Count; guard++)
            {
                if (Near(current, first, toleranceFt)) { closed = true; break; }
                loop.Add(current);

                var next = -1;
                var reversed = false;
                for (int i = 0; i < pool.Count; i++)
                {
                    if (used[i]) continue;
                    if (Near((pool[i].X1, pool[i].Y1), current, toleranceFt)) { next = i; break; }
                    if (Near((pool[i].X2, pool[i].Y2), current, toleranceFt)) { next = i; reversed = true; break; }
                }
                if (next < 0) break;
                used[next] = true;
                current = reversed ? (pool[next].X1, pool[next].Y1) : (pool[next].X2, pool[next].Y2);
            }

            if (!closed && Near(current, first, toleranceFt)) closed = true;
            if (closed && loop.Count >= 3) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>Absolute shoelace area of a vertex loop.</summary>
    public static double Area(IReadOnlyList<(double X, double Y)> loop)
    {
        double sum = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum) / 2;
    }

    /// <summary>The loop with the largest area, or null.</summary>
    public static List<(double X, double Y)>? Largest(IEnumerable<List<(double X, double Y)>> loops) =>
        loops.OrderByDescending(Area).FirstOrDefault();

    private static bool Near((double X, double Y) a, (double X, double Y) b, double tol) =>
        Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol;

    private static bool SameSegment(PlanSegment a, PlanSegment b, double tol) =>
        (Near((a.X1, a.Y1), (b.X1, b.Y1), tol) && Near((a.X2, a.Y2), (b.X2, b.Y2), tol)) ||
        (Near((a.X1, a.Y1), (b.X2, b.Y2), tol) && Near((a.X2, a.Y2), (b.X1, b.Y1), tol));
}

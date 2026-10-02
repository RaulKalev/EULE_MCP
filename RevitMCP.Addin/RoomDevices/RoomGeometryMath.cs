namespace RevitMCP.Addin.RoomDevices;

/// <summary>A plan point or vector in millimetres (host model internal coordinates).</summary>
public readonly struct P2
{
    public P2(double x, double y) { X = x; Y = y; }

    public double X { get; }
    public double Y { get; }

    public double Length => Math.Sqrt(X * X + Y * Y);
    public P2 Plus(P2 o) => new(X + o.X, Y + o.Y);
    public P2 Minus(P2 o) => new(X - o.X, Y - o.Y);
    public P2 Scaled(double k) => new(X * k, Y * k);
    public double Dot(P2 o) => X * o.X + Y * o.Y;
    public double Cross(P2 o) => X * o.Y - Y * o.X;
    public P2 Normalized() => Length < 1e-9 ? new P2(0, 0) : Scaled(1.0 / Length);
    /// <summary>The vector rotated 90° clockwise — the right-hand side of a direction.</summary>
    public P2 RightNormal() => new(Y, -X);
    public double DistanceTo(P2 o) => Minus(o).Length;
    public P2 Rounded(int digits = 1) => new(Math.Round(X, digits), Math.Round(Y, digits));

    public override string ToString() =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#}, {1:0.#})", X, Y);
}

/// <summary>Axis-aligned plan bounds in mm.</summary>
public readonly struct Bounds2
{
    public Bounds2(double minX, double minY, double maxX, double maxY)
    {
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    public double MinX { get; }
    public double MinY { get; }
    public double MaxX { get; }
    public double MaxY { get; }
    public double Width => MaxX - MinX;
    public double Height => MaxY - MinY;

    public bool Contains(P2 p, double margin = 0) =>
        p.X >= MinX - margin && p.X <= MaxX + margin && p.Y >= MinY - margin && p.Y <= MaxY + margin;

    public bool Intersects(Bounds2 o, double margin = 0) =>
        MinX - margin <= o.MaxX && MaxX + margin >= o.MinX && MinY - margin <= o.MaxY && MaxY + margin >= o.MinY;
}

/// <summary>
/// A room footprint: one outer loop and optional holes. After <see cref="RoomGeometryMath.Normalize"/>
/// the outer loop runs clockwise and holes counter-clockwise, so the room interior is always on the
/// right-hand side of every edge.
/// </summary>
public sealed class RoomPolygon
{
    public List<P2> Outer { get; set; } = [];
    public List<List<P2>> Holes { get; set; } = [];

    public IEnumerable<List<P2>> Loops
    {
        get
        {
            yield return Outer;
            foreach (var hole in Holes) yield return hole;
        }
    }
}

/// <summary>One straight piece of a room boundary, seen from inside the room.</summary>
public sealed class WallFace
{
    public int Index { get; set; }
    /// <summary>0 for the outer loop, 1.. for holes (columns, shafts).</summary>
    public int LoopIndex { get; set; }
    public P2 Start { get; set; }
    public P2 End { get; set; }
    public P2 NormalIntoRoom { get; set; }
    public double LengthMm => Start.DistanceTo(End);
    public P2 Direction => End.Minus(Start).Normalized();

    /// <summary>The point <paramref name="alongMm"/> from <see cref="Start"/>, pushed <paramref name="offsetMm"/> into the room.</summary>
    public P2 PointAt(double alongMm, double offsetMm = 0) =>
        Start.Plus(Direction.Scaled(alongMm)).Plus(NormalIntoRoom.Scaled(offsetMm));

    /// <summary>Distance along the face of the projection of <paramref name="p"/>.</summary>
    public double AlongOf(P2 p) => p.Minus(Start).Dot(Direction);

    /// <summary>Unsigned distance from <paramref name="p"/> to the face's infinite line.</summary>
    public double OffsetOf(P2 p) => Math.Abs(p.Minus(Start).Cross(Direction));

    /// <summary>Distance from <paramref name="p"/> to the face segment.</summary>
    public double DistanceTo(P2 p)
    {
        var along = Math.Max(0, Math.Min(LengthMm, AlongOf(p)));
        return PointAt(along).DistanceTo(p);
    }
}

/// <summary>A door or window projected onto a wall face, as a stretch along the face.</summary>
public sealed class FaceOpening
{
    public long Id { get; set; }
    /// <summary>"door" or "window".</summary>
    public string Kind { get; set; } = "door";
    public double FromMm { get; set; }
    public double ToMm { get; set; }
    public double CenterMm => (FromMm + ToMm) / 2;
    public double WidthMm => ToMm - FromMm;
}

/// <summary>
/// Pure plan geometry for room-based device placement: polygon tests, wall faces seen from inside
/// the room, grid layouts with coverage, door-side offsets and the placement sanity checks.
/// No Revit API dependency — unit tested in RevitMCP.Tests.
/// </summary>
public static class RoomGeometryMath
{
    public const double MinimumEdgeMm = 1.0;

    // ── Polygons ─────────────────────────────────────────────────────────────

    /// <summary>Shoelace area; positive for counter-clockwise loops.</summary>
    public static double SignedArea(IReadOnlyList<P2> loop)
    {
        double sum = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    public static double AreaMm2(RoomPolygon polygon) =>
        Math.Abs(SignedArea(polygon.Outer)) - polygon.Holes.Sum(h => Math.Abs(SignedArea(h)));

    /// <summary>
    /// Drops repeated and near-duplicate points (including a closing point equal to the first),
    /// then orients the outer loop clockwise and holes counter-clockwise.
    /// </summary>
    public static RoomPolygon Normalize(RoomPolygon polygon)
    {
        var outer = Clean(polygon.Outer);
        if (SignedArea(outer) > 0) outer.Reverse();

        var holes = new List<List<P2>>();
        foreach (var hole in polygon.Holes)
        {
            var h = Clean(hole);
            if (h.Count < 3) continue;
            if (SignedArea(h) < 0) h.Reverse();
            holes.Add(h);
        }
        return new RoomPolygon { Outer = outer, Holes = holes };
    }

    private static List<P2> Clean(IReadOnlyList<P2> loop)
    {
        var result = new List<P2>();
        foreach (var p in loop)
        {
            if (result.Count > 0 && result[^1].DistanceTo(p) < MinimumEdgeMm) continue;
            result.Add(p);
        }
        while (result.Count > 1 && result[0].DistanceTo(result[^1]) < MinimumEdgeMm)
            result.RemoveAt(result.Count - 1);
        return result;
    }

    public static Bounds2 BoundsOf(IEnumerable<P2> points)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in points)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        return new Bounds2(minX, minY, maxX, maxY);
    }

    /// <summary>Area centroid of a simple loop; falls back to the vertex average for degenerate loops.</summary>
    public static P2 Centroid(IReadOnlyList<P2> loop)
    {
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < loop.Count; i++)
        {
            var p = loop[i];
            var q = loop[(i + 1) % loop.Count];
            var cross = p.X * q.Y - q.X * p.Y;
            a += cross;
            cx += (p.X + q.X) * cross;
            cy += (p.Y + q.Y) * cross;
        }
        if (Math.Abs(a) < 1e-9)
            return new P2(loop.Average(p => p.X), loop.Average(p => p.Y));
        a /= 2;
        return new P2(cx / (6 * a), cy / (6 * a));
    }

    /// <summary>Even-odd point-in-loop test.</summary>
    public static bool LoopContains(IReadOnlyList<P2> loop, P2 p)
    {
        var inside = false;
        for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
        {
            var a = loop[i];
            var b = loop[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) &&
                p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    /// <summary>True when <paramref name="p"/> is inside the outer loop and outside every hole.</summary>
    public static bool Contains(RoomPolygon polygon, P2 p) =>
        LoopContains(polygon.Outer, p) && !polygon.Holes.Any(h => LoopContains(h, p));

    public static double DistanceToBoundary(RoomPolygon polygon, P2 p)
    {
        var best = double.MaxValue;
        foreach (var loop in polygon.Loops)
        {
            for (int i = 0; i < loop.Count; i++)
                best = Math.Min(best, DistanceToSegment(p, loop[i], loop[(i + 1) % loop.Count]));
        }
        return best;
    }

    public static double DistanceToSegment(P2 p, P2 a, P2 b)
    {
        var ab = b.Minus(a);
        var len2 = ab.Dot(ab);
        if (len2 < 1e-12) return p.DistanceTo(a);
        var t = Math.Max(0, Math.Min(1, p.Minus(a).Dot(ab) / len2));
        return a.Plus(ab.Scaled(t)).DistanceTo(p);
    }

    /// <summary>
    /// A point well inside the room: the centroid when it is inside and clear of the boundary,
    /// otherwise the middle of the widest horizontal or vertical interior span, which works for
    /// L-, U- and other non-convex rooms.
    /// </summary>
    public static P2 InteriorPoint(RoomPolygon polygon)
    {
        var centroid = Centroid(polygon.Outer);
        var bounds = BoundsOf(polygon.Outer);
        var minSide = Math.Min(bounds.Width, bounds.Height);
        if (Contains(polygon, centroid) && DistanceToBoundary(polygon, centroid) >= minSide * 0.25)
            return centroid;

        var best = centroid;
        var bestClearance = Contains(polygon, centroid) ? DistanceToBoundary(polygon, centroid) : -1;

        const int samples = 15;
        for (int s = 1; s <= samples; s++)
        {
            var t = (double)s / (samples + 1);
            foreach (var candidate in SpanMidpoints(polygon, bounds.MinY + t * bounds.Height, horizontal: true)
                         .Concat(SpanMidpoints(polygon, bounds.MinX + t * bounds.Width, horizontal: false)))
            {
                if (!Contains(polygon, candidate)) continue;
                var clearance = DistanceToBoundary(polygon, candidate);
                if (clearance > bestClearance)
                {
                    best = candidate;
                    bestClearance = clearance;
                }
            }
        }
        return best;
    }

    /// <summary>Midpoints of the interior spans of a horizontal (y = c) or vertical (x = c) scan line.</summary>
    private static IEnumerable<P2> SpanMidpoints(RoomPolygon polygon, double c, bool horizontal)
    {
        var hits = new List<double>();
        foreach (var loop in polygon.Loops)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var (a1, a2, b1, b2) = horizontal ? (a.Y, a.X, b.Y, b.X) : (a.X, a.Y, b.X, b.Y);
                if ((a1 > c) == (b1 > c)) continue;
                hits.Add(a2 + (c - a1) * (b2 - a2) / (b1 - a1));
            }
        }
        hits.Sort();
        for (int i = 0; i + 1 < hits.Count; i += 2)
        {
            var mid = (hits[i] + hits[i + 1]) / 2;
            yield return horizontal ? new P2(mid, c) : new P2(c, mid);
        }
    }

    // ── Wall faces ───────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the room-side faces of every boundary edge. The normal points into the room; it is
    /// derived from the loop orientation and confirmed by a point-in-room test 50 mm inside.
    /// Expects a polygon normalized with <see cref="Normalize"/>.
    /// </summary>
    public static List<WallFace> BuildFaces(RoomPolygon polygon)
    {
        var faces = new List<WallFace>();
        var loopIndex = 0;
        foreach (var loop in polygon.Loops)
        {
            for (int i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                if (a.DistanceTo(b) < MinimumEdgeMm) continue;

                var normal = b.Minus(a).Normalized().RightNormal();
                var probe = a.Plus(b).Scaled(0.5).Plus(normal.Scaled(50));
                if (!Contains(polygon, probe))
                    normal = normal.Scaled(-1);

                faces.Add(new WallFace
                {
                    Index = faces.Count,
                    LoopIndex = loopIndex,
                    Start = a,
                    End = b,
                    NormalIntoRoom = normal
                });
            }
            loopIndex++;
        }
        return faces;
    }

    /// <summary>The face nearest to <paramref name="p"/>, or null when there are none.</summary>
    public static WallFace? NearestFace(IReadOnlyList<WallFace> faces, P2 p) =>
        faces.Count == 0 ? null : faces.OrderBy(f => f.DistanceTo(p)).First();

    /// <summary>
    /// Projects an opening (door/window centre + width) onto the face it sits in. Returns null when
    /// the opening is farther than <paramref name="maxOffsetMm"/> from the face line or does not overlap it.
    /// </summary>
    public static FaceOpening? ProjectOpening(
        WallFace face, long id, string kind, P2 center, double widthMm, double maxOffsetMm = 400)
    {
        if (face.OffsetOf(center) > maxOffsetMm) return null;
        var along = face.AlongOf(center);
        var half = Math.Max(0, widthMm) / 2;
        if (along + half < 0 || along - half > face.LengthMm) return null;
        return new FaceOpening { Id = id, Kind = kind, FromMm = along - half, ToMm = along + half };
    }

    /// <summary>Plan rotation in degrees (counter-clockwise from +X) of a direction.</summary>
    public static double AngleDeg(P2 direction) =>
        NormalizeDeg(Math.Atan2(direction.Y, direction.X) * 180.0 / Math.PI);

    public static double NormalizeDeg(double deg)
    {
        var d = deg % 360.0;
        if (d < 0) d += 360.0;
        return Math.Abs(d - 360.0) < 1e-9 ? 0 : d;
    }

    // ── Doors ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Along-face position for a device beside a door. <paramref name="lockSign"/> is +1 when the
    /// lock (latch) side is towards the face end, -1 towards its start. "hinge" uses the other side.
    /// The device sits <paramref name="offsetMm"/> clear of the door edge.
    /// </summary>
    public static double AlongBesideDoor(FaceOpening door, int lockSign, string doorSide, double offsetMm)
    {
        var sign = string.Equals(doorSide, "hinge", StringComparison.OrdinalIgnoreCase) ? -lockSign : lockSign;
        return sign > 0 ? door.ToMm + offsetMm : door.FromMm - offsetMm;
    }

    /// <summary>
    /// Lock side of a door on a face from its hand vector: +1 when the hand vector points towards
    /// the face end. <paramref name="handPointsToLatch"/> sets the hand-vector convention.
    /// Returns 0 when the hand vector is (nearly) perpendicular to the face.
    /// </summary>
    public static int LockSignFromHand(WallFace face, P2 hand, bool handPointsToLatch)
    {
        var d = hand.Normalized().Dot(face.Direction);
        if (Math.Abs(d) < 0.3) return 0;
        var sign = d > 0 ? 1 : -1;
        return handPointsToLatch ? sign : -sign;
    }

    /// <summary>
    /// Heuristic lock side when no hand vector exists (IFC doors): hinges usually sit next to the
    /// nearer corner so the leaf opens against the wall, so the lock is on the side with more wall.
    /// </summary>
    public static int LockSignFromCorners(WallFace face, FaceOpening door) =>
        door.FromMm <= face.LengthMm - door.ToMm ? 1 : -1;

    // ── Grid layout ──────────────────────────────────────────────────────────

    public sealed class GridResult
    {
        public List<P2> Points { get; } = [];
        public double StepXMm { get; set; }
        public double StepYMm { get; set; }
        /// <summary>Every point of a cell is within this distance of the cell centre (half the cell diagonal).</summary>
        public double CoverageRadiusMm { get; set; }
        public int SamplesChecked { get; set; }
        public int SamplesUncovered { get; set; }
        public double UncoveredAreaM2 { get; set; }
        public List<P2> UncoveredSamples { get; } = [];
    }

    /// <summary>
    /// Covers the room with a regular grid. The cell size is the largest that keeps both spacing
    /// ≤ <paramref name="maxSpacingMm"/> and the wall distance of the outermost points ≤
    /// <paramref name="maxDistFromWallMm"/> (0 = no wall limit). Cell centres outside the room are
    /// replaced by the interior sample of that cell closest to its centre, so L-shaped rooms stay covered.
    /// Coverage is then verified on a <paramref name="sampleStepMm"/> sample grid.
    /// </summary>
    public static GridResult Grid(
        RoomPolygon polygon,
        double maxSpacingMm,
        double maxDistFromWallMm = 0,
        double sampleStepMm = 500,
        double? coverageRadiusMm = null)
    {
        var result = new GridResult();
        if (maxSpacingMm <= 0) throw new ArgumentOutOfRangeException(nameof(maxSpacingMm));

        var limit = maxDistFromWallMm > 0 ? Math.Min(maxSpacingMm, 2 * maxDistFromWallMm) : maxSpacingMm;
        var bounds = BoundsOf(polygon.Outer);
        var nx = Math.Max(1, (int)Math.Ceiling(bounds.Width / limit - 1e-9));
        var ny = Math.Max(1, (int)Math.Ceiling(bounds.Height / limit - 1e-9));
        var stepX = bounds.Width / nx;
        var stepY = bounds.Height / ny;
        result.StepXMm = stepX;
        result.StepYMm = stepY;
        result.CoverageRadiusMm = coverageRadiusMm ?? Math.Sqrt(stepX * stepX + stepY * stepY) / 2;

        var samples = SamplePoints(polygon, bounds, sampleStepMm);

        for (int iy = 0; iy < ny; iy++)
        {
            for (int ix = 0; ix < nx; ix++)
            {
                var center = new P2(bounds.MinX + stepX * (ix + 0.5), bounds.MinY + stepY * (iy + 0.5));
                if (Contains(polygon, center))
                {
                    result.Points.Add(center);
                    continue;
                }

                var cell = new Bounds2(bounds.MinX + stepX * ix, bounds.MinY + stepY * iy,
                    bounds.MinX + stepX * (ix + 1), bounds.MinY + stepY * (iy + 1));
                var inCell = samples.Where(s => cell.Contains(s)).ToList();
                if (inCell.Count > 0)
                    result.Points.Add(inCell.OrderBy(s => s.DistanceTo(center)).First());
            }
        }

        // Coverage check: every interior sample must be within the radius of some point.
        result.SamplesChecked = samples.Count;
        foreach (var s in samples)
        {
            var nearest = result.Points.Count == 0 ? double.MaxValue : result.Points.Min(p => p.DistanceTo(s));
            if (nearest > result.CoverageRadiusMm + 1)
            {
                result.SamplesUncovered++;
                result.UncoveredSamples.Add(s);
            }
        }
        result.UncoveredAreaM2 = Math.Round(result.SamplesUncovered * sampleStepMm * sampleStepMm / 1e6, 2);
        return result;
    }

    /// <summary>Interior sample points on a regular grid (cell centres of a <paramref name="step"/> raster).</summary>
    public static List<P2> SamplePoints(RoomPolygon polygon, Bounds2 bounds, double step)
    {
        var samples = new List<P2>();
        if (step <= 0) return samples;
        for (var y = bounds.MinY + step / 2; y < bounds.MaxY; y += step)
        for (var x = bounds.MinX + step / 2; x < bounds.MaxX; x += step)
        {
            var p = new P2(x, y);
            if (Contains(polygon, p)) samples.Add(p);
        }
        return samples;
    }

    /// <summary>Maps a relative (0…1) position inside the room's bounding box to a plan point.</summary>
    public static P2 FromRelative(Bounds2 bounds, double u, double v) =>
        new(bounds.MinX + u * bounds.Width, bounds.MinY + v * bounds.Height);

    // ── Placement checks ─────────────────────────────────────────────────────

    /// <summary>
    /// Sanity checks for a wall-mounted device at <paramref name="alongMm"/> on <paramref name="face"/>.
    /// Returns human-readable warnings; an empty list means no problem was found.
    /// </summary>
    public static List<string> CheckWallPosition(
        WallFace face,
        double alongMm,
        IReadOnlyList<FaceOpening> openings,
        double heightMm,
        IReadOnlyList<DoorSwing> swings,
        double openingMarginMm = 100,
        double cornerMarginMm = 200)
    {
        var warnings = new List<string>();

        if (alongMm < 0 || alongMm > face.LengthMm)
            warnings.Add($"Position {alongMm:0} mm is outside the wall face (length {face.LengthMm:0} mm).");
        else if (alongMm < cornerMarginMm || face.LengthMm - alongMm < cornerMarginMm)
            warnings.Add($"Closer than {cornerMarginMm:0} mm to a corner.");

        foreach (var o in openings)
        {
            if (alongMm >= o.FromMm - openingMarginMm && alongMm <= o.ToMm + openingMarginMm)
                warnings.Add($"Inside {o.Kind} {o.Id} opening ({o.FromMm:0}–{o.ToMm:0} mm along the wall, ±{openingMarginMm:0} mm).");
        }

        foreach (var s in swings)
        {
            if (heightMm <= s.LeafHeightMm && alongMm >= s.FromMm && alongMm <= s.ToMm)
                warnings.Add($"Behind the leaf of door {s.DoorId} when it is open.");
        }

        return warnings;
    }

    /// <summary>The wall stretch an open door leaf covers on the hinge side.</summary>
    public sealed class DoorSwing
    {
        public long DoorId { get; set; }
        public double FromMm { get; set; }
        public double ToMm { get; set; }
        public double LeafHeightMm { get; set; } = 2100;
    }

    /// <summary>
    /// Leaf stretch for a door that swings into this room: a leaf as wide as the door lies along the
    /// wall on the hinge side when the door stands open.
    /// </summary>
    public static DoorSwing SwingStretch(FaceOpening door, int lockSign, double leafHeightMm = 2100)
    {
        var w = door.WidthMm;
        return lockSign > 0
            ? new DoorSwing { DoorId = door.Id, FromMm = door.FromMm - w, ToMm = door.FromMm, LeafHeightMm = leafHeightMm }
            : new DoorSwing { DoorId = door.Id, FromMm = door.ToMm, ToMm = door.ToMm + w, LeafHeightMm = leafHeightMm };
    }

    /// <summary>Points from <paramref name="candidates"/> within <paramref name="minDistanceMm"/> of any point in <paramref name="existing"/>.</summary>
    public static bool IsNearAny(P2 candidate, IEnumerable<P2> existing, double minDistanceMm) =>
        existing.Any(e => e.DistanceTo(candidate) < minDistanceMm);
}

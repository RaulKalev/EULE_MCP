using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Addin.Tools;
#if !REVIT2024
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
#endif

namespace RevitMCP.Addin.RoomDevices;

/// <summary>Where rooms are read from: the host model or one Revit/IFC link, with its transform.</summary>
internal sealed class RoomSource
{
    public Document HostDoc { get; set; } = null!;
    public Document Doc { get; set; } = null!;
    public Transform Transform { get; set; } = Transform.Identity;
    public RevitLinkInstance? Link { get; set; }
    public bool IsLink => Link != null;
    public string Label => Link != null ? $"link {Link.Id.Value} ({SafeName(Link)})" : "host model";

    public P2 ToHostMm(XYZ p)
    {
        var h = Transform.OfPoint(p);
        return new P2(h.X * RoomUnits.MmPerFoot, h.Y * RoomUnits.MmPerFoot);
    }

    public double ToHostZMm(XYZ p) => Transform.OfPoint(p).Z * RoomUnits.MmPerFoot;

    public P2 ToHostDirection(XYZ v)
    {
        var h = Transform.OfVector(v);
        return new P2(h.X, h.Y).Normalized();
    }

    /// <summary>Bounding box of an element in host coordinates (all 8 corners transformed).</summary>
    public (Bounds2 Plan, double MinZMm, double MaxZMm)? HostBounds(Element e)
    {
        BoundingBoxXYZ? bb;
        try { bb = e.get_BoundingBox(null); }
        catch { return null; }
        if (bb == null) return null;

        var pts = new List<XYZ>();
        foreach (var x in new[] { bb.Min.X, bb.Max.X })
        foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
        foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
            pts.Add(bb.Transform.OfPoint(new XYZ(x, y, z)));

        var plan = RoomGeometryMath.BoundsOf(pts.Select(ToHostMm));
        var zs = pts.Select(ToHostZMm).ToList();
        return (plan, zs.Min(), zs.Max());
    }

    private static string SafeName(Element e)
    {
        try { return e.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}

internal static class RoomUnits
{
    public const double MmPerFoot = 304.8;
    public static double MmToFt(double mm) => mm / MmPerFoot;
    public static double FtToMm(double ft) => ft * MmPerFoot;
}

/// <summary>A boundary piece as Revit reported it, before normalization — keeps the bounding element.</summary>
internal sealed class RawBoundarySegment
{
    public P2 A { get; set; }
    public P2 B { get; set; }
    public long? ElementId { get; set; }
    public bool IsCurved { get; set; }
}

/// <summary>A room or IfcSpace in host coordinates (mm).</summary>
internal sealed class RoomRecord
{
    public long SourceId { get; set; }
    /// <summary>"Room" or "IfcSpace".</summary>
    public string Kind { get; set; } = "Room";
    public Element Element { get; set; } = null!;
    public string Number { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? SourceLevelName { get; set; }
    public Level? HostLevel { get; set; }
    public string? HostLevelName => HostLevel?.Name;
    /// <summary>Host level elevation in internal coordinates (Level.ProjectElevation) in mm.</summary>
    public double HostLevelElevationMm { get; set; }
    /// <summary>Room floor (bottom) elevation in host internal coordinates, mm.</summary>
    public double FloorZMm { get; set; }
    public double AreaM2 { get; set; }
    public double? HeightMm { get; set; }
    public RoomPolygon Polygon { get; set; } = new();
    public List<RawBoundarySegment> RawSegments { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    private List<WallFace>? _faces;
    public List<WallFace> Faces => _faces ??= RoomGeometryMath.BuildFaces(Polygon);

    private P2? _interior;
    public P2 InteriorPoint => _interior ??= RoomGeometryMath.InteriorPoint(Polygon);

    public Bounds2 Bounds => RoomGeometryMath.BoundsOf(Polygon.Outer);

    /// <summary>The bounding element of a face, matched back to the raw Revit boundary segments.</summary>
    public RawBoundarySegment? RawSegmentFor(WallFace face)
    {
        var mid = face.Start.Plus(face.End).Scaled(0.5);
        return RawSegments
            .Where(s => RoomGeometryMath.DistanceToSegment(mid, s.A, s.B) < 2.0)
            .OrderBy(s => RoomGeometryMath.DistanceToSegment(mid, s.A, s.B))
            .FirstOrDefault();
    }
}

/// <summary>A door or window from the source model, in host coordinates.</summary>
internal sealed class OpeningRecord
{
    public long Id { get; set; }
    public string Kind { get; set; } = "door";
    public Element Element { get; set; } = null!;
    public P2 Center { get; set; }
    public double WidthMm { get; set; }
    public double? HeightMm { get; set; }
    public double? SillHeightMm { get; set; }
    public double BottomZMm { get; set; }
    public P2? Facing { get; set; }
    public P2? Hand { get; set; }
    public long? FromRoomId { get; set; }
    public long? ToRoomId { get; set; }
    public long? HostWallId { get; set; }
    public string TypeName { get; set; } = string.Empty;
}

internal sealed class RoomFilter
{
    public string LevelName { get; set; } = string.Empty;
    public string[] RoomNumbers { get; set; } = [];
    public string NameFilter { get; set; } = string.Empty;
}

/// <summary>
/// Reads rooms (or IfcSpaces when a link has no Room elements) with their boundaries, doors,
/// windows and ceiling heights, all transformed to host coordinates in mm. Host-model rooms and
/// rooms in Revit links come from Room.GetBoundarySegments (finish faces); IFC links reuse the
/// IfcSpace footprint extraction of the IFC-space-to-room tools.
/// </summary>
internal sealed class RoomSourceService
{
    private readonly RoomSource _source;
    private List<(Level Level, double ElevationFt)>? _hostLevels;
    private List<OpeningRecord>? _openings;
    private List<(Element Element, Bounds2 Plan, double MinZ, double MaxZ)>? _ceilings;

    public RoomSourceService(RoomSource source) => _source = source;

    public RoomSource Source => _source;

    // ── Source resolution ─────────────────────────────────────────────────────

    /// <summary>
    /// Picks the room source. source=host reads host rooms; source=link reads the given link, or
    /// the only loaded link when linkInstanceId is omitted. Default: link when linkInstanceId is set.
    /// </summary>
    public static RoomSource? ResolveSource(Document hostDoc, string source, long linkInstanceId, out string? error)
    {
        error = null;
        var mode = (source ?? string.Empty).Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = linkInstanceId != 0 ? "link" : "host";

        if (mode == "host")
            return new RoomSource { HostDoc = hostDoc, Doc = hostDoc };

        if (mode != "link")
        {
            error = $"source must be 'host' or 'link' (got '{source}').";
            return null;
        }

        RevitLinkInstance? link;
        if (linkInstanceId != 0)
        {
            link = hostDoc.GetElement(new ElementId(linkInstanceId)) as RevitLinkInstance;
            if (link == null)
            {
                error = $"Element {linkInstanceId} is not a Revit/IFC link instance. {DescribeLinks(hostDoc)}";
                return null;
            }
        }
        else
        {
            var loaded = LoadedLinks(hostDoc);
            if (loaded.Count != 1)
            {
                error = loaded.Count == 0
                    ? "No loaded links in the host model."
                    : $"Several links are loaded — pass linkInstanceId. {DescribeLinks(hostDoc)}";
                return null;
            }
            link = loaded[0];
        }

        var linkDoc = link.GetLinkDocument();
        if (linkDoc == null)
        {
            error = $"Link {link.Id.Value} is not loaded.";
            return null;
        }

        Transform transform;
        try { transform = link.GetTotalTransform(); }
        catch { transform = link.GetTransform(); }

        return new RoomSource { HostDoc = hostDoc, Doc = linkDoc, Link = link, Transform = transform };
    }

    public static List<RevitLinkInstance> LoadedLinks(Document hostDoc) =>
        new FilteredElementCollector(hostDoc)
            .OfClass(typeof(RevitLinkInstance))
            .Cast<RevitLinkInstance>()
            .Where(l => { try { return l.GetLinkDocument() != null; } catch { return false; } })
            .ToList();

    public static string DescribeLinks(Document hostDoc)
    {
        var links = LoadedLinks(hostDoc);
        if (links.Count == 0) return "No loaded links.";
        return "Loaded links: " + string.Join("; ", links.Take(15).Select(l => $"{l.Id.Value} = {l.Name}"));
    }

    // ── Levels ────────────────────────────────────────────────────────────────

    public List<(Level Level, double ElevationFt)> HostLevels =>
        _hostLevels ??= new FilteredElementCollector(_source.HostDoc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Select(l => (l, l.ProjectElevation))
            .OrderBy(t => t.ProjectElevation)
            .ToList();

    /// <summary>The host level nearest to <paramref name="zMm"/> (host internal), preferring the one at or below it.</summary>
    public Level? HostLevelFor(double zMm)
    {
        if (HostLevels.Count == 0) return null;
        var zFt = RoomUnits.MmToFt(zMm);
        const double toleranceFt = 0.5; // 150 mm
        var below = HostLevels.Where(l => l.ElevationFt <= zFt + toleranceFt).ToList();
        return below.Count > 0
            ? below.OrderByDescending(l => l.ElevationFt).First().Level
            : HostLevels.OrderBy(l => Math.Abs(l.ElevationFt - zFt)).First().Level;
    }

    // ── Rooms ─────────────────────────────────────────────────────────────────

    public List<RoomRecord> LoadRooms(RoomFilter filter, List<string> warnings, int limit = 500)
    {
        var rooms = new FilteredElementCollector(_source.Doc)
            .OfCategory(BuiltInCategory.OST_Rooms)
            .WhereElementIsNotElementType()
            .OfType<Room>()
            .Where(r => r.Area > 1e-6 && r.Location != null)
            .ToList();

        var records = new List<RoomRecord>();
        if (rooms.Count > 0)
        {
            foreach (var room in rooms)
            {
                if (!MatchesName(room.Number, SafeRoomName(room), filter)) continue;
                var record = FromRoom(room);
                if (record == null) continue;
                if (!MatchesLevel(record, filter)) continue;
                records.Add(record);
                if (records.Count >= limit) break;
            }
            return records;
        }

        if (!_source.IsLink)
        {
            warnings.Add("The host model has no placed rooms. Pass source=link with linkInstanceId to read the AR link.");
            return records;
        }

        warnings.Add("The link has no Room elements — reading IfcSpace geometry instead.");
        return LoadIfcSpaces(filter, warnings, limit);
    }

    private static bool MatchesName(string number, string name, RoomFilter filter)
    {
        if (filter.RoomNumbers.Length > 0 &&
            !filter.RoomNumbers.Any(n => string.Equals(n.Trim(), number?.Trim(), StringComparison.OrdinalIgnoreCase)))
            return false;

        if (filter.NameFilter.Length == 0) return true;
        try
        {
            return Regex.IsMatch(name ?? string.Empty, filter.NameFilter, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return (name ?? string.Empty).IndexOf(filter.NameFilter, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    private static bool MatchesLevel(RoomRecord record, RoomFilter filter)
    {
        if (filter.LevelName.Length == 0) return true;
        return string.Equals(record.HostLevelName, filter.LevelName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(record.SourceLevelName, filter.LevelName, StringComparison.OrdinalIgnoreCase);
    }

    private RoomRecord? FromRoom(Room room)
    {
        var record = new RoomRecord
        {
            SourceId = room.Id.Value,
            Kind = "Room",
            Element = room,
            Number = room.Number ?? string.Empty,
            Name = SafeRoomName(room),
            AreaM2 = Math.Round(room.Area * 0.09290304, 2),
            SourceLevelName = room.Level?.Name
        };

        try
        {
            var height = room.UnboundedHeight;
            if (height > 0) record.HeightMm = Math.Round(RoomUnits.FtToMm(height), 0);
        }
        catch { }

        // Floor elevation: the room's level in the source model, moved into host coordinates.
        var levelZ = room.Level != null ? room.Level.ProjectElevation : 0.0;
        double baseOffset = 0;
        try { baseOffset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET)?.AsDouble() ?? 0; }
        catch { }
        record.FloorZMm = _source.ToHostZMm(new XYZ(0, 0, levelZ + baseOffset));
        AssignHostLevel(record);

        var options = new SpatialElementBoundaryOptions
        {
            SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
        };

        IList<IList<BoundarySegment>>? loops;
        try { loops = room.GetBoundarySegments(options); }
        catch (Exception ex)
        {
            record.Warnings.Add($"Could not read the boundary: {ex.Message}");
            return null;
        }
        if (loops == null || loops.Count == 0) return null;

        var polyLoops = new List<List<P2>>();
        foreach (var loop in loops)
        {
            var points = new List<P2>();
            foreach (var segment in loop)
            {
                var curve = segment.GetCurve();
                var curved = curve is not Line;
                var tess = curved ? curve.Tessellate() : new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) };
                var mm = tess.Select(_source.ToHostMm).ToList();
                long? elementId = segment.ElementId != ElementId.InvalidElementId ? segment.ElementId.Value : null;
                for (int i = 0; i + 1 < mm.Count; i++)
                    record.RawSegments.Add(new RawBoundarySegment { A = mm[i], B = mm[i + 1], ElementId = elementId, IsCurved = curved });
                points.AddRange(mm.Take(mm.Count - 1));
            }
            if (points.Count >= 3) polyLoops.Add(points);
        }
        if (polyLoops.Count == 0) return null;

        // Revit returns the outer loop first in practice, but pick the largest to be safe.
        var outer = polyLoops.OrderByDescending(l => Math.Abs(RoomGeometryMath.SignedArea(l))).First();
        record.Polygon = RoomGeometryMath.Normalize(new RoomPolygon
        {
            Outer = outer,
            Holes = polyLoops.Where(l => !ReferenceEquals(l, outer)).ToList()
        });
        if (record.RawSegments.Any(s => s.IsCurved))
            record.Warnings.Add("The boundary has curved segments; they are tessellated into straight faces.");
        return record;
    }

    private List<RoomRecord> LoadIfcSpaces(RoomFilter filter, List<string> warnings, int limit)
    {
#if REVIT2024
        warnings.Add("Reading IfcSpace geometry needs Revit 2026 (the IFC space tools are not built for Revit 2024). " +
                     "Convert the spaces to rooms in the link, or use a Revit link with rooms.");
        return [];
#else
        var records = new List<RoomRecord>();
        var collector = new IfcSpaceCollector();
        var reader = new IfcParameterReader();
        var extractor = new IfcSpaceGeometryExtractor();
        var options = new IfcGeometryExtractionOptions();

        foreach (var detection in collector.Collect(_source.Doc, includeProbable: false))
        {
            var element = detection.Element;
            var meta = reader.ReadMetadata(element, options);
            var number = meta.Number ?? string.Empty;
            var name = meta.Name ?? string.Empty;
            if (!MatchesName(number, name, filter)) continue;

            var bounds = _source.HostBounds(element);
            if (bounds == null) continue;

            var record = new RoomRecord
            {
                SourceId = element.Id.Value,
                Kind = "IfcSpace",
                Element = element,
                Number = number,
                Name = name,
                SourceLevelName = meta.StoreyName,
                FloorZMm = bounds.Value.MinZMm,
                HeightMm = Math.Round(bounds.Value.MaxZMm - bounds.Value.MinZMm, 0)
            };
            AssignHostLevel(record);
            if (!MatchesLevel(record, filter)) continue;

            var footprint = extractor.Extract(element, _source.Transform, RoomUnits.MmToFt(record.HostLevelElevationMm), options);
            if (footprint.OuterLoop == null)
            {
                warnings.Add($"IfcSpace {number} ({element.Id.Value}): no footprint — {string.Join("; ", footprint.Errors)}");
                continue;
            }

            // The footprint is already in host coordinates.
            var outer = LoopToMm(footprint.OuterLoop, record);
            var holes = footprint.InnerLoops.Select(l => LoopToMm(l, record)).Where(l => l.Count >= 3).ToList();
            record.Polygon = RoomGeometryMath.Normalize(new RoomPolygon { Outer = outer, Holes = holes });
            record.AreaM2 = meta.AreaM2 ?? Math.Round(RoomGeometryMath.AreaMm2(record.Polygon) / 1e6, 2);
            record.Warnings.AddRange(footprint.Warnings);
            records.Add(record);
            if (records.Count >= limit) break;
        }

        return records;
#endif
    }

    private static List<P2> LoopToMm(CurveLoop loop, RoomRecord record)
    {
        var points = new List<P2>();
        foreach (var curve in loop)
        {
            var curved = curve is not Line;
            var tess = curved ? curve.Tessellate() : new List<XYZ> { curve.GetEndPoint(0), curve.GetEndPoint(1) };
            var mm = tess.Select(p => new P2(RoomUnits.FtToMm(p.X), RoomUnits.FtToMm(p.Y))).ToList();
            for (int i = 0; i + 1 < mm.Count; i++)
                record.RawSegments.Add(new RawBoundarySegment { A = mm[i], B = mm[i + 1], IsCurved = curved });
            points.AddRange(mm.Take(mm.Count - 1));
        }
        return points;
    }

    private void AssignHostLevel(RoomRecord record)
    {
        record.HostLevel = HostLevelFor(record.FloorZMm);
        record.HostLevelElevationMm = record.HostLevel != null
            ? RoomUnits.FtToMm(record.HostLevel.ProjectElevation)
            : 0;
    }

    private static string SafeRoomName(Room room)
    {
        try { return room.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? room.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    // ── Doors and windows ─────────────────────────────────────────────────────

    public List<OpeningRecord> Openings => _openings ??= CollectOpenings();

    private List<OpeningRecord> CollectOpenings()
    {
        var result = new List<OpeningRecord>();
        foreach (var (category, kind) in new[] { (BuiltInCategory.OST_Doors, "door"), (BuiltInCategory.OST_Windows, "window") })
        {
            foreach (var element in new FilteredElementCollector(_source.Doc).OfCategory(category).WhereElementIsNotElementType())
            {
                var record = ToOpening(element, kind);
                if (record != null) result.Add(record);
            }
        }
        return result;
    }

    private OpeningRecord? ToOpening(Element element, string kind)
    {
        var bounds = _source.HostBounds(element);
        var record = new OpeningRecord { Id = element.Id.Value, Kind = kind, Element = element };
        try { record.TypeName = element.Document.GetElement(element.GetTypeId())?.Name ?? string.Empty; }
        catch { }

        if (element is FamilyInstance fi)
        {
            if (fi.Location is LocationPoint lp)
                record.Center = _source.ToHostMm(lp.Point);
            else if (bounds != null)
                record.Center = Center(bounds.Value.Plan);
            else
                return null;

            try { record.Facing = _source.ToHostDirection(fi.FacingOrientation); } catch { }
            try { record.Hand = _source.ToHostDirection(fi.HandOrientation); } catch { }
            try { record.FromRoomId = fi.FromRoom?.Id.Value; } catch { }
            try { record.ToRoomId = fi.ToRoom?.Id.Value; } catch { }
            try { record.HostWallId = fi.Host?.Id.Value; } catch { }

            var widthParam = kind == "door" ? BuiltInParameter.DOOR_WIDTH : BuiltInParameter.WINDOW_WIDTH;
            var heightParam = kind == "door" ? BuiltInParameter.DOOR_HEIGHT : BuiltInParameter.WINDOW_HEIGHT;
            record.WidthMm = LengthMm(fi, widthParam, "Width", "Laius", "Rough Width") ?? 0;
            record.HeightMm = LengthMm(fi, heightParam, "Height", "Kõrgus", "Rough Height");
            record.SillHeightMm = LengthMm(fi, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM, "Sill Height");
        }
        else if (bounds != null)
        {
            record.Center = Center(bounds.Value.Plan);
        }
        else
        {
            return null;
        }

        if (bounds != null)
        {
            record.BottomZMm = bounds.Value.MinZMm;
            if (record.WidthMm <= 0)
            {
                // Width along the hand direction when known, else the longer plan side.
                record.WidthMm = record.Hand is { } hand && Math.Abs(hand.X) > 0.9
                    ? bounds.Value.Plan.Width
                    : record.Hand is { } h2 && Math.Abs(h2.Y) > 0.9
                        ? bounds.Value.Plan.Height
                        : Math.Max(bounds.Value.Plan.Width, bounds.Value.Plan.Height);
            }
            record.HeightMm ??= bounds.Value.MaxZMm - bounds.Value.MinZMm;
        }

        return record;
    }

    private static P2 Center(Bounds2 b) => new((b.MinX + b.MaxX) / 2, (b.MinY + b.MaxY) / 2);

    private static double? LengthMm(FamilyInstance fi, BuiltInParameter bip, params string[] names)
    {
        foreach (var element in new Element?[] { fi, fi.Symbol })
        {
            if (element == null) continue;
            try
            {
                var p = element.get_Parameter(bip);
                if (p is { StorageType: StorageType.Double } && p.AsDouble() > 0)
                    return Math.Round(RoomUnits.FtToMm(p.AsDouble()), 1);
            }
            catch { }
            foreach (var name in names)
            {
                try
                {
                    var p = element.LookupParameter(name);
                    if (p is { StorageType: StorageType.Double } && p.AsDouble() > 0)
                        return Math.Round(RoomUnits.FtToMm(p.AsDouble()), 1);
                }
                catch { }
            }
        }
        return null;
    }

    /// <summary>
    /// Doors/windows of a room: Revit's From/To Room for Room sources, otherwise openings whose
    /// centre lies within <paramref name="maxDistanceMm"/> of the room boundary.
    /// </summary>
    public List<OpeningRecord> OpeningsOf(RoomRecord room, double maxDistanceMm = 400)
    {
        var bounds = room.Bounds;
        return Openings.Where(o =>
        {
            if (room.Kind == "Room" && (o.FromRoomId != null || o.ToRoomId != null))
                return o.FromRoomId == room.SourceId || o.ToRoomId == room.SourceId;
            if (!bounds.Contains(o.Center, maxDistanceMm)) return false;
            if (Math.Abs(o.BottomZMm - room.FloorZMm) > 3000 && o.Kind == "door") return false;
            return RoomGeometryMath.DistanceToBoundary(room.Polygon, o.Center) <= maxDistanceMm;
        }).ToList();
    }

    // ── Ceilings ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Height of the lowest ceiling above the room's interior point, measured from the room floor.
    /// Null when no ceiling of the source model covers that point.
    /// </summary>
    public double? CeilingHeightMm(RoomRecord room)
    {
        _ceilings ??= new FilteredElementCollector(_source.Doc)
            .OfCategory(BuiltInCategory.OST_Ceilings)
            .WhereElementIsNotElementType()
            .Select(e => (e, b: _source.HostBounds(e)))
            .Where(t => t.b != null)
            .Select(t => (t.e, t.b!.Value.Plan, t.b.Value.MinZMm, t.b.Value.MaxZMm))
            .ToList();

        var p = room.InteriorPoint;
        var maxAbove = (room.HeightMm ?? 6000) + 1000;
        var candidates = _ceilings
            .Where(c => c.Plan.Contains(p))
            .Select(c => c.MinZ - room.FloorZMm)
            .Where(h => h > 1000 && h < maxAbove)
            .ToList();
        return candidates.Count > 0 ? Math.Round(candidates.Min(), 0) : null;
    }

    // ── Linked elements by category ───────────────────────────────────────────

    /// <summary>Elements of the given categories in the source model whose plan bounds overlap the room.</summary>
    public List<(Element Element, string Category, Bounds2 Plan, double MinZ, double MaxZ)> ElementsInRoom(
        RoomRecord room, IEnumerable<string> categories, List<string> warnings)
    {
        var result = new List<(Element, string, Bounds2, double, double)>();
        var resolver = new Query.CategoryResolver();
        var roomBounds = room.Bounds;

        foreach (var name in categories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var resolved = resolver.Resolve(_source.Doc, name);
            if (resolved.Category == null)
            {
                warnings.Add($"Category '{name}' was not found in the {_source.Label}.");
                continue;
            }

            foreach (var e in new FilteredElementCollector(_source.Doc).OfCategoryId(resolved.Category.Id).WhereElementIsNotElementType())
            {
                var b = _source.HostBounds(e);
                if (b == null || !roomBounds.Intersects(b.Value.Plan)) continue;
                var c = new P2((b.Value.Plan.MinX + b.Value.Plan.MaxX) / 2, (b.Value.Plan.MinY + b.Value.Plan.MaxY) / 2);
                if (!RoomGeometryMath.Contains(room.Polygon, c)) continue;
                result.Add((e, resolved.Category.Name, b.Value.Plan, b.Value.MinZMm, b.Value.MaxZMm));
            }
        }
        return result;
    }

    // ── Request helpers ───────────────────────────────────────────────────────

    public static RoomFilter ParseFilter(Dictionary<string, object?> args) => new()
    {
        LevelName = ToolArguments.GetString(args, "levelName").Trim(),
        RoomNumbers = ToolArguments.GetStringArray(args, "roomNumbers")
            .Concat(new[] { ToolArguments.GetString(args, "roomNumber") })
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToArray(),
        NameFilter = ToolArguments.GetString(args, "nameFilter").Trim() is { Length: > 0 } n
            ? n
            : ToolArguments.GetString(args, "roomFilter").Trim()
    };

    public static RoomSourceService? FromArguments(Document hostDoc, Dictionary<string, object?> args, out string? error)
    {
        var source = ResolveSource(
            hostDoc,
            ToolArguments.GetString(args, "source"),
            ToolArguments.GetLong(args, "linkInstanceId"),
            out error);
        return source == null ? null : new RoomSourceService(source);
    }
}

using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.FilledRegions;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.RoomDevices;

namespace RevitMCP.Addin.Tools.FilledRegions;

/// <summary>A tag or dimension attached to a filled region.</summary>
internal sealed record AttachedAnnotation(long Id, string Kind);

/// <summary>The Revit side of the filled region tools: references, frames, curves, parameters and annotation lookups.</summary>
internal static class FilledRegionSupport
{
    public const double MmPerFoot = 304.8;

    public static Vec3 ToMm(XYZ p) => new(p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot);
    public static Vec3 ToUnit(XYZ v) => new(v.X, v.Y, v.Z);
    public static XYZ ToXyz(Vec3 mm) => new(mm.X / MmPerFoot, mm.Y / MmPerFoot, mm.Z / MmPerFoot);

    public static ViewFrame FrameOf(View view) =>
        new(ToMm(view.Origin), ToUnit(view.RightDirection), ToUnit(view.UpDirection), ToUnit(view.ViewDirection));

    public static double ShortCurveMm(Document doc) => doc.Application.ShortCurveTolerance * MmPerFoot;

    // ── References ────────────────────────────────────────────────────────────

    public static View? ResolveView(Document doc, string reference, out string? reason)
    {
        reason = null;
        var text = reference.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            if (doc.GetElement(new ElementId(id)) is View byId) return byId;
            reason = $"Element {id} is not a view.";
            return null;
        }

        var matches = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => !v.IsTemplate && string.Equals(v.Name, text, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 1) return matches[0];
        reason = matches.Count == 0
            ? $"No view is named '{text}'."
            : $"{matches.Count} views are named '{text}' ({string.Join(", ", matches.Select(v => $"{v.Id.Value} {v.ViewType}"))}): pass the id.";
        return null;
    }

    /// <summary>Why regions cannot be drawn in this view, or null when they can.</summary>
    public static string? UnsupportedReason(Document doc, View view)
    {
        if (view.IsTemplate) return $"'{view.Name}' is a view template.";
        if (view is ViewSchedule || view.ViewType is ViewType.Schedule or ViewType.ColumnSchedule or ViewType.PanelSchedule)
            return $"'{view.Name}' is a schedule.";
        if (view is View3D || view.ViewType is ViewType.ThreeD or ViewType.Walkthrough)
            return $"'{view.Name}' is a 3D view; Revit has no detail plane there.";

        // FilledRegion.IsRegionCreationEnabledInView is in the API documentation but not public, so this is a list of the
        // view kinds that take detail items. Anything further Revit refuses shows up in the preview's trial run.
        return view.ViewType is ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.AreaPlan or ViewType.EngineeringPlan or
            ViewType.Section or ViewType.Elevation or ViewType.Detail or ViewType.DraftingView or ViewType.Legend or ViewType.DrawingSheet
            ? null
            : $"'{view.Name}' is a {view.ViewType} view, which takes no filled regions.";
    }

    public static FilledRegionType? ResolveType(Document doc, string reference)
    {
        var text = reference.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            return doc.GetElement(new ElementId(id)) as FilledRegionType;
        return Types(doc).FirstOrDefault(t => string.Equals(t.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    public static List<FilledRegionType> Types(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(FilledRegionType)).Cast<FilledRegionType>().ToList();

    public static string TypeNames(Document doc) => string.Join(", ", Types(doc).Select(t => t.Name).OrderBy(n => n).Take(30));

    public static List<GraphicsStyle> LineStyles(Document doc) =>
        FilledRegion.GetValidLineStyleIdsForFilledRegion(doc).Select(doc.GetElement).OfType<GraphicsStyle>().ToList();

    public static ElementId? ResolveLineStyle(Document doc, string reference)
    {
        var text = reference.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var candidate = new ElementId(id);
            return FilledRegion.IsValidLineStyleIdForFilledRegion(doc, candidate) ? candidate : null;
        }
        return LineStyles(doc).FirstOrDefault(s => string.Equals(s.GraphicsStyleCategory?.Name ?? s.Name, text, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    // ── Curves ────────────────────────────────────────────────────────────────

    /// <summary>The loops as Revit curves on the view's plane. Each piece ends exactly where the next starts.</summary>
    public static IList<CurveLoop> ToCurveLoops(IEnumerable<RegionLoop> loops, ViewFrame frame)
    {
        var result = new List<CurveLoop>();
        foreach (var loop in loops)
        {
            var curveLoop = new CurveLoop();
            for (var i = 0; i < loop.Segments.Count; i++)
            {
                var segment = loop.Segments[i];
                var start = ToXyz(frame.ToModel(segment.Start));
                var end = ToXyz(frame.ToModel(loop.Segments[(i + 1) % loop.Segments.Count].Start));
                Curve curve = segment.Mid is { } mid
                    ? Arc.Create(start, end, ToXyz(frame.ToModel(mid)))
                    : Line.CreateBound(start, end);
                curveLoop.Append(curve);
            }
            result.Add(curveLoop);
        }
        return result;
    }

    /// <summary>A Revit curve as boundary pieces in host mm: lines and bound arcs stay as they are, anything else is flattened.</summary>
    public static List<ModelSegment> ToSegments(Curve curve, Transform transform, List<string> warnings)
    {
        Vec3 Host(XYZ p) => ToMm(transform.OfPoint(p));
        var segments = new List<ModelSegment>();
        if (curve is Line)
        {
            segments.Add(new ModelSegment(Host(curve.GetEndPoint(0)), Host(curve.GetEndPoint(1))));
        }
        else if (curve is Arc && curve.IsBound)
        {
            segments.Add(new ModelSegment(Host(curve.GetEndPoint(0)), Host(curve.GetEndPoint(1)), Host(curve.Evaluate(0.5, true))));
        }
        else
        {
            var points = curve.Tessellate().Select(Host).ToList();
            for (var i = 0; i + 1 < points.Count; i++) segments.Add(new ModelSegment(points[i], points[i + 1]));
            warnings.Add($"A {curve.GetType().Name} boundary piece was flattened into {Math.Max(0, points.Count - 1)} straight lines.");
        }
        return segments;
    }

    /// <summary>The boundaries of a region as re-usable points: closed loops in model mm with (u, v) and arcMid.</summary>
    public static List<List<object>> DescribeBoundary(FilledRegion region, ViewFrame? frame)
    {
        var result = new List<List<object>>();
        foreach (var loop in region.GetBoundaries())
        {
            var points = new List<object>();
            Curve? last = null;
            foreach (var curve in loop)
            {
                foreach (var segment in ToSegments(curve, Transform.Identity, new List<string>()))
                    points.Add(DescribePoint(segment.Start, segment.Mid, frame));
                last = curve;
            }
            if (last != null) points.Add(DescribePoint(ToMm(last.GetEndPoint(1)), null, frame));
            result.Add(points);
        }
        return result;
    }

    private static object DescribePoint(Vec3 point, Vec3? arcMid, ViewFrame? frame)
    {
        var uv = frame?.ToUv(point);
        var mid = arcMid is { } m
            ? new { x = Math.Round(m.X, 2), y = Math.Round(m.Y, 2), z = Math.Round(m.Z, 2) }
            : null;
        return new
        {
            x = Math.Round(point.X, 2),
            y = Math.Round(point.Y, 2),
            z = Math.Round(point.Z, 2),
            u = uv == null ? (double?)null : Math.Round(uv.Value.U, 2),
            v = uv == null ? (double?)null : Math.Round(uv.Value.V, 2),
            arcMid = mid
        };
    }

    /// <summary>A flat loop in model mm, for a preview's includeLoops.</summary>
    public static List<object> DescribeLoop(RegionLoop loop, ViewFrame frame)
    {
        var points = new List<object>(loop.Segments.Count + 1);
        foreach (var segment in loop.Segments)
            points.Add(DescribePoint(frame.ToModel(segment.Start), segment.Mid is { } mid ? frame.ToModel(mid) : null, frame));
        if (loop.Segments.Count > 0) points.Add(DescribePoint(frame.ToModel(loop.Segments[0].Start), null, frame));
        return points;
    }

    /// <summary>The (u, v) extent of the loops, as model corners, so a list of many regions stays readable.</summary>
    public static object? ExtentOf(IReadOnlyList<RegionLoop> loops, ViewFrame frame)
    {
        var points = loops.SelectMany(l => l.Polyline()).ToList();
        if (points.Count == 0) return null;
        return new
        {
            uMin = Math.Round(points.Min(p => p.U), 1),
            vMin = Math.Round(points.Min(p => p.V), 1),
            uMax = Math.Round(points.Max(p => p.U), 1),
            vMax = Math.Round(points.Max(p => p.V), 1)
        };
    }

    public static double? RevitAreaM2(Element region)
    {
        try
        {
            var area = region.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED);
            return area is { StorageType: StorageType.Double } ? Math.Round(area.AsDouble() * 0.09290304, 3) : null;
        }
        catch
        {
            return null;
        }
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    /// <summary>Why this instance parameter cannot be written, or null when it can.</summary>
    public static string? ParameterProblem(Document doc, Element element, string name)
    {
        var resolution = ParameterResolver.Resolve(doc, element, new ParameterTarget { Name = name, ExactMatch = true });
        var parameter = ParameterResolver.Selected(resolution);
        if (parameter == null) return resolution.Problem ?? $"Parameter '{name}' does not exist on this element.";
        return parameter.IsReadOnly ? $"Parameter '{name}' is read-only." : null;
    }

    /// <summary>Writes the value as Revit would show it (a length in the project's units). Null on success, else why not.</summary>
    public static string? SetParameter(Document doc, Element element, string name, string value)
    {
        var problem = ParameterProblem(doc, element, name);
        if (problem != null) return problem;
        var parameter = ParameterResolver.Selected(ParameterResolver.Resolve(doc, element, new ParameterTarget { Name = name, ExactMatch = true }))!;
        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.String:
                    return parameter.Set(value) ? null : $"Revit refused '{value}' for '{name}'.";
                case StorageType.Integer:
                    if (bool.TryParse(value, out var flag)) return parameter.Set(flag ? 1 : 0) ? null : $"Revit refused '{value}' for '{name}'.";
                    if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                        return parameter.Set(number) ? null : $"Revit refused '{value}' for '{name}'.";
                    return $"'{value}' is not a whole number or true/false, which '{name}' needs.";
                case StorageType.Double:
                    if (parameter.SetValueString(value)) return null;
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                        return parameter.Set(raw) ? null : $"Revit refused '{value}' for '{name}'.";
                    return $"'{value}' is not a number, which '{name}' needs.";
                case StorageType.ElementId:
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        return parameter.Set(new ElementId(id)) ? null : $"Revit refused element {id} for '{name}'.";
                    return $"'{name}' needs an element id, not '{value}'.";
                default:
                    return $"Parameter '{name}' has no stored value type.";
            }
        }
        catch (Exception ex)
        {
            return $"Revit refused '{value}' for '{name}': {ex.Message}";
        }
    }

    /// <summary>Copies every writable instance value except the type itself. Returns the names it could not copy.</summary>
    public static List<string> CopyParameters(Element from, Element to)
    {
        var failed = new List<string>();
        foreach (Parameter source in from.Parameters)
        {
            try
            {
                if (source.IsReadOnly || !source.HasValue || source.Definition == null) continue;
                if (source.Id.Value is (long)BuiltInParameter.ELEM_TYPE_PARAM or (long)BuiltInParameter.ELEM_FAMILY_PARAM or
                    (long)BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM or (long)BuiltInParameter.SYMBOL_ID_PARAM)
                    continue;
                var target = to.get_Parameter(source.Definition);
                if (target == null || target.IsReadOnly) continue;
                var copied = source.StorageType switch
                {
                    StorageType.String => target.Set(source.AsString() ?? string.Empty),
                    StorageType.Integer => target.Set(source.AsInteger()),
                    StorageType.Double => target.Set(source.AsDouble()),
                    StorageType.ElementId => target.Set(source.AsElementId()),
                    _ => true
                };
                if (!copied) failed.Add(source.Definition.Name);
            }
            catch
            {
                failed.Add(source.Definition?.Name ?? "?");
            }
        }
        return failed;
    }

    public static List<object> DescribeParameters(Document doc, Element element)
    {
        var values = new ParameterReader().ReadParameters(doc, element, new ParameterReadOptions { IncludeInstanceParameters = true, IncludeTypeParameters = false });
        return values.Select(p => (object)new { name = p.Name, value = p.Value, unit = p.Unit, readOnly = p.IsReadOnly }).ToList();
    }

    // ── Annotations and ownership ─────────────────────────────────────────────

    /// <summary>Tags on the region and dimensions referencing it or its sketch lines, in the region's own view.</summary>
    public static List<AttachedAnnotation> AttachedAnnotations(Document doc, FilledRegion region)
    {
        var result = new List<AttachedAnnotation>();
        var ownerView = region.OwnerViewId;
        if (ownerView == null || ownerView == ElementId.InvalidElementId) return result;

        var related = new HashSet<long> { region.Id.Value };
        try
        {
            foreach (var id in region.GetDependentElements(null)) related.Add(id.Value);
        }
        catch
        {
            // Dependents only widen the dimension match; the region's own id still works without them.
        }

        var inView = new ElementOwnerViewFilter(ownerView);
        foreach (var tag in new FilteredElementCollector(doc).OfClass(typeof(IndependentTag)).WherePasses(inView).Cast<IndependentTag>())
        {
            try
            {
                if (tag.GetTaggedLocalElementIds().Any(id => id.Value == region.Id.Value))
                    result.Add(new AttachedAnnotation(tag.Id.Value, "tag"));
            }
            catch
            {
                // A tag of a linked element has no local ids to compare.
            }
        }

        foreach (var dimension in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).WherePasses(inView).Cast<Dimension>())
        {
            // Every region's sketch has Automatic Sketch Dimensions of its own: Revit's, nothing anyone placed.
            if (dimension.Category?.Id.Value != (long)BuiltInCategory.OST_Dimensions) continue;
            try
            {
                foreach (Reference reference in dimension.References)
                {
                    if (!related.Contains(reference.ElementId.Value)) continue;
                    result.Add(new AttachedAnnotation(dimension.Id.Value, "dimension"));
                    break;
                }
            }
            catch
            {
                // Some dimension kinds expose no references; they cannot be matched.
            }
        }
        return result;
    }

    /// <summary>The line style the region's boundary is drawn with; null when it cannot be read or the boundaries differ.</summary>
    public static ElementId? BoundaryLineStyle(Document doc, FilledRegion region, out bool mixed)
    {
        mixed = false;
        try
        {
            var styles = region.GetDependentElements(new ElementClassFilter(typeof(CurveElement)))
                .Select(doc.GetElement).OfType<CurveElement>()
                .Select(c => c.LineStyle?.Id).Where(id => id != null).Select(id => id!)
                .GroupBy(id => id.Value).Select(g => g.First()).ToList();
            mixed = styles.Count > 1;
            return styles.Count == 1 ? styles[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Who else has this element checked out; null when this user may edit it.</summary>
    public static string? OwnedByOther(Document doc, ElementId id)
    {
        if (!doc.IsWorkshared) return null;
        try
        {
            var status = WorksharingUtils.GetCheckoutStatus(doc, id, out var owner);
            return status == CheckoutStatus.OwnedByOtherUser ? owner : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Asks Revit whether the region's sketch can be edited in place. It does not change this tool's behaviour: a reshape replaces.</summary>
    public static bool? SketchEditingSupported(Document doc, ElementId id)
    {
        try
        {
            using var scope = new SketchEditScope(doc, "Revit MCP - probe");
            return scope.IsSketchEditingSupportedForSketchBasedElement(id);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Reads the shape of a spec from the model and takes it into a view's plane. Direct specs, rules and reshapes all come
/// through here. A linked room or element is moved into host coordinates with the link instance's transform first, so the
/// region lines up with what the host view shows.
/// </summary>
internal sealed class RegionShapeReader
{
    private readonly Document _doc;
    private const double RoomGapToleranceMm = 2.0;

    private readonly double _joinToleranceMm = new LoopValidationOptions().JoinToleranceMm;
    private readonly Dictionary<long, List<RoomRecord>> _roomRecords = new();

    public RegionShapeReader(Document doc) => _doc = doc;

    public ShapeResult Read(RegionShapeSpec shape, View view, ViewFrame frame) => shape.Kind switch
    {
        RegionShapeKind.Loops => RegionShapeBuilder.FromLoops(shape, frame, _joinToleranceMm),
        RegionShapeKind.Rectangle => RegionShapeBuilder.FromRectangle(shape, frame),
        RegionShapeKind.Room => ReadRoom(shape, view, frame),
        RegionShapeKind.DetailLines => ReadDetailLines(shape, view, frame),
        RegionShapeKind.ElementBox => ReadElementBox(shape, view, frame),
        _ => ShapeResult.Fail(RegionCodes.InvalidSpec, "The spec has no shape.")
    };

    private ShapeResult ReadRoom(RegionShapeSpec shape, View view, ViewFrame frame)
    {
        var source = RoomSourceService.ResolveSource(_doc, shape.LinkInstanceId != 0 ? "link" : "host", shape.LinkInstanceId, out var linkError);
        if (source == null) return ShapeResult.Fail(RegionCodes.LinkNotFound, linkError ?? "The link was not found.");

        var warnings = new List<string>();
        var id = shape.RoomId;
        if (id == 0)
        {
            var service = new RoomSourceService(source);
            var filter = new RoomFilter { RoomNumbers = new[] { shape.RoomNumber ?? string.Empty }, LevelName = shape.RoomLevel?.Trim() ?? string.Empty };
            var found = service.LoadRooms(filter, warnings, 20);
            if (found.Count == 0) return ShapeResult.Fail(RegionCodes.RoomNotFound, $"No room numbered '{shape.RoomNumber}' in the {source.Label}.");
            if (found.Count > 1)
                return ShapeResult.Fail(RegionCodes.RoomAmbiguous,
                    $"{found.Count} rooms are numbered '{shape.RoomNumber}' ({string.Join(", ", found.Select(r => $"{r.SourceId} on {r.SourceLevelName}"))}): pass level or the id.");
            id = found[0].SourceId;
        }

        var element = source.Doc.GetElement(new ElementId(id));
        if (element == null) return ShapeResult.Fail(RegionCodes.RoomNotFound, $"Element {id} does not exist in the {source.Label}.");
        var label = $"Room {id} of the {source.Label}";

        ShapeResult result;
        if (element is SpatialElement spatial)
        {
            result = ReadSpatial(spatial, source, shape.BoundaryLocation, view, frame, label);
        }
        else
        {
            // An IFC link has spaces, not rooms: their footprint comes from the room reader, already in host coordinates.
            var record = RecordsOf(source, warnings).FirstOrDefault(r => r.SourceId == id);
            if (record == null) return ShapeResult.Fail(RegionCodes.NoRoomBoundary, $"Element {id} of the {source.Label} is not a room or space.");
            var loops = new List<IReadOnlyList<ModelSegment>> { ToSegments(record.Polygon.Outer, record.FloorZMm) };
            loops.AddRange(record.Polygon.Holes.Select(h => (IReadOnlyList<ModelSegment>)ToSegments(h, record.FloorZMm)));
            result = RegionShapeBuilder.FromModelLoops(loops, frame, label);
            warnings.AddRange(record.Warnings);
            AddLevelWarning(view, record.FloorZMm, label, warnings);
        }

        result.Warnings.AddRange(warnings);
        return result;
    }

    /// <summary>A room drawn in the plan of another level still lines up in plan, but is usually a mistake.</summary>
    private static void AddLevelWarning(View view, double floorZMm, string label, List<string> warnings)
    {
        var level = view.GenLevel;
        if (level == null) return;
        var difference = floorZMm - level.ProjectElevation * FilledRegionSupport.MmPerFoot;
        if (Math.Abs(difference) > 10)
            warnings.Add($"{label} is {difference:0} mm from this plan's level '{level.Name}'.");
    }

    private ShapeResult ReadSpatial(SpatialElement spatial, RoomSource source, string location, View view, ViewFrame frame, string label)
    {
        if (spatial.Area <= 1e-6)
            return ShapeResult.Fail(RegionCodes.RoomNotBounded, $"{label} is not placed or not enclosed: it has no area.");

        IList<IList<BoundarySegment>>? boundary;
        try
        {
            boundary = spatial.GetBoundarySegments(new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = BoundaryLocationOf(location) });
        }
        catch (Exception ex)
        {
            return ShapeResult.Fail(RegionCodes.RoomNotBounded, $"{label}: Revit could not read the boundary ({ex.Message}).");
        }
        if (boundary == null || boundary.Count == 0)
            return ShapeResult.Fail(RegionCodes.RoomNotBounded, $"{label} has no boundary.");

        var warnings = new List<string>();
        var loops = new List<IReadOnlyList<ModelSegment>>();
        foreach (var loop in boundary)
        {
            var segments = new List<ModelSegment>();
            foreach (var piece in loop) segments.AddRange(FilledRegionSupport.ToSegments(piece.GetCurve(), source.Transform, warnings));
            loops.Add(segments);
        }
        if (loops[0].Count > 0) AddLevelWarning(view, loops[0][0].Start.Z, label, warnings);

        var result = RegionShapeBuilder.FromModelLoops(loops, frame, label);
        // Revit leaves out a boundary piece too short to be a curve, so a real room can have a sub-millimetre gap.
        foreach (var loop in result.Loops) loop.CloseGaps(RoomGapToleranceMm);
        result.Warnings.AddRange(warnings);
        return result;
    }

    private static SpatialElementBoundaryLocation BoundaryLocationOf(string location)
    {
        if (location.Equals("center", StringComparison.OrdinalIgnoreCase)) return SpatialElementBoundaryLocation.Center;
        if (location.Equals("coreCenter", StringComparison.OrdinalIgnoreCase)) return SpatialElementBoundaryLocation.CoreCenter;
        if (location.Equals("coreBoundary", StringComparison.OrdinalIgnoreCase)) return SpatialElementBoundaryLocation.CoreBoundary;
        return SpatialElementBoundaryLocation.Finish;
    }

    private List<RoomRecord> RecordsOf(RoomSource source, List<string> warnings)
    {
        var key = source.Link?.Id.Value ?? 0;
        if (!_roomRecords.TryGetValue(key, out var records))
        {
            records = new RoomSourceService(source).LoadRooms(new RoomFilter(), warnings, 5000);
            _roomRecords[key] = records;
        }
        return records;
    }

    private static List<ModelSegment> ToSegments(IReadOnlyList<P2> points, double zMm)
    {
        var segments = new List<ModelSegment>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            segments.Add(new ModelSegment(new Vec3(a.X, a.Y, zMm), new Vec3(b.X, b.Y, zMm)));
        }
        return segments;
    }

    private ShapeResult ReadDetailLines(RegionShapeSpec shape, View view, ViewFrame frame)
    {
        var warnings = new List<string>();
        var pieces = new List<ModelSegment>();
        foreach (var id in shape.DetailLineIds)
        {
            if (_doc.GetElement(new ElementId(id)) is not CurveElement line)
                return ShapeResult.Fail(RegionCodes.LineNotFound, $"Element {id} is not a detail or model line.");
            if (line.ViewSpecific && line.OwnerViewId != view.Id)
                warnings.Add($"Line {id} is drawn in another view; it is used where it lies.");
            pieces.AddRange(FilledRegionSupport.ToSegments(line.GeometryCurve, Transform.Identity, warnings));
        }

        var result = RegionShapeBuilder.FromLoosePieces(pieces, frame, _joinToleranceMm, "The lines");
        result.Warnings.AddRange(warnings);
        return result;
    }

    private ShapeResult ReadElementBox(RegionShapeSpec shape, View view, ViewFrame frame)
    {
        var source = RoomSourceService.ResolveSource(_doc, shape.LinkInstanceId != 0 ? "link" : "host", shape.LinkInstanceId, out var linkError);
        if (source == null) return ShapeResult.Fail(RegionCodes.LinkNotFound, linkError ?? "The link was not found.");

        var element = source.Doc.GetElement(new ElementId(shape.ElementId));
        if (element == null) return ShapeResult.Fail(RegionCodes.ElementNotFound, $"Element {shape.ElementId} does not exist in the {source.Label}.");

        var box = element.get_BoundingBox(null) ?? (source.IsLink ? null : element.get_BoundingBox(view));
        if (box == null) return ShapeResult.Fail(RegionCodes.NoBoundingBox, $"Element {shape.ElementId} has no bounding box.");

        var corners = new List<Vec3>(8);
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
            corners.Add(FilledRegionSupport.ToMm(source.Transform.OfPoint(box.Transform.OfPoint(new XYZ(x, y, z)))));
        return RegionShapeBuilder.FromCorners(corners, frame);
    }
}

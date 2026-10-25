using System.Globalization;
using Autodesk.Revit.DB;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Documentation.Views;

/// <summary>
/// View extents from the model (#104): boxes of elements, rooms (host or linked, moved into host coordinates) and
/// scope boxes, and applying a box as a crop region or a 3D section box. The box math itself is in
/// <see cref="ViewExtentMath"/>. Used by revit_create_views, revit_set_view_extent and the clash review view.
/// </summary>
public static class ViewExtentService
{
    public const double MmPerFoot = 304.8;

    // ── Units ─────────────────────────────────────────────────────────────────

    public static XYZ ToXyz(Vec3 mm) => new(mm.X / MmPerFoot, mm.Y / MmPerFoot, mm.Z / MmPerFoot);
    public static Vec3 ToMm(XYZ ft) => new(ft.X * MmPerFoot, ft.Y * MmPerFoot, ft.Z * MmPerFoot);
    public static Vec3 ToUnit(XYZ v) => new(v.X, v.Y, v.Z);
    public static XYZ ToXyzUnit(Vec3 v) => new(v.X, v.Y, v.Z);

    /// <summary>A model-space bounding box (identity transform) in feet.</summary>
    public static BoundingBoxXYZ ToBoundingBox(BoxMm box) => new() { Min = ToXyz(box.Min), Max = ToXyz(box.Max) };

    /// <summary>A Revit box in model mm: all eight corners through its transform.</summary>
    public static BoxMm? ToBoxMm(BoundingBoxXYZ? box)
    {
        if (box == null) return null;
        var transform = box.Transform ?? Transform.Identity;
        var corners = new List<Vec3>();
        foreach (var x in new[] { box.Min.X, box.Max.X })
        foreach (var y in new[] { box.Min.Y, box.Max.Y })
        foreach (var z in new[] { box.Min.Z, box.Max.Z })
            corners.Add(ToMm(transform.OfPoint(new XYZ(x, y, z))));
        return ViewExtentMath.Bounds(corners);
    }

    public static FrameMm FrameOf(Transform transform) =>
        new(ToMm(transform.Origin), ToUnit(transform.BasisX), ToUnit(transform.BasisY), ToUnit(transform.BasisZ));

    // ── Box union and padding on Revit boxes (clash review) ────────────────────

    /// <summary>The smallest model box holding all the boxes; null when none is given.</summary>
    public static BoundingBoxXYZ? Union(params BoundingBoxXYZ?[] boxes)
    {
        var union = ViewExtentMath.Union(boxes.Select(ToBoxMm));
        return union == null ? null : ToBoundingBox(union);
    }

    /// <summary>The box grown by <paramref name="paddingMm"/> on every side.</summary>
    public static BoundingBoxXYZ Pad(BoundingBoxXYZ box, double paddingMm) =>
        ToBoundingBox(ViewExtentMath.Pad(ToBoxMm(box)!, paddingMm));

    /// <summary>Sets and activates a 3D view's section box. MUST be called inside an open transaction.</summary>
    public static void ApplySectionBox(View3D view, BoundingBoxXYZ box)
    {
        view.SetSectionBox(box);
        view.IsSectionBoxActive = true;
    }

    public static void ApplySectionBox(View3D view, BoxMm box) => ApplySectionBox(view, ToBoundingBox(box));

    // ── Sources ───────────────────────────────────────────────────────────────

    /// <summary>The union of the elements' bounding boxes; missing or box-less elements are reported in warnings.</summary>
    public static BoxMm? ElementsBox(Document doc, IEnumerable<long> ids, List<string> warnings)
    {
        var boxes = new List<BoxMm?>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(new ElementId(id));
            if (element == null)
            {
                warnings.Add($"Element {id} was not found; it does not count towards the extent.");
                continue;
            }
            BoundingBoxXYZ? box = null;
            try { box = element.get_BoundingBox(null); }
            catch { }
            if (box == null)
            {
                warnings.Add($"Element {id} ({element.Category?.Name ?? element.GetType().Name}) has no model bounding box; it does not count towards the extent.");
                continue;
            }
            boxes.Add(ToBoxMm(box));
        }
        return ViewExtentMath.Union(boxes);
    }

    /// <summary>
    /// A room's (or space's) box in host mm: the finish-face boundary in plan, the room's own height in z. A room in a
    /// link is read from the link document and moved into host coordinates with the link transform.
    /// </summary>
    public static BoxMm? RoomBox(Document hostDoc, long roomId, long linkInstanceId, List<string> warnings, out string? error)
    {
        var source = RoomSourceService.ResolveSource(hostDoc, linkInstanceId != 0 ? "link" : "host", linkInstanceId, out error);
        if (source == null) return null;

        if (source.Doc.GetElement(new ElementId(roomId)) is not SpatialElement room)
        {
            error = $"Element {roomId} is not a room or space in the {source.Label}." +
                    (linkInstanceId == 0 ? " For a room in a link pass its linkInstanceId." : string.Empty);
            return null;
        }

        var bounds = source.HostBounds(room);
        var plan = new List<Vec3>();
        try
        {
            var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish };
            var loops = room.GetBoundarySegments(options);
            if (loops != null)
                foreach (var loop in loops)
                foreach (var segment in loop)
                foreach (var point in segment.GetCurve().Tessellate())
                {
                    var p = source.ToHostMm(point);
                    plan.Add(new Vec3(p.X, p.Y, 0));
                }
        }
        catch (Exception ex)
        {
            warnings.Add($"The boundary of room {roomId} could not be read ({ex.Message}); its bounding box is used instead.");
        }

        if (plan.Count < 3 && bounds == null)
        {
            error = $"Room {roomId} in the {source.Label} has no boundary and no bounding box (is it placed and enclosed?).";
            return null;
        }

        double minX, minY, maxX, maxY;
        if (plan.Count >= 3)
        {
            minX = plan.Min(p => p.X); minY = plan.Min(p => p.Y);
            maxX = plan.Max(p => p.X); maxY = plan.Max(p => p.Y);
        }
        else
        {
            var b = bounds!.Value.Plan;
            minX = b.MinX; minY = b.MinY; maxX = b.MaxX; maxY = b.MaxY;
        }

        double minZ, maxZ;
        if (bounds != null)
        {
            minZ = bounds.Value.MinZMm;
            maxZ = bounds.Value.MaxZMm;
        }
        else
        {
            var levelZ = room.Level != null ? room.Level.ProjectElevation : 0.0;
            minZ = source.ToHostZMm(new XYZ(0, 0, levelZ));
            maxZ = minZ + 3000;
        }
        if (maxZ - minZ < 1) maxZ = minZ + 3000;
        return new BoxMm(new Vec3(minX, minY, minZ), new Vec3(maxX, maxY, maxZ));
    }

    /// <summary>A scope box by exact name or element id.</summary>
    public static Element? FindScopeBox(Document doc, string reference, out string? error)
    {
        error = null;
        var scopeBoxes = new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_VolumeOfInterest)
            .WhereElementIsNotElementType()
            .ToElements();
        var wanted = reference.Trim();
        var byName = scopeBoxes.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.Ordinal))
                     ?? scopeBoxes.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
        if (byName != null) return byName;
        if (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = scopeBoxes.FirstOrDefault(e => e.Id.Value == id);
            if (byId != null) return byId;
        }
        error = scopeBoxes.Count == 0
            ? $"No scope box '{wanted}': the document has no scope boxes."
            : $"No scope box '{wanted}'. Scope boxes: {string.Join(", ", scopeBoxes.Take(25).Select(e => $"'{e.Name}' ({e.Id.Value})"))}.";
        return null;
    }

    /// <summary>
    /// The box of a region spec in host mm, padded (<see cref="ViewExtentMath.DefaultPaddingMm"/> unless the spec says
    /// otherwise). <paramref name="scopeBox"/> receives the scope box element when the region names one.
    /// </summary>
    public static BoxMm? RegionBox(
        Document doc, RegionSpec region, List<string> warnings, out Element? scopeBox, out string? error, double? defaultPaddingMm = null)
    {
        scopeBox = null;
        error = null;
        BoxMm? box;
        if (region.Box != null) box = region.Box;
        else if (region.ElementIds.Count > 0)
        {
            box = ElementsBox(doc, region.ElementIds, warnings);
            if (box == null) error = $"None of the {region.ElementIds.Count} element(s) has a model bounding box.";
        }
        else if (region.RoomId != null) box = RoomBox(doc, region.RoomId.Value, region.RoomLinkInstanceId, warnings, out error);
        else if (!string.IsNullOrWhiteSpace(region.ScopeBox))
        {
            scopeBox = FindScopeBox(doc, region.ScopeBox!, out error);
            box = scopeBox == null ? null : ToBoxMm(scopeBox.get_BoundingBox(null));
            if (scopeBox != null && box == null) error = $"Scope box '{scopeBox.Name}' has no bounding box.";
        }
        else
        {
            error = "No extent given: box, elements, room or scopeBox.";
            return null;
        }

        if (box == null) return null;
        var padding = region.PaddingMm ?? defaultPaddingMm ?? ViewExtentMath.DefaultPaddingMm;
        return padding == 0 ? box : ViewExtentMath.Pad(box, padding);
    }

    // ── Applying a box ────────────────────────────────────────────────────────

    /// <summary>The scope box controlling a view's crop, or null.</summary>
    public static Element? AssignedScopeBox(View view)
    {
        try
        {
            var parameter = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            var id = parameter?.AsElementId();
            return id == null || id == ElementId.InvalidElementId ? null : view.Document.GetElement(id);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>True when the view can take a scope box (it has a writable scope box parameter).</summary>
    public static bool CanTakeScopeBox(View view)
    {
        try
        {
            var parameter = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
            return parameter != null && !parameter.IsReadOnly;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Assigns a scope box to a view. MUST be called inside an open transaction.</summary>
    public static void AssignScopeBox(View view, Element scopeBox)
    {
        var parameter = view.get_Parameter(BuiltInParameter.VIEWER_VOLUME_OF_INTEREST_CROP);
        if (parameter == null || parameter.IsReadOnly)
            throw new InvalidOperationException($"View '{view.Name}' ({view.ViewType}) cannot take a scope box.");
        if (!parameter.Set(scopeBox.Id))
            throw new InvalidOperationException($"Revit refused scope box '{scopeBox.Name}' for view '{view.Name}'.");
    }

    /// <summary>
    /// The crop box a model box gives a view: the box's corners in the view's crop frame, keeping the crop box's own
    /// depth (near/far) range. Returns the new box and its local extent in mm.
    /// </summary>
    public static (BoundingBoxXYZ Crop, BoxMm LocalMm) CropFor(View view, BoxMm modelBox)
    {
        var current = view.CropBox ?? throw new InvalidOperationException($"View '{view.Name}' exposes no crop box.");
        var transform = current.Transform ?? Transform.Identity;
        var local = ViewExtentMath.InFrame(modelBox, FrameOf(transform));
        var crop = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = new XYZ(local.Min.X / MmPerFoot, local.Min.Y / MmPerFoot, current.Min.Z),
            Max = new XYZ(local.Max.X / MmPerFoot, local.Max.Y / MmPerFoot, current.Max.Z)
        };
        var localMm = new BoxMm(
            new Vec3(local.Min.X, local.Min.Y, current.Min.Z * MmPerFoot),
            new Vec3(local.Max.X, local.Max.Y, current.Max.Z * MmPerFoot));
        return (crop, localMm);
    }

    /// <summary>
    /// Crops a plan, section, callout or elevation to a model box (rectangular; a custom crop shape is removed). Reuses
    /// <see cref="ViewCropRegionToolSupport.Apply"/>. MUST be called inside an open transaction.
    /// </summary>
    public static BoxMm ApplyCrop(View view, BoxMm modelBox, bool cropVisible)
    {
        var (crop, local) = CropFor(view, modelBox);
        ViewCropRegionToolSupport.Apply(view, new ViewCropRegionToolSupport.CropRegionSnapshot(crop, null, true, cropVisible, false));
        return local;
    }

    /// <summary>A view's current crop box in its own frame, mm; null when it has none.</summary>
    public static object? DescribeCrop(View view)
    {
        try
        {
            var crop = view.CropBox;
            if (crop == null) return null;
            return new
            {
                active = view.CropBoxActive,
                visible = view.CropBoxVisible,
                minLocalMm = ToMm(crop.Min).ToMm(),
                maxLocalMm = ToMm(crop.Max).ToMm()
            };
        }
        catch
        {
            return null;
        }
    }
}

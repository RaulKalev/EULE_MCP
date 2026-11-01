using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.DTOs;

namespace RevitMCP.Addin.Coordination.ClashFix;

/// <summary>An element of the host model or of a loaded link, with the transform from its document to the host.</summary>
internal sealed class PlacedElement
{
    public PlacedElement(Element element, RevitLinkInstance? link, Transform toHost)
    {
        Element = element;
        Link = link;
        ToHost = toHost;
    }

    public Element Element { get; }
    public Document Document => Element.Document;
    public RevitLinkInstance? Link { get; }
    public Transform ToHost { get; }
    public long LinkInstanceId => Link?.Id.Value ?? 0;
    public bool IsHost => Link == null;
    public string Key => ClashGeometry.KeyOf(LinkInstanceId, Element.Id.Value);

    public static PlacedElement? Find(Document host, ClashElementRefDto reference)
    {
        if (reference.LinkInstanceId is { } linkId && linkId > 0)
        {
            if (host.GetElement(new ElementId(linkId)) is not RevitLinkInstance link) return null;
            var linkDoc = link.GetLinkDocument();
            var linked = linkDoc?.GetElement(new ElementId(reference.ElementId));
            return linked == null ? null : new PlacedElement(linked, link, link.GetTotalTransform());
        }

        var element = host.GetElement(new ElementId(reference.ElementId));
        return element == null ? null : new PlacedElement(element, null, Transform.Identity);
    }

    /// <summary>The reference a clash run stores for this element.</summary>
    public ClashElementRefDto ToRef() => new()
    {
        ElementId = Element.Id.Value,
        UniqueId = Element.UniqueId,
        Category = Element.Category?.Name ?? string.Empty,
        Family = (Element as FamilyInstance)?.Symbol?.FamilyName ?? string.Empty,
        Type = Element.Document.GetElement(Element.GetTypeId())?.Name ?? string.Empty,
        Model = IsHost ? "Host" : "Linked",
        LinkInstanceId = Link?.Id.Value,
        LinkName = Link?.Name
    };
}

/// <summary>An element that intersects a solid, with the intersection volume.</summary>
internal sealed class IntersectingElement
{
    public IntersectingElement(PlacedElement placed, double volumeMm3)
    {
        Placed = placed;
        VolumeMm3 = volumeMm3;
    }

    public PlacedElement Placed { get; }
    public double VolumeMm3 { get; }

    public object Describe() => new
    {
        elementId = Placed.Element.Id.Value,
        linkInstanceId = Placed.Link?.Id.Value,
        linkName = Placed.Link?.Name,
        category = Placed.Element.Category?.Name,
        name = Placed.Element.Name,
        volumeMm3 = Math.Round(VolumeMm3)
    };
}

/// <summary>
/// Solid geometry for clash checks and fixes, in host coordinates: every solid of an element (not only the first, as
/// the detector's pre-check takes), translated copies, intersection volumes and the elements a solid runs into in the
/// host and its loaded links. API thread.
/// </summary>
internal static class ClashGeometry
{
    public const double FtToMm = 304.8;

    private static readonly Options GeometryOptions = new()
    {
        ComputeReferences = false,
        DetailLevel = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = false
    };

    /// <summary>Categories that occupy no physical space, or that wrap other elements (links, imports, groups).</summary>
    private static readonly HashSet<BuiltInCategory> NonPhysical = new()
    {
        BuiltInCategory.OST_Rooms, BuiltInCategory.OST_MEPSpaces, BuiltInCategory.OST_Areas, BuiltInCategory.OST_HVAC_Zones,
        BuiltInCategory.OST_Lines, BuiltInCategory.OST_RvtLinks, BuiltInCategory.OST_IOSModelGroups,
        BuiltInCategory.OST_ElectricalCircuit, BuiltInCategory.OST_Wire, BuiltInCategory.OST_Cameras,
        BuiltInCategory.OST_SectionBox, BuiltInCategory.OST_Mass, BuiltInCategory.OST_Entourage,
        BuiltInCategory.OST_Planting, BuiltInCategory.OST_Topography
    };

    public static string KeyOf(long linkInstanceId, long elementId) => linkInstanceId + ":" + elementId;

    public static double MmToFt(double mm) => mm / FtToMm;

    /// <summary>Every solid of the element with a volume, moved into host coordinates.</summary>
    public static List<Solid> Solids(PlacedElement placed)
    {
        var result = new List<Solid>();
        try
        {
            var geometry = placed.Element.get_Geometry(GeometryOptions);
            if (geometry != null) Collect(geometry, result);
        }
        catch
        {
            // An element whose geometry cannot be read has nothing to clash with.
        }

        return placed.ToHost.IsIdentity
            ? result
            : result.Select(s => SolidUtils.CreateTransformed(s, placed.ToHost)).ToList();
    }

    private static void Collect(GeometryElement geometry, List<Solid> result)
    {
        foreach (var obj in geometry)
        {
            if (obj is Solid solid && solid.Volume > 1e-9) result.Add(solid);
            else if (obj is GeometryInstance instance && instance.GetInstanceGeometry() is { } inner) Collect(inner, result);
        }
    }

    public static List<Solid> Translate(List<Solid> solids, XYZ offset)
    {
        if (offset.IsZeroLength()) return solids;
        var transform = Transform.CreateTranslation(offset);
        return solids.Select(s => SolidUtils.CreateTransformed(s, transform)).ToList();
    }

    /// <summary>The total intersection volume in mm³; <paramref name="failures"/> counts solid pairs Revit could not intersect.</summary>
    public static double IntersectionVolumeMm3(List<Solid> a, List<Solid> b, out int failures)
    {
        failures = 0;
        var volume = 0.0;
        foreach (var sa in a)
        {
            foreach (var sb in b)
            {
                try
                {
                    var intersection = BooleanOperationsUtils.ExecuteBooleanOperation(sa, sb, BooleanOperationsType.Intersect);
                    if (intersection != null) volume += intersection.Volume;
                }
                catch
                {
                    failures++;
                }
            }
        }
        return volume * FtToMm * FtToMm * FtToMm;
    }

    /// <summary>The tolerance volume of the clash detector: a cube of <paramref name="toleranceMm"/>.</summary>
    public static double ToleranceVolumeMm3(double toleranceMm) => Math.Pow(Math.Max(0, toleranceMm), 3);

    /// <summary>Points on every edge of the solids, in mm (what the travel bound projects).</summary>
    public static List<FixVector> PointsMm(List<Solid> solids)
    {
        var points = new List<FixVector>();
        foreach (var solid in solids)
        {
            foreach (Edge edge in solid.Edges)
            {
                IList<XYZ> tessellated;
                try { tessellated = edge.Tessellate(); }
                catch { continue; }
                foreach (var p in tessellated) points.Add(ToMm(p));
            }
        }
        return points;
    }

    public static FixVector ToMm(XYZ p) => new(p.X * FtToMm, p.Y * FtToMm, p.Z * FtToMm);

    public static FixVector ToVector(XYZ p) => new(p.X, p.Y, p.Z);

    public static XYZ ToFeet(FixVector mm) => new(MmToFt(mm.X), MmToFt(mm.Y), MmToFt(mm.Z));

    /// <summary>
    /// The physical elements of the host and its loaded links that intersect <paramref name="solids"/> (host
    /// coordinates) by more than <paramref name="toleranceMm3"/>, except those in <paramref name="exclude"/> (keys from
    /// <see cref="KeyOf"/>). At most <paramref name="max"/> are measured.
    /// </summary>
    public static List<IntersectingElement> FindIntersecting(
        Document host, List<Solid> solids, ISet<string> exclude, double toleranceMm3, int max = 100)
    {
        var result = new List<IntersectingElement>();
        if (solids.Count == 0) return result;

        Search(host, null, Transform.Identity, solids, exclude, toleranceMm3, max, result);

        foreach (var link in new FilteredElementCollector(host).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
        {
            if (result.Count >= max) break;
            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) continue;
            var toHost = link.GetTotalTransform();
            var toLink = toHost.Inverse;
            var linkSolids = solids.Select(s => SolidUtils.CreateTransformed(s, toLink)).ToList();
            Search(linkDoc, link, toHost, linkSolids, exclude, toleranceMm3, max, result);
        }

        return result;
    }

    private static void Search(
        Document doc, RevitLinkInstance? link, Transform toHost, List<Solid> solids, ISet<string> exclude,
        double toleranceMm3, int max, List<IntersectingElement> result)
    {
        var linkId = link?.Id.Value ?? 0;
        var ids = new HashSet<ElementId>();
        foreach (var solid in solids)
        {
            Outline outline;
            try
            {
                var local = solid.GetBoundingBox();
                var box = Clash.Geometry.ClashTransformHelper.TransformBoundingBox(local, local.Transform);
                outline = new Outline(box.Min, box.Max);
            }
            catch
            {
                continue;
            }
            if (outline.IsEmpty) continue;

            try
            {
                foreach (var id in new FilteredElementCollector(doc)
                             .WhereElementIsNotElementType()
                             .WherePasses(new BoundingBoxIntersectsFilter(outline))
                             .WherePasses(new ElementIntersectsSolidFilter(solid))
                             .ToElementIds())
                    ids.Add(id);
            }
            catch
            {
                // Revit refuses some solids for the filter; the others still search.
            }
        }

        foreach (var id in ids)
        {
            if (result.Count >= max) return;
            if (exclude.Contains(KeyOf(linkId, id.Value))) continue;
            var element = doc.GetElement(id);
            if (!IsPhysical(element)) continue;

            // Measured in the element's own document: the transform between the two is rigid.
            var placedInDoc = new PlacedElement(element!, null, Transform.Identity);
            var volume = IntersectionVolumeMm3(solids, Solids(placedInDoc), out _);
            if (volume > toleranceMm3) result.Add(new IntersectingElement(new PlacedElement(element!, link, toHost), volume));
        }
    }

    private static bool IsPhysical(Element? element)
    {
        if (element == null || element is RevitLinkInstance || element is ImportInstance || element is Group ||
            element is SpatialElement || element.ViewSpecific)
            return false;
        var category = element.Category;
        if (category == null || category.CategoryType != CategoryType.Model) return false;
        return !NonPhysical.Contains((BuiltInCategory)category.Id.Value);
    }
}

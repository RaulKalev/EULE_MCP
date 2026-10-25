using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Mep;

/// <summary>An open end connector found near a point.</summary>
internal sealed class MepEndCandidate
{
    public Element Element { get; init; } = null!;
    public Connector Connector { get; init; } = null!;
    public double DistanceMm { get; init; }
}

/// <summary>Connector lookups for tray and conduit runs (#106). API thread.</summary>
internal static class MepConnectorFinder
{
    public static ConnectorManager? ManagerOf(Element? element)
    {
        try
        {
            return element switch
            {
                MEPCurve curve => curve.ConnectorManager,
                FamilyInstance instance => instance.MEPModel?.ConnectorManager,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The end connectors of a curve or fitting (not the curve connectors along its side).</summary>
    public static List<Connector> EndConnectors(Element? element)
    {
        var result = new List<Connector>();
        var manager = ManagerOf(element);
        if (manager == null) return result;
        try
        {
            foreach (Connector connector in manager.Connectors)
                if (connector.ConnectorType == ConnectorType.End) result.Add(connector);
        }
        catch
        {
            // An element whose connectors cannot be read has none to offer.
        }
        return result;
    }

    public static List<Connector> OpenEnds(Element? element) => EndConnectors(element).Where(c => !IsConnected(c)).ToList();

    public static bool IsConnected(Connector connector)
    {
        try { return connector.IsConnected; }
        catch { return false; }
    }

    /// <summary>The end connector of <paramref name="element"/> nearest <paramref name="point"/>, open ones only when asked.</summary>
    public static Connector? NearestEnd(Element? element, XYZ point, bool openOnly = false) =>
        EndConnectors(element)
            .Where(c => !openOnly || !IsConnected(c))
            .OrderBy(c => c.Origin.DistanceTo(point))
            .FirstOrDefault();

    public static MepPoint ToMm(XYZ point) =>
        new(MepTypeCatalog.FtToMm(point.X), MepTypeCatalog.FtToMm(point.Y), MepTypeCatalog.FtToMm(point.Z));

    /// <summary>The connector as the pure geometry sees it: where it is and the way it points out of its element.</summary>
    public static MepEnd ToEnd(Connector connector)
    {
        var z = connector.CoordinateSystem.BasisZ;
        return new MepEnd(ToMm(connector.Origin), new MepPoint(z.X, z.Y, z.Z));
    }

    /// <summary>
    /// Open end connectors of trays (or conduits) within <paramref name="snapMm"/> of <paramref name="point"/>, nearest
    /// first. Connected ends are never offered. <paramref name="exclude"/> holds the run's own elements.
    /// </summary>
    public static List<MepEndCandidate> Candidates(Document doc, string kind, XYZ point, double snapMm, ICollection<ElementId> exclude)
    {
        var snapFt = snapMm / MepTypeCatalog.MmPerFoot;
        var outline = new Outline(point - new XYZ(snapFt, snapFt, snapFt), point + new XYZ(snapFt, snapFt, snapFt));
        var category = kind == MepKind.CableTray ? BuiltInCategory.OST_CableTray : BuiltInCategory.OST_Conduit;
        var collector = new FilteredElementCollector(doc)
            .OfCategory(category)
            .WhereElementIsNotElementType()
            .WherePasses(new BoundingBoxIntersectsFilter(outline));

        var result = new List<MepEndCandidate>();
        foreach (var element in collector)
        {
            if (exclude.Contains(element.Id) || !MepTypeCatalog.IsKind(element, kind)) continue;
            foreach (var connector in OpenEnds(element))
            {
                var distance = MepTypeCatalog.FtToMm(connector.Origin.DistanceTo(point));
                if (distance <= snapMm)
                    result.Add(new MepEndCandidate { Element = element, Connector = connector, DistanceMm = distance });
            }
        }
        return result.OrderBy(c => c.DistanceMm).ThenBy(c => c.Element.Id.Value).ToList();
    }

    /// <summary>True when the two elements are joined directly or through one fitting.</summary>
    public static bool AreConnected(Element a, Element b)
    {
        foreach (var connector in EndConnectors(a))
        {
            if (!IsConnected(connector)) continue;
            foreach (Connector other in connector.AllRefs)
            {
                var owner = other.Owner;
                if (owner == null || owner.Id == a.Id) continue;
                if (owner.Id == b.Id) return true;
                if (owner is FamilyInstance fitting &&
                    EndConnectors(fitting).Any(fc => IsConnected(fc) && fc.AllRefs.Cast<Connector>().Any(r => r.Owner?.Id == b.Id)))
                    return true;
            }
        }
        return false;
    }

    /// <summary>The size of a tray (width × height) or conduit (diameter) in mm.</summary>
    public static (double? WidthMm, double? HeightMm, double? DiameterMm) SizeOf(Element element)
    {
        if (element is Autodesk.Revit.DB.Electrical.Conduit)
            return (null, null, ParameterMm(element, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM));
        return (ParameterMm(element, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM),
            ParameterMm(element, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM), null);
    }

    public static bool SameSize(Element a, Element b)
    {
        var sa = SizeOf(a);
        var sb = SizeOf(b);
        return Same(sa.WidthMm, sb.WidthMm) && Same(sa.HeightMm, sb.HeightMm) && Same(sa.DiameterMm, sb.DiameterMm);
    }

    private static bool Same(double? a, double? b) => !a.HasValue || !b.HasValue ? a.HasValue == b.HasValue : Math.Abs(a.Value - b.Value) < 0.5;

    public static double? ParameterMm(Element element, BuiltInParameter bip)
    {
        try
        {
            var parameter = element.get_Parameter(bip);
            if (parameter == null || parameter.StorageType != StorageType.Double || !parameter.HasValue) return null;
            return MepMath.Round(MepTypeCatalog.FtToMm(parameter.AsDouble()));
        }
        catch
        {
            return null;
        }
    }
}

using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Placement;

/// <summary>
/// Extracts dimensionable references from model elements. Revit dimensions only accept
/// references (datum lines, family reference planes, geometry curves/faces) — not bare
/// elements — so each element kind needs its own extraction strategy.
/// </summary>
internal static class DimensionReferenceResolver
{
    /// <summary>
    /// Reference for linear/angular dimensioning plus the element's axis curve when one exists.
    /// Walls and datum elements expose an invisible axis line (with a valid Reference) in their
    /// geometry when non-visible objects are included — that line is the preferred target.
    /// </summary>
    public static (Reference? Reference, Curve? Curve, string? Error) ResolveLinear(Element element, View view)
    {
        switch (element)
        {
            case FamilyInstance fi:
            {
                var r = FirstFamilyReference(fi);
                if (r != null) return (r, AxisCurve(element), null);
                break;
            }
            case ReferencePlane rp:
                return (rp.GetReference(), Line.CreateUnbound(rp.GetPlane().Origin, rp.Direction), null);
            case CurveElement ce when ce.GeometryCurve?.Reference != null:
                return (ce.GeometryCurve.Reference, ce.GeometryCurve, null);
        }

        var (reference, curve) = ScanGeometryForCurveReference(element, view, wantArc: false);
        if (reference != null)
            return (reference, curve ?? AxisCurve(element), null);

        // Datum elements (grids, levels) accept a plain element reference for dimensioning.
        if (element is DatumPlane)
            return (new Reference(element), AxisCurve(element), null);

        return (null, null,
            $"Element {element.Id.Value} ('{element.Name}', {element.Category?.Name}) exposes no dimensionable reference. " +
            "Supported: walls, grids, levels, reference planes, model/detail lines, and family instances with reference planes.");
    }

    /// <summary>Arc geometry (with reference) for radial / diameter / arc-length dimensioning.</summary>
    public static (Reference? Reference, Arc? Arc, string? Error) ResolveArc(Element element, View view)
    {
        if (element is CurveElement ce && ce.GeometryCurve is Arc curveArc && ce.GeometryCurve.Reference != null)
            return (ce.GeometryCurve.Reference, curveArc, null);

        var (reference, curve) = ScanGeometryForCurveReference(element, view, wantArc: true);
        if (reference != null && curve is Arc arc)
            return (reference, arc, null);

        return (null, null,
            $"Element {element.Id.Value} ('{element.Name}', {element.Category?.Name}) has no arc geometry with a usable reference. " +
            "Use an arc wall, arc model/detail line, or another element with circular geometry.");
    }

    /// <summary>A representative point of the element used to lay out the dimension line.</summary>
    public static XYZ? MeasurePoint(Element element, View view)
    {
        switch (element)
        {
            case Grid g when g.Curve != null:
                return g.Curve.Evaluate(0.5, true);
            case CurveElement ce when ce.GeometryCurve != null:
                return ce.GeometryCurve.Evaluate(0.5, true);
        }

        switch (element.Location)
        {
            case LocationPoint lp:
                return lp.Point;
            case LocationCurve lc:
                return lc.Curve.Evaluate(0.5, true);
        }

        var bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        return bb != null ? (bb.Min + bb.Max) / 2 : null;
    }

    private static Reference? FirstFamilyReference(FamilyInstance fi)
    {
        foreach (var type in new[]
                 {
                     FamilyInstanceReferenceType.CenterLeftRight,
                     FamilyInstanceReferenceType.CenterFrontBack,
                     FamilyInstanceReferenceType.CenterElevation,
                     FamilyInstanceReferenceType.StrongReference,
                     FamilyInstanceReferenceType.WeakReference
                 })
        {
            try
            {
                var refs = fi.GetReferences(type);
                if (refs.Count > 0) return refs[0];
            }
            catch { /* family has no references of this type */ }
        }
        return null;
    }

    private static Curve? AxisCurve(Element element) => element switch
    {
        Grid g => g.Curve,
        CurveElement ce => ce.GeometryCurve,
        _ => element.Location is LocationCurve lc ? lc.Curve : null
    };

    private static (Reference? Reference, Curve? Curve) ScanGeometryForCurveReference(Element element, View view, bool wantArc)
    {
        foreach (var options in OptionVariants(view))
        {
            GeometryElement? geometry = null;
            try { geometry = element.get_Geometry(options); } catch { }
            if (geometry == null) continue;

            var match = FindCurve(geometry, wantArc);
            if (match.Reference != null) return match;
        }
        return (null, null);
    }

    private static IEnumerable<Options> OptionVariants(View view)
    {
        Options? viewOptions = null;
        try { viewOptions = new Options { ComputeReferences = true, IncludeNonVisibleObjects = true, View = view }; }
        catch { /* some views cannot be used for geometry extraction */ }
        if (viewOptions != null) yield return viewOptions;

        yield return new Options { ComputeReferences = true, IncludeNonVisibleObjects = true };
    }

    private static (Reference? Reference, Curve? Curve) FindCurve(GeometryElement geometry, bool wantArc)
    {
        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case Curve c when (wantArc ? c is Arc : c is Line) && c.Reference != null:
                    return (c.Reference, c);
                case GeometryInstance gi:
                {
                    var nested = FindCurve(gi.GetInstanceGeometry(), wantArc);
                    if (nested.Reference != null) return nested;
                    break;
                }
            }
        }
        return (null, null);
    }
}

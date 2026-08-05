using Autodesk.Revit.DB;

namespace RevitMCP.Addin.CadManagement;

/// <summary>
/// Reads the loose line work out of a CAD import as straight segments in host coordinates.
///
/// This is the counterpart to <see cref="CadPointExtractor"/>, which wants block inserts, points and
/// circles. A drawing that never blocked its symbols has none of those — the luminaires are bare
/// lines — so the segments are collected here and reassembled into fixtures by
/// <see cref="CadShapeMath.Cluster"/>.
///
/// Curves are tessellated rather than special-cased: an arc, a spline and an ellipse all come back
/// as short straight pieces, which is exactly what the clustering and the box fitting need.
/// </summary>
internal sealed class CadCurveExtractor
{
    /// <summary>Nesting depth cap — deeply nested blocks are legal in DWG and would otherwise recurse forever.</summary>
    private const int MaxDepth = 8;

    /// <summary>
    /// Guard against a drawing large enough to exhaust memory before the caller ever sees a result.
    /// Hit in practice only by pointing this at a whole architectural background.
    /// </summary>
    public const int MaxSegments = 400_000;

    private static readonly Options GeometryOptions = new()
    {
        ComputeReferences = false,
        DetailLevel = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = false
    };

    private readonly CadLayerResolver _layers;
    private bool _hitSegmentCap;

    public CadCurveExtractor(Document doc)
    {
        _layers = new CadLayerResolver(doc);
    }

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Walks the import and returns every straight segment on <paramref name="layerFilter"/>.
    /// A null filter takes every layer, which is almost never what a caller wants on a real drawing.
    /// </summary>
    public List<CadSegment> Extract(
        ImportInstance import,
        ISet<string>? layerFilter,
        CancellationToken cancellationToken)
    {
        var segments = new List<CadSegment>();

        GeometryElement? geometry;
        try { geometry = import.get_Geometry(GeometryOptions); }
        catch (Exception ex)
        {
            Warnings.Add($"Could not read geometry from '{CadPointExtractor.SafeName(import)}': {ex.Message}");
            return segments;
        }

        if (geometry == null)
        {
            Warnings.Add($"'{CadPointExtractor.SafeName(import)}' has no readable geometry in this document.");
            return segments;
        }

        Walk(geometry, Transform.Identity, 0, segments, layerFilter, cancellationToken);

        if (_hitSegmentCap)
        {
            Warnings.Add(
                $"Stopped after {MaxSegments:N0} segments — the layers named carry more line work than " +
                "any set of fixtures would. Narrow the layers before reading shapes.");
        }

        return segments;
    }

    private void Walk(
        GeometryElement geometry,
        Transform transform,
        int depth,
        List<CadSegment> segments,
        ISet<string>? layerFilter,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
        {
            Warnings.Add($"Stopped at {MaxDepth} levels of nested blocks — deeper geometry was ignored.");
            return;
        }

        foreach (var obj in geometry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_hitSegmentCap)
                return;

            if (obj is GeometryInstance instance)
            {
                // Transforms are composed explicitly rather than left to GetInstanceGeometry, so a
                // symbol nested inside a block still lands in host coordinates unambiguously.
                var composed = transform.Multiply(instance.Transform);

                GeometryElement? symbolGeometry;
                try { symbolGeometry = instance.GetSymbolGeometry(); }
                catch { continue; }

                if (symbolGeometry != null)
                    Walk(symbolGeometry, composed, depth + 1, segments, layerFilter, cancellationToken);

                continue;
            }

            Record(obj, transform, segments, layerFilter);
        }
    }

    private void Record(
        GeometryObject obj,
        Transform transform,
        List<CadSegment> segments,
        ISet<string>? layerFilter)
    {
        var layer = _layers.LayerOf(obj) ?? CadLayerResolver.NoLayer;
        if (layerFilter != null && !layerFilter.Contains(layer))
            return;

        switch (obj)
        {
            case Curve curve:
            {
                IList<XYZ> points;
                try { points = curve.Tessellate(); }
                catch { return; }

                // Anything that is not a straight line arrives as several pieces; that is the cue
                // that the symbol is round, which is how a downlight is told from a batten.
                var fromArc = curve is not Line;
                AddChain(points, layer, fromArc, transform, segments);
                return;
            }

            case PolyLine polyline:
            {
                IList<XYZ> points;
                try { points = polyline.GetCoordinates(); }
                catch { return; }

                AddChain(points, layer, fromArc: false, transform, segments);
                return;
            }
        }
    }

    private void AddChain(
        IList<XYZ> points,
        string layer,
        bool fromArc,
        Transform transform,
        List<CadSegment> segments)
    {
        if (points == null || points.Count < 2)
            return;

        var previous = transform.OfPoint(points[0]);

        for (var i = 1; i < points.Count; i++)
        {
            if (segments.Count >= MaxSegments)
            {
                _hitSegmentCap = true;
                return;
            }

            var current = transform.OfPoint(points[i]);

            segments.Add(new CadSegment
            {
                X1 = ToMm(previous.X),
                Y1 = ToMm(previous.Y),
                X2 = ToMm(current.X),
                Y2 = ToMm(current.Y),
                Zmm = ToMm((previous.Z + current.Z) / 2.0),
                Layer = layer,
                FromArc = fromArc
            });

            previous = current;
        }
    }

    private static double ToMm(double feet) =>
        UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);
}

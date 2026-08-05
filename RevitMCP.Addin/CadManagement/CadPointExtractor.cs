using Autodesk.Revit.DB;

namespace RevitMCP.Addin.CadManagement;

/// <summary>What a layer holds, so the caller can tell a locations layer from a drawing layer.</summary>
public sealed class CadLayerSummary
{
    public string LayerName { get; set; } = string.Empty;
    public int BlockCount { get; set; }
    public int PointCount { get; set; }
    public int CircleCount { get; set; }
    public int CurveCount { get; set; }
    public int TextCount { get; set; }
    public int OtherCount { get; set; }
    public double MinZmm { get; set; }
    public double MaxZmm { get; set; }
    public bool HasPlaceablePoints => BlockCount + PointCount + CircleCount > 0;
}

/// <summary>
/// Reads candidate placement locations out of an imported or linked CAD file.
///
/// Geometry is walked with <see cref="GeometryInstance.GetSymbolGeometry"/> and transforms composed
/// explicitly, rather than relying on <c>GetInstanceGeometry</c> to pre-apply them, so nested block
/// references land in host coordinates with no ambiguity about which transform was applied where.
/// </summary>
internal sealed class CadPointExtractor
{
    /// <summary>Nesting depth cap — deeply nested blocks are legal in DWG and would otherwise recurse forever.</summary>
    private const int MaxDepth = 8;

    private static readonly Options GeometryOptions = new()
    {
        ComputeReferences = false,
        DetailLevel = ViewDetailLevel.Fine,
        IncludeNonVisibleObjects = false
    };

    private readonly CadLayerResolver _layers;

    public CadPointExtractor(Document doc)
    {
        _layers = new CadLayerResolver(doc);
    }

    public List<string> Warnings { get; } = new();

    /// <summary>
    /// Walks the import once, returning every candidate point and a per-layer inventory.
    /// <paramref name="layerFilter"/> limits the points that are kept; the inventory always covers
    /// every layer so the caller can see what else the file has.
    /// </summary>
    public (List<CadPoint> Points, List<CadLayerSummary> Layers) Extract(
        ImportInstance import,
        ISet<string>? layerFilter,
        ISet<string> sources,
        CancellationToken cancellationToken)
    {
        var points = new List<CadPoint>();
        var layers = new Dictionary<string, CadLayerSummary>(StringComparer.OrdinalIgnoreCase);

        GeometryElement? geometry;
        try { geometry = import.get_Geometry(GeometryOptions); }
        catch (Exception ex)
        {
            Warnings.Add($"Could not read geometry from '{SafeName(import)}': {ex.Message}");
            return (points, new List<CadLayerSummary>());
        }

        if (geometry == null)
        {
            Warnings.Add($"'{SafeName(import)}' has no readable geometry in this document.");
            return (points, new List<CadLayerSummary>());
        }

        Walk(geometry, Transform.Identity, 0, points, layers, layerFilter, sources, cancellationToken);

        return (points, layers.Values
            .OrderByDescending(l => l.BlockCount + l.PointCount + l.CircleCount)
            .ThenBy(l => l.LayerName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private void Walk(
        GeometryElement geometry,
        Transform transform,
        int depth,
        List<CadPoint> points,
        Dictionary<string, CadLayerSummary> layers,
        ISet<string>? layerFilter,
        ISet<string> sources,
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

            if (obj is GeometryInstance instance)
            {
                var composed = transform.Multiply(instance.Transform);

                // Depth 0 is the import instance itself, not a block reference in the drawing.
                if (depth > 0)
                {
                    var layer = _layers.LayerOf(instance)
                                ?? _layers.FirstLayerInside(instance)
                                ?? CadLayerResolver.NoLayer;
                    var summary = SummaryFor(layers, layer);
                    summary.BlockCount++;
                    TrackHeight(summary, composed.Origin.Z);

                    if (Accepts(layerFilter, layer) && sources.Contains(CadPointMath.SourceBlock))
                    {
                        points.Add(new CadPoint
                        {
                            X = ToMm(composed.Origin.X),
                            Y = ToMm(composed.Origin.Y),
                            Z = ToMm(composed.Origin.Z),
                            Layer = layer,
                            Source = CadPointMath.SourceBlock,
                            RotationDegrees = CadPointMath.RotationDegreesFromBasis(
                                composed.BasisX.X, composed.BasisX.Y)
                        });
                    }
                }

                GeometryElement? symbolGeometry;
                try { symbolGeometry = instance.GetSymbolGeometry(); }
                catch { continue; }

                if (symbolGeometry != null)
                {
                    Walk(symbolGeometry, composed, depth + 1,
                        points, layers, layerFilter, sources, cancellationToken);
                }
                continue;
            }

            Record(obj, transform, points, layers, layerFilter, sources);
        }
    }

    private void Record(
        GeometryObject obj,
        Transform transform,
        List<CadPoint> points,
        Dictionary<string, CadLayerSummary> layers,
        ISet<string>? layerFilter,
        ISet<string> sources)
    {
        var layer = _layers.LayerOf(obj) ?? CadLayerResolver.NoLayer;
        var summary = SummaryFor(layers, layer);

        switch (obj)
        {
            case Point point:
            {
                var placed = transform.OfPoint(point.Coord);
                summary.PointCount++;
                TrackHeight(summary, placed.Z);
                if (Accepts(layerFilter, layer) && sources.Contains(CadPointMath.SourcePoint))
                    points.Add(Build(placed, layer, CadPointMath.SourcePoint));
                return;
            }

            case Arc arc when IsFullCircle(arc):
            {
                var placed = transform.OfPoint(arc.Center);
                summary.CircleCount++;
                TrackHeight(summary, placed.Z);
                if (Accepts(layerFilter, layer) && sources.Contains(CadPointMath.SourceCircle))
                    points.Add(Build(placed, layer, CadPointMath.SourceCircle));
                return;
            }

            case Curve curve:
                summary.CurveCount++;
                TrackHeight(summary, SafeCurveZ(curve));
                return;

            case PolyLine polyline:
                summary.CurveCount++;
                TrackHeight(summary, SafePolylineZ(polyline));
                return;

            default:
                // Revit surfaces DWG text as a Mesh or Solid depending on the import; both are
                // counted separately so a text-only layer is obviously not a locations layer.
                if (obj is Mesh or Solid)
                    summary.TextCount++;
                else
                    summary.OtherCount++;
                return;
        }
    }

    /// <summary>An arc that closes on itself — how a DWG circle arrives in Revit.</summary>
    private static bool IsFullCircle(Arc arc)
    {
        try
        {
            if (!arc.IsBound)
                return true;
            var sweep = arc.GetEndParameter(1) - arc.GetEndParameter(0);
            return Math.Abs(Math.Abs(sweep) - 2 * Math.PI) < 1e-6;
        }
        catch { return false; }
    }

    private static CadPoint Build(XYZ placed, string layer, string source) => new()
    {
        X = ToMm(placed.X),
        Y = ToMm(placed.Y),
        Z = ToMm(placed.Z),
        Layer = layer,
        Source = source
    };

    private static bool Accepts(ISet<string>? layerFilter, string layer) =>
        layerFilter == null || layerFilter.Contains(layer);

    private static CadLayerSummary SummaryFor(Dictionary<string, CadLayerSummary> layers, string layer)
    {
        if (layers.TryGetValue(layer, out var existing))
            return existing;

        var created = new CadLayerSummary
        {
            LayerName = layer,
            MinZmm = double.MaxValue,
            MaxZmm = double.MinValue
        };
        layers[layer] = created;
        return created;
    }

    private static void TrackHeight(CadLayerSummary summary, double elevationFt)
    {
        var mm = ToMm(elevationFt);
        if (mm < summary.MinZmm) summary.MinZmm = mm;
        if (mm > summary.MaxZmm) summary.MaxZmm = mm;
    }

    private static double SafeCurveZ(Curve curve)
    {
        try { return curve.GetEndPoint(0).Z; }
        catch { return 0.0; }
    }

    private static double SafePolylineZ(PolyLine polyline)
    {
        try
        {
            var coordinates = polyline.GetCoordinates();
            return coordinates.Count > 0 ? coordinates[0].Z : 0.0;
        }
        catch { return 0.0; }
    }

    private static double ToMm(double feet) =>
        UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    public static string SafeName(Element element)
    {
        try { return element.Name ?? string.Empty; }
        catch { return string.Empty; }
    }
}

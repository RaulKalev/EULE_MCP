using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.CadManagement;

/// <summary>One reconstructed fixture, with the type, height and angle it would be placed at.</summary>
internal sealed class CadShapePlacement
{
    public CadShape Shape { get; set; } = null!;
    public FamilySymbol? Symbol { get; set; }
    public FamilyPlacementType PlacementType { get; set; }

    /// <summary>Where the type came from: <c>map</c> or <c>auto</c>.</summary>
    public string? TypeSource { get; set; }

    public double ElevationMm { get; set; }
    public double RotationDegrees { get; set; }
    public bool AlreadyPlaced { get; set; }
    public string? BlockedReason { get; set; }

    public bool WillPlace => Symbol != null && BlockedReason == null && !AlreadyPlaced;

    public object ToPayload() => new
    {
        signature = Shape.Signature,
        kind = Shape.Kind,
        layer = Shape.Layer,
        x = Math.Round(Shape.CenterX, 1),
        y = Math.Round(Shape.CenterY, 1),
        z = Math.Round(ElevationMm, 1),
        lengthMm = Math.Round(Shape.LengthMm, 1),
        widthMm = Math.Round(Shape.WidthMm, 1),
        rotationDegrees = Math.Round(RotationDegrees, 2),
        segmentCount = Shape.SegmentCount,
        familyName = Symbol?.Family?.Name,
        typeName = Symbol?.Name,
        typeId = Symbol?.Id.Value,
        typeSource = TypeSource,
        alreadyPlaced = AlreadyPlaced,
        willPlace = WillPlace,
        reason = BlockedReason
    };
}

/// <summary>
/// The full plan for placing families on fixtures reconstructed from loose CAD line work. Built once
/// and used by both the preview and the write tool so the two cannot disagree.
/// </summary>
internal sealed class CadShapePlacementPlan
{
    public ImportInstance Import { get; set; } = null!;
    public ElevationPlan Elevation { get; set; } = null!;
    public View? View { get; set; }
    public Level? Level { get; set; }
    public Element? Host { get; set; }
    public List<string> Layers { get; set; } = new();
    public List<CadShapePlacement> Placements { get; set; } = new();
    public List<CadShape> Shapes { get; set; } = new();
    public int TotalShapesFound { get; set; }
    public int OversizeCount { get; set; }

    public int WillPlaceCount => Placements.Count(p => p.WillPlace);
    public int AlreadyPlacedCount => Placements.Count(p => p.AlreadyPlaced);
    public int UnmappedCount => Placements.Count(p => p.Symbol == null);
    public int BlockedCount => Placements.Count(p => p.BlockedReason != null);

    /// <summary>Signature counts with the type each one resolved to — the table to show the user.</summary>
    public List<object> DescribeSignatures() =>
        Placements
            .GroupBy(p => p.Shape.Signature, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return (object)new
                {
                    signature = g.Key,
                    kind = first.Shape.Kind,
                    count = g.Count(),
                    averageLengthMm = Math.Round(g.Average(p => p.Shape.LengthMm), 1),
                    averageWidthMm = Math.Round(g.Average(p => p.Shape.WidthMm), 1),
                    familyName = first.Symbol?.Family?.Name,
                    typeName = first.Symbol?.Name,
                    typeId = first.Symbol?.Id.Value,
                    typeSource = first.TypeSource,
                    willPlace = g.Count(p => p.WillPlace),
                    alreadyPlaced = g.Count(p => p.AlreadyPlaced),
                    reason = first.Symbol == null ? first.BlockedReason : null
                };
            })
            .ToList();
}

internal static class CadShapePlacementPlanner
{
    public const int DefaultMaxInstances = 500;
    public const int HardMaxInstances = 2000;

    /// <summary>
    /// Builds the plan. Returns null with <paramref name="error"/> set when the request cannot be
    /// honoured at all; a fixture that could not be typed is recorded on its own placement instead,
    /// so one unmapped signature never stops the rest from being placed.
    /// </summary>
    public static CadShapePlacementPlan? Build(
        UIApplication uiapp,
        McpToolRequest request,
        List<string> warnings,
        out string? error,
        CancellationToken cancellationToken = default)
    {
        error = null;
        var uidoc = uiapp.ActiveUIDocument;
        var doc = uidoc!.Document;
        var arguments = request.Arguments;

        var import = CadPlacementRequest.ResolveImport(doc, arguments, out error);
        if (import == null)
            return null;

        var layers = CadPlacementRequest.ParseLayers(arguments);
        if (layers == null)
        {
            error = "layers is required — CAD layer names differ per project. " +
                    "Run revit_get_cad_shapes without layers, show the user which layers carry curves, " +
                    "and ask which ones hold the fixtures.";
            return null;
        }

        var elevation = CadPlacementRequest.ParseElevation(doc, arguments, out error);
        if (elevation == null)
            return null;

        var bindings = CadShapeTypeResolver.ParseTypeMap(doc, arguments, out error);
        if (bindings == null)
            return null;

        var shapes = ReadShapes(doc, import, layers, arguments, warnings, out var oversize, cancellationToken);
        if (shapes.Count == 0)
        {
            error = $"No fixture geometry was reconstructed from {string.Join(", ", layers)}. " +
                    "Run revit_get_cad_shapes without layers to see which layers carry curves.";
            return null;
        }

        Element? host = null;
        var hostElementId = ToolArguments.GetLong(arguments, "hostElementId");
        if (hostElementId > 0)
        {
            host = doc.GetElement(new ElementId(hostElementId));
            if (host == null)
            {
                error = $"Host element {hostElementId} was not found.";
                return null;
            }
        }

        var plan = new CadShapePlacementPlan
        {
            Import = import,
            Elevation = elevation,
            Level = elevation.Level,
            Host = host,
            Layers = layers.ToList(),
            Shapes = shapes,
            TotalShapesFound = shapes.Count,
            OversizeCount = oversize
        };

        var requestedMax = ToolArguments.GetInt(arguments, "maxInstances", DefaultMaxInstances);
        var maxInstances = requestedMax <= 0 ? DefaultMaxInstances : Math.Min(requestedMax, HardMaxInstances);
        if (shapes.Count > maxInstances)
        {
            warnings.Add(
                $"{shapes.Count} fixture(s) were reconstructed but maxInstances is {maxInstances}; " +
                $"{shapes.Count - maxInstances} were dropped. Raise maxInstances (up to {HardMaxInstances}) " +
                "to place them all.");
            shapes = shapes.Take(maxInstances).ToList();
        }

        var autoMatch = ToolArguments.GetBool(arguments, "autoMatchTypes", true);
        var autoTolerance = ToolArguments.GetDouble(
            arguments, "autoMatchToleranceMm", CadShapeTypeResolver.DefaultAutoMatchToleranceMm);
        var pool = autoMatch
            ? CadShapeTypeResolver.BuildAutoMatchPool(
                doc,
                ToolArguments.GetString(arguments, "autoMatchFamilyName"),
                ToolArguments.GetString(arguments, "autoMatchCategory"))
            : new List<FamilySymbol>();

        var rotationOffset = ToolArguments.GetDouble(arguments, "rotationOffsetDegrees");
        var applyRotation = ToolArguments.GetBool(arguments, "applyShapeRotation", true);

        // Auto-matching is per signature, not per fixture: every 1200x200 rectangle in the drawing is
        // the same luminaire, and resolving each one separately would only invite them to disagree.
        var autoResolved = new Dictionary<string, CadShapeTypeBinding?>(StringComparer.OrdinalIgnoreCase);
        var autoReasons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var shape in shapes)
        {
            var placement = new CadShapePlacement
            {
                Shape = shape,
                RotationDegrees = CadPointMath.Normalize(
                    (applyRotation ? shape.RotationDegrees : 0.0) + rotationOffset)
            };

            var binding = ResolveBinding(shape, bindings, pool, autoMatch, autoTolerance, autoResolved, autoReasons);
            if (binding != null)
            {
                placement.Symbol = binding.Symbol;
                placement.TypeSource = binding.Source;
                try { placement.PlacementType = binding.Symbol.Family.FamilyPlacementType; }
                catch { placement.PlacementType = FamilyPlacementType.OneLevelBased; }
            }
            else
            {
                placement.BlockedReason =
                    $"no family type for signature '{shape.Signature}' — " +
                    autoReasons.GetValueOrDefault(shape.Signature, "map it in typeMap");
            }

            if (shape.Oversize)
            {
                placement.BlockedReason =
                    $"{shape.LengthMm:F0} mm across — too large for a fixture, so this is drawing line " +
                    "work that touches a symbol rather than a symbol. Narrow the layers, or lower " +
                    "joinToleranceMm so it stops being pulled into one cluster.";
            }

            if (elevation.TryResolve(shape.Zmm, out var elevationMm, out var elevationError))
                placement.ElevationMm = elevationMm;
            else
                placement.BlockedReason ??= elevationError;

            plan.Placements.Add(placement);
        }

        MarkAlreadyPlaced(doc, plan, arguments, warnings);
        ResolveViews(uidoc, doc, plan, arguments, warnings);

        if (plan.UnmappedCount > 0)
        {
            warnings.Add(
                $"{plan.UnmappedCount} fixture(s) have no family type and will be skipped. " +
                "Add their signatures to typeMap — revit_get_cad_shapes lists every signature found.");
        }

        return plan;
    }

    /// <summary>Reads the line work and reassembles it into fixtures.</summary>
    private static List<CadShape> ReadShapes(
        Document doc,
        ImportInstance import,
        ISet<string> layers,
        Dictionary<string, object?> arguments,
        List<string> warnings,
        out int oversizeCount,
        CancellationToken cancellationToken)
    {
        var extractor = new CadCurveExtractor(doc);
        var segments = extractor.Extract(import, layers, cancellationToken);
        warnings.AddRange(extractor.Warnings);

        var shapes = CadShapeMath.Cluster(
            segments,
            ToolArguments.GetDouble(arguments, "joinToleranceMm", CadShapeMath.DefaultJoinToleranceMm),
            ToolArguments.GetDouble(arguments, "signatureBucketMm", CadShapeMath.DefaultSignatureBucketMm),
            ToolArguments.GetDouble(arguments, "maxShapeSizeMm", CadShapeMath.DefaultMaxShapeSizeMm));

        oversizeCount = shapes.Count(s => s.Oversize);
        if (oversizeCount > 0)
        {
            warnings.Add(
                $"{oversizeCount} cluster(s) came out larger than a fixture should be and are skipped. " +
                "That is what a drawing line touching a symbol looks like — check the layers.");
        }

        return shapes;
    }

    /// <summary>The caller's map first; the footprint fallback only for signatures it leaves out.</summary>
    private static CadShapeTypeBinding? ResolveBinding(
        CadShape shape,
        Dictionary<string, CadShapeTypeBinding> bindings,
        IReadOnlyList<FamilySymbol> pool,
        bool autoMatch,
        double toleranceMm,
        Dictionary<string, CadShapeTypeBinding?> autoResolved,
        Dictionary<string, string> autoReasons)
    {
        if (bindings.TryGetValue(shape.Signature, out var mapped))
            return mapped;

        if (!autoMatch)
        {
            autoReasons[shape.Signature] = "map it in typeMap (autoMatchTypes is off)";
            return null;
        }

        if (autoResolved.TryGetValue(shape.Signature, out var cached))
            return cached;

        var binding = CadShapeTypeResolver.AutoMatch(shape, pool, toleranceMm, out var reason);
        autoResolved[shape.Signature] = binding;
        if (binding == null && reason != null)
            autoReasons[shape.Signature] = reason;

        return binding;
    }

    /// <summary>
    /// Flags fixtures that already carry an instance, so re-running after a tweak tops the model up
    /// instead of stacking a second luminaire on every ceiling. Checked per type, because two
    /// signatures placing different families must not shadow each other.
    /// </summary>
    private static void MarkAlreadyPlaced(
        Document doc,
        CadShapePlacementPlan plan,
        Dictionary<string, object?> arguments,
        List<string> warnings)
    {
        if (!ToolArguments.GetBool(arguments, "skipExisting", true))
            return;

        var tolerance = ToolArguments.GetDouble(
            arguments, "duplicateToleranceMm", CadPlacementRequest.DefaultDuplicateToleranceMm);
        if (tolerance <= 0)
            return;

        var existingByType = ExistingInstancesByType(doc);

        foreach (var group in plan.Placements
                     .Where(p => p.Symbol != null)
                     .GroupBy(p => p.Symbol!.Id.Value))
        {
            if (!existingByType.TryGetValue(group.Key, out var existing) || existing.Count == 0)
                continue;

            var candidates = group
                .Select(p => new CadPoint { X = p.Shape.CenterX, Y = p.Shape.CenterY })
                .ToList();

            var flags = CadPointMath.MarkExisting(candidates, existing, tolerance);

            var index = 0;
            foreach (var placement in group)
                placement.AlreadyPlaced = flags[index++];
        }

        var alreadyPlaced = plan.AlreadyPlacedCount;
        if (alreadyPlaced > 0)
        {
            warnings.Add(
                $"{alreadyPlaced} fixture(s) already have an instance of their type within " +
                $"{tolerance:F0} mm and are skipped. Set skipExisting=false to place anyway.");
        }
    }

    /// <summary>Where instances of each type already stand, in millimetres, for duplicate detection.</summary>
    private static Dictionary<long, List<CadPoint>> ExistingInstancesByType(Document doc)
    {
        var existing = new Dictionary<long, List<CadPoint>>();

        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(FamilyInstance))
                     .WhereElementIsNotElementType())
        {
            if (element is not FamilyInstance instance)
                continue;

            try
            {
                var typeId = instance.Symbol?.Id.Value;
                if (typeId == null)
                    continue;
                if (instance.Location is not LocationPoint location)
                    continue;

                if (!existing.TryGetValue(typeId.Value, out var bucket))
                {
                    bucket = new List<CadPoint>();
                    existing[typeId.Value] = bucket;
                }

                bucket.Add(new CadPoint
                {
                    X = CadPlacementRequest.FtToMm(location.Point.X),
                    Y = CadPlacementRequest.FtToMm(location.Point.Y),
                    Z = CadPlacementRequest.FtToMm(location.Point.Z)
                });
            }
            catch { }
        }

        return existing;
    }

    /// <summary>
    /// A view is only needed when one of the resolved families is view-based. Resolving it up front
    /// for every plan would refuse perfectly good requests that never needed a view.
    /// </summary>
    private static void ResolveViews(
        UIDocument uidoc,
        Document doc,
        CadShapePlacementPlan plan,
        Dictionary<string, object?> arguments,
        List<string> warnings)
    {
        if (!plan.Placements.Any(p => p.WillPlace && p.PlacementType == FamilyPlacementType.ViewBased))
            return;

        var (view, viewError) = PlacementHelpers.ResolveGraphicalView(
            uidoc, doc, ToolArguments.GetLong(arguments, "viewId"));

        if (view == null)
        {
            foreach (var placement in plan.Placements
                         .Where(p => p.PlacementType == FamilyPlacementType.ViewBased))
                placement.BlockedReason ??= viewError;

            warnings.Add($"View-based families need a view: {viewError}");
            return;
        }

        plan.View = view;
    }
}

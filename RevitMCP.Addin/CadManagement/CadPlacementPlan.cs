using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.CadManagement;

/// <summary>One location to place at, with everything already resolved.</summary>
internal sealed class CadPlacement
{
    public CadPoint Point { get; set; } = null!;
    public double ElevationMm { get; set; }
    public double RotationDegrees { get; set; }
    public bool AlreadyPlaced { get; set; }
    public string? BlockedReason { get; set; }

    public bool WillPlace => BlockedReason == null && !AlreadyPlaced;

    public object ToPayload() => new
    {
        x = Math.Round(Point.X, 1),
        y = Math.Round(Point.Y, 1),
        z = Math.Round(ElevationMm, 1),
        layer = Point.Layer,
        source = Point.Source,
        rotationDegrees = Math.Round(RotationDegrees, 2),
        alreadyPlaced = AlreadyPlaced,
        willPlace = WillPlace,
        reason = BlockedReason
    };
}

/// <summary>
/// The full plan for placing a family at every location a DWG marks: the import, the layers, the
/// family, the elevation, and one entry per point. Built once and used by both the preview and the
/// write tool so the two cannot disagree.
/// </summary>
internal sealed class CadPlacementPlan
{
    public ImportInstance Import { get; set; } = null!;
    public FamilySymbol Symbol { get; set; } = null!;
    public FamilyPlacementType PlacementType { get; set; }
    public ElevationPlan Elevation { get; set; } = null!;
    public View? View { get; set; }
    public Level? Level { get; set; }
    public Element? Host { get; set; }
    public List<string> Layers { get; set; } = new();
    public List<CadPlacement> Placements { get; set; } = new();
    public int TotalPointsFound { get; set; }

    public int WillPlaceCount => Placements.Count(p => p.WillPlace);
    public int AlreadyPlacedCount => Placements.Count(p => p.AlreadyPlaced);
    public int BlockedCount => Placements.Count(p => p.BlockedReason != null);
}

internal static class CadPlacementPlanner
{
    /// <summary>
    /// Builds the plan. Returns null with <paramref name="error"/> set when the request cannot be
    /// honoured; per-point problems are recorded on the individual placements instead.
    /// </summary>
    public static CadPlacementPlan? Build(
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
                    "Run revit_get_cad_placement_points without layers, show the user which layers " +
                    "carry blocks, points or circles, and ask which ones hold the locations.";
            return null;
        }

        var sources = CadPlacementRequest.ParseSources(arguments, out error);
        if (sources == null)
            return null;

        var elevation = CadPlacementRequest.ParseElevation(doc, arguments, out error);
        if (elevation == null)
            return null;

        var (symbol, symbolError) = FamilyInstancePlacer.ResolveSymbol(
            doc,
            ToolArguments.GetLong(arguments, "typeId"),
            ToolArguments.GetString(arguments, "familyName"),
            ToolArguments.GetString(arguments, "typeName"));
        if (symbol == null)
        {
            error = symbolError!;
            return null;
        }

        var placementType = symbol.Family.FamilyPlacementType;

        View? view = null;
        if (placementType == FamilyPlacementType.ViewBased)
        {
            var (resolvedView, viewError) = PlacementHelpers.ResolveGraphicalView(
                uidoc, doc, ToolArguments.GetLong(arguments, "viewId"));
            if (resolvedView == null)
            {
                error = viewError!;
                return null;
            }
            view = resolvedView;
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

        var mergeTolerance = CadPlacementRequest.ParseMergeTolerance(arguments, warnings);
        var extractor = new CadPointExtractor(doc);
        var (raw, layerSummaries) = extractor.Extract(import, layers, sources, cancellationToken);
        warnings.AddRange(extractor.Warnings);

        var known = new HashSet<string>(
            layerSummaries.Select(l => l.LayerName), StringComparer.OrdinalIgnoreCase);
        foreach (var requested in layers.Where(l => !known.Contains(l)))
            warnings.Add($"Layer '{requested}' does not exist in this CAD file.");

        var points = CadPointMath.Merge(raw, mergeTolerance);
        if (points.Count == 0)
        {
            error = $"No block inserts, points or circles were found on {string.Join(", ", layers)}. " +
                    "Run revit_get_cad_placement_points without layers to see what each layer holds.";
            return null;
        }

        if (raw.Count > points.Count)
        {
            warnings.Add(
                $"{raw.Count - points.Count} duplicate mark(s) within {mergeTolerance:F1} mm were merged.");
        }

        var plan = new CadPlacementPlan
        {
            Import = import,
            Symbol = symbol,
            PlacementType = placementType,
            Elevation = elevation,
            View = view,
            Level = elevation.Level,
            Host = host,
            Layers = layers.ToList(),
            TotalPointsFound = points.Count
        };

        var requestedMax = ToolArguments.GetInt(arguments, "maxInstances", CadPlacementRequest.DefaultMaxInstances);
        var maxInstances = requestedMax <= 0
            ? CadPlacementRequest.DefaultMaxInstances
            : Math.Min(requestedMax, CadPlacementRequest.HardMaxInstances);
        if (points.Count > maxInstances)
        {
            warnings.Add(
                $"{points.Count} locations were found but maxInstances is {maxInstances}; " +
                $"{points.Count - maxInstances} were dropped. Raise maxInstances " +
                $"(up to {CadPlacementRequest.HardMaxInstances}) to place them all.");
            points = points.Take(maxInstances).ToList();
        }

        var skipExisting = ToolArguments.GetBool(arguments, "skipExisting", true);
        var duplicateTolerance = ToolArguments.GetDouble(
            arguments, "duplicateToleranceMm", CadPlacementRequest.DefaultDuplicateToleranceMm);
        var existingFlags = skipExisting
            ? CadPointMath.MarkExisting(points, ExistingInstances(doc, symbol), duplicateTolerance)
            : points.Select(_ => false).ToList();

        var applyBlockRotation = ToolArguments.GetBool(arguments, "applyBlockRotation", true);
        var rotationOffset = ToolArguments.GetDouble(arguments, "rotationOffsetDegrees");

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var placement = new CadPlacement
            {
                Point = point,
                AlreadyPlaced = existingFlags[index],
                RotationDegrees = CadPointMath.Normalize(
                    (applyBlockRotation ? point.RotationDegrees : 0.0) + rotationOffset)
            };

            if (elevation.TryResolve(point.Z, out var elevationMm, out var elevationError))
                placement.ElevationMm = elevationMm;
            else
                placement.BlockedReason = elevationError;

            plan.Placements.Add(placement);
        }

        var alreadyPlaced = plan.AlreadyPlacedCount;
        if (alreadyPlaced > 0)
        {
            warnings.Add(
                $"{alreadyPlaced} location(s) already have a '{symbol.Family.Name} : {symbol.Name}' " +
                $"within {duplicateTolerance:F0} mm and are skipped. Set skipExisting=false to place anyway.");
        }

        return plan;
    }

    /// <summary>Where instances of this type already stand, in millimetres, for duplicate detection.</summary>
    private static List<CadPoint> ExistingInstances(Document doc, FamilySymbol symbol)
    {
        var existing = new List<CadPoint>();
        foreach (var element in new FilteredElementCollector(doc)
                     .OfClass(typeof(FamilyInstance))
                     .WhereElementIsNotElementType())
        {
            if (element is not FamilyInstance instance)
                continue;

            try
            {
                if (instance.Symbol?.Id != symbol.Id)
                    continue;
                if (instance.Location is not LocationPoint location)
                    continue;

                existing.Add(new CadPoint
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
}

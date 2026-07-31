using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// The discovery half of placing items from a DWG: which layers hold locations, and where those
/// locations are. Layer names differ per project, so this tool exists to be run first and shown to
/// the user before anything is placed.
/// </summary>
public class GetCadPlacementPointsTool : IRevitMcpTool
{
    private const int DefaultLimit = 200;
    private const int HardLimit = 2000;

    public string Name => "revit_get_cad_placement_points";

    public string Description =>
        "Reads candidate placement locations out of an imported or linked DWG. Call it WITHOUT layers " +
        "first: it returns every CAD layer with a count of block inserts, points, circles, curves and " +
        "text, plus the height range each layer sits at — that is what to show the user when asking " +
        "which layers hold the locations and whether a mounting height is needed. Call it again with " +
        "layers to get the actual points, in millimetres, with the rotation carried over from block " +
        "references. Optional: importInstanceId (required when several CAD files are present), " +
        "pointSources (block|point|circle), mergeToleranceMm (default 1), limit (default 200). Read-only.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var warnings = new List<string>();

        var import = CadPlacementRequest.ResolveImport(doc, request.Arguments, out var error);
        if (import == null)
            return Task.FromResult(Fail(request, error!));

        var sources = CadPlacementRequest.ParseSources(request.Arguments, out error);
        if (sources == null)
            return Task.FromResult(Fail(request, error!));

        var layers = CadPlacementRequest.ParseLayers(request.Arguments);
        var mergeTolerance = CadPlacementRequest.ParseMergeTolerance(request.Arguments, warnings);

        var requestedLimit = ToolArguments.GetInt(request.Arguments, "limit", DefaultLimit);
        var limit = requestedLimit <= 0 ? DefaultLimit : Math.Min(requestedLimit, HardLimit);
        if (requestedLimit > HardLimit)
            warnings.Add($"limit {requestedLimit} exceeds the hard cap; using {HardLimit}.");

        var extractor = new CadPointExtractor(doc);
        var (raw, layerSummaries) = extractor.Extract(import, layers, sources, cancellationToken);
        warnings.AddRange(extractor.Warnings);

        if (layers != null)
        {
            var known = new HashSet<string>(
                layerSummaries.Select(l => l.LayerName), StringComparer.OrdinalIgnoreCase);
            foreach (var requested in layers.Where(l => !known.Contains(l)))
                warnings.Add($"Layer '{requested}' does not exist in this CAD file.");
        }

        var merged = CadPointMath.Merge(raw, mergeTolerance);
        var mergedAway = raw.Count - merged.Count;
        if (mergedAway > 0)
        {
            warnings.Add(
                $"{mergedAway} duplicate mark(s) within {mergeTolerance:F1} mm were merged — a symbol " +
                "drawn as a block around a circle yields one point, not two.");
        }

        var returned = merged.Take(limit).ToList();
        if (merged.Count > returned.Count)
        {
            warnings.Add(
                $"Returning {returned.Count} of {merged.Count} point(s). Raise limit to see the rest, " +
                "or place them directly with revit_place_from_cad, which reads all of them itself.");
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = layers == null
                ? $"Found {layerSummaries.Count} CAD layer(s) in '{CadPointExtractor.SafeName(import)}'. " +
                  "Pass layers to read their points."
                : $"Found {merged.Count} placement point(s) on {layers.Count} layer(s).",
            Data = new
            {
                importInstanceId = import.Id.Value,
                importName = CadPointExtractor.SafeName(import),
                layersRequested = layers?.ToList(),
                layers = layerSummaries.Select(Describe).ToList(),
                pointCount = merged.Count,
                returnedCount = returned.Count,
                elevation = layers == null ? null : DescribeElevation(merged),
                points = layers == null
                    ? null
                    : returned.Select(p => new
                    {
                        x = Math.Round(p.X, 1),
                        y = Math.Round(p.Y, 1),
                        z = Math.Round(p.Z, 1),
                        layer = p.Layer,
                        source = p.Source,
                        rotationDegrees = Math.Round(p.RotationDegrees, 2),
                        mergedCount = p.MergedCount
                    }).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static object Describe(CadLayerSummary layer) => new
    {
        layerName = layer.LayerName,
        placeable = layer.HasPlaceablePoints,
        blockCount = layer.BlockCount,
        pointCount = layer.PointCount,
        circleCount = layer.CircleCount,
        curveCount = layer.CurveCount,
        textCount = layer.TextCount,
        otherCount = layer.OtherCount,
        minZmm = layer.MaxZmm < layer.MinZmm ? (double?)null : Math.Round(layer.MinZmm, 1),
        maxZmm = layer.MaxZmm < layer.MinZmm ? (double?)null : Math.Round(layer.MaxZmm, 1)
    };

    /// <summary>Tells the caller whether the drawing carries usable heights, or whether to ask for one.</summary>
    private static object DescribeElevation(IReadOnlyList<CadPoint> points)
    {
        var flat = CadPointMath.IsFlat(points);
        return new
        {
            isFlat = flat,
            minZmm = points.Count == 0 ? 0 : Math.Round(points.Min(p => p.Z), 1),
            maxZmm = points.Count == 0 ? 0 : Math.Round(points.Max(p => p.Z), 1),
            note = flat
                ? "Every point sits at the same height, so the drawing carries no mounting height. " +
                  "Ask the user for one and pass elevationMode=level with levelName and offsetMm, " +
                  "or elevationMode=explicit with elevationMm."
                : "The points carry differing heights; elevationMode=dwg keeps them."
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

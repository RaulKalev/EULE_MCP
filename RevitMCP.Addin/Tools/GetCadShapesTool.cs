using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// The discovery half of placing families on symbols a DWG never blocked.
///
/// Where <see cref="GetCadPlacementPointsTool"/> looks for block inserts, points and circles, this
/// one reassembles loose line work into fixtures and reports what sizes the drawing contains. The
/// signature table it returns is what the caller maps to family types.
/// </summary>
public class GetCadShapesTool : IRevitMcpTool
{
    private const int DefaultLimit = 200;
    private const int HardLimit = 2000;

    public string Name => "revit_get_cad_shapes";

    public string Description =>
        "Reconstructs fixtures from loose CAD line work — symbols drawn as bare lines, rectangles or " +
        "circles that were never made into blocks, so revit_get_cad_placement_points finds nothing on " +
        "them. Touching segments are grouped into one fixture, and the smallest box around each group " +
        "gives its centre and the angle it was drawn at. Call WITHOUT layers first for the layer " +
        "inventory, then again with layers to get the fixtures plus a signature table (e.g. " +
        "'rectangle 1200x200' x28) — those signatures are what revit_place_from_cad_shapes maps to " +
        "family types. Note the Revit API cannot read DWG text, so the drawing's type marks (V11.1 " +
        "and the like) are not available; shapes are identified by size, not by label. Optional: " +
        "importInstanceId, joinToleranceMm (default 2), signatureBucketMm (default 10), " +
        "maxShapeSizeMm (default 3000), limit (default 200). Read-only.";

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

        var layers = CadPlacementRequest.ParseLayers(request.Arguments);

        // Without layers there is nothing to reconstruct yet — the caller needs the inventory first,
        // and reading every curve in an architectural background to produce it would be pointless.
        if (layers == null)
        {
            var inventory = new CadPointExtractor(doc);
            var (_, layerSummaries) = inventory.Extract(
                import, null, new HashSet<string>(), cancellationToken);
            warnings.AddRange(inventory.Warnings);

            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Found {layerSummaries.Count} CAD layer(s) in " +
                          $"'{CadPointExtractor.SafeName(import)}'. Pass layers to reconstruct their fixtures.",
                Data = new
                {
                    importInstanceId = import.Id.Value,
                    importName = CadPointExtractor.SafeName(import),
                    layers = layerSummaries.Select(l => new
                    {
                        layerName = l.LayerName,
                        curveCount = l.CurveCount,
                        blockCount = l.BlockCount,
                        circleCount = l.CircleCount,
                        pointCount = l.PointCount,
                        textCount = l.TextCount,
                        // A layer of pure line work is exactly the case this tool exists for; a layer
                        // that already carries blocks is better served by the point-based tools.
                        worthReconstructing = l.CurveCount > 0 && !l.HasPlaceablePoints
                    }).ToList(),
                    note = "Layers with curveCount > 0 and no blocks or circles are the ones drawn as " +
                           "loose geometry. Layers that do carry blocks, points or circles are better " +
                           "placed with revit_place_from_cad."
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var requestedLimit = ToolArguments.GetInt(request.Arguments, "limit", DefaultLimit);
        var limit = requestedLimit <= 0 ? DefaultLimit : Math.Min(requestedLimit, HardLimit);
        if (requestedLimit > HardLimit)
            warnings.Add($"limit {requestedLimit} exceeds the hard cap; using {HardLimit}.");

        var extractor = new CadCurveExtractor(doc);
        var segments = extractor.Extract(import, layers, cancellationToken);
        warnings.AddRange(extractor.Warnings);

        if (segments.Count == 0)
        {
            warnings.Add(
                $"No curves were found on {string.Join(", ", layers)}. Run this tool without layers " +
                "to see which layers carry line work.");
        }

        var joinTolerance = ToolArguments.GetDouble(
            request.Arguments, "joinToleranceMm", CadShapeMath.DefaultJoinToleranceMm);
        var bucket = ToolArguments.GetDouble(
            request.Arguments, "signatureBucketMm", CadShapeMath.DefaultSignatureBucketMm);
        var maxShapeSize = ToolArguments.GetDouble(
            request.Arguments, "maxShapeSizeMm", CadShapeMath.DefaultMaxShapeSizeMm);

        var shapes = CadShapeMath.Cluster(segments, joinTolerance, bucket, maxShapeSize);
        var oversize = shapes.Count(s => s.Oversize);
        if (oversize > 0)
        {
            warnings.Add(
                $"{oversize} cluster(s) are larger than maxShapeSizeMm ({maxShapeSize:F0} mm) and are " +
                "flagged oversize — that is a drawing line touching a symbol, not a fixture. Lower " +
                "joinToleranceMm or narrow the layers.");
        }

        var returned = shapes.Take(limit).ToList();
        if (shapes.Count > returned.Count)
        {
            warnings.Add(
                $"Returning {returned.Count} of {shapes.Count} fixture(s). Raise limit to see the rest, " +
                "or place them directly with revit_place_from_cad_shapes, which reads all of them itself.");
        }

        var flat = shapes.Count > 0 &&
                   shapes.Max(s => s.Zmm) - shapes.Min(s => s.Zmm) <= 1.0;

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Reconstructed {shapes.Count} fixture(s) from {segments.Count} segment(s) on " +
                      $"{layers.Count} layer(s).",
            Data = new
            {
                importInstanceId = import.Id.Value,
                importName = CadPointExtractor.SafeName(import),
                layersRequested = layers.ToList(),
                segmentCount = segments.Count,
                shapeCount = shapes.Count,
                returnedCount = returned.Count,
                oversizeCount = oversize,
                joinToleranceMm = joinTolerance,
                signatureBucketMm = bucket,
                signatures = CadShapeMath.SummariseSignatures(shapes).Select(s => new
                {
                    signature = s.Signature,
                    kind = s.Kind,
                    count = s.Count,
                    averageLengthMm = Math.Round(s.LengthMm, 1),
                    averageWidthMm = Math.Round(s.WidthMm, 1)
                }).ToList(),
                elevation = new
                {
                    isFlat = flat,
                    minZmm = shapes.Count == 0 ? 0 : Math.Round(shapes.Min(s => s.Zmm), 1),
                    maxZmm = shapes.Count == 0 ? 0 : Math.Round(shapes.Max(s => s.Zmm), 1),
                    note = flat
                        ? "Every fixture sits at the same height, so the drawing carries no mounting " +
                          "height. Ask the user for one and pass elevationMode=level with levelName and " +
                          "offsetMm, or elevationMode=explicit with elevationMm."
                        : "The fixtures carry differing heights; elevationMode=dwg keeps them."
                },
                shapes = returned.Select(s => new
                {
                    signature = s.Signature,
                    kind = s.Kind,
                    layer = s.Layer,
                    x = Math.Round(s.CenterX, 1),
                    y = Math.Round(s.CenterY, 1),
                    z = Math.Round(s.Zmm, 1),
                    lengthMm = Math.Round(s.LengthMm, 1),
                    widthMm = Math.Round(s.WidthMm, 1),
                    rotationDegrees = Math.Round(s.RotationDegrees, 2),
                    segmentCount = s.SegmentCount,
                    oversize = s.Oversize
                }).ToList(),
                note = "Map each signature to a family type with typeMap on " +
                       "revit_preview_place_from_cad_shapes, e.g. " +
                       "[{\"signature\": \"rectangle 1200x200\", \"familyName\": \"POS-11-1\"}]."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

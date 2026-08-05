using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Places families on fixtures reconstructed from loose CAD line work — the symbols an electrical
/// drawing carries as bare lines rather than blocks.
/// </summary>
public class PlaceFromCadShapesTool : IRevitMcpTool
{
    /// <summary>How many created instances to list individually before the response is just noise.</summary>
    private const int MaxReportedInstances = 50;

    public string Name => "revit_place_from_cad_shapes";

    public string Description =>
        "Places families on fixtures reconstructed from loose CAD line work — symbols drawn as bare " +
        "lines or circles that were never made into blocks. Touching segments are grouped into one " +
        "fixture; the smallest box around the group gives the insertion point and the rotation. " +
        "Requires approval. Required: layers (names differ per project, so ask the user which ones " +
        "hold the fixtures) and elevationMode ('dwg' keeps the drawing height, 'level' takes " +
        "levelName plus offsetMm, 'explicit' takes an absolute elevationMm — a 2D drawing carries no " +
        "mounting height, so ask for one). Types come from typeMap, an array of " +
        "{signature, typeId | familyName/typeName}; signatures not mapped fall back to matching the " +
        "family's own footprint when autoMatchTypes is on, and are skipped when nothing fits " +
        "unambiguously. The Revit API cannot read DWG text, so the drawing's type marks (V11.1 and " +
        "the like) cannot be used — fixtures are identified by size. Optional: importInstanceId, " +
        "joinToleranceMm (default 2), signatureBucketMm (default 10), maxShapeSizeMm (default 3000), " +
        "autoMatchFamilyName, autoMatchCategory, autoMatchToleranceMm (default 50), " +
        "applyShapeRotation (default true), rotationOffsetDegrees, skipExisting (default true), " +
        "duplicateToleranceMm, maxInstances (default 500), viewId, hostElementId. Run " +
        "revit_preview_place_from_cad_shapes first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;
        var warnings = new List<string>();

        var plan = CadShapePlacementPlanner.Build(uiapp, request, warnings, out var error, cancellationToken);
        if (plan == null)
            return Task.FromResult(Fail(request, error!));

        if (plan.WillPlaceCount == 0)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = Nothing(plan),
                Data = new
                {
                    createdCount = 0,
                    shapesFound = plan.TotalShapesFound,
                    alreadyPlaced = plan.AlreadyPlacedCount,
                    unmapped = plan.UnmappedCount,
                    oversize = plan.OversizeCount,
                    signatures = plan.DescribeSignatures()
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var created = new List<object>();
        var errors = new List<string>();
        var placed = 0;

        cancellationToken.ThrowIfCancellationRequested();
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Place From CAD Shapes", () =>
        {
            // Activating a symbol is a model change, so it belongs inside the transaction. Each type
            // is activated once, however many fixtures use it.
            var activated = new HashSet<long>();

            foreach (var placement in plan.Placements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!placement.WillPlace || placement.Symbol == null)
                    continue;

                var symbol = placement.Symbol;
                if (activated.Add(symbol.Id.Value) && !symbol.IsActive)
                    symbol.Activate();

                var point = PlacementHelpers.PointFromMm(
                    placement.Shape.CenterX, placement.Shape.CenterY, placement.ElevationMm);

                try
                {
                    var instance = FamilyInstancePlacer.CreateInstance(
                        doc, uidoc, symbol, placement.PlacementType, point, plan.View, plan.Level, plan.Host);

                    if (Math.Abs(placement.RotationDegrees) > 1e-9)
                    {
                        var axisDirection = plan.View != null ? plan.View.ViewDirection : XYZ.BasisZ;
                        var axis = Line.CreateBound(point, point + axisDirection);
                        ElementTransformUtils.RotateElement(
                            doc, instance.Id, axis, placement.RotationDegrees * Math.PI / 180.0);
                    }

                    placed++;
                    if (created.Count < MaxReportedInstances)
                    {
                        created.Add(new
                        {
                            elementId = instance.Id.Value,
                            signature = placement.Shape.Signature,
                            familyName = symbol.Family?.Name,
                            typeName = symbol.Name,
                            typeSource = placement.TypeSource,
                            x = Math.Round(placement.Shape.CenterX, 1),
                            y = Math.Round(placement.Shape.CenterY, 1),
                            z = Math.Round(placement.ElevationMm, 1),
                            layer = placement.Shape.Layer,
                            rotationDegrees = Math.Round(placement.RotationDegrees, 2)
                        });
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(
                        $"{placement.Shape.Signature} at " +
                        $"({placement.Shape.CenterX:F0}, {placement.Shape.CenterY:F0}): {ex.Message}");
                }
            }
        });

        sw.Stop();

        if (!txSuccess)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed — no instances were placed.",
                Errors = errors,
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        if (created.Count < placed)
            warnings.Add($"Listing the first {created.Count} of {placed} created instance(s).");

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = placed > 0,
            Message = $"Placed {placed} of {plan.WillPlaceCount} instance(s) on fixtures reconstructed " +
                      $"from {CadPointExtractor.SafeName(plan.Import)} " +
                      $"({plan.AlreadyPlacedCount} fixture(s) already had one, " +
                      $"{plan.UnmappedCount} had no type).",
            Errors = errors,
            Data = new
            {
                importInstanceId = plan.Import.Id.Value,
                importName = CadPointExtractor.SafeName(plan.Import),
                layers = plan.Layers,
                levelName = plan.Level?.Name,
                elevationMode = plan.Elevation.Mode,
                elevationDescription = plan.Elevation.Describe(),
                shapesFound = plan.TotalShapesFound,
                createdCount = placed,
                alreadyPlaced = plan.AlreadyPlacedCount,
                unmapped = plan.UnmappedCount,
                oversize = plan.OversizeCount,
                failed = plan.WillPlaceCount - placed,
                signatures = plan.DescribeSignatures(),
                created
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>Says which of the three reasons left nothing to do, rather than a bare "nothing to place".</summary>
    private static string Nothing(CadShapePlacementPlan plan)
    {
        if (plan.UnmappedCount == plan.Placements.Count && plan.UnmappedCount > 0)
            return $"No family type resolved for any of the {plan.UnmappedCount} fixture(s) found. " +
                   "Map their signatures in typeMap — revit_get_cad_shapes lists every signature.";

        if (plan.AlreadyPlacedCount > 0)
            return $"Nothing to do — all {plan.AlreadyPlacedCount} fixture(s) already have an instance.";

        return "Nothing to place.";
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PlaceFromCadTool : IRevitMcpTool
{
    /// <summary>How many created instances to list individually before the response is just noise.</summary>
    private const int MaxReportedInstances = 50;

    public string Name => "revit_place_from_cad";

    public string Description =>
        "Places a family at every location an imported DWG marks — block inserts, points, or circles " +
        "on the layers you name. Requires approval. Required: layers (CAD layer names differ per " +
        "project, so ask the user which ones hold the locations), a family (typeId, or " +
        "familyName/typeName), and elevationMode: 'dwg' keeps the drawing's height, 'level' takes " +
        "levelName plus offsetMm, 'explicit' takes an absolute elevationMm — a 2D drawing carries no " +
        "mounting height, so ask for one. Optional: importInstanceId, pointSources (block|point|circle), " +
        "mergeToleranceMm, applyBlockRotation (default true), rotationOffsetDegrees, skipExisting " +
        "(default true, so re-running tops up instead of doubling), duplicateToleranceMm, maxInstances " +
        "(default 500), viewId, hostElementId. Run revit_preview_place_from_cad first.";

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

        var plan = CadPlacementPlanner.Build(uiapp, request, warnings, out var error, cancellationToken);
        if (plan == null)
            return Task.FromResult(Fail(request, error!));

        if (plan.WillPlaceCount == 0)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = plan.AlreadyPlacedCount > 0
                    ? $"Nothing to do — all {plan.AlreadyPlacedCount} location(s) already have a " +
                      $"{plan.Symbol.Family.Name} : {plan.Symbol.Name}."
                    : "Nothing to place.",
                Data = new
                {
                    createdCount = 0,
                    alreadyPlaced = plan.AlreadyPlacedCount,
                    blocked = plan.BlockedCount
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var created = new List<object>();
        var errors = new List<string>();
        var placed = 0;

        cancellationToken.ThrowIfCancellationRequested();
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Place From CAD", () =>
        {
            if (!plan.Symbol.IsActive)
                plan.Symbol.Activate();

            foreach (var placement in plan.Placements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!placement.WillPlace)
                    continue;

                var point = PlacementHelpers.PointFromMm(
                    placement.Point.X, placement.Point.Y, placement.ElevationMm);

                try
                {
                    var instance = FamilyInstancePlacer.CreateInstance(
                        doc, uidoc, plan.Symbol, plan.PlacementType, point, plan.View, plan.Level, plan.Host);

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
                            x = Math.Round(placement.Point.X, 1),
                            y = Math.Round(placement.Point.Y, 1),
                            z = Math.Round(placement.ElevationMm, 1),
                            layer = placement.Point.Layer,
                            rotationDegrees = Math.Round(placement.RotationDegrees, 2)
                        });
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"{placement.Point.Layer} at ({placement.Point.X:F0}, {placement.Point.Y:F0}): {ex.Message}");
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
            Message = $"Placed {placed} of {plan.WillPlaceCount} instance(s) of " +
                      $"{plan.Symbol.Family.Name} : {plan.Symbol.Name} from " +
                      $"{CadPointExtractor.SafeName(plan.Import)} " +
                      $"({plan.AlreadyPlacedCount} location(s) already had one).",
            Errors = errors,
            Data = new
            {
                importInstanceId = plan.Import.Id.Value,
                importName = CadPointExtractor.SafeName(plan.Import),
                layers = plan.Layers,
                familyName = plan.Symbol.Family.Name,
                typeName = plan.Symbol.Name,
                typeId = plan.Symbol.Id.Value,
                placementType = plan.PlacementType.ToString(),
                levelName = plan.Level?.Name,
                elevationMode = plan.Elevation.Mode,
                elevationDescription = plan.Elevation.Describe(),
                locationsFound = plan.TotalPointsFound,
                createdCount = placed,
                alreadyPlaced = plan.AlreadyPlacedCount,
                failed = plan.WillPlaceCount - placed,
                created
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

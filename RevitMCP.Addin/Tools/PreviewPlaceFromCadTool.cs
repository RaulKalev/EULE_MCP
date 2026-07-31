using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewPlaceFromCadTool : IRevitMcpTool
{
    /// <summary>How many individual locations to echo back before the list is just noise.</summary>
    private const int MaxSamplePoints = 25;

    public string Name => "revit_preview_place_from_cad";

    public string Description =>
        "Previews placing a family at every location a DWG marks, without changing the model. Same " +
        "arguments as revit_place_from_cad: layers (required), typeId or familyName/typeName, " +
        "elevationMode (dwg|level|explicit) with levelName/offsetMm/elevationMm, plus optional " +
        "importInstanceId, pointSources, mergeToleranceMm, applyBlockRotation, skipExisting, " +
        "maxInstances. Returns how many instances would be created, how many locations already have " +
        "one, the elevation each would land at, and a sample of the points.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (uiapp.ActiveUIDocument?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var warnings = new List<string>();
        var plan = CadPlacementPlanner.Build(uiapp, request, warnings, out var error, cancellationToken);
        if (plan == null)
            return Task.FromResult(Fail(request, error!));

        foreach (var blocked in plan.Placements.Where(p => p.BlockedReason != null).Take(5))
            warnings.Add($"Point on '{blocked.Point.Layer}': {blocked.BlockedReason}");

        var elevations = plan.Placements.Where(p => p.BlockedReason == null).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {plan.WillPlaceCount} instance(s) of " +
                      $"{plan.Symbol.Family.Name} : {plan.Symbol.Name} would be placed from " +
                      $"{plan.TotalPointsFound} location(s) ({plan.AlreadyPlacedCount} already placed).",
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
                viewName = plan.View?.Name,
                elevation = new
                {
                    mode = plan.Elevation.Mode,
                    description = plan.Elevation.Describe(),
                    minMm = elevations.Count == 0 ? 0 : Math.Round(elevations.Min(p => p.ElevationMm), 1),
                    maxMm = elevations.Count == 0 ? 0 : Math.Round(elevations.Max(p => p.ElevationMm), 1)
                },
                locationsFound = plan.TotalPointsFound,
                willPlace = plan.WillPlaceCount,
                alreadyPlaced = plan.AlreadyPlacedCount,
                blocked = plan.BlockedCount,
                sampleCount = Math.Min(MaxSamplePoints, plan.Placements.Count),
                sample = plan.Placements.Take(MaxSamplePoints).Select(p => p.ToPayload()).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// What <see cref="PlaceFromCadShapesTool"/> would create, without touching the model. Shares the
/// planner with the write tool, so the two cannot report different answers.
/// </summary>
public class PreviewPlaceFromCadShapesTool : IRevitMcpTool
{
    private const int MaxReportedPlacements = 50;

    public string Name => "revit_preview_place_from_cad_shapes";

    public string Description =>
        "Previews placing families on fixtures reconstructed from loose CAD line work, WITHOUT " +
        "changing the model. Same arguments as revit_place_from_cad_shapes. Returns the signature " +
        "table with the family type each one resolved to, how many instances would be created, how " +
        "many fixtures already have one, and which signatures have no type yet. Read-only.";

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
        var plan = CadShapePlacementPlanner.Build(uiapp, request, warnings, out var error, cancellationToken);
        if (plan == null)
            return Task.FromResult(Fail(request, error!));

        var willPlace = plan.Placements.Where(p => p.WillPlace).ToList();
        var sample = plan.Placements.Take(MaxReportedPlacements).Select(p => p.ToPayload()).ToList();

        if (plan.Placements.Count > sample.Count)
            warnings.Add($"Listing the first {sample.Count} of {plan.Placements.Count} fixture(s).");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{plan.WillPlaceCount} instance(s) would be placed on " +
                      $"{plan.TotalShapesFound} reconstructed fixture(s) " +
                      $"({plan.AlreadyPlacedCount} already have one, {plan.UnmappedCount} have no type).",
            Data = new
            {
                importInstanceId = plan.Import.Id.Value,
                importName = CadPointExtractor.SafeName(plan.Import),
                layers = plan.Layers,
                elevationMode = plan.Elevation.Mode,
                elevationDescription = plan.Elevation.Describe(),
                levelName = plan.Level?.Name,
                shapesFound = plan.TotalShapesFound,
                willPlaceCount = plan.WillPlaceCount,
                alreadyPlacedCount = plan.AlreadyPlacedCount,
                unmappedCount = plan.UnmappedCount,
                oversizeCount = plan.OversizeCount,
                elevationRangeMm = willPlace.Count == 0
                    ? null
                    : new
                    {
                        minMm = Math.Round(willPlace.Min(p => p.ElevationMm), 1),
                        maxMm = Math.Round(willPlace.Max(p => p.ElevationMm), 1)
                    },
                signatures = plan.DescribeSignatures(),
                placements = sample
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

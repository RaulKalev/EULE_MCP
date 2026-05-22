using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class EstimateCircuitLengthTool : IRevitMcpTool
{
    public string Name => "revit_estimate_circuit_length";
    public string Description => "Estimates circuit length from panel and connected element model coordinates. Methods: StraightLineMax, StraightLineSum, ManhattanMax (default), ManhattanSum, NearestNeighborPath. Accepts: circuitId (required), method (string), routingMultiplier (double, default 1.25), includeElementBreakdown (bool).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string[] Assumptions =
    [
        "Length is estimated from model element coordinate positions, not actual installed cable routing.",
        "Routing multiplier applied to account for cable path overhead.",
        "Results are preliminary and must be reviewed by an engineer."
    ];

    private static readonly string Warning =
        "This is a preliminary length estimate, not an installed cable route measurement.";

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        if (circuitId == 0) return Task.FromResult(Fail(request, "circuitId is required."));

        var method = ToolArguments.GetString(request.Arguments, "method", "ManhattanMax");
        var multiplier = GetDouble(request.Arguments, "routingMultiplier", 1.25);
        var breakdown = ToolArguments.GetBool(request.Arguments, "includeElementBreakdown", true);

        var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
        if (circuit == null) return Task.FromResult(Fail(request, $"Circuit {circuitId} not found."));

        // Panel location
        var panel = CircuitDtoBuilder.TryGetPanel(circuit);
        if (panel == null)
            return Task.FromResult(Fail(request, $"Circuit {circuitId} has no panel — cannot estimate length."));

        var panelResolved = CircuitLocationResolver.Resolve(panel);
        if (panelResolved == null)
            return Task.FromResult(Fail(request, "Panel location could not be resolved."));

        var panelPt = panelResolved.Value.Pt; // in feet

        // Connected elements
        var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>();
        int missingLoc = 0;
        try
        {
            if (circuit.Elements != null)
            {
                foreach (Element e in circuit.Elements)
                {
                    var r = CircuitLocationResolver.Resolve(e);
                    if (r == null) { missingLoc++; elements.Add((e.Id.Value, null)); }
                    else elements.Add((e.Id.Value, r.Value.Pt));
                }
            }
        }
        catch { }

        var (rawFeet, farthestId) = CircuitLengthEstimator.Estimate(panelPt, elements, method);
        double rawMeters = LengthUnitHelper.RoundMeters(LengthUnitHelper.ToMeters(rawFeet));
        double estimatedMeters = LengthUnitHelper.RoundMeters(rawMeters * multiplier);

        // Element breakdown in meters
        object? breakdownData = null;
        if (breakdown)
        {
            breakdownData = elements
                .Where(e => e.Loc.HasValue)
                .Select(e =>
                {
                    var loc = e.Loc!.Value;
                    double distFt = CircuitLengthEstimator.Manhattan(panelPt.X, panelPt.Y, panelPt.Z, loc.X, loc.Y, loc.Z);
                    return new
                    {
                        elementId = e.Id,
                        distanceFromPanelMeters = LengthUnitHelper.RoundMeters(LengthUnitHelper.ToMeters(distFt))
                    };
                })
                .OrderByDescending(x => x.distanceFromPanelMeters)
                .ToList<object>();
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Estimated circuit length for circuit {circuitId}: {estimatedMeters:F2} m ({method}, ×{multiplier}).",
            Data = new
            {
                circuitId,
                panelName = panel.Name,
                circuitNumber = circuit.CircuitNumber ?? "",
                method,
                routingMultiplier = multiplier,
                rawLengthMeters = rawMeters,
                estimatedLengthMeters = estimatedMeters,
                connectedElementCount = elements.Count,
                elementsWithLocation = elements.Count - missingLoc,
                elementsMissingLocation = missingLoc,
                farthestElementId = farthestId,
                elementBreakdown = breakdownData,
                assumptions = Assumptions
            },
            Warnings = [Warning],
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static double GetDouble(Dictionary<string, object?> args, string key, double def)
    {
        if (!args.TryGetValue(key, out var val)) return def;
        return val switch
        {
            double d => d,
            float f => f,
            Newtonsoft.Json.Linq.JValue jv => (double)jv,
            _ => def
        };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

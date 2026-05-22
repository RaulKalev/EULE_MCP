using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class EstimateCircuitLengthsTool : IRevitMcpTool
{
    public string Name => "revit_estimate_circuit_lengths";
    public string Description => "Estimates lengths for many circuits at once. Filter by panelName, systemType, or provide explicit circuitIds. Methods: StraightLineMax, StraightLineSum, ManhattanMax (default), ManhattanSum, NearestNeighborPath. Accepts: panelName, systemType, circuitIds (long[]), method, routingMultiplier (default 1.25), limit (default 1000).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var circuitIds = ToolArguments.GetLongArray(request.Arguments, "circuitIds");
        var method = ToolArguments.GetString(request.Arguments, "method", "ManhattanMax");
        var multiplier = GetDouble(request.Arguments, "routingMultiplier", 1.25);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 1000);

        List<ElectricalSystem> circuits;
        if (circuitIds.Length > 0)
        {
            circuits = circuitIds
                .Take(limit)
                .Select(id => doc.GetElement(new ElementId(id)) as ElectricalSystem)
                .Where(c => c != null)
                .Cast<ElectricalSystem>()
                .ToList();
        }
        else
        {
            circuits = CircuitQueryService.GetAll(doc);
            if (!string.IsNullOrWhiteSpace(panelName) || !string.IsNullOrWhiteSpace(systemType))
                circuits = CircuitQueryService.Filter(circuits, panelName, null, systemType);
            circuits = circuits.Take(limit).ToList();
        }

        var results = new List<object>();
        var failures = new List<object>();

        foreach (var c in circuits)
        {
            if (cancellationToken.IsCancellationRequested) break;

            var panel = CircuitDtoBuilder.TryGetPanel(c);
            if (panel == null) { failures.Add(new { circuitId = c.Id.Value, reason = "No panel assigned." }); continue; }

            var panelR = CircuitLocationResolver.Resolve(panel);
            if (panelR == null) { failures.Add(new { circuitId = c.Id.Value, reason = "Panel location not available." }); continue; }

            var panelPt = panelR.Value.Pt;
            var elements = new List<(long Id, (double X, double Y, double Z)? Loc)>();
            try
            {
                if (c.Elements != null)
                    foreach (Element e in c.Elements)
                    {
                        var r = CircuitLocationResolver.Resolve(e);
                        elements.Add((e.Id.Value, r?.Pt));
                    }
            }
            catch { }

            var (rawFeet, _) = CircuitLengthEstimator.Estimate(panelPt, elements, method);
            double rawMeters = LengthUnitHelper.RoundMeters(LengthUnitHelper.ToMeters(rawFeet));
            double estimated = LengthUnitHelper.RoundMeters(rawMeters * multiplier);

            results.Add(new
            {
                circuitId = c.Id.Value,
                panelName = panel.Name,
                circuitNumber = c.CircuitNumber ?? "",
                estimatedLengthMeters = estimated,
                elementsWithLocation = elements.Count(e => e.Loc.HasValue),
                elementsMissingLocation = elements.Count(e => !e.Loc.HasValue),
                status = "Estimated"
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Estimated lengths for {results.Count} of {circuits.Count} circuits. {failures.Count} failed.",
            Data = new
            {
                circuitCount = circuits.Count,
                estimatedCount = results.Count,
                failedCount = failures.Count,
                method,
                routingMultiplier = multiplier,
                circuits = results,
                failures
            },
            Warnings = ["Length estimates are preliminary and should be reviewed by an engineer."],
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

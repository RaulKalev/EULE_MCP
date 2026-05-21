using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetCircuitCompatibleElementsTool : IRevitMcpTool
{
    public string Name => "revit_get_circuit_compatible_elements";
    public string Description => "Finds elements and checks whether they can be added to an electrical circuit. Supports current selection, explicit element IDs, or category+filter query. Optionally validates against a target circuit.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly CircuitElementResolver _resolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var category = ToolArguments.GetString(request.Arguments, "category");
        var targetCircuitId = ToolArguments.GetInt(request.Arguments, "targetCircuitId", 0);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (!useSelection && elementIds.Length == 0 && string.IsNullOrWhiteSpace(category))
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, or category."));

        ElementQueryOptions? queryOptions = null;
        if (!useSelection && elementIds.Length == 0 && !string.IsNullOrWhiteSpace(category))
        {
            queryOptions = new ElementQueryOptions
            {
                Category = category,
                Filters = filtersParsed.Items,
                Limit = limit
            };
        }

        var (ids, resolveError) = _resolver.ResolveElementIds(
            doc, uidoc, useSelection, elementIds, queryOptions, limit);
        if (!string.IsNullOrEmpty(resolveError))
            return Task.FromResult(Fail(request, resolveError));

        ElectricalSystem? targetCircuit = null;
        if (targetCircuitId > 0)
        {
            var elem = doc.GetElement(new ElementId((long)targetCircuitId));
            targetCircuit = elem as ElectricalSystem;
            if (targetCircuit == null)
                return Task.FromResult(Fail(request, $"No electrical circuit found with ID {targetCircuitId}."));
        }

        var results = _resolver.CheckCompatibility(doc, ids, targetCircuit);

        var compatible = results
            .Where(r => r.IsCompatible)
            .Select(r => new
            {
                elementId = r.ElementId,
                category = r.Category,
                family = r.Family,
                type = r.Type,
                currentCircuitIds = r.CurrentCircuitIds
            }).ToList<object>();

        var incompatible = results
            .Where(r => !r.IsCompatible)
            .Select(r => new
            {
                elementId = r.ElementId,
                reason = r.IncompatibilityReason ?? "Unknown reason"
            }).ToList<object>();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{compatible.Count} compatible, {incompatible.Count} incompatible of {results.Count} candidate(s).",
            Data = new
            {
                totalCandidates = results.Count,
                compatibleCount = compatible.Count,
                incompatibleCount = incompatible.Count,
                compatibleElements = compatible,
                incompatibleElements = incompatible
            },
            Warnings = filtersParsed.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

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

public class AddElementsToCircuitTool : IRevitMcpTool
{
    public string Name => "revit_add_elements_to_circuit";
    public string Description => "Adds elements to an existing electrical circuit from current selection, explicit element IDs, or a query. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly CircuitElementResolver _elementResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var targetCircuitId = ToolArguments.GetInt(request.Arguments, "targetCircuitId", 0);
        if (targetCircuitId == 0)
            return Task.FromResult(Fail(request, "targetCircuitId is required."));

        var circuitElem = doc.GetElement(new ElementId((long)targetCircuitId));
        if (circuitElem is not ElectricalSystem circuit)
            return Task.FromResult(Fail(request, $"No electrical circuit found with ID {targetCircuitId}."));

        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var category = ToolArguments.GetString(request.Arguments, "category");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (!useSelection && elementIds.Length == 0 && string.IsNullOrWhiteSpace(category))
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, or category."));

        ElementQueryOptions? queryOptions = null;
        if (!useSelection && elementIds.Length == 0)
        {
            queryOptions = new ElementQueryOptions
            {
                Category = category,
                Filters = filtersParsed.Items,
                Limit = limit
            };
        }

        var (ids, resolveError) = _elementResolver.ResolveElementIds(
            doc, uidoc, useSelection, elementIds, queryOptions, limit);
        if (!string.IsNullOrEmpty(resolveError))
            return Task.FromResult(Fail(request, resolveError));

        if (ids.Count == 0)
            return Task.FromResult(Fail(request, "No elements resolved."));

        // Pre-validate compatibility
        var compatibility = _elementResolver.CheckCompatibility(doc, ids, circuit);
        var compatibleIds = compatibility
            .Where(r => r.IsCompatible)
            .Select(r => new ElementId(r.ElementId))
            .ToList();
        var preRejected = compatibility
            .Where(r => !r.IsCompatible)
            .Select(r => (r.ElementId, r.IncompatibilityReason ?? "Not compatible"))
            .ToList();

        if (compatibleIds.Count == 0)
            return Task.FromResult(Fail(request, "No compatible elements found to add."));

        var result = CircuitMutationService.AddToCircuit(doc, circuit, compatibleIds);
        var allRejected = preRejected
            .Concat(result.Rejected)
            .Select(r => new { elementId = r.Item1, reason = r.Item2 })
            .ToList<object>();

        var warnings = filtersParsed.Warnings;

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = result.Success,
            Message = result.Message,
            Data = new
            {
                targetCircuitId,
                addedCount = result.Added.Count,
                rejectedCount = allRejected.Count,
                addedElementIds = result.Added,
                rejectedElements = allRejected
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

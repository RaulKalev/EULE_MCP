using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class MoveElementsBetweenCircuitsTool : IRevitMcpTool
{
    public string Name => "revit_move_elements_between_circuits";
    public string Description => "Moves elements from the circuit(s) they are on to another existing circuit, from current selection, explicit element IDs, or a query. Elements with no circuit are simply added. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly CircuitElementResolver _elementResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var targetCircuitId = ToolArguments.GetLong(request.Arguments, "targetCircuitId");
        if (targetCircuitId == 0)
            return Task.FromResult(Fail(request, "targetCircuitId is required."));

        if (doc.GetElement(new ElementId(targetCircuitId)) is not ElectricalSystem target)
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

        // Compatibility also reports each element's current circuits and rejects the ones already
        // on the target, which is what makes a repeat call harmless.
        var compatibility = _elementResolver.CheckCompatibility(doc, ids, target);
        var movable = compatibility
            .Where(r => r.IsCompatible)
            .Select(r => (new ElementId(r.ElementId), r.CurrentCircuitIds))
            .ToList();
        var preRejected = compatibility
            .Where(r => !r.IsCompatible)
            .Select(r => (r.ElementId, r.IncompatibilityReason ?? "Not compatible"))
            .ToList();

        if (movable.Count == 0)
            return Task.FromResult(Fail(request, "No elements to move: " +
                string.Join("; ", preRejected.Select(r => r.Item2).Distinct())));

        var result = CircuitMutationService.MoveBetweenCircuits(doc, target, movable);
        var allRejected = preRejected
            .Concat(result.Rejected)
            .Select(r => new { elementId = r.Item1, reason = r.Item2 })
            .ToList<object>();

        var warnings = filtersParsed.Warnings.Concat(result.Warnings).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = result.Success,
            Message = result.Message,
            Data = new
            {
                targetCircuitId,
                movedCount = result.Moved.Count,
                rejectedCount = allRejected.Count,
                movedElementIds = result.Moved,
                rejectedElements = allRejected
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

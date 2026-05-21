using System.Diagnostics;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CreateElectricalCircuitTool : IRevitMcpTool
{
    public string Name => "revit_create_electrical_circuit";
    public string Description => "Creates a new electrical circuit from current selection, explicit element IDs, or a category+filter query. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly CircuitElementResolver _elementResolver = new();
    private readonly PanelResolver _panelResolver = new();

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
        var systemTypeStr = ToolArguments.GetString(request.Arguments, "systemType", "PowerCircuit");
        var panelElementId = ToolArguments.GetInt(request.Arguments, "panelElementId", 0);
        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var wireTypeName = ToolArguments.GetString(request.Arguments, "wireTypeName");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (!useSelection && elementIds.Length == 0 && string.IsNullOrWhiteSpace(category))
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, or category."));

        if (!Enum.TryParse<ElectricalSystemType>(systemTypeStr, ignoreCase: true, out var systemType))
        {
            var valid = string.Join(", ", Enum.GetNames(typeof(ElectricalSystemType)));
            return Task.FromResult(Fail(request,
                $"Unknown system type '{systemTypeStr}'. Valid values: {valid}"));
        }

        // Resolve elements
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

        // Resolve panel (optional)
        Autodesk.Revit.DB.FamilyInstance? panel = null;
        if (panelElementId > 0 || !string.IsNullOrWhiteSpace(panelName))
        {
            var (resolvedPanel, panelError) = _panelResolver.Resolve(doc, panelElementId, panelName);
            if (resolvedPanel == null)
                return Task.FromResult(Fail(request, panelError));
            panel = resolvedPanel;
        }

        // Resolve wire type (optional)
        Autodesk.Revit.DB.Electrical.WireType? wireType = null;
        if (!string.IsNullOrWhiteSpace(wireTypeName))
        {
            var (wt, wtError) = WireTypeResolver.Resolve(doc, wireTypeName);
            if (wt == null) return Task.FromResult(Fail(request, wtError));
            wireType = wt;
        }

        var result = CircuitMutationService.CreateCircuit(doc, ids, systemType, panel, wireType);
        var warnings = filtersParsed.Warnings.Concat(result.Errors).ToList();

        sw.Stop();
        if (!result.Success)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = result.Message,
                Errors = result.Errors,
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = result.Message,
            Data = new
            {
                circuitId = result.CircuitId,
                systemType = systemTypeStr,
                panelName = panel?.Name ?? "",
                elementCount = result.AddedElementIds.Count,
                addedElementIds = result.AddedElementIds
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Selects every element connected to any circuit assigned to a panel, in a single
/// operation. Without this, an agent has to enumerate circuits on the panel and select
/// each circuit's elements one call at a time — slow, and easy to get wrong since
/// revit_select_circuit_elements defaults to replacing the selection on every call.
/// </summary>
public class SelectElementsByPanelTool : IRevitMcpTool
{
    public string Name => "revit_select_elements_by_panel";
    public string Description => "Selects every element connected to any circuit assigned to a panel, in one operation — the fast path for \"select all elements on panel X\" instead of selecting circuit by circuit. Accepts: panelName or panelElementId (one required), systemType (optional filter, e.g. 'PowerCircuit'), replaceSelection (bool, default true), zoomToSelection (bool, default false).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly PanelResolver _panelResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var panelElementId = ToolArguments.GetLong(request.Arguments, "panelElementId");
        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var replaceSelection = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoomToSelection = ToolArguments.GetBool(request.Arguments, "zoomToSelection");

        if (panelElementId <= 0 && string.IsNullOrWhiteSpace(panelName))
            return Task.FromResult(Fail(request, "Provide panelName or panelElementId."));

        var (panel, error) = _panelResolver.Resolve(doc, panelElementId, panelName ?? string.Empty);
        if (panel == null)
            return Task.FromResult(Fail(request, error));

        var circuitsOnPanel = CircuitQueryService.GetAll(doc)
            .Where(c => CircuitDtoBuilder.TryGetPanel(c)?.Id == panel.Id)
            .ToList();

        if (!string.IsNullOrWhiteSpace(systemType))
        {
            circuitsOnPanel = Enum.TryParse<ElectricalSystemType>(systemType, ignoreCase: true, out var parsedType)
                ? circuitsOnPanel.Where(c => c.SystemType == parsedType).ToList()
                : circuitsOnPanel.Where(c => c.SystemType.ToString().Contains(systemType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (circuitsOnPanel.Count == 0)
        {
            var scopeMsg = string.IsNullOrWhiteSpace(systemType) ? "" : $" with systemType '{systemType}'";
            return Task.FromResult(Fail(request, $"No circuits found assigned to panel '{panel.Name}'{scopeMsg}."));
        }

        var elementIds = new HashSet<ElementId>();
        int circuitsWithNoElements = 0;
        foreach (var circuit in circuitsOnPanel)
        {
            try
            {
                var es = circuit.Elements;
                if (es == null || es.Size == 0) { circuitsWithNoElements++; continue; }
                foreach (Element e in es)
                    elementIds.Add(e.Id);
            }
            catch { circuitsWithNoElements++; }
        }

        if (elementIds.Count == 0)
            return Task.FromResult(Fail(request, $"Panel '{panel.Name}' has {circuitsOnPanel.Count} circuit(s) but none have connected elements."));

        var existing = replaceSelection ? new List<ElementId>() : uidoc.Selection.GetElementIds().ToList();
        var merged = existing.Union(elementIds).ToList();
        uidoc.Selection.SetElementIds(merged);

        if (zoomToSelection)
            try { uidoc.ShowElements(merged); } catch { }

        var warnings = new List<string>();
        if (circuitsWithNoElements > 0)
            warnings.Add($"{circuitsWithNoElements} circuit(s) on this panel had no connected elements or could not be read.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {elementIds.Count} element(s) across {circuitsOnPanel.Count} circuit(s) on panel '{panel.Name}'.",
            Data = new
            {
                panelElementId = panel.Id.Value,
                panelName = panel.Name,
                circuitCount = circuitsOnPanel.Count,
                selectedCount = elementIds.Count,
                selectedElementIds = elementIds.Select(id => id.Value).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

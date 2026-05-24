using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SetCircuitPathModeTool : IRevitMcpTool
{
    public string Name => "revit_set_circuit_path_mode";
    public string Description =>
        "Changes electrical circuits from Farthest Device to All Devices path mode. Preserves manual/custom " +
        "paths (CircuitPathMode.Custom or HasCustomCircuitPath). Scope: useSelection=true (circuits containing " +
        "selected elements), circuitIds (explicit list), or all circuits when neither is provided. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var circuitIds = ToolArguments.GetLongArray(request.Arguments, "circuitIds");

        List<ElectricalSystem> circuits;
        string mode;
        var warnings = new List<string>();

        if (useSelection)
        {
            mode = "selection";
            var selIds = uidoc.Selection.GetElementIds().ToList();
            if (selIds.Count == 0)
                return Task.FromResult(Fail(request, "No elements selected."));

            var seenIds = new HashSet<long>();
            circuits = new List<ElectricalSystem>();

            foreach (var eid in selIds)
            {
                var element = doc.GetElement(eid);

                // Direct ElectricalSystem selection (e.g. clicking a wire/circuit)
                if (element is ElectricalSystem es)
                {
                    if (seenIds.Add(es.Id.Value)) circuits.Add(es);
                    continue;
                }

                // Device selection — trace up to parent circuits
                if (element is FamilyInstance fi && fi.MEPModel != null)
                {
                    try
                    {
                        var parentCircuits = new FilteredElementCollector(doc)
                            .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                            .Where(sys => sys.Elements != null && sys.Elements.Cast<Element>().Any(e => e.Id == fi.Id))
                            .ToList();
                        foreach (var sys in parentCircuits)
                            if (seenIds.Add(sys.Id.Value)) circuits.Add(sys);
                    }
                    catch { }
                }
            }

            if (circuits.Count == 0)
                return Task.FromResult(Fail(request, "No electrical circuits found in the current selection."));
        }
        else if (circuitIds.Length > 0)
        {
            mode = "explicit";
            circuits = new List<ElectricalSystem>();
            var seenCircuitIds = new HashSet<long>();
            foreach (var id in circuitIds)
            {
                var elem = doc.GetElement(new ElementId(id));
                if (elem is ElectricalSystem es)
                {
                    if (seenCircuitIds.Add(es.Id.Value))
                        circuits.Add(es);
                    else
                        warnings.Add($"Circuit ID {id} was provided more than once — duplicate skipped.");
                }
                else
                {
                    warnings.Add($"Element ID {id} is not an electrical circuit — skipped.");
                }
            }
            if (circuits.Count == 0)
                return Task.FromResult(Fail(request, "None of the provided circuitIds resolved to electrical circuits."));
        }
        else
        {
            mode = "all";
            circuits = new FilteredElementCollector(doc)
                .OfClass(typeof(ElectricalSystem))
                .Cast<ElectricalSystem>()
                .ToList();
            if (circuits.Count == 0)
                return Task.FromResult(Fail(request, "No electrical circuits found in the document."));
        }

        var result = CircuitMutationService.SetPathMode(doc, circuits);

        foreach (var a in result.Actions.Where(a => a.Action == "skipped_custom"))
            warnings.Add($"Circuit {a.CircuitNumber} (ID {a.CircuitId}): skipped — manual/custom circuit path was preserved.");

        int unsupportedCount = result.Actions.Count(a => a.Action.StartsWith("skipped_unsupported_mode:", StringComparison.OrdinalIgnoreCase));
        if (unsupportedCount > 0)
            warnings.Add($"{unsupportedCount} circuit(s) skipped — path mode was not FarthestDevice, AllDevices, or Custom.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = result.Success,
            Message = result.Message,
            Data = result.Success ? (object?)new
            {
                mode,
                updatedCount = result.UpdatedCount,
                alreadyCorrectCount = result.AlreadyCorrectCount,
                skippedCustomPathCount = result.SkippedCustomPathCount,
                skippedUnsupportedModeCount = result.SkippedUnsupportedModeCount,
                totalProcessed = circuits.Count,
                circuits = result.Actions.Select(a => new
                {
                    circuitId = a.CircuitId,
                    circuitNumber = a.CircuitNumber,
                    action = a.Action
                }).ToList()
            } : null,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class FindCircuitsByElementParameterTool : IRevitMcpTool
{
    public string Name => "revit_find_circuits_by_element_parameter";
    public string Description => "Finds electrical circuits that contain elements matching category and parameter filters. Useful for: find circuits in room 201, find circuits containing devices of a specific type, find circuits where ELENEA_Osasüsteem = ATS. Returns distinct circuits with matched element IDs.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly ElementQueryEngine _queryEngine = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var elementCategory = ToolArguments.GetString(request.Arguments, "elementCategory");
        var filtersParsed = ToolArguments.GetFiltersWithWarnings(request.Arguments);
        var includeElements = ToolArguments.GetBool(request.Arguments, "includeElements", true);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (string.IsNullOrWhiteSpace(elementCategory) && filtersParsed.Items.Count == 0)
            return Task.FromResult(Fail(request, "Provide elementCategory and/or filters to narrow the search."));

        var queryOptions = new ElementQueryOptions
        {
            Category = elementCategory,
            Filters = filtersParsed.Items,
            Limit = limit
        };
        var queryResult = _queryEngine.Query(doc, uidoc, queryOptions);
        if (!queryResult.Success)
            return Task.FromResult(Fail(request, queryResult.Message));

        // For each matching element, collect its electrical circuits
        var circuitMap = new Dictionary<long, List<long>>(); // circuitId -> matchedElementIds
        foreach (var elemDto in queryResult.Elements)
        {
            if (cancellationToken.IsCancellationRequested) break;
            var element = doc.GetElement(new ElementId(elemDto.ElementId));
            if (element is not FamilyInstance fi || fi.MEPModel == null) continue;
            try
            {
                var elemCircuits = new FilteredElementCollector(doc)
                    .OfClass(typeof(ElectricalSystem)).Cast<ElectricalSystem>()
                    .Where(sys => sys.Elements != null && sys.Elements.Cast<Element>().Any(e => e.Id == fi.Id))
                    .ToList();
                foreach (var sys in elemCircuits)
                {
                    if (!circuitMap.ContainsKey(sys.Id.Value))
                        circuitMap[sys.Id.Value] = new List<long>();
                    circuitMap[sys.Id.Value].Add(elemDto.ElementId);
                }
            }
            catch { }
        }

        var circuits = new List<object>();
        foreach (var (circuitId, matchedIds) in circuitMap)
        {
            var circuitElem = doc.GetElement(new ElementId(circuitId));
            if (circuitElem is not ElectricalSystem circuit) continue;
            var panel = CircuitDtoBuilder.TryGetPanel(circuit);
            circuits.Add(new
            {
                circuitId,
                circuitNumber = circuit.CircuitNumber ?? "",
                panelName = panel?.Name ?? "",
                panelElementId = panel?.Id?.Value ?? 0L,
                systemType = circuit.SystemType.ToString(),
                wireType = CircuitDtoBuilder.GetWireTypeName(doc, circuit),
                matchedElementCount = matchedIds.Count,
                matchedElementIds = includeElements ? (object)matchedIds : null
            });
        }

        var warnings = new List<string>(filtersParsed.Warnings);
        warnings.AddRange(queryResult.Warnings);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {circuits.Count} circuit(s) containing {queryResult.Elements.Count} matching element(s).",
            Data = new { circuitCount = circuits.Count, matchingElementCount = queryResult.Elements.Count, circuits },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

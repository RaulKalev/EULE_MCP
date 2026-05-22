using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class FindElementsOnCircuitTool : IRevitMcpTool
{
    public string Name => "revit_find_elements_on_circuit";
    public string Description => "Lists all elements connected to a specific electrical circuit with category, family, type, level, and optional parameter values. Accepts: circuitId (long, required), returnParameters (string[]), limit (default 500).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly ParameterReader _paramReader = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var circuitId = ToolArguments.GetLong(request.Arguments, "circuitId");
        var returnParams = ToolArguments.GetStringArray(request.Arguments, "returnParameters");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        if (circuitId <= 0) return Task.FromResult(Fail(request, "circuitId is required."));
        var circuit = doc.GetElement(new ElementId(circuitId)) as ElectricalSystem;
        if (circuit == null) return Task.FromResult(Fail(request, $"No electrical circuit found with ID {circuitId}."));

        var elements = new List<object>();
        try
        {
            var es = circuit.Elements;
            if (es != null)
            {
                foreach (Element e in es)
                {
                    if (elements.Count >= limit) break;
                    if (cancellationToken.IsCancellationRequested) break;
                    var typeId = e.GetTypeId();
                    var typeElem = typeId != null && typeId != ElementId.InvalidElementId ? doc.GetElement(typeId) : null;
                    string level = "";
                    try
                    {
                        var lvl = e.get_Parameter(BuiltInParameter.SCHEDULE_LEVEL_PARAM)
                               ?? e.get_Parameter(BuiltInParameter.INSTANCE_SCHEDULE_ONLY_LEVEL_PARAM);
                        if (lvl != null) level = doc.GetElement(lvl.AsElementId())?.Name ?? "";
                    }
                    catch { }

                    Dictionary<string, string>? paramValues = null;
                    if (returnParams.Length > 0)
                    {
                        paramValues = new Dictionary<string, string>();
                        var readOpts = new ParameterReadOptions { IncludeInstanceParameters = true, IncludeTypeParameters = true };
                        foreach (var p in _paramReader.ReadParameters(doc, e, readOpts))
                            if (returnParams.Any(rp => ParameterMatcher.Matches(p.Name, rp, "ContainsNormalized")))
                                paramValues[p.Name] = p.Value;
                    }
                    elements.Add(new
                    {
                        elementId = e.Id.Value,
                        category = e.Category?.Name ?? "",
                        family = e is FamilyInstance fi ? fi.Symbol?.Family?.Name ?? "" : "",
                        type = typeElem?.Name ?? e.Name,
                        level,
                        parameters = (object?)paramValues
                    });
                }
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request, $"Could not read circuit elements: {ex.Message}"));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Circuit {circuit.CircuitNumber ?? circuitId.ToString()} has {elements.Count} connected element(s).",
            Data = new { circuitId, circuitNumber = circuit.CircuitNumber ?? "", elementCount = elements.Count, elements },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

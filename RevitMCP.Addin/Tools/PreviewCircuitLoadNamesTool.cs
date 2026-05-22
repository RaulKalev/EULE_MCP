using System.Diagnostics;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewCircuitLoadNamesTool : IRevitMcpTool
{
    public string Name => "revit_preview_circuit_load_names";
    public string Description => "Previews load name proposals for circuits without modifying the model. Generates names from a template with {ParameterName} placeholders resolved from circuit or connected element parameters. Accepts: panelName (optional filter), template (string, e.g. '{Room Number} {Category}'), source ('ConnectedElements'|'CircuitParameters', default ConnectedElements), systemType, limit (default 500).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var panelName = ToolArguments.GetString(request.Arguments, "panelName");
        var template = ToolArguments.GetString(request.Arguments, "template");
        var source = ToolArguments.GetString(request.Arguments, "source", "ConnectedElements");
        var systemType = ToolArguments.GetString(request.Arguments, "systemType");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var circuits = CircuitQueryService.GetAll(doc);
        if (!string.IsNullOrWhiteSpace(panelName) || !string.IsNullOrWhiteSpace(systemType))
            circuits = CircuitQueryService.Filter(circuits, panelName, null, systemType);
        var filtered = circuits.Take(limit).ToList();

        var changes = new List<object>();
        foreach (var circuit in filtered)
        {
            if (cancellationToken.IsCancellationRequested) break;
            string oldName = ""; try { oldName = circuit.LookupParameter("Load Name")?.AsString() ?? ""; } catch { }

            string newName = !string.IsNullOrWhiteSpace(template)
                ? FillTemplate(doc, circuit, template, source)
                : GenerateFromElements(doc, circuit);

            if (!string.IsNullOrWhiteSpace(newName))
                changes.Add(new
                {
                    circuitId = circuit.Id.Value,
                    circuitNumber = circuit.CircuitNumber ?? "",
                    oldLoadName = oldName,
                    newLoadName = newName,
                    willChange = !string.Equals(oldName, newName, StringComparison.Ordinal)
                });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Previewed load name proposals for {filtered.Count} circuit(s). {changes.Count} have proposals.",
            Data = new { circuitCount = filtered.Count, proposalCount = changes.Count, changes },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string FillTemplate(Document doc, ElectricalSystem circuit, string template, string source)
    {
        var result = template;
        foreach (Match m in Regex.Matches(template, @"\{([^}]+)\}"))
        {
            var paramName = m.Groups[1].Value;
            result = result.Replace(m.Value, GetParamValue(doc, circuit, paramName, source));
        }
        return result.Trim();
    }

    private static string GetParamValue(Document doc, ElectricalSystem circuit, string paramName, string source)
    {
        try
        {
            var p = circuit.LookupParameter(paramName);
            if (p != null)
            {
                var v = p.AsString() ?? p.AsValueString() ?? "";
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch { }

        if (!source.Equals("ConnectedElements", StringComparison.OrdinalIgnoreCase)) return "";

        try
        {
            if (circuit.Elements != null)
            {
                foreach (Element e in circuit.Elements)
                {
                    foreach (Parameter p in e.Parameters)
                    {
                        if (!ParameterMatcher.Matches(p.Definition?.Name ?? "", paramName, "ContainsNormalized")) continue;
                        var v = p.AsString() ?? p.AsValueString() ?? "";
                        if (!string.IsNullOrWhiteSpace(v)) return v;
                    }
                    break; // use first element only
                }
            }
        }
        catch { }
        return "";
    }

    private static string GenerateFromElements(Document doc, ElectricalSystem circuit)
    {
        try
        {
            if (circuit.Elements != null)
                foreach (Element e in circuit.Elements)
                    return e.Category?.Name ?? e.Name;
        }
        catch { }
        return "";
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

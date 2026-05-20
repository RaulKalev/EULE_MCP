using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetElementParametersTool : IRevitMcpTool
{
    public string Name => "revit_get_element_parameters";
    public string Description => "Returns all parameters for specified elements or the current selection.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var doc = uidoc.Document;
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");

        ICollection<ElementId> ids;
        if (useSelection)
        {
            ids = uidoc.Selection.GetElementIds();
        }
        else if (elementIds.Length > 0)
        {
            ids = elementIds.Select(id => new ElementId(id)).ToList();
        }
        else
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = "Provide elementIds or set useSelection=true."
            });
        }

        var result = new List<object>();
        var warnings = new List<string>();

        foreach (var id in ids.Take(20)) // cap at 20 elements
        {
            if (cancellationToken.IsCancellationRequested) break;

            var elem = doc.GetElement(id);
            if (elem == null)
            {
                warnings.Add($"Element {id.Value} not found.");
                continue;
            }

            var parameters = elem.Parameters
                .Cast<Parameter>()
                .OrderBy(p => p.Definition?.Name ?? string.Empty)
                .Select(p => BuildParamEntry(p))
                .ToList();

            result.Add(new
            {
                elementId = elem.Id.Value,
                name = elem.Name,
                category = elem.Category?.Name ?? string.Empty,
                parameterCount = parameters.Count,
                parameters
            });
        }

        if (ids.Count > 20)
            warnings.Add($"Capped at 20 elements. {ids.Count - 20} elements omitted.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned parameters for {result.Count} element(s).",
            Data = new { elementCount = result.Count, elements = result },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static object BuildParamEntry(Parameter p)
    {
        var name = p.Definition?.Name ?? string.Empty;
        var storageType = p.StorageType.ToString();
        var isReadOnly = p.IsReadOnly;
        var isShared = p.IsShared;
        var guid = isShared ? p.GUID.ToString() : string.Empty;

        string value;
        try
        {
            value = p.StorageType switch
            {
                StorageType.Double => p.AsValueString() ?? p.AsDouble().ToString("F4"),
                StorageType.Integer => p.AsValueString() ?? p.AsInteger().ToString(),
                StorageType.String => p.AsString() ?? string.Empty,
                StorageType.ElementId => p.AsElementId()?.Value.ToString() ?? string.Empty,
                _ => string.Empty
            };
        }
        catch
        {
            value = string.Empty;
        }

        return new { name, value, storageType, isReadOnly, isShared, guid };
    }
}

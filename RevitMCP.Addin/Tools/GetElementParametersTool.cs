using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetElementParametersTool : IRevitMcpTool
{
    public string Name => "revit_get_element_parameters";
    public string Description => "Returns parameters for specified elements or the current selection. Supports instance/type parameters, shared parameter metadata, and optional name filtering.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var includeInstance = ToolArguments.GetBool(request.Arguments, "includeInstanceParameters", true);
        var includeType = ToolArguments.GetBool(request.Arguments, "includeTypeParameters", true);
        var parameterNames = ToolArguments.GetStringArray(request.Arguments, "parameterNames");

        ICollection<ElementId> ids;
        if (useSelection)
            ids = uidoc.Selection.GetElementIds();
        else if (elementIds.Length > 0)
            ids = elementIds.Select(id => new ElementId(id)).ToList();
        else
            return Task.FromResult(Fail(request, "Provide elementIds or set useSelection=true."));

        var reader = new ParameterReader();
        var readOpts = new ParameterReadOptions
        {
            IncludeInstanceParameters = includeInstance,
            IncludeTypeParameters = includeType,
            ParameterNames = parameterNames,
            ParameterNameMatchMode = "Contains"
        };

        var result = new List<object>();
        var warnings = new List<string>();
        const int cap = 20;

        foreach (var id in ids.Take(cap))
        {
            if (cancellationToken.IsCancellationRequested) break;

            var elem = doc.GetElement(id);
            if (elem == null) { warnings.Add($"Element {id.Value} not found."); continue; }

            var parameters = reader.ReadParameters(doc, elem, readOpts);

            var typeId = elem.GetTypeId();
            var typeElem = (typeId != null && typeId != ElementId.InvalidElementId) ? doc.GetElement(typeId) : null;

            result.Add(new
            {
                elementId = elem.Id.Value,
                uniqueId = elem.UniqueId,
                name = elem.Name,
                category = elem.Category?.Name ?? string.Empty,
                type = typeElem?.Name ?? string.Empty,
                typeElementId = typeElem?.Id.Value,
                parameterCount = parameters.Count,
                parameters = parameters.Select(p => new
                {
                    p.Name,
                    p.Value,
                    p.Scope,
                    p.StorageType,
                    p.IsReadOnly,
                    p.IsShared,
                    p.Guid,
                    p.ParameterId,
                    p.BuiltInParameterName
                })
            });
        }

        if (ids.Count > cap)
            warnings.Add($"Capped at {cap} elements. {ids.Count - cap} elements omitted.");

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

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetElementParametersTool : IRevitMcpTool
{
    public string Name => "revit_get_element_parameters";
    public string Description => "Returns parameters for specified elements or the current selection. Supports instance/type parameters, shared parameter metadata, and optional name filtering. includeModelState=true adds phaseCreated, phaseDemolished, designOption and workset; includeOwnership=true adds ownedBy.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);

        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        if (useSelection && uidoc == null)
            return Task.FromResult(ToolDocument.ActiveRequired(request, "useSelection", doc.Title));
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var includeInstance = ToolArguments.GetBool(request.Arguments, "includeInstanceParameters", true);
        var includeType = ToolArguments.GetBool(request.Arguments, "includeTypeParameters", true);
        var parameterNames = ToolArguments.GetStringArray(request.Arguments, "parameterNames");
        var includeModelState = ToolArguments.GetBool(request.Arguments, "includeModelState", false);
        var includeOwnership = ToolArguments.GetBool(request.Arguments, "includeOwnership", false);
        var states = includeModelState || includeOwnership ? new RevitMCP.Addin.ModelState.ElementModelStateReader(doc) : null;

        ICollection<ElementId> ids;
        if (useSelection)
            ids = uidoc!.Selection.GetElementIds();
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

            var parameterRows = parameters.Select(p => new
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
            });

            if (states == null)
            {
                // The default shape, unchanged (#109 keeps default payloads byte-for-byte).
                result.Add(new
                {
                    elementId = elem.Id.Value,
                    uniqueId = elem.UniqueId,
                    name = elem.Name,
                    category = elem.Category?.Name ?? string.Empty,
                    type = typeElem?.Name ?? string.Empty,
                    typeElementId = typeElem?.Id.Value,
                    parameterCount = parameters.Count,
                    parameters = parameterRows
                });
                continue;
            }

            var state = states.Read(elem, includeOwnership);
            result.Add(new ElementParametersWithState
            {
                ElementId = elem.Id.Value,
                UniqueId = elem.UniqueId,
                Name = elem.Name,
                Category = elem.Category?.Name ?? string.Empty,
                Type = typeElem?.Name ?? string.Empty,
                TypeElementId = typeElem?.Id.Value,
                PhaseCreated = includeModelState ? state.PhaseCreated : null,
                PhaseDemolished = includeModelState ? state.PhaseDemolished : null,
                DesignOption = includeModelState ? state.DesignOption : null,
                Workset = includeModelState ? state.Workset : null,
                OwnedBy = state.OwnedBy,
                ParameterCount = parameters.Count,
                Parameters = parameterRows
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

/// <summary>One element of revit_get_element_parameters with its model state (#109); the default shape stays anonymous.</summary>
internal sealed class ElementParametersWithState
{
    [Newtonsoft.Json.JsonProperty("elementId")] public long ElementId { get; set; }
    [Newtonsoft.Json.JsonProperty("uniqueId")] public string UniqueId { get; set; } = string.Empty;
    [Newtonsoft.Json.JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [Newtonsoft.Json.JsonProperty("category")] public string Category { get; set; } = string.Empty;
    [Newtonsoft.Json.JsonProperty("type")] public string Type { get; set; } = string.Empty;
    [Newtonsoft.Json.JsonProperty("typeElementId")] public long? TypeElementId { get; set; }
    [Newtonsoft.Json.JsonProperty("phaseCreated", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)] public string? PhaseCreated { get; set; }
    [Newtonsoft.Json.JsonProperty("phaseDemolished", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)] public string? PhaseDemolished { get; set; }
    [Newtonsoft.Json.JsonProperty("designOption", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)] public string? DesignOption { get; set; }
    [Newtonsoft.Json.JsonProperty("workset", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)] public string? Workset { get; set; }
    [Newtonsoft.Json.JsonProperty("ownedBy", NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)] public string? OwnedBy { get; set; }
    [Newtonsoft.Json.JsonProperty("parameterCount")] public int ParameterCount { get; set; }
    [Newtonsoft.Json.JsonProperty("parameters")] public object? Parameters { get; set; }
}

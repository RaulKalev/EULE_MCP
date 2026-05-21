using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetElementsInfoTool : IRevitMcpTool
{
    public string Name => "revit_get_elements_info";
    public string Description => "Returns structured element info and selected parameter values for selection, explicit element IDs, or category+filter queries. More controlled than revit_get_element_parameters.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private static readonly ElementQueryEngine _engine = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var opts = new ElementQueryOptions
        {
            UseSelection = ToolArguments.GetBool(request.Arguments, "useSelection"),
            ElementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds").ToList(),
            Category = ToolArguments.GetString(request.Arguments, "category"),
            Filters = ToolArguments.GetFilters(request.Arguments),
            ReturnParameters = ToolArguments.GetStringArray(request.Arguments, "parameterNames").ToList(),
            IncludeInstanceParameters = ToolArguments.GetBool(request.Arguments, "includeInstanceParameters", true),
            IncludeTypeParameters = ToolArguments.GetBool(request.Arguments, "includeTypeParameters", true),
            Limit = ToolArguments.GetInt(request.Arguments, "limit", 500)
        };

        if (!opts.UseSelection && opts.ElementIds.Count == 0 && string.IsNullOrWhiteSpace(opts.Category))
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, or a category."));

        var result = _engine.Query(uidoc.Document, uidoc, opts);
        if (!result.Success)
            return Task.FromResult(Fail(request, result.Message));

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {result.Elements.Count} of {result.TotalMatched} matched elements.",
            Data = new
            {
                totalMatched = result.TotalMatched,
                returned = result.Elements.Count,
                elements = result.Elements
            },
            Warnings = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

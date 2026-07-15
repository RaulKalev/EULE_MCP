using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListDimensionTypesTool : IRevitMcpTool
{
    public string Name => "revit_list_dimension_types";
    public string Description =>
        "Lists all dimension types in the active document with their style " +
        "(Linear, Angular, Radial, Diameter, ArcLength, SpotElevation, SpotCoordinate, SpotSlope). " +
        "Use the returned typeId as dimensionTypeId in revit_place_dimensions. " +
        "Optional: styleFilter (e.g. 'Linear') to narrow the list.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var styleFilter = ToolArguments.GetString(request.Arguments, "styleFilter").Trim();

        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .Select(t =>
            {
                string style;
                try { style = t.StyleType.ToString(); } catch { style = "Unknown"; }
                return new { typeId = t.Id.Value, name = t.Name, style };
            })
            .Where(t => string.IsNullOrEmpty(styleFilter) || t.style.Contains(styleFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.style)
            .ThenBy(t => t.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{types.Count} dimension type(s) found.",
            Data = new { types },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

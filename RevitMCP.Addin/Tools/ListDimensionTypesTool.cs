using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools.AnnotationTypes;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListDimensionTypesTool : IRevitMcpTool
{
    public string Name => "revit_list_dimension_types";
    public string Description =>
        "Lists all dimension types in the active document with their style " +
        "(Linear, Angular, Radial, Diameter, ArcLength, SpotElevation, SpotCoordinate, SpotSlope). " +
        "Use the returned typeId as dimensionTypeId in revit_place_dimensions. " +
        "Optional: styleFilter (e.g. 'Linear') to narrow the list; details=true adds the fields revit_set_dimension_types " +
        "edits (text size mm, font, colour, line weight, tick mark, witness line extension and gap mm, units format, …); " +
        "includeUsage=true adds how many dimensions use each type.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;

        var styleFilter = ToolArguments.GetString(request.Arguments, "styleFilter").Trim();
        var details = ToolArguments.GetBool(request.Arguments, "details", false);
        var includeUsage = ToolArguments.GetBool(request.Arguments, "includeUsage", false);
        var kind = AnnotationTypeKind.Dimension;
        var usage = includeUsage ? kind.Usage(doc) : null;

        var types = new FilteredElementCollector(doc)
            .OfClass(typeof(DimensionType))
            .Cast<DimensionType>()
            .Where(t => !string.IsNullOrEmpty(t.Name))
            .Select(t =>
            {
                string style;
                try { style = t.StyleType.ToString(); } catch { style = "Unknown"; }
                return (Type: t, Style: style);
            })
            .Where(t => string.IsNullOrEmpty(styleFilter) || t.Style.Contains(styleFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Style)
            .ThenBy(t => t.Type.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => details
                ? AnnotationTypeReader.Describe(doc, t.Type, kind, "typeId", usage, null)
                : usage != null
                    ? new { typeId = t.Type.Id.Value, name = t.Type.Name, style = t.Style, usage = usage.TryGetValue(t.Type.Id.Value, out var n) ? n : 0 }
                    : (object)new { typeId = t.Type.Id.Value, name = t.Type.Name, style = t.Style })
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

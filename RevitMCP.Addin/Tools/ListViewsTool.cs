using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListViewsTool : IRevitMcpTool
{
    public string Name => "revit_list_views";
    public string Description =>
        "Lists views in the active Revit document. Supports filters: viewTypes (string array, e.g. [\"FloorPlan\",\"Section\"]), includeTemplates (bool, default false), " +
        "nameFilter (string, substring match on view name), includePlacedStatus (bool, default true), returnParameters (string array of extra parameter names), limit (int, default 0=all). " +
        "Returns: elementId, uniqueId, name, viewType, isTemplate, isPlacedOnSheet, sheetId/sheetNumber/sheetName (when placed), viewTemplateId, viewTemplateName, scale, discipline, plus any requested parameters.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    private static string TryGetDiscipline(View v)
    {
        try { return v.Discipline.ToString(); }
        catch { return string.Empty; }
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var viewTypesFilter  = ToolArguments.GetStringArray(request.Arguments, "viewTypes");
        var includeTemplates = ToolArguments.GetBool(request.Arguments, "includeTemplates", false);
        var nameFilter       = ToolArguments.GetString(request.Arguments, "nameFilter");
        var returnParams     = ToolArguments.GetStringArray(request.Arguments, "returnParameters");
        var limit            = ToolArguments.GetInt(request.Arguments, "limit", 0);

        // Build sheet→view mapping for placed status + sheet info
        var viewToSheet = new Dictionary<ElementId, (long sheetId, string sheetNumber, string sheetName)>();
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            foreach (var vid in sheet.GetAllPlacedViews())
                viewToSheet[vid] = (sheet.Id.Value, sheet.SheetNumber, sheet.Name);
        }

        // Template name lookup
        var templateNames = new Dictionary<ElementId, string>();
        foreach (var tv in new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Where(v => v.IsTemplate))
            templateNames[tv.Id] = tv.Name;

        ParameterReader? paramReader = returnParams.Length > 0 ? new ParameterReader() : null;
        var paramReadOpts = new ParameterReadOptions
        {
            IncludeInstanceParameters = true,
            IncludeTypeParameters = false,
            ParameterNames = returnParams.ToList(),
            ParameterNameMatchMode = "Contains"
        };

        IEnumerable<View> query = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => includeTemplates || !v.IsTemplate);

        if (viewTypesFilter.Length > 0)
            query = query.Where(v => viewTypesFilter.Any(vt =>
                string.Equals(v.ViewType.ToString(), vt, StringComparison.OrdinalIgnoreCase)));

        if (!string.IsNullOrWhiteSpace(nameFilter))
            query = query.Where(v => v.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        var ordered = query.OrderBy(v => v.ViewType.ToString()).ThenBy(v => v.Name);
        IEnumerable<View> final = limit > 0 ? ordered.Take(limit) : ordered;

        var views = new List<object>();
        foreach (var v in final)
        {
            viewToSheet.TryGetValue(v.Id, out var sheetInfo);
            templateNames.TryGetValue(v.ViewTemplateId, out var templateName);

            Dictionary<string, string?>? extraParams = null;
            if (paramReader != null)
            {
                var pvals = paramReader.ReadParameters(doc, v, paramReadOpts);
                var pd = new Dictionary<string, string?>();
                foreach (var p in pvals) pd[p.Name] = p.Value;
                extraParams = pd;
            }

            var entry = new Dictionary<string, object?>
            {
                ["elementId"]       = v.Id.Value,
                ["uniqueId"]        = v.UniqueId,
                ["name"]            = v.Name,
                ["viewType"]        = v.ViewType.ToString(),
                ["isTemplate"]      = v.IsTemplate,
                ["isPlacedOnSheet"] = viewToSheet.ContainsKey(v.Id),
                ["sheetId"]         = viewToSheet.ContainsKey(v.Id) ? sheetInfo.sheetId : (object?)null,
                ["sheetNumber"]     = viewToSheet.ContainsKey(v.Id) ? sheetInfo.sheetNumber : null,
                ["sheetName"]       = viewToSheet.ContainsKey(v.Id) ? sheetInfo.sheetName : null,
                ["viewTemplateId"]  = v.ViewTemplateId != ElementId.InvalidElementId ? v.ViewTemplateId.Value : (object?)null,
                ["viewTemplateName"]= templateName,
                ["scale"]           = v.Scale > 0 ? $"1:{v.Scale}" : "N/A",
                ["discipline"]      = TryGetDiscipline(v)
            };
            if (extraParams != null)
                entry["parameters"] = extraParams;

            views.Add(entry);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {views.Count} views.",
            Data = new { count = views.Count, views },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

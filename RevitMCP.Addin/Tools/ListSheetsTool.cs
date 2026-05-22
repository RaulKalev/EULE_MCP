using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListSheetsTool : IRevitMcpTool
{
    public string Name => "revit_list_sheets";
    public string Description =>
        "Lists sheets in the active Revit document. Supports: nameFilter (string), numberFilter (string), " +
        "returnParameters (string array — defaults to [Sheet Number, Sheet Name, Project Number, Project Status, " +
        "Projekti osa, Grupi tähis, Järjekorra tähis, Current Revision, Märkus, Sheet Issue Date] when set to [\"default\"]), " +
        "includeViewports (bool, default false — include viewport detail), limit (int, default 0=all). " +
        "Returns: elementId, uniqueId, sheetNumber, sheetName, titleBlockId, titleBlockName, viewportCount, placedViews, plus any requested parameters.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Sheets;

    private static readonly string[] DefaultReturnParameters =
    [
        "Sheet Number", "Sheet Name", "Project Number", "Project Status",
        "Projekti osa", "Grupi tähis", "Järjekorra tähis",
        "Current Revision", "Märkus", "Sheet Issue Date"
    ];

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var nameFilter       = ToolArguments.GetString(request.Arguments, "nameFilter");
        var numberFilter     = ToolArguments.GetString(request.Arguments, "numberFilter");
        var includeViewports = ToolArguments.GetBool(request.Arguments, "includeViewports", false);
        var returnParamsArg  = ToolArguments.GetStringArray(request.Arguments, "returnParameters");
        var limit            = ToolArguments.GetInt(request.Arguments, "limit", 0);

        // Expand "default" keyword to standard Estonian/Revit sheet params
        string[] returnParams = returnParamsArg.Length == 1 && returnParamsArg[0] == "default"
            ? DefaultReturnParameters
            : returnParamsArg;

        ParameterReader? paramReader = returnParams.Length > 0 ? new ParameterReader() : null;
        var paramReadOpts = new ParameterReadOptions
        {
            IncludeInstanceParameters = true,
            IncludeTypeParameters = false,
            ParameterNames = returnParams.ToList(),
            ParameterNameMatchMode = "Contains"
        };

        IEnumerable<ViewSheet> query = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .OrderBy(s => s.SheetNumber);

        if (!string.IsNullOrWhiteSpace(nameFilter))
            query = query.Where(s => s.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(numberFilter))
            query = query.Where(s => s.SheetNumber.Contains(numberFilter, StringComparison.OrdinalIgnoreCase));
        if (limit > 0)
            query = query.Take(limit);

        var sheets = new List<object>();
        foreach (var s in query)
        {
            var placedViews = s.GetAllPlacedViews()
                .Select(vid => doc.GetElement(vid))
                .Where(v => v != null)
                .Select(v => v!.Name)
                .ToList();

            // Titleblock
            long? tbId = null;
            string? tbName = null;
            try
            {
                var tb = new FilteredElementCollector(doc)
                    .OwnedByView(s.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilyInstance>()
                    .FirstOrDefault();
                if (tb != null)
                {
                    tbId = tb.Id.Value;
                    tbName = $"{tb.Symbol.FamilyName} : {tb.Symbol.Name}";
                }
            }
            catch { }

            List<object>? viewportDetails = null;
            if (includeViewports)
            {
                viewportDetails = new List<object>();
                try
                {
                    foreach (var vpId in s.GetAllViewports())
                    {
                        if (doc.GetElement(vpId) is Viewport vp)
                        {
                            var linkedView = doc.GetElement(vp.ViewId);
                            viewportDetails.Add(new
                            {
                                viewportId = vp.Id.Value,
                                viewId = vp.ViewId.Value,
                                viewName = linkedView?.Name
                            });
                        }
                    }
                }
                catch { }
            }

            Dictionary<string, string?>? extraParams = null;
            if (paramReader != null)
            {
                var pvals = paramReader.ReadParameters(doc, s, paramReadOpts);
                extraParams = pvals.ToDictionary(p => p.Name, p => p.Value);
            }

            var entry = new Dictionary<string, object?>
            {
                ["elementId"]      = s.Id.Value,
                ["uniqueId"]       = s.UniqueId,
                ["sheetNumber"]    = s.SheetNumber,
                ["sheetName"]      = s.Name,
                ["titleBlockId"]   = tbId,
                ["titleBlockName"] = tbName,
                ["viewportCount"]  = placedViews.Count,
                ["placedViews"]    = placedViews
            };
            if (includeViewports)
                entry["viewports"] = viewportDetails;
            if (extraParams != null)
                entry["parameters"] = extraParams;

            sheets.Add(entry);
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Returned {sheets.Count} sheets.",
            Data = new { count = sheets.Count, sheets },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

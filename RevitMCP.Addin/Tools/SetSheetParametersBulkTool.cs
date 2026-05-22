using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Tools;

public class SetSheetParametersBulkTool : IRevitMcpTool
{
    public string Name => "revit_set_sheet_parameters_bulk";
    public string Description =>
        "Sets one or more parameters on multiple sheets in a single transaction. Requires approval. " +
        "Required: sheetIds (long array) OR sheetNumbers (string array), " +
        "parameters (object — key=parameterName, value=value). " +
        "Optional: nameFilter (string).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var sheetIds    = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNums   = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var nameFilter  = ToolArguments.GetString(request.Arguments, "nameFilter");
        var paramValues = GetParamDict(request.Arguments);

        if (sheetIds.Length == 0 && sheetNums.Length == 0 && string.IsNullOrWhiteSpace(nameFilter))
            return Task.FromResult(Fail(request, "Provide sheetIds, sheetNumbers, or nameFilter."));
        if (paramValues.Count == 0)
            return Task.FromResult(Fail(request, "parameters object is required."));

        IEnumerable<ViewSheet> sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>();

        if (sheetIds.Length > 0)       sheets = sheets.Where(s => sheetIds.ToHashSet().Contains(s.Id.Value));
        else if (sheetNums.Length > 0) sheets = sheets.Where(s => sheetNums.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains(s.SheetNumber));
        if (!string.IsNullOrWhiteSpace(nameFilter)) sheets = sheets.Where(s => s.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));

        int updated  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Set Sheet Parameters Bulk");
        t.Start();
        foreach (var sheet in sheets)
        {
            var setCount = 0;
            foreach (var (pName, pVal) in paramValues)
            {
                try
                {
                    var p = FindParameter(sheet, pName);
                    if (p == null) { warnings.Add($"Sheet '{sheet.SheetNumber}': param '{pName}' not found."); continue; }
                    if (p.IsReadOnly) { warnings.Add($"Sheet '{sheet.SheetNumber}': param '{pName}' is read-only."); continue; }
                    SetParam(p, pVal?.ToString() ?? "");
                    setCount++;
                }
                catch (Exception ex) { warnings.Add($"Sheet '{sheet.SheetNumber}', param '{pName}': {ex.Message}"); }
            }
            if (setCount > 0) { updated++; results.Add(new { sheetId = sheet.Id.Value, sheetNumber = sheet.SheetNumber }); }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = updated > 0,
            Message    = $"Updated parameters on {updated} sheet(s).",
            Data       = new { updated, results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static void SetParam(Parameter p, string strVal)
    {
        switch (p.StorageType)
        {
            case StorageType.String:  p.Set(strVal); break;
            case StorageType.Integer when int.TryParse(strVal, out var iv): p.Set(iv); break;
            case StorageType.Double  when double.TryParse(strVal,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var dv): p.Set(dv); break;
        }
    }

    private static Dictionary<string, object?> GetParamDict(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("parameters", out var val)) return [];
        if (val is JObject jo)
            return jo.Properties().ToDictionary(p => p.Name, p => (object?)p.Value.ToObject<object>());
        return [];
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };

    /// <summary>Looks up a parameter by exact name, then falls back to case-insensitive contains match.</summary>
    private static Parameter? FindParameter(Element element, string name)
    {
        var exact = element.LookupParameter(name);
        if (exact != null) return exact;
        Parameter? match = null;
        foreach (Parameter p in element.Parameters)
        {
            if (p.Definition.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                if (match != null) return null; // ambiguous — require exact name
                match = p;
            }
        }
        return match;
    }
}

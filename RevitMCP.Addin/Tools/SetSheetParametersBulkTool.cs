using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
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
        "Optional: nameFilter (string), includeTitleBlockInstance (bool: a parameter the sheet does not have is written " +
        "to the instance of the title block on the sheet).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;

        var sheetIds    = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNums   = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var nameFilter  = ToolArguments.GetString(request.Arguments, "nameFilter");
        var paramValues = GetParamDict(request.Arguments);
        var includeTitleBlock = ToolArguments.GetBool(request.Arguments, "includeTitleBlockInstance");

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

        cancellationToken.ThrowIfCancellationRequested();
        using var t = new Transaction(doc, "Revit MCP - Set Sheet Parameters Bulk");
        t.Start();
        foreach (var sheet in sheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var setCount = 0;
            var titleBlockSet = new List<string>();
            var titleBlock = includeTitleBlock ? GetTitleBlockValuesTool.TitleBlocksOf(doc, sheet).FirstOrDefault() : null;
            foreach (var (pName, pVal) in paramValues)
            {
                try
                {
                    var p = ParameterFinder.Find(sheet, pName, out var problem);
                    var onTitleBlock = false;
                    if (p == null && titleBlock != null)
                    {
                        p = ParameterFinder.Find(titleBlock, pName, out var titleBlockProblem);
                        onTitleBlock = p != null;
                        if (p == null) problem = titleBlockProblem;
                    }
                    if (p == null) { warnings.Add($"Sheet '{sheet.SheetNumber}': {problem}"); continue; }
                    if (p.IsReadOnly) { warnings.Add($"Sheet '{sheet.SheetNumber}': param '{pName}' is read-only."); continue; }
                    SetParam(p, pVal?.ToString() ?? "");
                    setCount++;
                    if (onTitleBlock) titleBlockSet.Add(pName);
                }
                catch (Exception ex) { warnings.Add($"Sheet '{sheet.SheetNumber}', param '{pName}': {ex.Message}"); }
            }
            if (setCount > 0) { updated++; results.Add(new { sheetId = sheet.Id.Value, sheetNumber = sheet.SheetNumber, titleBlockParameters = titleBlockSet.Count > 0 ? titleBlockSet : null }); }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);

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
        JObject? jo = val switch
        {
            JObject j => j,
            JToken t  => t as JObject,
            string s  => ToolArguments.TryParseJObject(s),
            _         => null
        };
        if (jo == null) return [];
        return jo.Properties().ToDictionary(p => p.Name, p => (object?)p.Value.ToObject<object>());
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

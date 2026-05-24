using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Excel;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Excel;

public class ExcelAppendTableRowsTool : IRevitMcpTool
{
    public string Name => "excel_append_table_rows";
    public string Description => "Appends rows after the last data row in a worksheet, matching values to columns by header name. Optionally targets a named Excel table. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Excel;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath = ToolArguments.GetString(request.Arguments, "filePath");
        var worksheetName = ToolArguments.GetString(request.Arguments, "worksheetName");
        var tableName = ToolArguments.GetString(request.Arguments, "tableName");
        var matchHeaders = ToolArguments.GetBool(request.Arguments, "matchHeaders", true);
        var backup = ToolArguments.GetBool(request.Arguments, "backupBeforeSave", true);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));
        if (string.IsNullOrWhiteSpace(worksheetName))
            return Task.FromResult(Fail(request, "worksheetName is required."));

        var rows = ParseRowDicts(request.Arguments, "rows");
        if (rows.Count == 0)
            return Task.FromResult(Fail(request, "No rows provided in 'rows' array."));

        var (result, error) = ExcelWorkbookModifier.AppendTableRows(
            filePath, worksheetName, tableName, matchHeaders, rows, backup);
        sw.Stop();

        if (error != null)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = error,
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Appended rows to '{worksheetName}'.",
            Data = result,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static List<Dictionary<string, string>> ParseRowDicts(Dictionary<string, object?> args, string key)
    {
        var result = new List<Dictionary<string, string>>();
        if (!args.TryGetValue(key, out var raw) || raw is not JArray arr) return result;

        foreach (var item in arr)
        {
            if (item is not JObject obj) continue;
            var dict = new Dictionary<string, string>();
            foreach (var prop in obj.Properties())
                dict[prop.Name] = prop.Value.Value<string>() ?? string.Empty;
            result.Add(dict);
        }

        return result;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Excel;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Excel;

public class ExcelReadRangeTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name => "excel_read_range";
    public string Description => "Reads a specific cell range from an Excel worksheet. Returns values, formulas, and data types. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Excel;

    private static readonly FilePathPolicy _policy = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath = ToolArguments.GetString(request.Arguments, "filePath");
        var worksheetName = ToolArguments.GetString(request.Arguments, "worksheetName");
        var rangeAddress = ToolArguments.GetString(request.Arguments, "rangeAddress");
        var includeFormulas = ToolArguments.GetBool(request.Arguments, "includeFormulas", false);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));
        if (string.IsNullOrWhiteSpace(worksheetName))
            return Task.FromResult(Fail(request, "worksheetName is required."));
        if (string.IsNullOrWhiteSpace(rangeAddress))
            return Task.FromResult(Fail(request, "rangeAddress is required."));

        var policyError = _policy.ValidateRead(filePath);
        if (policyError != null)
            return Task.FromResult(Fail(request, policyError));

        var (result, error) = ExcelWorkbookInspector.ReadRange(filePath, worksheetName, rangeAddress, includeFormulas);
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
            Message = $"Read range {rangeAddress} from sheet '{worksheetName}'.",
            Data = result,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

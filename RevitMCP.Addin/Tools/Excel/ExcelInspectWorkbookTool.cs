using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Excel;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Excel;

public class ExcelInspectWorkbookTool : IRevitMcpTool
{
    public string Name => "excel_inspect_workbook";
    public string Description => "Reads Excel workbook metadata (sheet names, used ranges, headers) without modifying the file. Optionally returns preview rows.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Excel;

    private static readonly FilePathPolicy _policy = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath = ToolArguments.GetString(request.Arguments, "filePath");
        var includePreview = ToolArguments.GetBool(request.Arguments, "includePreviewRows", false);
        var previewCount = ToolArguments.GetInt(request.Arguments, "previewRowCount", 10);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));

        var policyError = _policy.ValidateRead(filePath);
        if (policyError != null)
            return Task.FromResult(Fail(request, policyError));

        var (result, error) = ExcelWorkbookInspector.Inspect(filePath, includePreview, previewCount);
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
            Message = $"Inspected workbook with {result!.Worksheets.Count} visible sheet(s).",
            Data = result,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

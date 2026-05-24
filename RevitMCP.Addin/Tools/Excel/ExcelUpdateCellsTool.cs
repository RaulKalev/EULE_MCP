using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Excel;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Excel;

public class ExcelUpdateCellsTool : IRevitMcpTool
{
    public string Name => "excel_update_cells";
    public string Description => "Updates specific cells in an existing Excel file without changing formatting. Requires approval. Creates a timestamped backup by default. Set dryRun=true to preview changes without saving.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Excel;

    private static readonly FilePathPolicy _policy = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath = ToolArguments.GetString(request.Arguments, "filePath");
        var worksheetName = ToolArguments.GetString(request.Arguments, "worksheetName");
        var backup = ToolArguments.GetBool(request.Arguments, "backupBeforeSave", true);
        var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", false);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));
        if (string.IsNullOrWhiteSpace(worksheetName))
            return Task.FromResult(Fail(request, "worksheetName is required."));

        var policyError = _policy.ValidateWrite(filePath);
        if (policyError != null)
            return Task.FromResult(Fail(request, policyError));

        var updates = ParseCellUpdates(request.Arguments);
        if (updates.Count == 0)
            return Task.FromResult(Fail(request, "No cell updates provided in 'updates' array."));

        var (result, error) = ExcelWorkbookModifier.UpdateCells(filePath, worksheetName, updates, backup, dryRun);
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
            Message = dryRun
                ? $"[DRY RUN] Would update {updates.Count} cell(s) in '{worksheetName}'."
                : $"Updated {updates.Count} cell(s) in '{worksheetName}'.",
            Data = result,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static List<(string Cell, string? Value)> ParseCellUpdates(Dictionary<string, object?> args)
    {
        var result = new List<(string, string?)>();
        if (!args.TryGetValue("updates", out var raw) || raw is not JArray arr) return result;

        foreach (var item in arr)
        {
            var cell = item["cell"]?.Value<string>() ?? string.Empty;
            var value = item["value"]?.Value<string>();
            if (!string.IsNullOrWhiteSpace(cell))
                result.Add((cell, value));
        }

        return result;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FileSystem;

public class FileWriteTextTool : IRevitMcpTool
{
    public string Name => "file_write_text";
    public string Description => "Writes a UTF-8 text file to disk. Requires approval. Will not overwrite unless overwrite=true. Creates parent directories if createDirectories=true.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FileSystem;

    private static readonly FileSystemService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath = ToolArguments.GetString(request.Arguments, "filePath");
        var content = ToolArguments.GetString(request.Arguments, "content");
        var overwrite = ToolArguments.GetBool(request.Arguments, "overwrite", false);
        var createDirs = ToolArguments.GetBool(request.Arguments, "createDirectories", true);
        var backupBeforeOverwrite = ToolArguments.GetBool(request.Arguments, "backupBeforeOverwrite", overwrite);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));

        var result = _service.WriteText(filePath, content, overwrite, createDirs, backupBeforeOverwrite);
        sw.Stop();

        if (!result.Success)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = result.Error!,
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = result.WasOverwritten
                ? $"File overwritten at {result.FilePath} ({result.SizeBytes:N0} bytes)"
                : $"File created at {result.FilePath} ({result.SizeBytes:N0} bytes)",
            Data = new
            {
                filePath = result.FilePath,
                sizeBytes = result.SizeBytes,
                wasOverwritten = result.WasOverwritten,
                createdNew = !result.WasOverwritten,
                backupPath = result.BackupPath
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

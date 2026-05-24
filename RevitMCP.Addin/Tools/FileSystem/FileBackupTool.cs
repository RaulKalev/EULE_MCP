using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FileSystem;

public class FileBackupTool : IRevitMcpTool
{
    public string Name => "file_backup";
    public string Description => "Creates a timestamped backup copy of a file. Requires user approval. Backup name format: <stem>_<suffix>_<timestamp><ext>. Default suffix is 'backup'.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FileSystem;

    private static readonly FileSystemService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath           = ToolArguments.GetString(request.Arguments, "filePath");
        var backupDirectory    = ToolArguments.GetString(request.Arguments, "backupDirectory");
        var suffix             = ToolArguments.GetString(request.Arguments, "suffix");
        var preserveTimestamps = ToolArguments.GetBool(request.Arguments, "preserveTimestamps", true);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));

        if (string.IsNullOrWhiteSpace(suffix)) suffix = "backup";

        var result = _service.BackupFile(filePath, backupDirectory ?? string.Empty, suffix, preserveTimestamps);
        sw.Stop();

        if (!result.Success)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = result.Error!,
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Backup created: {result.BackupPath} ({result.SizeBytes:N0} bytes)",
            Data       = new
            {
                sourcePath          = result.SourcePath,
                backupPath          = result.BackupPath,
                sizeBytes           = result.SizeBytes,
                preservedTimestamps = result.PreservedTimestamps
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

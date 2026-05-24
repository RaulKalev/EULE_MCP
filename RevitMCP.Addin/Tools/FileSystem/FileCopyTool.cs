using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FileSystem;

public class FileCopyTool : IRevitMcpTool
{
    public string Name => "file_copy";
    public string Description => "Copies a file to a destination path. Requires user approval. Will not overwrite an existing destination unless overwrite=true.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FileSystem;

    private static readonly FileSystemService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var sourcePath      = ToolArguments.GetString(request.Arguments, "sourcePath");
        var destinationPath = ToolArguments.GetString(request.Arguments, "destinationPath");
        var overwrite           = ToolArguments.GetBool(request.Arguments, "overwrite", false);
        var createDirectories   = ToolArguments.GetBool(request.Arguments, "createDirectories", true);
        var preserveTimestamps  = ToolArguments.GetBool(request.Arguments, "preserveTimestamps", true);

        if (string.IsNullOrWhiteSpace(sourcePath))
            return Task.FromResult(Fail(request, "sourcePath is required."));
        if (string.IsNullOrWhiteSpace(destinationPath))
            return Task.FromResult(Fail(request, "destinationPath is required."));

        var result = _service.CopyFile(sourcePath, destinationPath, overwrite, createDirectories, preserveTimestamps);
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
            Message    = result.Overwritten
                ? $"File copied (overwritten) to {result.DestinationPath} ({result.SizeBytes:N0} bytes)"
                : $"File copied to {result.DestinationPath} ({result.SizeBytes:N0} bytes)",
            Data       = new
            {
                sourcePath          = result.SourcePath,
                destinationPath     = result.DestinationPath,
                sizeBytes           = result.SizeBytes,
                overwritten         = result.Overwritten,
                preservedTimestamps = result.PreservedTimestamps
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

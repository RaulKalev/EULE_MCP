using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FileSystem;

public class FileInspectTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name => "file_inspect";
    public string Description => "Inspects a file or folder: returns existence, type, size, timestamps, attributes and optional SHA-256 hash. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FileSystem;

    private static readonly FileSystemService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath = ToolArguments.GetString(request.Arguments, "filePath");
        var includeHash = ToolArguments.GetBool(request.Arguments, "includeHash", false);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));

        var result = _service.Inspect(filePath, includeHash);
        sw.Stop();

        if (!result.Success)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = result.Error!,
                DurationMs = sw.ElapsedMilliseconds
            });

        if (!result.Exists)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = true,
                Message    = $"Path does not exist: {result.Path}",
                Data       = new { exists = false, path = result.Path },
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"{result.Type}: {result.Path} ({result.SizeBytes:N0} bytes)",
            Data       = new
            {
                exists        = true,
                path          = result.Path,
                type          = result.Type,
                extension     = result.Extension,
                sizeBytes     = result.SizeBytes,
                createdAtUtc  = result.CreatedAtUtc,
                modifiedAtUtc = result.ModifiedAtUtc,
                isReadOnly    = result.IsReadOnly,
                attributes    = result.Attributes,
                hashSha256    = result.HashSha256
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FileSystem;

public class FileReadTextTool : IRevitMcpTool
{
    public string Name => "file_read_text";
    public string Description => "Reads a UTF-8 text file from a local path. Returns content and metadata. Default max 1 MB. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FileSystem;

    private static readonly FileSystemService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var filePath = ToolArguments.GetString(request.Arguments, "filePath");
        var maxBytes = ToolArguments.GetInt(request.Arguments, "maxBytes", 0);

        if (string.IsNullOrWhiteSpace(filePath))
            return Task.FromResult(Fail(request, "filePath is required."));

        var result = _service.ReadText(filePath, maxBytes);
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
            Message = $"Read {result.SizeBytes:N0} bytes from {result.FilePath}",
            Data = new
            {
                filePath = result.FilePath,
                exists = result.Exists,
                sizeBytes = result.SizeBytes,
                content = result.Content
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

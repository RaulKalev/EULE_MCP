using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FileSystem;

public class FileListDirectoryTool : IRevitMcpTool
{
    public string Name => "file_list_directory";
    public string Description => "Lists files and folders in a directory. Supports searchPattern (e.g. *.xlsx), recursive flag, and maxResults cap. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FileSystem;

    private static readonly FileSystemService _service = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var folderPath = ToolArguments.GetString(request.Arguments, "folderPath");
        var searchPattern = ToolArguments.GetString(request.Arguments, "searchPattern", "*");
        var recursive = ToolArguments.GetBool(request.Arguments, "recursive", false);
        var maxResults = ToolArguments.GetInt(request.Arguments, "maxResults", 500);

        if (string.IsNullOrWhiteSpace(folderPath))
            return Task.FromResult(Fail(request, "folderPath is required."));

        var result = _service.ListDirectory(folderPath, searchPattern, recursive, maxResults);
        sw.Stop();

        if (!result.Success)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = result.Error!,
                DurationMs = sw.ElapsedMilliseconds
            });

        var warnings = new List<string>();
        if (result.Truncated)
            warnings.Add($"Results truncated at {result.MaxResults}. Use a more specific searchPattern or increase maxResults.");

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {result.Entries.Count} entries in {result.FolderPath}.",
            Data = new
            {
                folderPath = result.FolderPath,
                entryCount = result.Entries.Count,
                truncated = result.Truncated,
                entries = result.Entries.Select(e => new
                {
                    name = e.Name,
                    fullPath = e.FullPath,
                    extension = e.Extension,
                    sizeBytes = e.SizeBytes,
                    modifiedAt = e.ModifiedAt,
                    type = e.Type
                }).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

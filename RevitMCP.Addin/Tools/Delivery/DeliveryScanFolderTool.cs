using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Delivery;

/// <summary>
/// Scans a delivery folder and returns a structured file inventory with parsed EULE drawing names.
/// Read-only; enforces FileAccessPolicy for allowed read paths.
/// </summary>
public class DeliveryScanFolderTool : IRevitMcpTool
{
    public string Name => "delivery_scan_folder";
    public string Description => "Scans a delivery folder and returns a structured file inventory with parsed EULE drawing names. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Delivery;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var folderPath = ToolArguments.GetString(request.Arguments, "folderPath");
        if (string.IsNullOrWhiteSpace(folderPath))
            return Task.FromResult(Fail(request, "folderPath is required."));

        var recursive = ToolArguments.GetBool(request.Arguments, "recursive", true);
        var extArr = ToolArguments.GetStringArray(request.Arguments, "includeExtensions");
        var maxResults = ToolArguments.GetInt(request.Arguments, "maxResults", 5000);

        IEnumerable<string>? includeExtensions = extArr.Length > 0 ? extArr : null;

        var policy = new FilePathPolicy();
        var scanner = new DeliveryFileScanner(policy);
        var result = scanner.Scan(folderPath, recursive, includeExtensions, maxResults);

        if (!result.Success)
            return Task.FromResult(Fail(request, result.Error!));

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Scanned {result.Files.Count} file(s) in '{folderPath}'.",
            Data = new
            {
                folderPath = result.FolderPath,
                totalFiles = result.Files.Count,
                parsedFiles = result.Files.Count(f => f.Parsed != null),
                unparsedFiles = result.Files.Count(f => f.Parsed == null),
                files = result.Files.Select(f => new
                {
                    fileName = f.FileName,
                    extension = f.Extension,
                    fullPath = f.FullPath,
                    sizeBytes = f.SizeBytes,
                    modifiedAt = f.ModifiedAt,
                    parsed = f.Parsed == null ? null : (object)new
                    {
                        projectNumber = f.Parsed.ProjectNumber,
                        stage = f.Parsed.Stage,
                        sheetNumber = f.Parsed.SheetNumber,
                        discipline = f.Parsed.Discipline,
                        group = f.Parsed.Group,
                        sequence = f.Parsed.Sequence,
                        description = f.Parsed.Description,
                        revision = f.Parsed.Revision
                    }
                }).ToList(),
                warnings = result.Warnings
            },
            Warnings = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

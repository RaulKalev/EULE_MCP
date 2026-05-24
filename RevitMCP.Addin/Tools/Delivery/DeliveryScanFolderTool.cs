using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

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

        // Optional folder-policy checks
        var folderPolicy = BuildFolderPolicy(request);
        var policyIssues = new List<RevitMCP.Core.Models.Issues.IssueDto>();
        if (folderPolicy != null)
        {
            int seq = result.Files.Count + 1;
            var runId = Guid.NewGuid().ToString("N")[..8];
            policyIssues = DeliveryFolderPolicyChecker.Check(result, folderPolicy, runId, ref seq);
        }

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
                subFolders = result.SubFolders,
                warnings = result.Warnings,
                policyIssueCount = policyIssues.Count,
                policyIssues = policyIssues
            },
            Warnings = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static DeliveryFolderPolicy? BuildFolderPolicy(McpToolRequest request)
    {
        var args = request.Arguments;
        bool hasAny = ToolArguments.GetBool(args, "checkTempFiles", false)
                   || ToolArguments.GetBool(args, "checkOldRevisions", false)
                   || ToolArguments.GetBool(args, "checkSuspiciousExtensions", false)
                   || ToolArguments.GetBool(args, "checkRequiredFolders", false)
                   || ToolArguments.GetStringArray(args, "requiredProjectFileExtensions").Length > 0;
        if (!hasAny) return null;

        var policy = new DeliveryFolderPolicy
        {
            CheckTempFiles             = ToolArguments.GetBool(args, "checkTempFiles", true),
            CheckOldRevisions          = ToolArguments.GetBool(args, "checkOldRevisions", true),
            CheckSuspiciousExtensions  = ToolArguments.GetBool(args, "checkSuspiciousExtensions", false),
            CheckRequiredFolders       = ToolArguments.GetBool(args, "checkRequiredFolders", false),
        };
        var requiredFolders = ToolArguments.GetStringArray(args, "requiredFolders");
        if (requiredFolders.Length > 0) policy.RequiredFolders = [.. requiredFolders];

        var allowedExtras = ToolArguments.GetStringArray(args, "allowedExtraExtensions");
        if (allowedExtras.Length > 0) policy.AllowedExtraExtensions = [.. allowedExtras];

        var reqProjectExts = ToolArguments.GetStringArray(args, "requiredProjectFileExtensions");
        if (reqProjectExts.Length > 0) policy.RequiredProjectFileExtensions = [.. reqProjectExts];

        var ignored = ToolArguments.GetStringArray(args, "ignoredPatterns");
        if (ignored.Length > 0) policy.IgnoredPatterns = [.. ignored];

        return policy;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}


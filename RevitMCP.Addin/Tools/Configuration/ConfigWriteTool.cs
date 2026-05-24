using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Configuration;

/// <summary>
/// Writes a complete JSON object to a config file, replacing all existing content.
/// </summary>
public class ConfigWriteTool : IRevitMcpTool
{
    public string Name => "config_write";
    public string Description => "Replaces the entire content of a JSON config file for a given scope (company, user, project, tool-state). Requires approval. Creates a timestamped backup by default.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Configuration;

    private static readonly JsonConfigService _svc = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var scope               = ToolArguments.GetString(request.Arguments, "scope");
        var projectRoot         = ToolArguments.GetString(request.Arguments, "projectRoot");
        var jsonContent         = ToolArguments.GetString(request.Arguments, "jsonContent");
        var backupBeforeOverwrite = ToolArguments.GetBool(request.Arguments, "backupBeforeOverwrite", true);

        if (string.IsNullOrWhiteSpace(scope))
            return Task.FromResult(Fail(request, "scope is required (company | user | project | tool-state)."));
        if (string.IsNullOrWhiteSpace(jsonContent))
            return Task.FromResult(Fail(request, "jsonContent is required."));

        var (filePath, pathErr) = ConfigPathResolver.Resolve(scope, projectRoot);
        if (pathErr != null) return Task.FromResult(Fail(request, pathErr));

        var (success, writeErr, backupPath) = _svc.Write(filePath!, jsonContent, backupBeforeOverwrite);
        sw.Stop();

        if (!success)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = writeErr!,
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Config written to {filePath}",
            Data       = new { scope, filePath, backupPath },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

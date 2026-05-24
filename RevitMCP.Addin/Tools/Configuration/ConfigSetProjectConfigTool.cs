using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Configuration;

/// <summary>
/// Writes or replaces the project-scoped config file (.rktools/mcp.project.config.json).
/// </summary>
public class ConfigSetProjectConfigTool : IRevitMcpTool
{
    public string Name => "config_set_project_config";
    public string Description => "Writes or replaces the project-scoped MCP config file (.rktools/mcp.project.config.json) inside the specified project root. Requires approval. Creates a timestamped backup by default.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Configuration;

    private static readonly JsonConfigService _svc = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var projectRoot           = ToolArguments.GetString(request.Arguments, "projectRoot");
        var jsonContent           = ToolArguments.GetString(request.Arguments, "jsonContent");
        var backupBeforeOverwrite = ToolArguments.GetBool(request.Arguments, "backupBeforeOverwrite", true);

        if (string.IsNullOrWhiteSpace(projectRoot))
            return Task.FromResult(Fail(request, "projectRoot is required."));
        if (string.IsNullOrWhiteSpace(jsonContent))
            return Task.FromResult(Fail(request, "jsonContent is required."));

        var (filePath, pathErr) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeProject, projectRoot);
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
            Message    = $"Project config written to {filePath}",
            Data       = new { projectRoot, filePath, backupPath },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

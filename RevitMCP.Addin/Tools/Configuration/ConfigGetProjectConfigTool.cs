using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Configuration;

/// <summary>
/// Reads the project-scoped config file (.rktools/mcp.project.config.json) relative to a given project root.
/// </summary>
public class ConfigGetProjectConfigTool : IRevitMcpTool
{
    public string Name => "config_get_project_config";
    public string Description => "Reads the project-scoped MCP config file (.rktools/mcp.project.config.json) located inside the specified project root folder. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Configuration;

    private static readonly JsonConfigService _svc = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var projectRoot     = ToolArguments.GetString(request.Arguments, "projectRoot");
        var createIfMissing = ToolArguments.GetBool(request.Arguments, "createIfMissing", false);

        if (string.IsNullOrWhiteSpace(projectRoot))
            return Task.FromResult(Fail(request, "projectRoot is required."));

        var (filePath, pathErr) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeProject, projectRoot);
        if (pathErr != null) return Task.FromResult(Fail(request, pathErr));

        var (config, readErr) = _svc.Read(filePath!, createIfMissing);
        sw.Stop();

        if (readErr != null)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = readErr,
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Project config read from {filePath}",
            Data       = new { projectRoot, filePath, config },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

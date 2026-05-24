using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Configuration;

/// <summary>
/// Reads a JSON config file by scope (company | user | project | tool-state).
/// </summary>
public class ConfigReadTool : IRevitMcpTool
{
    public string Name => "config_read";
    public string Description => "Reads a JSON configuration file for a given scope (company, user, project, tool-state). Read-only. Returns the config as a JSON object.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Configuration;

    private static readonly JsonConfigService _svc = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var scope           = ToolArguments.GetString(request.Arguments, "scope");
        var projectRoot     = ToolArguments.GetString(request.Arguments, "projectRoot");
        var createIfMissing = ToolArguments.GetBool(request.Arguments, "createIfMissing", false);

        if (string.IsNullOrWhiteSpace(scope))
            return Task.FromResult(Fail(request, "scope is required (company | user | project | tool-state)."));

        var (filePath, pathErr) = ConfigPathResolver.Resolve(scope, projectRoot);
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
            Message    = $"Config read from {filePath}",
            Data       = new { scope, filePath, config },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

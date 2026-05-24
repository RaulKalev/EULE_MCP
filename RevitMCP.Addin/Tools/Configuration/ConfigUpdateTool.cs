using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Configuration;

/// <summary>
/// Updates specific properties inside a JSON config file using dot-path keys
/// (e.g. "excel.defaultBackupBeforeSave" or "$.excel.defaultBackupBeforeSave").
/// </summary>
public class ConfigUpdateTool : IRevitMcpTool
{
    public string Name => "config_update";
    public string Description => "Updates specific properties in a JSON config file using dot-path keys (e.g. '$.excel.defaultBackupBeforeSave'). Requires approval. Creates the file if it does not exist when createIfMissing=true.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Configuration;

    private static readonly JsonConfigService _svc = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var scope               = ToolArguments.GetString(request.Arguments, "scope");
        var projectRoot         = ToolArguments.GetString(request.Arguments, "projectRoot");
        var backupBeforeOverwrite = ToolArguments.GetBool(request.Arguments, "backupBeforeOverwrite", true);
        var createIfMissing     = ToolArguments.GetBool(request.Arguments, "createIfMissing", false);

        if (string.IsNullOrWhiteSpace(scope))
            return Task.FromResult(Fail(request, "scope is required (company | user | project | tool-state)."));

        var updates = ParseUpdates(request.Arguments);
        if (updates.Count == 0)
            return Task.FromResult(Fail(request, "No updates provided in 'updates' object."));

        var (filePath, pathErr) = ConfigPathResolver.Resolve(scope, projectRoot);
        if (pathErr != null) return Task.FromResult(Fail(request, pathErr));

        var (success, writeErr, backupPath, applied) =
            _svc.Update(filePath!, updates, backupBeforeOverwrite, createIfMissing);
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
            Message    = $"Config updated: {applied.Count} property/properties set in {filePath}",
            Data       = new { scope, filePath, backupPath, updatedPaths = applied },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static Dictionary<string, JsonNode?> ParseUpdates(Dictionary<string, object?> args)
    {
        var result = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);
        if (!args.TryGetValue("updates", out var raw)) return result;

        if (raw is JObject jo)
        {
            foreach (var prop in jo.Properties())
                result[prop.Name] = ToJsonNode(prop.Value);
        }
        return result;
    }

    private static JsonNode? ToJsonNode(JToken token)
    {
        if (token.Type == JTokenType.Null) return null;
        return JsonNode.Parse(token.ToString(Newtonsoft.Json.Formatting.None));
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

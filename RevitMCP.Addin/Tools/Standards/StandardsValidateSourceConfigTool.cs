using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Standards;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Standards;

/// <summary>
/// Validates the StandardsSources.json config — checks paths exist and IDs are unique.
/// Tool name: standards_validate_source_config
/// </summary>
public class StandardsValidateSourceConfigTool : IRevitMcpTool, IBackgroundMcpTool
{
    public string Name        => "standards_validate_source_config";
    public string Description => "Validates the StandardsSources.json config file: checks source IDs are unique, paths exist, and reports any issues. Creates an example config if no config file exists.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Documentation;

    private static readonly StandardsSourceService _sourceService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!File.Exists(_sourceService.ConfigFilePath))
        {
            var examplePath = _sourceService.SaveExample();
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = true,
                Message    = $"No config file found. An example config has been created at: {examplePath}. Edit it to point to your standards documents, then run standards_index_sources.",
                Data       = new { configFile = examplePath, created = true },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var validations = _sourceService.Validate();
        var config      = _sourceService.Load();
        var ids         = config.Sources.Select(s => s.Id).ToList();
        var duplicates  = ids.GroupBy(x => x).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        var issues = new List<string>();
        foreach (var (src, error) in validations)
            if (error != null) issues.Add($"[{src.Id}] {error}");
        foreach (var dup in duplicates)
            issues.Add($"Duplicate source ID: '{dup}'");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = !issues.Any(),
            Message    = issues.Any()
                ? $"Config has {issues.Count} issue(s). Fix before indexing."
                : $"Config is valid. {config.Sources.Count} source(s) configured ({config.Sources.Count(s => s.Enabled)} enabled).",
            Data       = new
            {
                configFile = _sourceService.ConfigFilePath,
                sourcesCount = config.Sources.Count,
                enabledCount = config.Sources.Count(s => s.Enabled),
                issues
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

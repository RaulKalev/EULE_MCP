using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class RunClashPresetTool : IRevitMcpTool
{
    public string Name => "revit_run_clash_preset";
    public string Description => "Runs all rules in a named clash detection preset and returns merged results with per-rule clash counts.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashPresetService _presetService = new();
    private readonly ClashCandidateCollector _collector = new();
    private readonly HardClashDetector _hardDetector = new();
    private readonly ClearanceClashDetector _clearanceDetector = new();
    private readonly ClashRunCacheService _cache = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var args = request.Arguments;
        var presetName = ToolArguments.GetString(args, "presetName");
        if (string.IsNullOrWhiteSpace(presetName)) return Task.FromResult(Fail(request, "presetName is required."));

        var preset = _presetService.FindByName(presetName);
        if (preset == null) return Task.FromResult(Fail(request, $"Preset '{presetName}' not found."));

        var (valid, errors) = _presetService.Validate(preset);
        if (!valid) return Task.FromResult(Fail(request, $"Preset invalid: {string.Join("; ", errors)}"));

        var includeLinks = ToolArguments.GetBool(args, "includeLinks", true);
        var includeGenericModels = ToolArguments.GetBool(args, "includeGenericModels", true);
        var includeImportedGeometry = ToolArguments.GetBool(args, "includeImportedGeometry", true);
        var limit = ToolArguments.GetInt(args, "limit", 1000);
        var maxPairs = ToolArguments.GetInt(args, "maxPairs", 100_000);
        var saveAsLastRun = ToolArguments.GetBool(args, "saveAsLastRun", true);

        var allClashes = new List<ClashResultDto>();
        var allWarnings = new List<string>();
        var countByRule = new Dictionary<string, int>();
        int clashOffset = 0;

        foreach (var rule in preset.Rules)
        {
            var incGM = includeGenericModels && rule.IncludeGenericModels;
            var incIG = includeImportedGeometry && rule.IncludeImportedGeometry;
            var incLinks = includeLinks && (rule.TargetScope == "HostAndLinks");

            var (sources, sw1) = _collector.Collect(doc, rule.SourceCategories, incLinks, incGM, incIG, null, 0);
            var (targets, tw1) = _collector.Collect(doc, rule.TargetCategories, incLinks, incGM, incIG, null, 0);
            allWarnings.AddRange(sw1); allWarnings.AddRange(tw1);

            List<ClashResultDto> ruleClashes;
            List<string> ruleWarnings;

            if (rule.ClashType == "Clearance")
            {
                (ruleClashes, ruleWarnings) = _clearanceDetector.Detect(sources, targets, rule.Name, rule.Severity, rule.ClearanceMm, limit, maxPairs);
            }
            else
            {
                (ruleClashes, ruleWarnings) = _hardDetector.Detect(sources, targets, rule.Name, rule.Severity, rule.ToleranceMm, limit, maxPairs);
            }

            allWarnings.AddRange(ruleWarnings);

            // Re-index clash IDs to be globally unique across rules
            for (var i = 0; i < ruleClashes.Count; i++)
                ruleClashes[i].ClashId = $"CL-{clashOffset + i + 1:D4}";
            clashOffset += ruleClashes.Count;

            countByRule[rule.Name] = ruleClashes.Count;
            allClashes.AddRange(ruleClashes);
        }

        var run = new ClashRunResultDto
        {
            RunId = Guid.NewGuid().ToString("N")[..8].ToUpper(),
            RunAt = DateTime.Now,
            PresetName = presetName,
            TotalClashes = allClashes.Count,
            Clashes = allClashes,
            Warnings = allWarnings.Distinct().ToList(),
            CountByRule = countByRule
        };

        if (saveAsLastRun) _cache.Save(run);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preset '{presetName}': {allClashes.Count} total clash(es) across {preset.Rules.Count} rule(s). {(saveAsLastRun ? "Saved as last run." : "")}",
            Data = run,
            Warnings = run.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

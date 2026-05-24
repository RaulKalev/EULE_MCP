using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

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
        var allowBoundingBoxFallback = ToolArguments.GetBool(args, "allowBoundingBoxFallback", false);
        var returnIssueReport = ToolArguments.GetBool(args, "returnIssueReport", false);

        var allClashes = new List<ClashResultDto>();
        var allWarnings = new List<string>();
        var countByRule = new Dictionary<string, int>();
        int clashOffset = 0;

        foreach (var rule in preset.Rules)
        {
            // A. Respect cancellation between rules — return partial results cleanly rather
            //    than leaving Revit grinding indefinitely after the client has disconnected.
            if (cancellationToken.IsCancellationRequested)
            {
                allWarnings.Add($"Preset execution cancelled after {countByRule.Count}/{preset.Rules.Count} rule(s). Remaining rules were not run. Results are partial.");
                break;
            }

            var incGM = includeGenericModels && rule.IncludeGenericModels;
            var incIG = includeImportedGeometry && rule.IncludeImportedGeometry;
            var incLinks = includeLinks && (rule.TargetScope == "HostAndLinks");

            var (sources, sw1) = _collector.Collect(doc, rule.SourceCategories, incLinks, incGM, incIG, null, 0);
            var (targets, tw1) = _collector.Collect(doc, rule.TargetCategories, incLinks, incGM, incIG, null, 0);
            allWarnings.AddRange(sw1); allWarnings.AddRange(tw1);

            // B. Skip rules with no real candidates — avoids expensive detector work on empty sets.
            if (sources.Count == 0 || targets.Count == 0)
            {
                var skipReason = sources.Count == 0
                    ? $"no source candidates for [{string.Join(", ", rule.SourceCategories)}]"
                    : $"no target candidates for [{string.Join(", ", rule.TargetCategories)}]";
                allWarnings.Add($"Rule '{rule.Name}': skipped — {skipReason}.");
                countByRule[rule.Name] = 0;
                continue;
            }

            // C. Observability — record candidate counts before entering detection work.
            allWarnings.Add($"Rule '{rule.Name}': {sources.Count} source(s), {targets.Count} target(s), running {rule.ClashType} detection.");

            List<ClashResultDto> ruleClashes;
            List<string> ruleWarnings;

            if (rule.ClashType == "Clearance")
            {
                (ruleClashes, ruleWarnings) = _clearanceDetector.Detect(sources, targets, rule.Name, rule.Severity, rule.ClearanceMm, limit, maxPairs, "ExpandedBoundingBox", cancellationToken);
            }
            else
            {
                (ruleClashes, ruleWarnings) = _hardDetector.Detect(sources, targets, rule.Name, rule.Severity, rule.ToleranceMm, limit, maxPairs, allowBoundingBoxFallback, cancellationToken);
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

        IssueReportDto? issueReport = null;
        if (returnIssueReport)
        {
            var reportRunId = run.RunId;
            var issues = allClashes.Select((c, i) => new IssueDto
            {
                IssueId = $"{reportRunId}-{i + 1:D4}",
                RunId = reportRunId,
                SourceTool = Name,
                Severity = c.Severity switch
                {
                    "Critical" => IssueSeverity.Critical,
                    "High" => IssueSeverity.Error,
                    "Low" => IssueSeverity.Info,
                    _ => IssueSeverity.Warning
                },
                Status = IssueStatus.Open,
                Category = c.ClashType == "Clearance" ? "ClashDetection.Clearance" : "ClashDetection.Hard",
                Title = $"{c.ClashType}: {c.Source.Category} ↔ {c.Target.Category} [{c.RuleName}]",
                Description = c.Message,
                ElementId = c.Source.ElementId,
                LinkedElementId = c.Target.ElementId,
                Phase = c.RuleName,
                CreatedAt = DateTimeOffset.UtcNow
            }).ToList();
            issueReport = new IssueReportDto
            {
                RunId = reportRunId,
                Title = $"Clash Preset — {presetName}",
                SourceTools = [Name],
                Issues = issues
            };
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preset '{presetName}': {allClashes.Count} total clash(es) across {preset.Rules.Count} rule(s). {(saveAsLastRun ? "Saved as last run." : "")}",
            Data = new { run, issueReport },
            Warnings = run.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

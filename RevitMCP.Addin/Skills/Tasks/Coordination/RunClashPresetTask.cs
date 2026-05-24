using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.Coordination;

/// <summary>
/// Task ID: coordination.run.clash-preset
/// Runs a named clash detection preset and stores detected issues in SharedData["issueDtos"].
/// Setting "presetName" is required.
/// </summary>
internal sealed class RunClashPresetTask : ISkillTask
{
    public string Id           => "coordination.run.clash-preset";
    public string Name         => "Run Clash Preset";
    public bool   ChangesModel => false;

    private readonly ClashPresetService       _presetService     = new();
    private readonly ClashCandidateCollector  _collector         = new();
    private readonly HardClashDetector        _hardDetector      = new();
    private readonly ClearanceClashDetector   _clearanceDetector = new();

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var presetName = SkillSettings.GetString(taskDef.Settings, "presetName");
            if (string.IsNullOrWhiteSpace(presetName))
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                    Message = "Setting 'presetName' is required.", DurationMs = sw.ElapsedMilliseconds
                };
            }

            var preset = _presetService.FindByName(presetName);
            if (preset == null)
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                    Message = $"Clash preset '{presetName}' not found.", DurationMs = sw.ElapsedMilliseconds
                };
            }

            var limit          = SkillSettings.GetInt(taskDef.Settings, "limit", 1000);
            var maxPairs       = SkillSettings.GetInt(taskDef.Settings, "maxPairs", 100_000);
            var includeLinks   = SkillSettings.GetBool(taskDef.Settings, "includeLinks", true);
            var allowBBFallback = SkillSettings.GetBool(taskDef.Settings, "allowBoundingBoxFallback", false);

            var allClashes  = new List<RevitMCP.Addin.Coordination.Clash.DTOs.ClashResultDto>();
            var allWarnings = new List<string>();
            var clashOffset = 0;

            foreach (var rule in preset.Rules)
            {
                var incLinks = includeLinks && rule.TargetScope == "HostAndLinks";
                var (sources, sw1) = _collector.Collect(ctx.Document, rule.SourceCategories, incLinks, rule.IncludeGenericModels, rule.IncludeImportedGeometry, null, 0);
                var (targets, tw1) = _collector.Collect(ctx.Document, rule.TargetCategories, incLinks, rule.IncludeGenericModels, rule.IncludeImportedGeometry, null, 0);
                allWarnings.AddRange(sw1); allWarnings.AddRange(tw1);

                if (sources.Count == 0 || targets.Count == 0)
                {
                    allWarnings.Add($"Rule '{rule.Name}': skipped — no candidates.");
                    continue;
                }

                List<RevitMCP.Addin.Coordination.Clash.DTOs.ClashResultDto> ruleClashes;
                List<string> ruleWarnings;

                if (rule.ClashType == "Clearance")
                    (ruleClashes, ruleWarnings) = _clearanceDetector.Detect(sources, targets, rule.Name, rule.Severity, rule.ClearanceMm, limit, maxPairs, "ExpandedBoundingBox", CancellationToken.None);
                else
                    (ruleClashes, ruleWarnings) = _hardDetector.Detect(sources, targets, rule.Name, rule.Severity, rule.ToleranceMm, limit, maxPairs, allowBBFallback, CancellationToken.None);

                allWarnings.AddRange(ruleWarnings);

                for (var i = 0; i < ruleClashes.Count; i++)
                    ruleClashes[i].ClashId = $"CL-{clashOffset + i + 1:D4}";
                clashOffset += ruleClashes.Count;

                allClashes.AddRange(ruleClashes);
            }

            // Convert to IssueDto
            var issues = allClashes.Select((c, i) => new IssueDto
            {
                IssueId    = $"{ctx.RunId}-{i + 1:D4}",
                RunId      = ctx.RunId,
                SourceTool = Id,
                Severity   = c.Severity switch
                {
                    "Critical" => IssueSeverity.Critical,
                    "High"     => IssueSeverity.Error,
                    "Low"      => IssueSeverity.Info,
                    _          => IssueSeverity.Warning
                },
                Status      = IssueStatus.Open,
                Category    = c.ClashType == "Clearance" ? "ClashDetection.Clearance" : "ClashDetection.Hard",
                Title       = $"{c.ClashType}: {c.Source.Category} ↔ {c.Target.Category} [{c.RuleName}]",
                Description = c.Message,
                ElementId   = c.Source.ElementId,
                LinkedElementId = c.Target.ElementId,
                Phase       = c.RuleName,
                CreatedAt   = DateTimeOffset.UtcNow
            }).ToList();

            ctx.SharedData["issueDtos"] = issues;

            return new SkillTaskResult
            {
                TaskId     = Id, TaskName = Name, Success = true,
                Message    = $"Preset '{presetName}': {allClashes.Count} clash(es) detected.",
                IssueCount = issues.Count, Warnings = allWarnings, DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId = Id, TaskName = Name, Success = false,
                Message = $"Clash preset run failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}

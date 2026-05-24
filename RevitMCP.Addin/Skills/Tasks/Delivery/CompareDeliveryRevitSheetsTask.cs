using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.Delivery;

/// <summary>
/// Task ID: delivery.compare.revit-sheets
/// Reads the delivery scan result from SharedData["deliveryScan"],
/// collects sheets from the active Revit document, and runs
/// <see cref="DeliveryRevitSheetComparer.Compare"/> to produce issues.
/// Issues are stored in SharedData["issueDtos"] for downstream export tasks.
/// </summary>
internal sealed class CompareDeliveryRevitSheetsTask : ISkillTask
{
    public string Id           => "delivery.compare.revit-sheets";
    public string Name         => "Compare Delivery Files vs Revit Sheets";
    public bool   ChangesModel => false;

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (!ctx.SharedData.TryGetValue("deliveryScan", out var scanRaw) || scanRaw is not DeliveryScanResult scan)
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                    Message = "deliveryScan not found in SharedData. Run delivery.scan.folder first.",
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            // Settings
            var requiredExtsRaw     = SkillSettings.GetString(taskDef.Settings, "requiredExtensions", "pdf,dwg");
            var requiredExtensions  = requiredExtsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                                     .Select(e => e.TrimStart('.').ToLowerInvariant()).ToList();
            var stageFilterRaw      = SkillSettings.GetString(taskDef.Settings, "stageFilter");
            var stageFilter         = string.IsNullOrWhiteSpace(stageFilterRaw) ? new List<string>()
                : stageFilterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            var disciplineFilterRaw = SkillSettings.GetString(taskDef.Settings, "disciplineFilter");
            var disciplineFilter    = string.IsNullOrWhiteSpace(disciplineFilterRaw) ? new List<string>()
                : disciplineFilterRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

            // Collect Revit sheets
            var revitSheets = new FilteredElementCollector(ctx.Document)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder)
                .Select(s =>
                {
                    var parsed = RevitSheetNumberParser.Parse(s.SheetNumber);
                    return new RevitSheetDescriptor
                    {
                        SheetNumber   = parsed?.SheetNumberCore ?? s.SheetNumber,
                        SheetName     = s.Name,
                        Discipline    = parsed?.Discipline,
                        Stage         = parsed?.Stage,
                        ProjectNumber = parsed?.ProjectNumber
                    };
                })
                .ToList();

            var issues = DeliveryRevitSheetComparer.Compare(
                scan, revitSheets, requiredExtensions, stageFilter, disciplineFilter, ctx.RunId);

            // Store issues — both as IssueDto list and as SkillIssue list
            ctx.SharedData["issueDtos"] = issues;

            var warnings = new List<string>(scan.Warnings);
            var criticals = issues.Count(i => i.Severity == IssueSeverity.Critical);

            return new SkillTaskResult
            {
                TaskId     = Id, TaskName = Name, Success = true,
                Message    = $"Compared {revitSheets.Count} sheet(s). Found {issues.Count} issue(s) ({criticals} critical).",
                IssueCount = issues.Count, Warnings = warnings, DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId = Id, TaskName = Name, Success = false,
                Message = $"Delivery comparison failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}

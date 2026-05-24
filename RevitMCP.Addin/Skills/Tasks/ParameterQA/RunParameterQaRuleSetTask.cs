using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.ParameterQA;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Skills;
using RevitMCP.Addin.Skills.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Skills.Tasks.ParameterQA;

/// <summary>
/// Task ID: parameterqa.run.rule-set
/// Runs a named parameter QA rule set and stores issues in SharedData["issueDtos"].
/// Setting "ruleSetName" is required.
/// </summary>
internal sealed class RunParameterQaRuleSetTask : ISkillTask
{
    public string Id           => "parameterqa.run.rule-set";
    public string Name         => "Run Parameter QA Rule Set";
    public bool   ChangesModel => false;

    private readonly ParameterQaRuleSetService _service          = new();
    private readonly CategoryResolver          _categoryResolver  = new();

    public SkillTaskResult Run(SkillContext ctx, SkillTaskDefinition taskDef)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var ruleSetName = SkillSettings.GetString(taskDef.Settings, "ruleSetName");
            if (string.IsNullOrWhiteSpace(ruleSetName))
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                    Message = "Setting 'ruleSetName' is required.", DurationMs = sw.ElapsedMilliseconds
                };
            }

            var ruleSet = _service.FindByName(ruleSetName);
            if (ruleSet == null)
            {
                return new SkillTaskResult
                {
                    TaskId = Id, TaskName = Name, Success = false, CriticalFailure = true,
                    Message = $"Parameter QA rule set '{ruleSetName}' not found.", DurationMs = sw.ElapsedMilliseconds
                };
            }

            var limitPerRule = SkillSettings.GetInt(taskDef.Settings, "limitPerRule", 5000);
            var allIssues    = new List<IssueDto>();
            var warnings     = new List<string>();
            int seq          = 0;

            foreach (var rule in ruleSet.Rules)
            {
                var resolve = _categoryResolver.Resolve(ctx.Document, rule.Category);
                if (resolve.Category == null)
                {
                    warnings.Add($"Category '{rule.Category}' not found in model — rule skipped.");
                    continue;
                }

                var elementIds = new FilteredElementCollector(ctx.Document)
                    .WhereElementIsNotElementType()
                    .OfCategoryId(resolve.Category.Id)
                    .ToElementIds()
                    .ToList();

                var opts = new ParameterCompletenessOptions
                {
                    IncludeInstanceParameters = rule.IncludeInstanceParameters,
                    IncludeTypeParameters     = rule.IncludeTypeParameters,
                    Limit                     = limitPerRule
                };

                var result = ParameterCompletenessChecker.Check(
                    ctx.Document, elementIds, [.. rule.RequiredParameters], opts);

                foreach (var prob in result.ProblemElements)
                {
                    foreach (var issueMsg in prob.Issues)
                    {
                        allIssues.Add(new IssueDto
                        {
                            IssueId    = $"{ctx.RunId}-PQA-{++seq:D4}",
                            RunId      = ctx.RunId,
                            SourceTool = Id,
                            Severity   = issueMsg.Contains("missing", StringComparison.OrdinalIgnoreCase)
                                ? IssueSeverity.Error : IssueSeverity.Warning,
                            Status     = IssueStatus.Open,
                            Category   = $"ParameterQA.{rule.Category}",
                            Title      = issueMsg,
                            Description = $"Element {prob.ElementId} ({rule.Category}): {issueMsg}",
                            ElementId  = prob.ElementId,
                            CreatedAt  = DateTimeOffset.UtcNow
                        });
                    }
                }
            }

            ctx.SharedData["issueDtos"] = allIssues;

            return new SkillTaskResult
            {
                TaskId     = Id, TaskName = Name, Success = true, Warnings = warnings,
                Message    = $"Rule set '{ruleSetName}': {allIssues.Count} parameter issue(s) found.",
                IssueCount = allIssues.Count, DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            return new SkillTaskResult
            {
                TaskId = Id, TaskName = Name, Success = false,
                Message = $"Parameter QA run failed: {ex.Message}", DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}

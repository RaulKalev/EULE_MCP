using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ParameterQA;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools.ParameterQA;

/// <summary>
/// Runs a named parameter QA rule set against the active Revit document.
/// Each rule in the set is checked independently; results are merged into a single report.
/// </summary>
public class RunParameterQaRuleSetTool : IRevitMcpTool
{
    public string Name => "revit_run_parameter_qa_rule_set";
    public string Description =>
        "Runs a named parameter QA rule set against the active model. " +
        "Each rule checks that required parameters are filled for all elements in its category. " +
        "Use revit_list_parameter_qa_rule_sets to discover available rule sets.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private readonly ParameterQaRuleSetService _service = new();
    private readonly CategoryResolver _categoryResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;

        var ruleSetName = ToolArguments.GetString(request.Arguments, "ruleSetName");
        if (string.IsNullOrWhiteSpace(ruleSetName))
            return Task.FromResult(Fail(request, "ruleSetName is required."));

        var limitPerRule = ToolArguments.GetInt(request.Arguments, "limitPerRule", 5000);
        var returnIssueReport = ToolArguments.GetBool(request.Arguments, "returnIssueReport", true);

        var ruleSet = _service.FindByName(ruleSetName);
        if (ruleSet == null)
            return Task.FromResult(Fail(request, $"Rule set '{ruleSetName}' not found. Use revit_list_parameter_qa_rule_sets to see available sets."));

        var ruleResults = new List<object>();
        var allProblemElements = new List<ProblemElementInfo>();
        var warnings = new List<string>();
        int totalElements = 0;

        foreach (var rule in ruleSet.Rules)
        {
            var resolve = _categoryResolver.Resolve(doc, rule.Category);
            if (resolve.Category == null)
            {
                warnings.Add($"Rule '{rule.Category}': category not found in model. " +
                             (resolve.Suggestions.Count > 0 ? $"Suggestions: {string.Join(", ", resolve.Suggestions)}" : ""));
                continue;
            }

            var elementIds = new FilteredElementCollector(doc)
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
                doc, elementIds, [.. rule.RequiredParameters], opts);

            totalElements += result.TotalElements;
            allProblemElements.AddRange(result.ProblemElements);

            if (elementIds.Count > limitPerRule)
                warnings.Add($"Rule '{rule.Category}': element limit ({limitPerRule}) reached — results may be incomplete.");

            ruleResults.Add(new
            {
                category           = rule.Category,
                totalElements      = result.TotalElements,
                completeElements   = result.CompleteElements,
                incompleteElements = result.IncompleteElements,
                completionPercent  = result.CompletionPercent,
                parameters         = result.ParameterStats.Select(s => new
                {
                    parameterName = s.ParameterName,
                    missingCount  = s.MissingCount,
                    emptyCount    = s.EmptyCount,
                    filledCount   = s.FilledCount
                })
            });
        }

        IssueReportDto? issueReport = null;
        if (returnIssueReport && allProblemElements.Count > 0)
        {
            var runId = Guid.NewGuid().ToString("N")[..8].ToUpper();
            int seq = 0;
            var issues = new List<IssueDto>();
            foreach (var elem in allProblemElements)
            {
                foreach (var issueText in elem.Issues)
                {
                    seq++;
                    issues.Add(new IssueDto
                    {
                        IssueId     = $"{runId}-{seq:D4}",
                        RunId       = runId,
                        SourceTool  = Name,
                        Severity    = issueText.Contains("missing", StringComparison.OrdinalIgnoreCase)
                            ? IssueSeverity.Error : IssueSeverity.Warning,
                        Status      = IssueStatus.Open,
                        Category    = "ParameterQA",
                        Title       = issueText,
                        Description = $"Element {elem.ElementId} ({elem.Category}): {issueText}",
                        ElementId   = elem.ElementId,
                        CreatedAt   = DateTimeOffset.UtcNow
                    });
                }
            }
            issueReport = new IssueReportDto
            {
                RunId       = runId,
                Title       = $"Parameter QA — {ruleSetName}",
                SourceTools = [Name],
                Issues      = issues
            };
        }

        int totalIncomplete = allProblemElements.Count;
        sw.Stop();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success   = true,
            Message   = $"Rule set '{ruleSetName}': checked {totalElements} elements across {ruleResults.Count} rule(s). {totalIncomplete} element(s) with issues.",
            Data = new
            {
                ruleSetName,
                ruleCount        = ruleSet.Rules.Count,
                totalElements,
                totalWithIssues  = totalIncomplete,
                rules            = ruleResults,
                issueReport
            },
            Warnings    = warnings,
            DurationMs  = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

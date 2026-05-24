using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Tools;

public class CheckParameterCompletenessTool : IRevitMcpTool
{
    public string Name => "revit_check_parameter_completeness";
    public string Description => "Checks whether required parameters exist and are filled for elements. Useful for model QA before issue/export.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    private readonly CategoryResolver _categoryResolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var doc = uidoc.Document;
        var category = ToolArguments.GetString(request.Arguments, "category");
        var useSelection = ToolArguments.GetBool(request.Arguments, "useSelection");
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var requiredParams = ToolArguments.GetStringArray(request.Arguments, "requiredParameters");
        var includeInstance = ToolArguments.GetBool(request.Arguments, "includeInstanceParameters", true);
        var includeType = ToolArguments.GetBool(request.Arguments, "includeTypeParameters", true);
        var treatWhitespaceAsEmpty = ToolArguments.GetBool(request.Arguments, "treatWhitespaceAsEmpty", true);
        var includeElementIds = ToolArguments.GetBool(request.Arguments, "includeElementIds", true);
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 5000);
        var returnIssueReport = ToolArguments.GetBool(request.Arguments, "returnIssueReport", false);

        if (requiredParams.Length == 0)
            return Task.FromResult(Fail(request, "requiredParameters is required (list of parameter names to check)."));

        // Determine element source
        IEnumerable<ElementId> sourceIds;

        if (useSelection)
        {
            sourceIds = uidoc.Selection.GetElementIds();
        }
        else if (elementIds.Length > 0)
        {
            sourceIds = elementIds.Select(id => new ElementId(id));
        }
        else if (!string.IsNullOrWhiteSpace(category))
        {
            var resolve = _categoryResolver.Resolve(doc, category);
            if (resolve.Category == null)
            {
                var sug = resolve.Suggestions.Count > 0
                    ? $" Did you mean: {string.Join(", ", resolve.Suggestions)}?"
                    : string.Empty;
                return Task.FromResult(Fail(request, resolve.Message + sug));
            }
            sourceIds = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategoryId(resolve.Category.Id)
                .ToElementIds();
        }
        else
        {
            return Task.FromResult(Fail(request, "Provide useSelection=true, elementIds, or category."));
        }

        var reader = new ParameterReader();
        var readOpts = new ParameterReadOptions
        {
            IncludeInstanceParameters = includeInstance,
            IncludeTypeParameters = includeType
        };

        // Per-required-parameter stats
        var paramCheckStats = requiredParams.ToDictionary(
            name => name,
            _ => new ParamCheckStats());

        var problemElements = new List<object>();
        var elementIssueList = new List<(long elementId, List<string> issues)>();
        int totalElements = 0;
        int completeElements = 0;
        int incompleteElements = 0;

        foreach (var elementId in sourceIds)
        {
            if (totalElements >= limit) break;

            var element = doc.GetElement(elementId);
            if (element?.Category == null) continue;

            totalElements++;

            var allParams = reader.ReadParameters(doc, element, readOpts);
            var elementIssues = new List<string>();

            foreach (var reqName in requiredParams)
            {
                var stats = paramCheckStats[reqName];

                var matched = allParams.FirstOrDefault(p =>
                    ParameterMatcher.Matches(p.Name, reqName, "ContainsNormalized"));

                if (matched == null)
                {
                    stats.MissingCount++;
                    elementIssues.Add($"{reqName} is missing");
                }
                else if (IsEmpty(matched.Value, treatWhitespaceAsEmpty))
                {
                    stats.EmptyCount++;
                    elementIssues.Add($"{reqName} is empty");
                }
                else
                {
                    stats.FilledCount++;
                }
            }

            if (elementIssues.Count > 0)
            {
                incompleteElements++;
                elementIssueList.Add((element.Id.Value, elementIssues));

                if (includeElementIds)
                {
                    var typeId = element.GetTypeId();
                    var typeElem = (typeId != null && typeId != ElementId.InvalidElementId) ? doc.GetElement(typeId) : null;
                    string? familyName = null;
                    if (element is FamilyInstance fi)
                        familyName = fi.Symbol?.Family?.Name;

                    problemElements.Add(new
                    {
                        elementId = element.Id.Value,
                        uniqueId = element.UniqueId,
                        category = element.Category.Name,
                        family = familyName ?? string.Empty,
                        type = typeElem?.Name ?? string.Empty,
                        level = GetLevelName(doc, element),
                        issues = elementIssues
                    });
                }
            }
            else
            {
                completeElements++;
            }
        }

        var completionPercent = totalElements > 0
            ? Math.Round(100.0 * completeElements / totalElements, 1)
            : 0.0;

        var parameterResults = requiredParams.Select(name =>
        {
            var s = paramCheckStats[name];
            return new
            {
                parameterName = name,
                missingCount = s.MissingCount,
                emptyCount = s.EmptyCount,
                filledCount = s.FilledCount
            };
        }).ToList();

        sw.Stop();

        IssueReportDto? issueReport = null;
        if (returnIssueReport)
        {
            var runId = Guid.NewGuid().ToString("N")[..8].ToUpper();
            int seq = 0;
            var issues = new List<IssueDto>();
            foreach (var (eid, issueTexts) in elementIssueList)
            {
                foreach (var iss in issueTexts)
                {
                    seq++;
                    issues.Add(new IssueDto
                    {
                        IssueId = $"{runId}-{seq:D4}",
                        RunId = runId,
                        SourceTool = Name,
                        Severity = iss.Contains("missing", StringComparison.OrdinalIgnoreCase)
                            ? IssueSeverity.Error
                            : IssueSeverity.Warning,
                        Status = IssueStatus.Open,
                        Category = "ParameterQA",
                        Title = iss,
                        Description = $"Element {eid}: {iss}",
                        ElementId = eid,
                        CreatedAt = DateTimeOffset.UtcNow
                    });
                }
            }
            issueReport = new IssueReportDto
            {
                RunId = runId,
                Title = $"Parameter Completeness — {category ?? "All Elements"}",
                SourceTools = [Name],
                Issues = issues
            };
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Checked {totalElements} elements. {completeElements} complete, {incompleteElements} incomplete.",
            Data = new
            {
                category,
                totalElements,
                completeElements,
                incompleteElements,
                completionPercent,
                parameters = parameterResults,
                problemElements,
                issueReport
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static bool IsEmpty(string value, bool treatWhitespaceAsEmpty) =>
        treatWhitespaceAsEmpty ? string.IsNullOrWhiteSpace(value) : string.IsNullOrEmpty(value);

    private static string GetLevelName(Document doc, Element element)
    {
        try
        {
            var lvlId = element.LevelId;
            if (lvlId != null && lvlId != ElementId.InvalidElementId)
                return (doc.GetElement(lvlId) as Level)?.Name ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };

    private class ParamCheckStats
    {
        public int MissingCount { get; set; }
        public int EmptyCount { get; set; }
        public int FilledCount { get; set; }
    }
}

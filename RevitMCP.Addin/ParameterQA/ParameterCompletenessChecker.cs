using Autodesk.Revit.DB;
using RevitMCP.Addin.Query;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.ParameterQA;

/// <summary>
/// Core stateless service for checking parameter completeness.
/// Extracted from <c>CheckParameterCompletenessTool</c> so that
/// <c>RunParameterQaRuleSetTool</c> can reuse the same logic.
/// No Revit UI dependency — takes a <see cref="Document"/> and pre-collected element IDs.
/// </summary>
public static class ParameterCompletenessChecker
{
    /// <summary>
    /// Checks required parameters for the given element IDs.
    /// </summary>
    public static ParameterCheckResult Check(
        Document doc,
        IEnumerable<ElementId> elementIds,
        string[] requiredParameters,
        ParameterCompletenessOptions options)
    {
        var reader = new ParameterReader();
        var readOpts = new ParameterReadOptions
        {
            IncludeInstanceParameters = options.IncludeInstanceParameters,
            IncludeTypeParameters = options.IncludeTypeParameters
        };

        var paramStats = requiredParameters.ToDictionary(
            name => name,
            _ => new ParamCheckStats());

        var problemElements = new List<ProblemElementInfo>();
        int total = 0;
        int complete = 0;
        int incomplete = 0;

        foreach (var elementId in elementIds)
        {
            if (total >= options.Limit) break;

            var element = doc.GetElement(elementId);
            if (element?.Category == null) continue;

            total++;

            var allParams = reader.ReadParameters(doc, element, readOpts);
            var elementIssues = new List<string>();

            foreach (var reqName in requiredParameters)
            {
                var stats = paramStats[reqName];
                var matched = allParams.FirstOrDefault(p =>
                    ParameterMatcher.Matches(p.Name, reqName, "ContainsNormalized"));

                if (matched == null)
                {
                    stats.MissingCount++;
                    elementIssues.Add($"{reqName} is missing");
                }
                else if (IsEmpty(matched.Value, options.TreatWhitespaceAsEmpty))
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
                incomplete++;
                var typeId = element.GetTypeId();
                var typeElem = (typeId != null && typeId != ElementId.InvalidElementId)
                    ? doc.GetElement(typeId) : null;
                string? familyName = null;
                if (element is FamilyInstance fi)
                    familyName = fi.Symbol?.Family?.Name;

                problemElements.Add(new ProblemElementInfo
                {
                    ElementId  = element.Id.Value,
                    UniqueId   = element.UniqueId,
                    Category   = element.Category.Name,
                    Family     = familyName ?? string.Empty,
                    Type       = typeElem?.Name ?? string.Empty,
                    Level      = GetLevelName(doc, element),
                    Issues     = elementIssues
                });
            }
            else
            {
                complete++;
            }
        }

        var paramResults = requiredParameters.Select(name =>
        {
            var s = paramStats[name];
            return new ParameterStatEntry
            {
                ParameterName = name,
                MissingCount  = s.MissingCount,
                EmptyCount    = s.EmptyCount,
                FilledCount   = s.FilledCount
            };
        }).ToList();

        return new ParameterCheckResult
        {
            TotalElements      = total,
            CompleteElements   = complete,
            IncompleteElements = incomplete,
            CompletionPercent  = total > 0 ? Math.Round(100.0 * complete / total, 1) : 0.0,
            ParameterStats     = paramResults,
            ProblemElements    = problemElements
        };
    }

    /// <summary>
    /// Converts a <see cref="ParameterCheckResult"/> into an <see cref="IssueReportDto"/>.
    /// </summary>
    public static IssueReportDto BuildIssueReport(
        ParameterCheckResult result,
        string sourceTool,
        string title)
    {
        var runId = Guid.NewGuid().ToString("N")[..8].ToUpper();
        int seq = 0;
        var issues = new List<IssueDto>();

        foreach (var elem in result.ProblemElements)
        {
            foreach (var issueText in elem.Issues)
            {
                seq++;
                issues.Add(new IssueDto
                {
                    IssueId    = $"{runId}-{seq:D4}",
                    RunId      = runId,
                    SourceTool = sourceTool,
                    Severity   = issueText.Contains("missing", StringComparison.OrdinalIgnoreCase)
                        ? IssueSeverity.Error : IssueSeverity.Warning,
                    Status     = IssueStatus.Open,
                    Category   = "ParameterQA",
                    Title      = issueText,
                    Description = $"Element {elem.ElementId}: {issueText}",
                    ElementId  = elem.ElementId,
                    CreatedAt  = DateTimeOffset.UtcNow
                });
            }
        }

        return new IssueReportDto
        {
            RunId       = runId,
            Title       = title,
            SourceTools = [sourceTool],
            Issues      = issues
        };
    }

    private static bool IsEmpty(string value, bool treatWhitespaceAsEmpty)
        => treatWhitespaceAsEmpty ? string.IsNullOrWhiteSpace(value) : string.IsNullOrEmpty(value);

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

    private sealed class ParamCheckStats
    {
        public int MissingCount { get; set; }
        public int EmptyCount   { get; set; }
        public int FilledCount  { get; set; }
    }
}

public sealed class ParameterCompletenessOptions
{
    public bool IncludeInstanceParameters  { get; init; } = true;
    public bool IncludeTypeParameters      { get; init; } = true;
    public bool TreatWhitespaceAsEmpty     { get; init; } = true;
    public int  Limit                      { get; init; } = 5000;
}

public sealed class ParameterCheckResult
{
    public int    TotalElements      { get; init; }
    public int    CompleteElements   { get; init; }
    public int    IncompleteElements { get; init; }
    public double CompletionPercent  { get; init; }
    public List<ParameterStatEntry>    ParameterStats  { get; init; } = [];
    public List<ProblemElementInfo>    ProblemElements { get; init; } = [];
}

public sealed class ParameterStatEntry
{
    public string ParameterName { get; init; } = string.Empty;
    public int    MissingCount  { get; init; }
    public int    EmptyCount    { get; init; }
    public int    FilledCount   { get; init; }
}

public sealed class ProblemElementInfo
{
    public long          ElementId  { get; init; }
    public string        UniqueId   { get; init; } = string.Empty;
    public string        Category   { get; init; } = string.Empty;
    public string        Family     { get; init; } = string.Empty;
    public string        Type       { get; init; } = string.Empty;
    public string        Level      { get; init; } = string.Empty;
    public List<string>  Issues     { get; init; } = [];
}

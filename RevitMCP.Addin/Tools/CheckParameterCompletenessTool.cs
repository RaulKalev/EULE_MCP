using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ParameterQA;
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

        var options = new ParameterCompletenessOptions
        {
            IncludeInstanceParameters = includeInstance,
            IncludeTypeParameters     = includeType,
            TreatWhitespaceAsEmpty    = treatWhitespaceAsEmpty,
            Limit                     = limit
        };

        var result = ParameterCompletenessChecker.Check(doc, sourceIds, requiredParams, options);

        sw.Stop();

        var parameterResults = result.ParameterStats.Select(s => new
        {
            parameterName = s.ParameterName,
            missingCount  = s.MissingCount,
            emptyCount    = s.EmptyCount,
            filledCount   = s.FilledCount
        }).ToList();

        var problemElements = includeElementIds
            ? result.ProblemElements.Select(e => (object)new
            {
                elementId = e.ElementId,
                uniqueId  = e.UniqueId,
                category  = e.Category,
                family    = e.Family,
                type      = e.Type,
                level     = e.Level,
                issues    = e.Issues
            }).ToList()
            : [];

        IssueReportDto? issueReport = returnIssueReport
            ? ParameterCompletenessChecker.BuildIssueReport(
                result, Name, $"Parameter Completeness — {category ?? "All Elements"}")
            : null;

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Checked {result.TotalElements} elements. {result.CompleteElements} complete, {result.IncompleteElements} incomplete.",
            Data = new
            {
                category,
                totalElements      = result.TotalElements,
                completeElements   = result.CompleteElements,
                incompleteElements = result.IncompleteElements,
                completionPercent  = result.CompletionPercent,
                parameters         = parameterResults,
                problemElements,
                issueReport
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

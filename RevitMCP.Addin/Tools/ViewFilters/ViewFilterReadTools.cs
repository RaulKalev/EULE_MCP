using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ViewFilters;

/// <summary>revit_list_view_filters (#134): the filters of a document, what they hold and where they are used.</summary>
public sealed class ListViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_list_view_filters";

    public string Description =>
        "Lists the view filters of the document: id, name, kind (rule | selection), categories, the rule tree (same shape as " +
        "revit_create_view_filters takes) or the member element ids, and which views and templates use each. Optional: " +
        "nameContains, kind, includeUsage (default true), includeRules (default true).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var nameContains = ToolArguments.GetString(request.Arguments, "nameContains").Trim();
        var kind = ToolArguments.GetString(request.Arguments, "kind").Trim().ToLowerInvariant();
        var includeUsage = ToolArguments.GetBool(request.Arguments, "includeUsage", true);
        var includeRules = ToolArguments.GetBool(request.Arguments, "includeRules", true);
        if (kind.Length > 0 && kind != ViewFilterKinds.Rule && kind != ViewFilterKinds.Selection)
            return Task.FromResult(ViewFilterBatch.Fail(request, $"kind must be rule or selection (got '{kind}').", ViewFilterCodes.InvalidSpec));

        var filters = ViewFilterModel.Filters(doc)
            .Where(f => kind.Length == 0 || ViewFilterModel.KindOf(f) == kind)
            .Where(f => nameContains.Length == 0 || f.Name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        var usage = includeUsage ? ViewFilterModel.UsageMap(doc) : null;
        var byTemplate = includeUsage ? ViewFilterModel.ViewsByTemplate(doc) : null;
        var cache = new ViewFilterCatalogCache();
        var described = new List<object>();
        foreach (var filter in filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            described.Add(ViewFilterModel.Describe(doc, filter, cache, includeRules,
                usage == null ? null : ViewFilterModel.DescribeUsage(filter.Id.Value, usage, byTemplate!)));
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{filters.Count} view filter(s).",
            Data = new { count = filters.Count, filters = described },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_list_filterable_parameters (#134): what a filter on these categories can test, and the operators each parameter takes.</summary>
public sealed class ListFilterableParametersTool : IRevitMcpTool
{
    public string Name => "revit_list_filterable_parameters";

    public string Description =>
        "Lists the parameters a view filter on the given categories can use (common to all of them, as in the Filters dialog): " +
        "id, name, builtIn, sharedGuid, valueKind (Text | Integer | YesNo | Number | ElementId) and operators. categories: names, " +
        "OST_ names or ids; without them it lists the filterable categories instead. Optional nameContains, limit (default 500).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var nameContains = ToolArguments.GetString(request.Arguments, "nameContains").Trim();
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);
        if (limit <= 0) limit = 500;
        bool Matches(string name) => nameContains.Length == 0 || name.IndexOf(nameContains, StringComparison.OrdinalIgnoreCase) >= 0;

        var references = ViewFilterRequestParser.ReadRefs(request.Arguments, "categories");
        if (references.Count == 0)
        {
            var filterable = ViewFilterModel.FilterableCategories(doc).Where(c => Matches(c.Name)).ToList();
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"No categories given: {filterable.Count} filterable categories listed. Pass categories to get their parameters.",
                Data = new
                {
                    count = filterable.Count,
                    filterableCategories = filterable.Select(c => new { id = c.Id, name = c.Name, builtIn = c.BuiltInName }).ToList()
                },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var issues = new List<ViewFilterIssue>();
        var categories = ViewFilterModel.ResolveCategories(doc, references, issues, "categories");
        if (issues.Count > 0)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = issues[0].Code,
                Message = string.Join(" ", issues.Select(i => i.Reason)),
                Data = new { issues = issues.Select(i => i.Describe()).ToList() },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var catalog = ViewFilterParameterCatalog.Build(doc, categories);
        var matching = catalog.Refs.Where(r => Matches(r.Name)).ToList();
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{matching.Count} filterable parameter(s) common to {categories.Count} categor{(categories.Count == 1 ? "y" : "ies")}.",
            Data = new
            {
                categories = categories.Select(c => ViewFilterModel.CategoryRef(doc, c)).ToList(),
                count = matching.Count,
                truncated = matching.Count > limit,
                parameters = matching.Take(limit).Select(r =>
                {
                    var kind = catalog.KindOf(r.Id);
                    return new
                    {
                        id = r.Id,
                        name = r.Name,
                        builtIn = r.BuiltInName,
                        sharedGuid = r.Guid,
                        valueKind = kind.ToString(),
                        operators = ViewFilterOperators.AllowedFor(kind)
                    };
                }).ToList()
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>revit_get_view_filters (#134): the filters in effect in a view or template, in order, with their settings.</summary>
public sealed class GetViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_get_view_filters";

    public string Description =>
        "For a view or view template (view: name or id; empty = the active view): the applied filters in order (the list in the " +
        "Filters tab, top first), with enabled, visible, graphics, and the controlling template when the template decides the view's " +
        "filters (source: view | template).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var reference = ToolArguments.GetString(request.Arguments, "view").Trim();
        View? view;
        if (reference.Length == 0)
        {
            if (!target.IsActive) return Task.FromResult(ToolDocument.ActiveRequired(request, "The active view", doc.Title));
            view = ToolDocument.UiDocumentOf(uiapp, target)?.ActiveView;
            if (view == null) return Task.FromResult(ViewFilterBatch.Fail(request, "There is no active view: pass view.", ViewFilterCodes.ViewNotFound));
        }
        else
        {
            view = ViewFilterModel.FindView(reference, ViewFilterModel.Views(doc), "view", out var issue);
            if (view == null) return Task.FromResult(ViewFilterBatch.Fail(request, issue!.Reason, issue.Code));
        }

        if (!ViewFilterModel.AllowsFilters(view))
            return Task.FromResult(ViewFilterBatch.Fail(request, $"View '{view.Name}' ({view.ViewType}) cannot have filters.", ViewFilterCodes.ViewCannotHaveFilters));

        var template = ViewFilterModel.TemplateOf(view);
        var controls = template != null && ViewFilterModel.TemplateControlsFilters(template);
        var effective = controls ? template! : view;
        var filters = new List<object>();
        var position = 0;
        foreach (var id in effective.GetOrderedFilters())
        {
            var filter = doc.GetElement(id) as FilterElement;
            filters.Add(new
            {
                order = ++position,
                id = id.Value,
                name = filter?.Name,
                kind = filter == null ? null : ViewFilterModel.KindOf(filter),
                enabled = effective.GetIsFilterEnabled(id),
                visible = effective.GetFilterVisibility(id),
                graphics = ViewFilterGraphics.Describe(doc, effective.GetFilterOverrides(id))
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{filters.Count} filter(s) in effect in '{view.Name}'{(controls ? $", decided by template '{template!.Name}'" : string.Empty)}.",
            Data = new
            {
                view = new { id = view.Id.Value, name = view.Name, viewType = view.ViewType.ToString(), isTemplate = view.IsTemplate },
                source = controls ? "template" : "view",
                template = template == null ? null : new { id = template.Id.Value, name = template.Name, controlsFilters = controls },
                count = filters.Count,
                filters
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

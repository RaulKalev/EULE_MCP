using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ViewFilters;

/// <summary>revit_preview_create_view_filters (#134).</summary>
public sealed class PreviewCreateViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_view_filters";

    public string Description =>
        "Previews creating view filters, without changing the model. Same arguments as revit_create_view_filters. Reports per " +
        "filter willCreate | skipped | invalid | failed with a status code and every problem (filter_name_exists, category_not_found, " +
        "category_not_filterable, parameter_not_filterable, operator_not_valid_for_type, rule_value_invalid, rule_not_acceptable, ...). " +
        "dryRun=true (default) also tries it in a rolled-back transaction.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateViewFiltersExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_create_view_filters (#134): rule filters with any AND/OR nesting, and selection filters.</summary>
public sealed class CreateViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_create_view_filters";

    public string Description =>
        "Creates view filters (max 200 per call). Requires approval. Rule filter: {name, categories: [names, OST_ names or ids], " +
        "rules: {and: [...]} | {or: [...]} | {parameter, operator, value, units?, tolerance?}} with any nesting; without rules the filter " +
        "selects its categories. Selection filter: {name, kind: selection, elementIds}. ifNameExists: fail (default) | skip. The filters " +
        "are not added to any view: use revit_apply_view_filters. One transaction, one undo; atomic=true undoes everything on any failure. " +
        "Run revit_preview_create_view_filters first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateViewFiltersExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class CreateViewFiltersExecutor
{
    private sealed class Item : ViewFilterWorkItem
    {
        public ViewFilterCreateSpec Spec = null!;
        public List<ElementId> Categories = new();
        public ElementFilter? Filter;
        public long? FilterId;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var parsed = ViewFilterRequestParser.ParseCreate(request.Arguments, out var error);
        if (parsed == null) return ViewFilterBatch.Fail(request, error!, ViewFilterCodes.InvalidSpec);

        var names = new HashSet<string>(ViewFilterModel.Filters(doc).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var cache = new ViewFilterCatalogCache();
        var builder = new ViewFilterRuleBuilder(doc, cache);
        var items = new List<Item>();
        foreach (var spec in parsed.Filters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new Item { Spec = spec };
            item.Issues.AddRange(spec.Issues);
            var path = $"filters[{spec.Index}]";

            if (spec.Name.Length > 0 && (names.Contains(spec.Name) || !FilterElement.IsNameUnique(doc, spec.Name)))
            {
                if (parsed.SkipExisting)
                {
                    item.SkipCode = ViewFilterCodes.FilterNameExists;
                    item.SkipReason = $"A view filter named '{spec.Name}' exists (ifNameExists=skip).";
                }
                else item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.FilterNameExists, $"A view filter named '{spec.Name}' exists.", path + ".name"));
            }

            if (spec.Kind == ViewFilterKinds.Selection)
            {
                foreach (var id in spec.ElementIds)
                    if (doc.GetElement(new ElementId(id)) == null)
                        item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.ElementNotFound, $"Element {id} does not exist.", path + ".elementIds"));
            }
            else
            {
                item.Categories = ViewFilterModel.ResolveCategories(doc, spec.Categories, item.Issues, path + ".categories");
                item.Filter = builder.Build(spec.Rules, item.Categories, item.Issues, path + ".rules");
            }
            items.Add(item);
        }

        var extra = new Dictionary<string, object?> { ["ifNameExists"] = parsed.SkipExisting ? "skip" : "fail" };
        return ViewFilterBatch.Run(request, doc, "Revit MCP - Create View Filters", "create", apply, parsed.Atomic, "filters", items,
            item => Perform(doc, item),
            (item, applied) => new
            {
                index = item.Spec.Index,
                name = item.Spec.Name,
                kind = item.Spec.Kind,
                status = item.Status("create", applied),
                code = item.Code,
                reason = item.Reason,
                issues = item.Issues.Count > 0 ? item.Issues.Select(i => i.Describe()).ToList() : null,
                filterId = applied && item.Done ? item.FilterId : null,
                categories = item.Categories.Select(c => ViewFilterModel.CategoryRef(doc, c)).ToList(),
                rules = ViewFilterRuleParser.Describe(item.Spec.Rules),
                ruleCount = ViewFilterRuleParser.CountRules(item.Spec.Rules),
                memberCount = item.Spec.Kind == ViewFilterKinds.Selection ? item.Spec.ElementIds.Count : (int?)null
            }, extra, sw, cancellationToken);
    }

    private static void Perform(Document doc, Item item)
    {
        FilterElement created;
        if (item.Spec.Kind == ViewFilterKinds.Selection)
        {
            var selection = SelectionFilterElement.Create(doc, item.Spec.Name);
            if (item.Spec.ElementIds.Count > 0)
                selection.AddSet(item.Spec.ElementIds.Select(i => new ElementId(i)).ToList());
            created = selection;
        }
        else
        {
            created = item.Filter != null
                ? ParameterFilterElement.Create(doc, item.Spec.Name, item.Categories, item.Filter)
                : ParameterFilterElement.Create(doc, item.Spec.Name, item.Categories);
        }
        item.FilterId = created.Id.Value;
    }
}

/// <summary>revit_preview_edit_view_filters (#134).</summary>
public sealed class PreviewEditViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_view_filters";

    public string Description =>
        "Previews editing view filters, without changing the model. Same arguments as revit_edit_view_filters. Reports per edit " +
        "willEdit | invalid | failed with a status code, the views and templates using the filter, and rule_parameter_not_applicable when " +
        "new categories would lose a rule. dryRun=true (default) also tries it in a rolled-back transaction.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EditViewFiltersExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_edit_view_filters (#134): rename, change categories or rules, change selection members.</summary>
public sealed class EditViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_edit_view_filters";

    public string Description =>
        "Edits view filters (max 200 per call). Requires approval. edits: [{filter: name or id, name? (rename), categories?, rules? or " +
        "clearRules: true, elementIds? (replace) or addElementIds? / removeElementIds? (selection filters)}]. Every view and template " +
        "using a filter changes with it. One transaction, one undo; atomic=true undoes everything on any failure. Run " +
        "revit_preview_edit_view_filters first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EditViewFiltersExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class EditViewFiltersExecutor
{
    private sealed class Item : ViewFilterWorkItem
    {
        public ViewFilterEditSpec Spec = null!;
        public FilterElement? Filter;
        public string? OldName;
        public List<ElementId>? Categories;
        public ElementFilter? NewFilter;
        public object? Usage;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var parsed = ViewFilterRequestParser.ParseEdit(request.Arguments, out var error);
        if (parsed == null) return ViewFilterBatch.Fail(request, error!, ViewFilterCodes.InvalidSpec);

        var filters = ViewFilterModel.Filters(doc);
        var usage = ViewFilterModel.UsageMap(doc);
        var byTemplate = ViewFilterModel.ViewsByTemplate(doc);
        var cache = new ViewFilterCatalogCache();
        var builder = new ViewFilterRuleBuilder(doc, cache);
        var items = new List<Item>();
        foreach (var spec in parsed.Edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new Item { Spec = spec };
            item.Issues.AddRange(spec.Issues);
            items.Add(item);
            var path = $"edits[{spec.Index}]";
            if (spec.Filter.Length == 0) continue;

            item.Filter = ViewFilterModel.FindFilter(spec.Filter, filters, path + ".filter", out var findIssue);
            if (item.Filter == null)
            {
                item.Issues.Add(findIssue!);
                continue;
            }
            item.OldName = item.Filter.Name;
            item.Usage = ViewFilterModel.DescribeUsage(item.Filter.Id.Value, usage, byTemplate);

            if (spec.NewName != null && spec.NewName.Length > 0 &&
                !string.Equals(spec.NewName, item.OldName, StringComparison.OrdinalIgnoreCase) &&
                filters.Any(f => string.Equals(f.Name, spec.NewName, StringComparison.OrdinalIgnoreCase)))
                item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.FilterNameExists, $"A view filter named '{spec.NewName}' exists.", path + ".name"));

            if (item.Filter is SelectionFilterElement)
            {
                if (spec.TouchesRuleFields)
                    item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.FilterKindMismatch, $"'{item.OldName}' is a selection filter: it has no categories or rules.", path));
                foreach (var id in (spec.ElementIds ?? new List<long>()).Concat(spec.AddElementIds ?? new List<long>()))
                    if (doc.GetElement(new ElementId(id)) == null)
                        item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.ElementNotFound, $"Element {id} does not exist.", path));
            }
            else
            {
                if (spec.TouchesMembers)
                    item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.FilterKindMismatch, $"'{item.OldName}' is a rule filter: it has no member elements.", path));
                PlanRuleFilter(doc, builder, item, (ParameterFilterElement)item.Filter, path);
            }
        }

        return ViewFilterBatch.Run(request, doc, "Revit MCP - Edit View Filters", "edit", apply, parsed.Atomic, "edits", items,
            item => Perform(doc, item),
            (item, applied) => new
            {
                index = item.Spec.Index,
                filter = item.Spec.Filter,
                filterId = item.Filter?.IsValidObject == true ? item.Filter.Id.Value : (long?)null,
                oldName = item.OldName,
                newName = item.Spec.NewName,
                status = item.Status("edit", applied),
                code = item.Code,
                reason = item.Reason,
                issues = item.Issues.Count > 0 ? item.Issues.Select(i => i.Describe()).ToList() : null,
                categories = item.Categories?.Select(c => ViewFilterModel.CategoryRef(doc, c)).ToList(),
                rules = item.Spec.HasRules ? ViewFilterRuleParser.Describe(item.Spec.Rules) : null,
                usage = item.Usage
            }, null, sw, cancellationToken);
    }

    private static void PlanRuleFilter(Document doc, ViewFilterRuleBuilder builder, Item item, ParameterFilterElement filter, string path)
    {
        var spec = item.Spec;
        if (spec.Categories != null)
            item.Categories = ViewFilterModel.ResolveCategories(doc, spec.Categories, item.Issues, path + ".categories");
        var categories = (ICollection<ElementId>?)item.Categories ?? filter.GetCategories();

        if (spec.HasRules)
        {
            item.NewFilter = builder.Build(spec.Rules, categories, item.Issues, path + ".rules");
            return;
        }

        // SetCategories silently drops rules that stop applying, so a preview must say it now.
        if (item.Categories == null || item.Categories.Count == 0 || spec.ClearRules) return;
        var existing = filter.GetElementFilter();
        if (existing != null && !ParameterFilterElement.AllRuleParametersApplicable(doc, item.Categories, existing))
            item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleParameterNotApplicable,
                $"A rule of '{filter.Name}' uses a parameter these categories do not have, and Revit would drop it. Pass new rules or clearRules=true with the categories.", path + ".categories"));
    }

    private static void Perform(Document doc, Item item)
    {
        var spec = item.Spec;
        if (item.Filter is ParameterFilterElement rule)
        {
            if (spec.ClearRules || (spec.HasRules && item.Categories != null)) rule.ClearRules();
            if (item.Categories != null) rule.SetCategories(item.Categories);
            if (spec.HasRules && item.NewFilter != null) rule.SetElementFilter(item.NewFilter);
        }
        else if (item.Filter is SelectionFilterElement selection)
        {
            if (spec.ElementIds != null) selection.SetElementIds(spec.ElementIds.Select(i => new ElementId(i)).ToList());
            if (spec.AddElementIds != null && spec.AddElementIds.Count > 0) selection.AddSet(spec.AddElementIds.Select(i => new ElementId(i)).ToList());
            if (spec.RemoveElementIds != null && spec.RemoveElementIds.Count > 0) selection.RemoveSet(spec.RemoveElementIds.Select(i => new ElementId(i)).ToList());
        }

        if (!string.IsNullOrEmpty(spec.NewName) && spec.NewName != item.Filter!.Name)
            item.Filter.Name = spec.NewName!;
    }
}

/// <summary>revit_preview_delete_view_filters (#134): every view and template that loses each filter.</summary>
public sealed class PreviewDeleteViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_preview_delete_view_filters";

    public string Description =>
        "Previews deleting view filters, without changing the model. filters: names or ids. Lists for each filter every view and template " +
        "using it, and the views that get it through a template. Reports filter_not_found for what does not exist.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DeleteViewFiltersExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_delete_view_filters (#134). DESTRUCTIVE: always asks for manual approval.</summary>
public sealed class DeleteViewFiltersTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_delete_view_filters";

    public string Description =>
        "DESTRUCTIVE: permanently deletes view filters. Always requires manual approval, which Direct Edit does not bypass. filters: names or " +
        "ids. They disappear from every view and template using them. One transaction, one undo. Run revit_preview_delete_view_filters first " +
        "and show its list of uses.";

    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DeleteViewFiltersExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => DeleteViewFiltersExecutor.Summary(uiapp, request);
}

internal static class DeleteViewFiltersExecutor
{
    private sealed class Item : ViewFilterWorkItem
    {
        public string Reference = string.Empty;
        public long? Id;
        public string? Name;
        public string? Kind;
        public object? Usage;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var references = ViewFilterRequestParser.ParseDelete(request.Arguments, out var error);
        if (references == null) return ViewFilterBatch.Fail(request, error!, ViewFilterCodes.InvalidSpec);

        var filters = ViewFilterModel.Filters(doc);
        var usage = ViewFilterModel.UsageMap(doc);
        var byTemplate = ViewFilterModel.ViewsByTemplate(doc);
        var items = new List<Item>();
        var seen = new HashSet<long>();
        for (var i = 0; i < references.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new Item { Reference = references[i] };
            items.Add(item);
            var filter = ViewFilterModel.FindFilter(references[i], filters, $"filters[{i}]", out var issue);
            if (filter == null)
            {
                item.Issues.Add(issue!);
                continue;
            }
            item.Id = filter.Id.Value;
            item.Name = filter.Name;
            item.Kind = ViewFilterModel.KindOf(filter);
            item.Usage = ViewFilterModel.DescribeUsage(filter.Id.Value, usage, byTemplate);
            if (!seen.Add(filter.Id.Value))
            {
                item.SkipCode = ViewFilterCodes.NothingToDo;
                item.SkipReason = "The same filter is named twice in this request.";
            }
        }

        return ViewFilterBatch.Run(request, doc, "Revit MCP - Delete View Filters", "delete", apply, false, "filters", items,
            item => doc.Delete(new ElementId(item.Id!.Value)),
            (item, applied) => new
            {
                filter = item.Reference,
                id = item.Id,
                name = item.Name,
                kind = item.Kind,
                status = item.Status("delete", applied),
                code = item.Code,
                reason = item.Reason,
                usage = item.Usage
            }, null, sw, cancellationToken);
    }

    public static string? Summary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var references = ViewFilterRequestParser.ParseDelete(request.Arguments, out _);
        if (references == null) return null;

        var doc = target.Document;
        var filters = ViewFilterModel.Filters(doc);
        var usage = ViewFilterModel.UsageMap(doc);
        var parts = new List<string>();
        foreach (var reference in references.Take(10))
        {
            var filter = ViewFilterModel.FindFilter(reference, filters, "filters", out _);
            if (filter == null)
            {
                parts.Add($"'{reference}' (not found)");
                continue;
            }
            usage.TryGetValue(filter.Id.Value, out var uses);
            var views = uses?.Count(v => !v.IsTemplate) ?? 0;
            var templates = uses?.Count(v => v.IsTemplate) ?? 0;
            parts.Add($"'{filter.Name}' (used in {views} view(s), {templates} template(s))");
        }
        var more = references.Count > 10 ? $" and {references.Count - 10} more" : string.Empty;
        return $"DESTRUCTIVE: Delete {references.Count} view filter(s): {string.Join("; ", parts)}{more}. Manual approval required.";
    }
}

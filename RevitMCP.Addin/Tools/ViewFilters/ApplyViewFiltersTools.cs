using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ViewFilters;

/// <summary>revit_preview_apply_view_filters (#134).</summary>
public sealed class PreviewApplyViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_preview_apply_view_filters";

    public string Description =>
        "Previews adding or removing view filters on views and templates, without changing the model. Same arguments as " +
        "revit_apply_view_filters. Lists where each change lands: when a view's template controls its filters the change goes to the " +
        "template, and the preview names it and every view it affects. Reports per operation a status and code. dryRun=true (default) also " +
        "tries it in a rolled-back transaction.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ApplyViewFiltersExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_apply_view_filters (#134): add, remove and order filters in Visibility/Graphics of views and templates.</summary>
public sealed class ApplyViewFiltersTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_apply_view_filters";

    public string Description =>
        "Adds or removes view filters on views and view templates, and sets their order. Requires approval. views: names or ids (templates " +
        "too). A view whose template controls its filters sends the change to the template. add: filter names or ids, adding with no graphics " +
        "(ready for the user to set), or {filter, enabled?, visible?, halftone?, transparency?, projectionLines? {color, weight, pattern}, " +
        "cutLines?, surfaceForeground? {color, pattern, visible}, surfaceBackground?, cutForeground?, cutBackground?, clearGraphics?}; on a " +
        "filter already applied the settings update it. remove: filter names or ids. order: filter names or ids, these first in this order " +
        "(the list in the Filters tab, top first; order decides which override wins). One transaction, one undo; atomic=true undoes everything on any failure. Run " +
        "revit_preview_apply_view_filters first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ApplyViewFiltersExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => ApplyViewFiltersExecutor.Summary(uiapp, request);
}

/// <summary>A view or template that takes filter changes, and the views asked for that led to it.</summary>
internal sealed class ViewFilterTarget
{
    public View View = null!;
    public List<ViewRoute> Routes = new();

    /// <summary>What the preview shows: the target, whether it is a template, and every view it affects.</summary>
    public object Describe(Dictionary<long, List<View>> byTemplate)
    {
        var isTemplate = View.IsTemplate;
        var controls = isTemplate && ViewFilterModel.TemplateControlsFilters(View);
        var affected = !isTemplate
            ? new List<View> { View }
            : controls && byTemplate.TryGetValue(View.Id.Value, out var list) ? list : new List<View>();
        return new
        {
            id = View.Id.Value,
            name = View.Name,
            isTemplate,
            templateControlsFilters = isTemplate ? controls : (bool?)null,
            requestedViews = Routes.Select(r => new { id = r.RequestedViewId, name = r.RequestedViewName, routedToTemplate = r.RoutedToTemplate }).ToList(),
            affectedViewCount = affected.Count,
            affectedViews = affected.Select(ViewFilterModel.ViewRef).ToList()
        };
    }

    /// <summary>Resolves references to views and groups them by where their filter changes land.</summary>
    public static List<ViewFilterTarget> Plan(Document doc, IEnumerable<string> references, List<ViewFilterIssue> issues, string path)
    {
        var views = ViewFilterModel.Views(doc);
        var targets = new List<ViewFilterTarget>();
        foreach (var reference in references)
        {
            var view = ViewFilterModel.FindView(reference, views, path, out var issue);
            if (view == null)
            {
                issues.Add(issue!);
                continue;
            }
            if (!ViewFilterModel.AllowsFilters(view))
            {
                issues.Add(new ViewFilterIssue(ViewFilterCodes.ViewCannotHaveFilters, $"View '{view.Name}' ({view.ViewType}) cannot have filters.", path, "view", view.Name));
                continue;
            }

            var route = ViewFilterModel.Route(view);
            var target = targets.FirstOrDefault(t => t.View.Id.Value == route.TargetId);
            if (target == null)
            {
                target = new ViewFilterTarget { View = route.TargetId == view.Id.Value ? view : (View)doc.GetElement(new ElementId(route.TargetId)) };
                targets.Add(target);
            }
            target.Routes.Add(route);
        }
        return targets;
    }
}

internal static class ApplyViewFiltersExecutor
{
    private const string ActionAdd = "add";
    private const string ActionUpdate = "update";
    private const string ActionRemove = "remove";
    private const string ActionOrder = "order";

    private sealed class Item : ViewFilterWorkItem
    {
        public string Action = string.Empty;
        public ViewFilterTarget? Target;
        public ElementId? FilterId;
        public string? FilterName;
        public ViewFilterApplyItem? Add;
        public Action<OverrideGraphicSettings>? Graphics;
        public List<long>? Order;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var parsed = ViewFilterRequestParser.ParseApply(request.Arguments, out var error);
        if (parsed == null) return ViewFilterBatch.Fail(request, error!, ViewFilterCodes.InvalidSpec);

        var items = Plan(doc, parsed, out var targets);
        var byTemplate = ViewFilterModel.ViewsByTemplate(doc);
        var extra = new Dictionary<string, object?> { ["targets"] = targets.Select(t => t.Describe(byTemplate)).ToList() };

        return ViewFilterBatch.Run(request, doc, "Revit MCP - Apply View Filters", "apply", apply, parsed.Atomic, "operations", items,
            Perform,
            (item, applied) => new
            {
                action = item.Action,
                targetId = item.Target?.View.Id.Value,
                target = item.Target?.View.Name,
                targetIsTemplate = item.Target?.View.IsTemplate,
                filterId = item.FilterId?.Value,
                filter = item.FilterName,
                status = item.Status("apply", applied),
                code = item.Code,
                reason = item.Reason,
                issues = item.Issues.Count > 0 ? item.Issues.Select(i => i.Describe()).ToList() : null,
                order = item.Order
            }, extra, sw, cancellationToken);
    }

    /// <summary>One operation per target and filter. A problem with the request itself is one invalid operation without a target.</summary>
    private static List<Item> Plan(Document doc, ViewFilterApplyRequest request, out List<ViewFilterTarget> targets)
    {
        var items = new List<Item>();
        var viewIssues = new List<ViewFilterIssue>();
        targets = ViewFilterTarget.Plan(doc, request.Views, viewIssues, "views");
        foreach (var issue in viewIssues)
        {
            var invalid = new Item { Action = "view" };
            invalid.Issues.Add(issue);
            items.Add(invalid);
        }

        var filters = ViewFilterModel.Filters(doc);

        var adds = new List<(ViewFilterApplyItem Add, FilterElement Filter, Action<OverrideGraphicSettings>? Graphics)>();
        foreach (var add in request.Add)
        {
            var path = $"add[{add.Index}]";
            var issues = new List<ViewFilterIssue>(add.Issues);
            FilterElement? filter = null;
            if (add.Filter.Length > 0)
            {
                filter = ViewFilterModel.FindFilter(add.Filter, filters, path + ".filter", out var findIssue);
                if (filter == null) issues.Add(findIssue!);
            }
            Action<OverrideGraphicSettings>? graphics = null;
            if (add.Graphics != null) graphics = ViewFilterGraphics.Resolve(doc, add.Graphics, issues, path);
            if (issues.Count > 0 || filter == null)
            {
                var invalid = new Item { Action = ActionAdd, FilterName = add.Filter, Add = add };
                invalid.Issues.AddRange(issues);
                items.Add(invalid);
                continue;
            }
            adds.Add((add, filter, graphics));
        }

        var removes = ResolveList(request.Remove, filters, "remove", ActionRemove, items);
        var order = ResolveList(request.Order, filters, "order", ActionOrder, items);

        foreach (var target in targets)
        {
            var current = ViewFilterModel.OrderedFilterIds(target.View);
            var added = new List<long>();
            var removed = new List<long>();

            foreach (var (add, filter, graphics) in adds)
            {
                var item = new Item { Target = target, FilterId = filter.Id, FilterName = filter.Name, Add = add, Graphics = graphics };
                var applied = current.Contains(filter.Id.Value);
                item.Action = applied ? ActionUpdate : ActionAdd;
                if (applied && !add.HasSettings)
                {
                    item.SkipCode = ViewFilterCodes.FilterAlreadyApplied;
                    item.SkipReason = $"'{filter.Name}' is already on '{target.View.Name}' and no settings were given.";
                }
                if (!applied) added.Add(filter.Id.Value);
                items.Add(item);
            }

            foreach (var filter in removes)
            {
                var item = new Item { Action = ActionRemove, Target = target, FilterId = filter.Id, FilterName = filter.Name };
                if (current.Contains(filter.Id.Value)) removed.Add(filter.Id.Value);
                else
                {
                    item.SkipCode = ViewFilterCodes.FilterNotApplied;
                    item.SkipReason = $"'{filter.Name}' is not on '{target.View.Name}'.";
                }
                items.Add(item);
            }

            if (request.Order.Count > 0) items.Add(PlanOrder(target, current, added, removed, order, filters));
        }
        return items;
    }

    private static List<FilterElement> ResolveList(List<string> references, List<FilterElement> filters, string key, string action, List<Item> items)
    {
        var resolved = new List<FilterElement>();
        for (var i = 0; i < references.Count; i++)
        {
            var filter = ViewFilterModel.FindFilter(references[i], filters, $"{key}[{i}]", out var issue);
            if (filter != null)
            {
                resolved.Add(filter);
                continue;
            }
            var invalid = new Item { Action = action, FilterName = references[i] };
            invalid.Issues.Add(issue!);
            items.Add(invalid);
        }
        return resolved;
    }

    private static Item PlanOrder(ViewFilterTarget target, List<long> current, List<long> added, List<long> removed, List<FilterElement> order, List<FilterElement> filters)
    {
        var item = new Item { Action = ActionOrder, Target = target };
        var after = ViewFilterOrderPlanner.AfterChanges(current, added, removed);
        var merged = ViewFilterOrderPlanner.Merge(after, order.Select(f => f.Id.Value).ToList(), out var missing);
        if (missing.Count > 0)
        {
            var names = missing.Select(id => filters.First(f => f.Id.Value == id).Name);
            item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.OrderFilterNotApplied,
                $"'order' names filters '{target.View.Name}' would not have: {string.Join(", ", names)}.", "order"));
            return item;
        }
        item.Order = merged;
        if (merged.SequenceEqual(after))
        {
            item.SkipCode = ViewFilterCodes.NothingToDo;
            item.SkipReason = $"'{target.View.Name}' already has this order.";
        }
        return item;
    }

    private static void Perform(Item item)
    {
        var view = item.Target!.View;
        switch (item.Action)
        {
            case ActionAdd:
            case ActionUpdate:
                ViewFilterApplier.AddOrUpdate(view, item.FilterId!, item.Add!.Enabled, item.Add.Visible, item.Add.ClearGraphics, item.Graphics, null);
                break;
            case ActionRemove:
                view.RemoveFilter(item.FilterId!);
                break;
            case ActionOrder:
                ViewFilterApplier.SetOrder(view, item.Order!);
                break;
        }
    }

    public static string? Summary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var parsed = ViewFilterRequestParser.ParseApply(request.Arguments, out _);
        if (parsed == null) return null;

        var doc = target.Document;
        var targets = ViewFilterTarget.Plan(doc, parsed.Views, new List<ViewFilterIssue>(), "views");
        var byTemplate = ViewFilterModel.ViewsByTemplate(doc);
        var parts = targets.Take(10).Select(t =>
        {
            if (!t.View.IsTemplate) return $"view '{t.View.Name}'";
            var affected = ViewFilterModel.TemplateControlsFilters(t.View) && byTemplate.TryGetValue(t.View.Id.Value, out var list) ? list.Count : 0;
            return $"template '{t.View.Name}' (affects {affected} view(s))";
        });
        var more = targets.Count > 10 ? $" and {targets.Count - 10} more" : string.Empty;
        return $"{ViewFilterSummaries.Apply(parsed)} Lands on: {string.Join(", ", parts)}{more}.";
    }
}

/// <summary>The writes on one view or template, shared by apply and copy.</summary>
internal static class ViewFilterApplier
{
    public static void AddOrUpdate(
        View view, ElementId filter, bool? enabled, bool? visible, bool clearGraphics, Action<OverrideGraphicSettings>? graphics, OverrideGraphicSettings? replace)
    {
        if (!view.IsFilterApplied(filter)) view.AddFilter(filter);

        if (replace != null)
        {
            view.SetFilterOverrides(filter, replace);
        }
        else if (clearGraphics || graphics != null)
        {
            var settings = clearGraphics ? new OverrideGraphicSettings() : new OverrideGraphicSettings(view.GetFilterOverrides(filter));
            graphics?.Invoke(settings);
            view.SetFilterOverrides(filter, settings);
        }

        if (visible != null) view.SetFilterVisibility(filter, visible.Value);
        if (enabled != null) view.SetIsFilterEnabled(filter, enabled.Value);
    }

    /// <summary>
    /// Revit has no call that reorders filters; AddFilter appends. So every filter's settings are read, all are removed and
    /// they are added back in the new order with those settings. Works the same in Revit 2024 and 2026.
    /// </summary>
    public static void SetOrder(View view, IReadOnlyList<long> order)
    {
        var ids = order.Select(i => new ElementId(i)).Where(view.IsFilterApplied).ToList();
        var settings = ids.Select(id => (Id: id, Overrides: view.GetFilterOverrides(id), Visible: view.GetFilterVisibility(id), Enabled: view.GetIsFilterEnabled(id))).ToList();

        // Anything not named (a filter the call could not see) stays after the named ones.
        var rest = view.GetOrderedFilters().Where(id => !ids.Contains(id))
            .Select(id => (Id: id, Overrides: view.GetFilterOverrides(id), Visible: view.GetFilterVisibility(id), Enabled: view.GetIsFilterEnabled(id))).ToList();

        foreach (var entry in settings.Concat(rest)) view.RemoveFilter(entry.Id);
        foreach (var entry in settings.Concat(rest))
        {
            view.AddFilter(entry.Id);
            view.SetFilterOverrides(entry.Id, entry.Overrides);
            view.SetFilterVisibility(entry.Id, entry.Visible);
            view.SetIsFilterEnabled(entry.Id, entry.Enabled);
        }
    }
}

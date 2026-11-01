using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ViewFilters;

/// <summary>revit_preview_copy_view_filters (#134).</summary>
public sealed class PreviewCopyViewFiltersTool : IRevitMcpTool
{
    public string Name => "revit_preview_copy_view_filters";

    public string Description =>
        "Previews copying view filters and their settings between views, templates and open projects, without changing either model. Same " +
        "arguments as revit_copy_view_filters. Reports for each filter the action (create | reuse | overwrite | skip), and in 'unresolved' every " +
        "category, parameter, rule value and pattern the target project does not have; a filter with an unresolved category, parameter or value " +
        "is not created. Selection filter members are reported as not copied. Lists every view a template target affects. dryRun=true (default) " +
        "also tries it in a rolled-back transaction in the target.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CopyViewFiltersExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_copy_view_filters (#134): by name; categories and parameters are resolved in the target.</summary>
public sealed class CopyViewFiltersTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_copy_view_filters";

    public string Description =>
        "Copies view filters and their settings between views, templates and open projects. Requires approval. document: the target project (empty " +
        "= active); sourceDocument: where they come from (empty = the target). sourceView: take its filters in order with enabled, visible and " +
        "graphics; filters: names or ids in the source (with sourceView: only these). targetViews: views or templates in the target to add them to " +
        "(a view whose template controls filters sends them to the template). Without targetViews the filters only arrive in the project. ifExists " +
        "(a filter of that name in the target): reuse (default) | overwrite | skip | fail. copySettings (default true). The source is never " +
        "changed. One transaction, one undo; atomic=true undoes everything on any failure. Run revit_preview_copy_view_filters first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CopyViewFiltersExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var parsed = ViewFilterRequestParser.ParseCopy(request.Arguments, out _);
        if (parsed == null) return null;
        var target = ToolDocument.Resolve(uiapp, request, out _);
        return target == null ? null : $"{ViewFilterSummaries.Copy(parsed)} Target project: '{target.Document.Title}'.";
    }
}

internal static class CopyViewFiltersExecutor
{
    /// <summary>The settings of one filter in the source view, already translated for the target document.</summary>
    private sealed class SourceSettings
    {
        public bool Enabled;
        public bool Visible;
        public OverrideGraphicSettings Overrides = null!;
        public object? Graphics;
        public List<string> MissingPatterns = new();
    }

    private abstract class Item : ViewFilterWorkItem
    {
    }

    /// <summary>A filter's definition arriving in the target.</summary>
    private sealed class DefinitionItem : Item
    {
        public string SourceReference = string.Empty;
        public string Name = string.Empty;
        public long? SourceId;
        public string? Kind;
        public string Action = ViewFilterCopyActions.Reuse;
        public string? RulesText;
        public FilterElement? Existing;
        public List<ElementId> Categories = new();
        public ElementFilter? Filter;
        public int? MembersNotCopied;
        public bool? DefinitionDiffers;
        public string? TargetRulesText;
        public long? TargetFilterId;
        public SourceSettings? Settings;
    }

    /// <summary>A filter added to one target view or template.</summary>
    private sealed class PlacementItem : Item
    {
        public DefinitionItem Definition = null!;
        public ViewFilterTarget Target = null!;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var parsed = ViewFilterRequestParser.ParseCopy(request.Arguments, out var error);
        if (parsed == null) return ViewFilterBatch.Fail(request, error!, ViewFilterCodes.InvalidSpec);

        var sourceDoc = doc;
        if (parsed.SourceDocument.Length > 0)
        {
            var source = RevitDocumentResolver.Resolve(uiapp, parsed.SourceDocument, out var sourceError);
            if (source == null)
                return ViewFilterBatch.Fail(request, sourceError ?? $"No open project '{parsed.SourceDocument}'.", ViewFilterCodes.SourceDocumentNotFound);
            sourceDoc = source.Document;
        }
        var sameDocument = sourceDoc.Equals(doc);

        if (sameDocument && parsed.TargetViews.Count == 0)
            return ViewFilterBatch.Fail(request, "Nothing to do: the source is the target, and no targetViews were given.", ViewFilterCodes.NothingToDo);

        var sourceFilters = ViewFilterModel.Filters(sourceDoc);
        var items = new List<Item>();
        var definitions = new List<DefinitionItem>();

        View? sourceView = null;
        if (parsed.SourceView.Length > 0)
        {
            sourceView = ViewFilterModel.FindView(parsed.SourceView, ViewFilterModel.Views(sourceDoc), "sourceView", out var viewIssue);
            if (sourceView == null) return ViewFilterBatch.Fail(request, viewIssue!.Reason, viewIssue.Code);
            if (!ViewFilterModel.AllowsFilters(sourceView))
                return ViewFilterBatch.Fail(request, $"View '{sourceView.Name}' ({sourceView.ViewType}) cannot have filters.", ViewFilterCodes.ViewCannotHaveFilters);
        }

        var selected = SelectFilters(parsed, sourceFilters, sourceView, definitions);
        var sourceCache = new ViewFilterCatalogCache();
        var targetCache = new ViewFilterCatalogCache();
        var builder = new ViewFilterRuleBuilder(doc, targetCache);
        var targetFilters = ViewFilterModel.Filters(doc);
        var targetNames = targetFilters.Select(f => f.Name).ToList();
        var effectiveSource = sourceView == null ? null : ViewFilterModel.EffectiveView(sourceView);

        foreach (var (filter, definition) in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanDefinition(doc, sourceDoc, sameDocument, parsed, filter, definition, targetFilters, targetNames, builder, sourceCache, targetCache);
            if (effectiveSource != null && parsed.CopySettings && effectiveSource.IsFilterApplied(filter.Id))
                definition.Settings = ReadSettings(sourceDoc, doc, effectiveSource, filter.Id);
        }

        items.AddRange(definitions);

        var targetIssues = new List<ViewFilterIssue>();
        var targets = ViewFilterTarget.Plan(doc, parsed.TargetViews, targetIssues, "targetViews");
        foreach (var issue in targetIssues)
        {
            var invalid = new DefinitionItem { Name = string.Empty, Action = "view", SourceReference = issue.Name ?? string.Empty };
            invalid.Issues.Add(issue);
            items.Add(invalid);
        }

        foreach (var view in targets)
        {
            var current = ViewFilterModel.OrderedFilterIds(view.View);
            foreach (var definition in definitions.Where(d => !d.IsSkipped))
                items.Add(PlanPlacement(definition, view, current));
        }

        var byTemplate = ViewFilterModel.ViewsByTemplate(doc);
        var extra = new Dictionary<string, object?>
        {
            ["source"] = new
            {
                document = sourceDoc.Title,
                view = sourceView == null ? null : new { id = sourceView.Id.Value, name = sourceView.Name },
                settingsFrom = effectiveSource == null ? null : new { id = effectiveSource.Id.Value, name = effectiveSource.Name, isTemplate = effectiveSource.IsTemplate }
            },
            ["targetDocument"] = doc.Title,
            ["sameDocument"] = sameDocument,
            ["ifExists"] = parsed.IfExists,
            ["unresolved"] = Unresolved(definitions),
            ["targets"] = targets.Select(t => t.Describe(byTemplate)).ToList()
        };

        return ViewFilterBatch.Run(request, doc, "Revit MCP - Copy View Filters", "copy", apply, parsed.Atomic, "operations", items,
            item => Perform(doc, item),
            (item, applied) => Describe(item, applied), extra, sw, cancellationToken);
    }

    /// <summary>The filters to copy, in source view order when there is a source view. A reference that is not found becomes an invalid definition.</summary>
    private static List<(FilterElement Filter, DefinitionItem Definition)> SelectFilters(
        ViewFilterCopyRequest parsed, List<FilterElement> sourceFilters, View? sourceView, List<DefinitionItem> definitions)
    {
        var selected = new List<(FilterElement, DefinitionItem)>();
        var named = new List<FilterElement>();
        for (var i = 0; i < parsed.Filters.Count; i++)
        {
            var filter = ViewFilterModel.FindFilter(parsed.Filters[i], sourceFilters, $"filters[{i}]", out var issue);
            if (filter != null)
            {
                if (!named.Contains(filter)) named.Add(filter);
                continue;
            }
            var missing = new DefinitionItem { SourceReference = parsed.Filters[i], Name = parsed.Filters[i] };
            missing.Issues.Add(issue!);
            definitions.Add(missing);
        }

        IEnumerable<FilterElement> ordered = named;
        if (sourceView != null)
        {
            var applied = ViewFilterModel.EffectiveView(sourceView).GetOrderedFilters().Select(id => sourceFilters.FirstOrDefault(f => f.Id == id)).Where(f => f != null).Cast<FilterElement>().ToList();
            ordered = parsed.Filters.Count == 0
                ? applied
                : applied.Where(named.Contains).Concat(named.Where(f => !applied.Contains(f)));
        }

        foreach (var filter in ordered)
        {
            var definition = new DefinitionItem { SourceReference = filter.Name, Name = filter.Name, SourceId = filter.Id.Value, Kind = ViewFilterModel.KindOf(filter) };
            definitions.Add(definition);
            selected.Add((filter, definition));
        }
        return selected;
    }

    private static void PlanDefinition(
        Document doc, Document sourceDoc, bool sameDocument, ViewFilterCopyRequest parsed, FilterElement filter, DefinitionItem item,
        List<FilterElement> targetFilters, List<string> targetNames, ViewFilterRuleBuilder builder, ViewFilterCatalogCache sourceCache, ViewFilterCatalogCache targetCache)
    {
        var path = $"filter '{filter.Name}'";
        item.Action = ViewFilterCopyMatcher.Decide(filter.Name, targetNames, parsed.IfExists, sameDocument);

        if (sameDocument)
        {
            item.TargetFilterId = filter.Id.Value;
            return;
        }

        item.Existing = targetFilters.FirstOrDefault(f => string.Equals(f.Name, filter.Name, StringComparison.OrdinalIgnoreCase));
        switch (item.Action)
        {
            case ViewFilterCopyActions.Fail:
                item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.FilterNameExists, $"A view filter named '{filter.Name}' exists in '{doc.Title}' (ifExists=fail).", path));
                return;
            case ViewFilterCopyActions.Skip:
                item.SkipCode = ViewFilterCodes.FilterNameExists;
                item.SkipReason = $"A view filter named '{filter.Name}' exists in '{doc.Title}' (ifExists=skip): left alone and not added to any view.";
                return;
        }

        if (filter is SelectionFilterElement selection)
        {
            // Element ids mean nothing in another project.
            var members = selection.GetElementIds().Count;
            item.MembersNotCopied = members;
            if (members > 0) item.Notes.Add($"{members} member element(s) of selection filter '{filter.Name}' cannot transfer between projects and are not copied.");
            if (item.Existing is ParameterFilterElement)
                Mismatch(item, path, "a rule filter", "a selection filter");
            if (item.Existing != null) item.TargetFilterId = item.Existing.Id.Value;
            return;
        }

        var rule = (ParameterFilterElement)filter;
        if (item.Existing is SelectionFilterElement)
        {
            Mismatch(item, path, "a selection filter", "a rule filter");
            return;
        }

        var sourceCategories = rule.GetCategories().ToList();
        var node = ViewFilterReader.Read(sourceDoc, rule.GetElementFilter(), sourceCategories, sourceCache, forCopy: true);
        item.RulesText = ViewFilterRuleParser.Describe(node);

        if (item.Action == ViewFilterCopyActions.Reuse && item.Existing is ParameterFilterElement existing)
        {
            item.TargetFilterId = existing.Id.Value;
            var targetCategories = existing.GetCategories().ToList();
            item.TargetRulesText = ViewFilterRuleParser.Describe(ViewFilterReader.Read(doc, existing.GetElementFilter(), targetCategories, targetCache, forCopy: true));
            item.DefinitionDiffers = item.TargetRulesText != item.RulesText ||
                                     !targetCategories.Select(c => c.Value).OrderBy(v => v).SequenceEqual(sourceCategories.Select(c => c.Value).OrderBy(v => v));
            if (item.DefinitionDiffers == true)
                item.Notes.Add($"'{filter.Name}' exists in '{doc.Title}' with another definition; the target's stays (ifExists=overwrite replaces it).");
            return;
        }

        foreach (var category in sourceCategories)
        {
            var name = Category.GetCategory(sourceDoc, category)?.Name ?? category.Value.ToString();
            if (Category.GetCategory(doc, category) == null)
                item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.CategoryNotFound, $"Category '{name}' does not exist in '{doc.Title}'.", path, "category", name));
            else if (!ParameterFilterUtilities.GetAllFilterableCategories().Contains(category))
                item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.CategoryNotFilterable, $"Category '{name}' cannot be used in a filter in '{doc.Title}'.", path, "category", name));
            else item.Categories.Add(category);
        }
        // Also with a category missing: every parameter the target lacks should be named in the same preview.
        if (item.Categories.Count > 0)
            item.Filter = builder.Build(node, item.Categories, item.Issues, path);
        else if (node != null)
            ReportRuleParameters(node, item, path);
    }

    /// <summary>The rules cannot be checked against categories that did not resolve, but every parameter should still be named.</summary>
    private static void ReportRuleParameters(ViewFilterNode node, DefinitionItem item, string path)
    {
        foreach (var rule in ViewFilterRuleParser.Rules(node).Where(r => r.ParameterName != null))
            item.Notes.Add($"Rule on '{rule.ParameterName}' was not checked in the target ({path}) because a category did not resolve.");
    }

    private static void Mismatch(DefinitionItem item, string path, string existing, string source)
    {
        item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.FilterKindMismatch, $"The target's filter of this name is {existing}, the source's is {source}.", path));
    }

    private static SourceSettings ReadSettings(Document sourceDoc, Document doc, View view, ElementId filter)
    {
        var missing = new List<string>();
        var overrides = ViewFilterGraphics.Translate(view.GetFilterOverrides(filter), sourceDoc, doc, missing);
        return new SourceSettings
        {
            Enabled = view.GetIsFilterEnabled(filter),
            Visible = view.GetFilterVisibility(filter),
            Overrides = overrides,
            Graphics = ViewFilterGraphics.Describe(doc, overrides),
            MissingPatterns = missing
        };
    }

    private static PlacementItem PlanPlacement(DefinitionItem definition, ViewFilterTarget target, List<long> current)
    {
        var item = new PlacementItem { Definition = definition, Target = target };
        if (definition.IsInvalid)
        {
            item.Issues.Add(new ViewFilterIssue(ViewFilterCodes.FilterNotCopied, $"'{definition.Name}' is not copied, so it cannot be added to '{target.View.Name}'.", definition.SourceReference));
            return item;
        }

        var applied = definition.TargetFilterId != null && current.Contains(definition.TargetFilterId.Value);
        if (applied && definition.Settings == null)
        {
            item.SkipCode = ViewFilterCodes.FilterAlreadyApplied;
            item.SkipReason = $"'{definition.Name}' is already on '{target.View.Name}' and no settings come with it.";
        }
        return item;
    }

    private static void Perform(Document doc, Item item)
    {
        switch (item)
        {
            case DefinitionItem definition:
                PerformDefinition(doc, definition);
                break;
            case PlacementItem placement:
                if (placement.Definition.TargetFilterId == null)
                    throw new InvalidOperationException($"'{placement.Definition.Name}' did not arrive in this project.");
                var filter = new ElementId(placement.Definition.TargetFilterId.Value);
                var settings = placement.Definition.Settings;
                ViewFilterApplier.AddOrUpdate(placement.Target.View, filter, settings?.Enabled, settings?.Visible, false, null, settings?.Overrides);
                break;
        }
    }

    private static void PerformDefinition(Document doc, DefinitionItem item)
    {
        switch (item.Action)
        {
            case ViewFilterCopyActions.Create:
                FilterElement created;
                if (item.Kind == ViewFilterKinds.Selection) created = SelectionFilterElement.Create(doc, item.Name);
                else
                    created = item.Filter != null
                        ? ParameterFilterElement.Create(doc, item.Name, item.Categories, item.Filter)
                        : ParameterFilterElement.Create(doc, item.Name, item.Categories);
                item.TargetFilterId = created.Id.Value;
                break;
            case ViewFilterCopyActions.Overwrite:
                if (item.Existing is ParameterFilterElement rule)
                {
                    rule.ClearRules();
                    rule.SetCategories(item.Categories);
                    if (item.Filter != null) rule.SetElementFilter(item.Filter);
                }
                item.TargetFilterId = item.Existing!.Id.Value;
                break;
        }
    }

    private static object Unresolved(List<DefinitionItem> definitions)
    {
        var missing = definitions
            .SelectMany(d => d.Issues.Where(i => i.Kind is "category" or "parameter" or "value" or "rule")
                .Select(i => new { filter = d.Name, kind = i.Kind, name = i.Name, code = i.Code, reason = i.Reason, notCreated = true }))
            .ToList();
        var patterns = definitions.Where(d => d.Settings != null)
            .SelectMany(d => d.Settings!.MissingPatterns.Select(p => new
            {
                filter = d.Name,
                kind = (string?)"pattern",
                name = (string?)p,
                code = ViewFilterCodes.PatternNotFound,
                reason = $"The target project has no {p}: left out of the filter's graphics.",
                notCreated = false
            }));
        var members = definitions.Where(d => d.MembersNotCopied > 0)
            .Select(d => new
            {
                filter = d.Name,
                kind = (string?)"selectionMembers",
                name = (string?)$"{d.MembersNotCopied} element(s)",
                code = "members_not_copied",
                reason = "A selection filter's elements cannot transfer between projects.",
                notCreated = false
            });
        return missing.Select(m => new { m.filter, m.kind, m.name, m.code, m.reason, m.notCreated })
            .Concat(patterns).Concat(members).ToList();
    }

    private static object Describe(Item item, bool applied)
    {
        switch (item)
        {
            case DefinitionItem d:
                return new
                {
                    operation = "definition",
                    filter = d.Name,
                    sourceFilterId = d.SourceId,
                    kind = d.Kind,
                    action = d.Action,
                    status = d.Status("copy", applied),
                    code = d.Code,
                    reason = d.Reason,
                    issues = d.Issues.Count > 0 ? d.Issues.Select(i => i.Describe()).ToList() : null,
                    notes = d.Notes.Count > 0 ? d.Notes : null,
                    targetFilterId = applied && d.Done ? d.TargetFilterId : null,
                    rules = d.RulesText,
                    definitionDiffers = d.DefinitionDiffers,
                    targetRules = d.DefinitionDiffers == true ? d.TargetRulesText : null,
                    membersNotCopied = d.MembersNotCopied,
                    settings = d.Settings == null ? null : new
                    {
                        enabled = d.Settings.Enabled,
                        visible = d.Settings.Visible,
                        graphics = d.Settings.Graphics,
                        missingPatterns = d.Settings.MissingPatterns.Count > 0 ? d.Settings.MissingPatterns : null
                    }
                };
            case PlacementItem p:
                return new
                {
                    operation = "addToView",
                    filter = p.Definition.Name,
                    targetId = p.Target.View.Id.Value,
                    target = p.Target.View.Name,
                    targetIsTemplate = p.Target.View.IsTemplate,
                    status = p.Status("copy", applied),
                    code = p.Code,
                    reason = p.Reason
                };
            default:
                return new { operation = "unknown" };
        }
    }
}

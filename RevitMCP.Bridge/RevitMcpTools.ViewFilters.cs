using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>View filters (#134, docs/view-filters.md): list, create, edit, delete, apply in Visibility/Graphics and view templates, copy between projects.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ViewFilterSpecsDescription =
        "Filters (max 200). Rule filter: {name, categories: [category names, OST_ names or ids], rules}. Selection filter: {name, kind: \"selection\", elementIds: [...]}. " +
        "rules is any nesting of {\"and\": [...]} and {\"or\": [...]} with leaves {parameter, operator, value, units?, tolerance?, onlyCategory?}; no rules = everything in the categories. " +
        "parameter: name, BuiltInParameter name, id or shared GUID (revit_list_filterable_parameters). operator: equals, notEquals, greater, greaterOrEqual, less, lessOrEqual, contains, notContains, beginsWith, notBeginsWith, endsWith, notEndsWith, hasValue, hasNoValue, associatedWithGlobalParameter, notAssociatedWithGlobalParameter, sharedParameterApplicable. " +
        "A number value is in the project's display units unless units=\"internal\"; an element reference is an id or a name.";

    private const string ViewFilterEditsDescription =
        "Edits (max 200): {filter: name or id, name? (rename), categories?, rules? (same shape as creating) or clearRules: true, elementIds? (replace members) or addElementIds? / removeElementIds? (selection filters)}.";

    private const string ViewFilterAddDescription =
        "Filters to add: a name or id (added with no graphics, ready for the user to set), or {filter, enabled?, visible?, halftone?, transparency? (0-100), projectionLines? {color, weight, pattern}, cutLines? {color, weight, pattern}, " +
        "surfaceForeground? {color, pattern, visible}, surfaceBackground?, cutForeground?, cutBackground?, clearGraphics?}. color: \"#RRGGBB\" or [r,g,b]; pattern: name, id, solid or none. On a filter already applied the settings update it.";

    [McpServerTool(Name = "revit_list_view_filters", ReadOnly = true),
     Description("Lists the view filters of the document: id, name, kind (rule | selection), categories, the rule tree (the shape revit_create_view_filters takes) or the member element ids, and which views and templates use each, including the views that get it through a template.")]
    public async Task<string> ListViewFilters(
        [Description("Only filters whose name contains this (case-insensitive).")] string? nameContains = null,
        [Description("rule or selection. Empty = both.")] string? kind = null,
        [Description("List the views and templates using each filter (default true).")] bool includeUsage = true,
        [Description("Include rule trees and member ids (default true).")] bool includeRules = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["nameContains"] = nameContains ?? string.Empty,
            ["kind"] = kind ?? string.Empty,
            ["includeUsage"] = includeUsage,
            ["includeRules"] = includeRules
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_list_filterable_parameters", ReadOnly = true),
     Description("Lists the parameters a view filter on the given categories can use (those common to all of them, as in Revit's Filters dialog): id, name, builtIn, sharedGuid, valueKind (Text, Integer, YesNo, Number, ElementId) and the operators it takes. Without categories it lists the filterable categories.")]
    public async Task<string> ListFilterableParameters(
        [Description("Category names, OST_ names or ids. Empty = list the filterable categories instead.")] object[]? categories = null,
        [Description("Only parameters (or categories) whose name contains this.")] string? nameContains = null,
        [Description("At most this many parameters (default 500).")] int limit = 500,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["categories"] = ToJToken(categories ?? []),
            ["nameContains"] = nameContains ?? string.Empty,
            ["limit"] = limit
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_filterable_parameters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_get_view_filters", ReadOnly = true),
     Description("For a view or view template: the filters in effect in order (the list in the Filters tab, top first; order decides which override wins), with enabled, visible and graphics, and the controlling template when it decides the view's filters (source: view | template).")]
    public async Task<string> GetViewFilters(
        [Description("View or view template name or id. Empty = the active view.")] string? view = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["view"] = view ?? string.Empty };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_get_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_view_filters", ReadOnly = true),
     Description("Previews revit_create_view_filters without changing the model. Reports per filter willCreate | skipped | invalid | failed, with every problem as a code and reason: filter_name_exists, duplicate_name_in_request, invalid_filter_name, no_categories, category_not_found, category_not_filterable, parameter_not_filterable, parameter_ambiguous, operator_unknown, operator_not_valid_for_type, rule_value_missing, rule_value_invalid, rule_value_not_found, rule_not_acceptable, element_not_found, and revit_refused from a trial run.")]
    public async Task<string> PreviewCreateViewFilters(
        [Description(ViewFilterSpecsDescription)] object[] filters,
        [Description("When a name is taken: fail (default) or skip.")] string ifNameExists = "fail",
        [Description("true: any invalid or refused filter rejects the whole request. Default false.")] bool atomic = false,
        [Description("Try the creation in a rolled-back transaction (default true). false = static checks only.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filters"] = ToJToken(filters),
            ["ifNameExists"] = ifNameExists ?? "fail",
            ["atomic"] = atomic,
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_create_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_create_view_filters"),
     Description("Creates view filters: rule filters with any nesting of AND and OR groups, and selection filters (a fixed set of elements). They are not added to any view: use revit_apply_view_filters. Requires approval. One transaction, one undo; each filter in its own sub-transaction (atomic=true undoes all on any failure). Returns per filter {index, name, status, code, reason, filterId}. Run revit_preview_create_view_filters first.")]
    public async Task<string> CreateViewFilters(
        [Description(ViewFilterSpecsDescription)] object[] filters,
        [Description("When a name is taken: fail (default) or skip.")] string ifNameExists = "fail",
        [Description("true: any invalid or refused filter rejects the whole request and nothing is created. Default false.")] bool atomic = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filters"] = ToJToken(filters),
            ["ifNameExists"] = ifNameExists ?? "fail",
            ["atomic"] = atomic
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_create_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_edit_view_filters", ReadOnly = true),
     Description("Previews revit_edit_view_filters without changing the model. Reports per edit willEdit | invalid | failed with a code and reason, the views and templates using the filter, and rule_parameter_not_applicable when new categories would lose a rule.")]
    public async Task<string> PreviewEditViewFilters(
        [Description(ViewFilterEditsDescription)] object[] edits,
        [Description("true: any invalid or refused edit rejects the whole request. Default false.")] bool atomic = false,
        [Description("Try the edits in a rolled-back transaction (default true). false = static checks only.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["edits"] = ToJToken(edits),
            ["atomic"] = atomic,
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_edit_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_edit_view_filters"),
     Description("Edits view filters: rename, change categories or rules, change a selection filter's members. Every view and template using a filter changes with it. Requires approval. One transaction, one undo (atomic=true undoes all on any failure). Run revit_preview_edit_view_filters first.")]
    public async Task<string> EditViewFilters(
        [Description(ViewFilterEditsDescription)] object[] edits,
        [Description("true: any invalid or refused edit rejects the whole request and nothing changes. Default false.")] bool atomic = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["edits"] = ToJToken(edits),
            ["atomic"] = atomic
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_edit_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_delete_view_filters", ReadOnly = true),
     Description("Previews revit_delete_view_filters without changing the model: for each filter every view and template using it, and the views that get it through a template. Reports filter_not_found for names that do not exist. dryRun=true (default) also tries it in a rolled-back transaction.")]
    public async Task<string> PreviewDeleteViewFilters(
        [Description("Filter names or ids (max 200).")] object[] filters,
        [Description("Try the deletion in a rolled-back transaction (default true).")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["filters"] = ToJToken(filters),
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_delete_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_delete_view_filters", Destructive = true),
     Description("DESTRUCTIVE: permanently deletes view filters. They disappear from every view and template using them. Always requires manual approval in Revit, which Direct Edit does not bypass. One transaction, one undo. Run revit_preview_delete_view_filters first and show the user its list of uses.")]
    public async Task<string> DeleteViewFilters(
        [Description("Filter names or ids (max 200).")] object[] filters,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["filters"] = ToJToken(filters) };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_delete_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_apply_view_filters", ReadOnly = true),
     Description("Previews revit_apply_view_filters without changing the model: 'targets' says where the change lands (a view whose template controls its filters sends it to the template; the preview names the template and lists every view it affects), 'operations' gives each add, update, remove and order a status and code (filter_not_found, view_not_found, view_cannot_have_filters, pattern_not_found, graphics_invalid, order_filter_not_applied, filter_already_applied, filter_not_applied, revit_refused).")]
    public async Task<string> PreviewApplyViewFilters(
        [Description("Views or view templates: names or ids.")] object[] views,
        [Description(ViewFilterAddDescription)] object[]? add = null,
        [Description("Filter names or ids to remove.")] object[]? remove = null,
        [Description("Filter names or ids: these first, in this order. The others follow as they are. This is the list in the Filters tab, top first; order decides which override wins.")] object[]? order = null,
        [Description("true: any invalid or refused operation rejects the whole request. Default false.")] bool atomic = false,
        [Description("Try it in a rolled-back transaction (default true). false = static checks only.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ViewFilterApplyArgs(views, add, remove, order, atomic);
        args["dryRun"] = dryRun;
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_apply_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_apply_view_filters"),
     Description("Adds or removes view filters in Visibility/Graphics of views and view templates, and sets their order. Graphics are optional: with none a filter is added with no overrides, ready for the user to set. A view whose template controls its filters sends the change to the template; a template can also be named directly. Requires approval. One transaction, one undo (atomic=true undoes all on any failure). Run revit_preview_apply_view_filters first.")]
    public async Task<string> ApplyViewFilters(
        [Description("Views or view templates: names or ids.")] object[] views,
        [Description(ViewFilterAddDescription)] object[]? add = null,
        [Description("Filter names or ids to remove.")] object[]? remove = null,
        [Description("Filter names or ids: these first, in this order. The others follow as they are. This is the list in the Filters tab, top first; order decides which override wins.")] object[]? order = null,
        [Description("true: any invalid or refused operation rejects the whole request and nothing changes. Default false.")] bool atomic = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ViewFilterApplyArgs(views, add, remove, order, atomic);
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_apply_view_filters", args, cancellationToken));
    }

    private static Dictionary<string, object?> ViewFilterApplyArgs(object[] views, object[]? add, object[]? remove, object[]? order, bool atomic) => new()
    {
        ["views"] = ToJToken(views),
        ["add"] = ToJToken(add ?? []),
        ["remove"] = ToJToken(remove ?? []),
        ["order"] = ToJToken(order ?? []),
        ["atomic"] = atomic
    };

    [McpServerTool(Name = "revit_preview_copy_view_filters", ReadOnly = true),
     Description("Previews revit_copy_view_filters without changing either model. Filters are matched by name; categories and parameters are resolved in the target. 'unresolved' names every category, parameter, rule value and pattern the target does not have; a filter with an unresolved category, parameter or value is not created. A selection filter's members are reported as not copied. 'targets' lists every view a template target affects.")]
    public async Task<string> PreviewCopyViewFilters(
        [Description("Filter names or ids in the source. With sourceView: only these. Without: required.")] object[]? filters = null,
        [Description("View or template in the source: its filters are taken in order with enabled, visible and graphics.")] string? sourceView = null,
        [Description("The open project they come from (title or path). Empty = the target document.")] string? sourceDocument = null,
        [Description("Views or templates in the target to add them to: names or ids. Empty = the filters only arrive in the project.")] object[]? targetViews = null,
        [Description("A filter of that name in the target: reuse (default), overwrite, skip or fail.")] string ifExists = "reuse",
        [Description("Copy enabled, visible and graphics from sourceView (default true).")] bool copySettings = true,
        [Description("true: any invalid or refused item rejects the whole request. Default false.")] bool atomic = false,
        [Description("Try it in a rolled-back transaction in the target (default true). false = static checks only.")] bool dryRun = true,
        [Description("The target project: " + DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ViewFilterCopyArgs(filters, sourceView, sourceDocument, targetViews, ifExists, copySettings, atomic);
        args["dryRun"] = dryRun;
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_copy_view_filters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_copy_view_filters"),
     Description("Copies view filters and their settings between views, templates and open projects, matching by name and resolving categories and parameters in the target. The source is never changed. Requires approval. One transaction in the target, one undo (atomic=true undoes all on any failure). Run revit_preview_copy_view_filters first: it reports what the target does not have.")]
    public async Task<string> CopyViewFilters(
        [Description("Filter names or ids in the source. With sourceView: only these. Without: required.")] object[]? filters = null,
        [Description("View or template in the source: its filters are taken in order with enabled, visible and graphics.")] string? sourceView = null,
        [Description("The open project they come from (title or path). Empty = the target document.")] string? sourceDocument = null,
        [Description("Views or templates in the target to add them to: names or ids. Empty = the filters only arrive in the project.")] object[]? targetViews = null,
        [Description("A filter of that name in the target: reuse (default), overwrite, skip or fail.")] string ifExists = "reuse",
        [Description("Copy enabled, visible and graphics from sourceView (default true).")] bool copySettings = true,
        [Description("true: any invalid or refused item rejects the whole request and nothing changes. Default false.")] bool atomic = false,
        [Description("The target project: " + DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ViewFilterCopyArgs(filters, sourceView, sourceDocument, targetViews, ifExists, copySettings, atomic);
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_copy_view_filters", args, cancellationToken));
    }

    private static Dictionary<string, object?> ViewFilterCopyArgs(
        object[]? filters, string? sourceView, string? sourceDocument, object[]? targetViews, string ifExists, bool copySettings, bool atomic) => new()
    {
        ["filters"] = ToJToken(filters ?? []),
        ["sourceView"] = sourceView ?? string.Empty,
        ["sourceDocument"] = sourceDocument ?? string.Empty,
        ["targetViews"] = ToJToken(targetViews ?? []),
        ["ifExists"] = ifExists ?? "reuse",
        ["copySettings"] = copySettings,
        ["atomic"] = atomic
    };
}

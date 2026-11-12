using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>View templates (docs/view-templates.md): inspect one, create from a view or a copy, edit.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ViewTemplateEditFields =
        "scale? (50 or \"1:50\"), detailLevel? (Coarse|Medium|Fine), discipline?, displayStyle? (Wireframe, Hidden Line, Shaded, …), " +
        "include?/exclude? [template parameter names it should / should not control], hideCategories?/showCategories? [category names, ids or OST_ names], " +
        "parameters? {name: value} (other parameters of the template; phase and phase filter by name)";

    [McpServerTool(Name = "revit_get_view_template", ReadOnly = true),
     Description("For one view template: view type, scale, detail level, discipline, display style, phase, phase filter, view range (plans), the parameters it controls and does not control, hidden categories, category overrides (capped), applied filters with visibility and graphics, and the views that use it.")]
    public async Task<string> GetViewTemplate(
        [Description("View template name or id (revit_list_view_templates lists them).")] string template,
        [Description("Most categories with overrides to list. Default 50.")] int maxCategories = 50,
        [Description("Most using views to list. Default 100.")] int maxViews = 100,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["template"] = template,
            ["maxCategories"] = maxCategories,
            ["maxViews"] = maxViews
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_get_view_template", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_view_templates", ReadOnly = true),
     Description("Previews revit_create_view_templates without changing the model. Same arguments. Per template: method (fromView | duplicate | copy), source and the edits' from/to; ready and problems. dryRun (default true) also runs it in a rolled-back transaction.")]
    public async Task<string> PreviewCreateViewTemplates(
        [Description("[{name, fromView? (view name or id) | duplicateFrom? (template name or id), " + ViewTemplateEditFields + "}]")] object[] templates,
        [Description("Run the creation in a transaction that is rolled back, to report what Revit refuses. Default true.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["templates"] = ToJToken(templates),
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_create_view_templates", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_create_view_templates"),
     Description("Creates view templates from a view (its settings) or as a copy of a template, optionally edited like revit_set_view_templates. Requires approval. One transaction, one undo; any problem leaves the document unchanged. No view uses a new template until revit_apply_view_template. Run revit_preview_create_view_templates first.")]
    public async Task<string> CreateViewTemplates(
        [Description("[{name, fromView? (view name or id) | duplicateFrom? (template name or id), " + ViewTemplateEditFields + "}]")] object[] templates,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["templates"] = ToJToken(templates)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_create_view_templates", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_view_templates", ReadOnly = true),
     Description("Previews revit_set_view_templates without changing the model. Same arguments. Per template: action (edit | unchanged), changes [{field, name, from, to}], viewCount and views (every view using it changes), warnings; ready and problems. dryRun (default true) also runs it in a rolled-back transaction.")]
    public async Task<string> PreviewSetViewTemplates(
        [Description("[{template (name or id), newName?, " + ViewTemplateEditFields + "}]")] object[] templates,
        [Description("Run the edits in a transaction that is rolled back, to report what Revit refuses. Default true.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["templates"] = ToJToken(templates),
            ["dryRun"] = dryRun
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_view_templates", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_view_templates"),
     Description("Edits view templates: rename, scale, detail level, discipline, display style, which parameters they control, category visibility and other parameters. Requires approval. Editing a template changes every view that uses it. One transaction, one undo; any problem leaves the document unchanged. Filters on a template: revit_apply_view_filters with the template as the view. Run revit_preview_set_view_templates first.")]
    public async Task<string> SetViewTemplates(
        [Description("[{template (name or id), newName?, " + ViewTemplateEditFields + "}]")] object[] templates,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["templates"] = ToJToken(templates)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_view_templates", args, cancellationToken));
    }
}

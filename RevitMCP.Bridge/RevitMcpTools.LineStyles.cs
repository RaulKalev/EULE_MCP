using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>
/// Line styles and object styles (docs/line-styles.md, docs/object-styles.md): list them, create and edit them, rename line
/// styles, count what uses a line style or filled region type, and delete them.
/// </summary>
internal sealed partial class RevitMcpTools
{
    private const string LineStylesDescription =
        "Line styles: [{name, weight? (Revit line weight number 1-16, not mm), color? (\"#RRGGBB\"), pattern? (a line pattern name of the document, or solid), newName? (renames an existing style)}]. " +
        "A name the document does not have is created (weight 1, black, solid unless given); an existing name gets only the fields given.";

    private const string ObjectStylesDescription =
        "Object styles: [{category (name, OST_ name or id), subcategory? (created when missing), projectionWeight?, cutWeight? (1-16), color? (\"#RRGGBB\"), pattern? (line pattern name or solid), material? (name or none)}]. " +
        "Only the fields given change.";

    private const string StyleReferencesDescription = "Names or ids.";

    [McpServerTool(Name = "revit_list_line_styles", ReadOnly = true),
     Description("Lists the line styles (subcategories of Lines) of the document, a project or an open family: id, name, weight (projection line weight 1-16), color, pattern (Solid when none) and builtIn (Revit's own, such as <Thin Lines>). canAddStyles says whether the document accepts new styles. includePatterns=true also lists the line pattern names a style can use.")]
    public async Task<string> ListLineStyles(
        [Description("Also list the document's line pattern names. Default false.")] bool includePatterns = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includePatterns"] = includePatterns
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_line_styles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_line_styles", ReadOnly = true),
     Description("Previews revit_set_line_styles without changing the model. Same arguments. Per style: action (create | edit | unchanged) with from/to for name, weight, color and pattern; ready and problems (a pattern the document does not have, a document that does not accept new line styles, a rename to a name in use).")]
    public async Task<string> PreviewSetLineStyles(
        [Description(LineStylesDescription)] object[] styles,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["styles"] = ToJToken(styles)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_line_styles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_line_styles"),
     Description("Creates line styles and edits existing ones (weight, colour, pattern, name) in the document: a project, or a family open in the Family Editor. Requires approval. One transaction, one undo; any problem leaves the document unchanged. Editing a style changes every line that uses it; Revit's own styles cannot be renamed. Run revit_preview_set_line_styles first; revit_list_line_styles includePatterns=true gives the pattern names. For a family being created, use lineStyles of revit_create_detail_family instead.")]
    public async Task<string> SetLineStyles(
        [Description(LineStylesDescription)] object[] styles,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["styles"] = ToJToken(styles)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_line_styles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_list_object_styles", ReadOnly = true),
     Description("Lists Object Styles of the document, a project or an open family: model, annotation and imported categories with projectionWeight, cutWeight (null when not cut), color, pattern, material, canAddSubcategory and their subcategories. In a family, familyCategory (the family's own category) is listed first. Paged: offset, limit (default 50, max 200), nextOffset.")]
    public async Task<string> ListObjectStyles(
        [Description("Only categories whose name contains this.")] string? category = null,
        [Description("model, annotation or imported; empty = all.")] string? kind = null,
        [Description("List each category's subcategories. Default true.")] bool includeSubcategories = true,
        [Description("Categories to skip. Default 0.")] int offset = 0,
        [Description("Categories to return (default 50, max 200).")] int limit = 50,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["kind"] = kind ?? string.Empty,
            ["includeSubcategories"] = includeSubcategories,
            ["offset"] = offset,
            ["limit"] = limit
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_object_styles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_object_styles", ReadOnly = true),
     Description("Previews revit_set_object_styles without changing the model. Same arguments. Per item: action (create | edit | unchanged) with from/to per field; ready and problems (a category, pattern or material that is not found, a cut weight on a category that is not cut, a material on an annotation category, the Lines category).")]
    public async Task<string> PreviewSetObjectStyles(
        [Description(ObjectStylesDescription)] object[] styles,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["styles"] = ToJToken(styles)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_object_styles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_object_styles"),
     Description("Edits Object Styles (line weights, colour, pattern, material of a category or subcategory) and creates subcategories, in a project or a family open in the Family Editor. Requires approval. With subcategory: created under category when missing, else edited; without: the category itself. Lines subcategories are line styles: use revit_set_line_styles. One transaction, one undo; any problem leaves the document unchanged. Run revit_preview_set_object_styles first; revit_list_object_styles gives the names.")]
    public async Task<string> SetObjectStyles(
        [Description(ObjectStylesDescription)] object[] styles,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["styles"] = ToJToken(styles)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_object_styles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_get_style_usage", ReadOnly = true),
     Description("Counts the elements that use line styles and/or filled region types: lines and other curves by line style, filled region boundary edges, filled and masking regions by type. Per style: count, byUse, byView with element ids (maxIds in all). notCounted says what it cannot see (loaded families, view templates and filters, Linework overrides).")]
    public async Task<string> GetStyleUsage(
        [Description("Line styles. " + StyleReferencesDescription)] object[]? lineStyles = null,
        [Description("Filled region types. " + StyleReferencesDescription)] object[]? filledRegionTypes = null,
        [Description("Element ids to list in all (default 50, max 1000).")] int maxIds = 50,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["lineStyles"] = ToJToken(lineStyles ?? []),
            ["filledRegionTypes"] = ToJToken(filledRegionTypes ?? []),
            ["maxIds"] = maxIds
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_get_style_usage", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_delete_styles", ReadOnly = true),
     Description("Previews revit_delete_styles without changing the model. Same arguments. Per style: status (delete | refused | not_found), code (style_in_use, builtin_style, last_filled_region_type, …), uses and reassignTo; ready is false when anything is refused, and then nothing would be deleted.")]
    public async Task<string> PreviewDeleteStyles(
        [Description("Line styles to delete. " + StyleReferencesDescription)] object[]? lineStyles = null,
        [Description("Filled region types to delete. " + StyleReferencesDescription)] object[]? filledRegionTypes = null,
        [Description("{lineStyle?, filledRegionType?}: the style the elements using a deleted style move to first.")] object? replaceWith = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["lineStyles"] = ToJToken(lineStyles ?? []),
            ["filledRegionTypes"] = ToJToken(filledRegionTypes ?? []),
            ["replaceWith"] = ToJToken(replaceWith)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_delete_styles", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_delete_styles", Destructive = true),
     Description("DESTRUCTIVE: permanently deletes line styles and filled region types. Always requires manual approval in Revit, which Direct Edit does not bypass. replaceWith moves the elements that use them first; without it a style in use is refused, as are Revit's own line styles and the last filled region type, and then nothing is deleted. One transaction, one undo. Run revit_preview_delete_styles first and show the user its list.")]
    public async Task<string> DeleteStyles(
        [Description("Line styles to delete. " + StyleReferencesDescription)] object[]? lineStyles = null,
        [Description("Filled region types to delete. " + StyleReferencesDescription)] object[]? filledRegionTypes = null,
        [Description("{lineStyle?, filledRegionType?}: the style the elements using a deleted style move to first.")] object? replaceWith = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["lineStyles"] = ToJToken(lineStyles ?? []),
            ["filledRegionTypes"] = ToJToken(filledRegionTypes ?? []),
            ["replaceWith"] = ToJToken(replaceWith)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_delete_styles", args, cancellationToken));
    }
}

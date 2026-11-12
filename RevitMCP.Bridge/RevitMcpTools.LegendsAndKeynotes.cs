using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Legends and keynotes (docs/legends-and-keynotes.md): list legends, fill one with a symbol key; read and reload the keynote table.</summary>
internal sealed partial class RevitMcpTools
{
    private const string LegendArgDescription = "The legend view: name or id (revit_list_legends).";
    private const string LegendTypesDescription =
        "Types to show: type ids, {family, type}, {family} (all its types) or \"Family: Type\". Or use category/families instead.";

    [McpServerTool(Name = "revit_list_legends", ReadOnly = true),
     Description("Lists the legend views: id, name, scale, sheets, textNotes and detailItems counts, and their legend components (id, typeId, family, type, category, viewDirection, detailLevel, position in printed mm). canSeed says whether the legend holds a legend component revit_populate_legend can copy. Revit cannot create legends from nothing: revit_create_views kind=legend duplicates one.")]
    public async Task<string> ListLegends(
        [Description("Only this legend (name or id).")] string? legend = null,
        [Description("List each legend's components. Default true; false gives counts only.")] bool includeComponents = true,
        [Description("Components listed per legend. Default 100.")] int maxComponents = 100,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["legend"] = legend ?? string.Empty,
            ["includeComponents"] = includeComponents,
            ["maxComponents"] = maxComponents
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_legends", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_populate_legend", ReadOnly = true),
     Description("Previews revit_populate_legend without changing the model. Same arguments. Per type: kind (component | detailInstance | unsupported), status, cell and label; toDelete lists what clearExisting would delete; ready and problems. dryRun=true (default) tries the placement in a rolled-back transaction, so types Revit refuses show here.")]
    public async Task<string> PreviewPopulateLegend(
        [Description(LegendArgDescription)] string legend,
        [Description(LegendTypesDescription)] object[]? types = null,
        [Description("Every type of this category (with or instead of families).")] string? category = null,
        [Description("Every type of these families.")] string[]? families = null,
        [Description("Grid columns. Default 1.")] int columns = 1,
        [Description("Row spacing in printed mm. Default 10.")] double rowSpacingMm = 10,
        [Description("Column spacing in printed mm. Default 90.")] double columnSpacingMm = 90,
        [Description("[xMm, yMm] of the first symbol's centre in printed mm. Default: one row under what the legend keeps.")] double[]? origin = null,
        [Description("typeName (default) | familyAndType | none | a type parameter name.")] string? label = null,
        [Description("Text note type name or id for the labels. Default: the document default.")] string? textType = null,
        [Description("Label left edge, printed mm right of the symbol centre. Default 12.")] double textOffsetMm = 12,
        [Description("Delete the legend components, text notes and detail items already in the legend, except the seed. Default false.")] bool clearExisting = false,
        [Description("request | family | type | category | label. Default request for types, else family.")] string? sortBy = null,
        [Description("The legend component to copy. Default: the legend's first.")] long? seedId = null,
        [Description("Try the placement in a rolled-back transaction. Default true.")] bool dryRun = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = LegendPopulateArgs(legend, types, category, families, columns, rowSpacingMm, columnSpacingMm, origin, label,
            textType, textOffsetMm, clearExisting, sortBy, seedId);
        args["dryRun"] = dryRun;
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_populate_legend", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_populate_legend"),
     Description("Fills an existing legend view with one symbol per family type and a text label beside each, in a grid. Requires approval. Model types are copies of a legend component already in the legend (the seed) switched to the type; detail item and generic annotation types are placed as instances. Revit's API cannot create a legend or its first legend component: place one by hand. clearExisting=true DELETES the legend components, text notes and detail items already in the legend, except the seed. One transaction, one undo; types Revit refuses are skipped and reported. Run revit_preview_populate_legend first.")]
    public async Task<string> PopulateLegend(
        [Description(LegendArgDescription)] string legend,
        [Description(LegendTypesDescription)] object[]? types = null,
        [Description("Every type of this category (with or instead of families).")] string? category = null,
        [Description("Every type of these families.")] string[]? families = null,
        [Description("Grid columns. Default 1.")] int columns = 1,
        [Description("Row spacing in printed mm. Default 10.")] double rowSpacingMm = 10,
        [Description("Column spacing in printed mm. Default 90.")] double columnSpacingMm = 90,
        [Description("[xMm, yMm] of the first symbol's centre in printed mm. Default: one row under what the legend keeps.")] double[]? origin = null,
        [Description("typeName (default) | familyAndType | none | a type parameter name.")] string? label = null,
        [Description("Text note type name or id for the labels. Default: the document default.")] string? textType = null,
        [Description("Label left edge, printed mm right of the symbol centre. Default 12.")] double textOffsetMm = 12,
        [Description("Delete the legend components, text notes and detail items already in the legend, except the seed. Default false.")] bool clearExisting = false,
        [Description("request | family | type | category | label. Default request for types, else family.")] string? sortBy = null,
        [Description("The legend component to copy. Default: the legend's first.")] long? seedId = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = LegendPopulateArgs(legend, types, category, families, columns, rowSpacingMm, columnSpacingMm, origin, label,
            textType, textOffsetMm, clearExisting, sortBy, seedId);
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_populate_legend", args, cancellationToken));
    }

    private static Dictionary<string, object?> LegendPopulateArgs(string legend, object[]? types, string? category, string[]? families,
        int columns, double rowSpacingMm, double columnSpacingMm, double[]? origin, string? label, string? textType, double textOffsetMm,
        bool clearExisting, string? sortBy, long? seedId)
    {
        var args = new Dictionary<string, object?>
        {
            ["legend"] = legend,
            ["columns"] = columns,
            ["rowSpacingMm"] = rowSpacingMm,
            ["columnSpacingMm"] = columnSpacingMm,
            ["textOffsetMm"] = textOffsetMm,
            ["clearExisting"] = clearExisting
        };
        if (types is { Length: > 0 }) args["types"] = ToJToken(types);
        if (!string.IsNullOrWhiteSpace(category)) args["category"] = category;
        if (families is { Length: > 0 }) args["families"] = families;
        if (origin != null) args["origin"] = origin;
        if (!string.IsNullOrWhiteSpace(label)) args["label"] = label;
        if (!string.IsNullOrWhiteSpace(textType)) args["textType"] = textType;
        if (!string.IsNullOrWhiteSpace(sortBy)) args["sortBy"] = sortBy;
        if (seedId != null) args["seedId"] = seedId;
        return args;
    }

    [McpServerTool(Name = "revit_get_keynotes", ReadOnly = true),
     Description("Reads the keynote table: entries {key, text, parent, children} in key order, with search (key or text), parent (direct children of a key; \"root\" = top level) and paging; the table's file path, path type and load status. usage=true also counts, per listed key, the types, materials and other elements whose Keynote parameter holds it, and lists keys used in the model that the table lacks. To assign a keynote to a type, set its Keynote parameter (builtInParameter KEYNOTE_PARAM) on the type id with revit_set_parameter.")]
    public async Task<string> GetKeynotes(
        [Description("Text to find in the key or the keynote text.")] string? search = null,
        [Description("Only the direct children of this key; \"root\" for the top level.")] string? parent = null,
        [Description("Entries to skip. Default 0.")] int offset = 0,
        [Description("Entries to return. Default 100, max 1000.")] int limit = 100,
        [Description("Count the types, materials and elements that carry each key. Default false.")] bool usage = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["search"] = search ?? string.Empty,
            ["offset"] = offset,
            ["limit"] = limit,
            ["usage"] = usage
        };
        if (parent != null) args["parent"] = parent;
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_get_keynotes", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_reload_keynote_table", ReadOnly = true),
     Description("Previews revit_reload_keynote_table without changing the model. Same arguments. Reads the keynote file itself (key TAB text TAB parent) and compares it with the table in the model: added, removed and changed keys, unreadable lines; ready and problems (a path outside the file access policy, a missing file). Revit's own read on reload is what counts.")]
    public async Task<string> PreviewReloadKeynoteTable(
        [Description("A local keynote .txt file to load instead of the current one. Empty = reload the current file.")] string? path = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = path ?? string.Empty
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_reload_keynote_table", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_reload_keynote_table"),
     Description("Reloads the keynote table from its current file, or loads it from path (a local keynote .txt file, which the table then points to). Requires approval. Reports entries before and after (added, removed, changed keys) and the errors Revit reported. One transaction, one undo. Run revit_preview_reload_keynote_table first.")]
    public async Task<string> ReloadKeynoteTable(
        [Description("A local keynote .txt file to load instead of the current one. Empty = reload the current file.")] string? path = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["path"] = path ?? string.Empty
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_reload_keynote_table", args, cancellationToken));
    }
}

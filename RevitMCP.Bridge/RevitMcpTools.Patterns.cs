using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Line and fill patterns (docs/patterns.md): list them, create new ones and replace existing definitions, in a project or a family.</summary>
internal sealed partial class RevitMcpTools
{
    private const string LinePatternsDescription =
        "Line patterns: [{name, segments: [{type: dash|space|dot, length (paper mm)}]}]. A dash or dot first, alternating with spaces, " +
        "an even count; a dot has no length. A name the document lacks is created; an existing name has its segments replaced.";

    private const string FillPatternsDescription =
        "Fill patterns: [{name, target? (drafting | model, default drafting), orientation? (toView | toHost | asText), and either " +
        "angle? (degrees), spacing (mm), crosshatch? (true or {angle?, spacing?}) or grids: [{angle, origin: [x, y], offset, shift, " +
        "segments: [{type: dash|space|dot, length}]}]}]. mm are paper mm for drafting patterns, model mm for model patterns. " +
        "A name the target lacks is created; an existing non-solid one has its definition replaced.";

    [McpServerTool(Name = "revit_list_line_patterns", ReadOnly = true),
     Description("Lists the line patterns of the document, a project or an open family: id, name, segments [{type: dash|space|dot, lengthMm}] in paper mm. Solid is built in and not listed. includeUsage=true adds the line styles that use each.")]
    public async Task<string> ListLinePatterns(
        [Description("Also list the line styles that use each pattern. Default false.")] bool includeUsage = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeUsage"] = includeUsage
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_line_patterns", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_line_patterns", ReadOnly = true),
     Description("Previews revit_set_line_patterns without changing the model. Same arguments. Per pattern: action (create | replace | unchanged) with the current and new segments; ready and problems.")]
    public async Task<string> PreviewSetLinePatterns(
        [Description(LinePatternsDescription)] object[] patterns,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["patterns"] = ToJToken(patterns)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_line_patterns", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_line_patterns"),
     Description("Creates line patterns and replaces the segments of existing ones in the document: a project, or a family open in the Family Editor. Requires approval. One transaction, one undo; any problem leaves the document unchanged. Replacing a pattern changes every line style that uses it. Run revit_preview_set_line_patterns first; then set a style's pattern with revit_set_line_styles.")]
    public async Task<string> SetLinePatterns(
        [Description(LinePatternsDescription)] object[] patterns,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["patterns"] = ToJToken(patterns)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_line_patterns", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_list_fill_patterns", ReadOnly = true),
     Description("Lists the fill patterns of the document, a project or an open family: id, name, target (drafting | model), isSolid, orientation and grids [{angle, origin [x, y], offset, shift, segments}] in degrees and mm (paper mm for drafting, model mm for model patterns).")]
    public async Task<string> ListFillPatterns(
        [Description("drafting, model or all (default).")] string? target = null,
        [Description("Include each pattern's grids. Default true.")] bool includeGrids = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["target"] = target ?? string.Empty,
            ["includeGrids"] = includeGrids
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_fill_patterns", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_fill_patterns", ReadOnly = true),
     Description("Previews revit_set_fill_patterns without changing the model. Same arguments. Per pattern: action (create | replace | unchanged) with the current and new grids; ready, problems and warnings.")]
    public async Task<string> PreviewSetFillPatterns(
        [Description(FillPatternsDescription)] object[] patterns,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["patterns"] = ToJToken(patterns)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_fill_patterns", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_fill_patterns"),
     Description("Creates fill patterns and replaces the definition of existing non-solid ones in the document: a project, or a family open in the Family Editor. Requires approval. The solid fill cannot be created or edited. One transaction, one undo; any problem leaves the document unchanged. Replacing a pattern changes every region, material and filter that uses it. Run revit_preview_set_fill_patterns first; then use the pattern with revit_set_filled_region_types.")]
    public async Task<string> SetFillPatterns(
        [Description(FillPatternsDescription)] object[] patterns,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["patterns"] = ToJToken(patterns)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_fill_patterns", args, cancellationToken));
    }
}

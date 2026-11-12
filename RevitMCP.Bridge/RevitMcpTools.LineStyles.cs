using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Line styles (docs/line-styles.md): list them, create new ones and edit existing ones, in a project or a family.</summary>
internal sealed partial class RevitMcpTools
{
    private const string LineStylesDescription =
        "Line styles: [{name, weight? (Revit line weight number 1-16, not mm), color? (\"#RRGGBB\"), pattern? (a line pattern name of the document, or solid)}]. " +
        "A name the document does not have is created (weight 1, black, solid unless given); an existing name gets only the fields given.";

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
     Description("Previews revit_set_line_styles without changing the model. Same arguments. Per style: action (create | edit | unchanged) with from/to for weight, color and pattern; ready and problems (a pattern the document does not have, a document that does not accept new line styles).")]
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
     Description("Creates line styles and edits existing ones (weight, colour, pattern) in the document: a project, or a family open in the Family Editor. Requires approval. One transaction, one undo; any problem leaves the document unchanged. Editing a style changes every line that uses it. Run revit_preview_set_line_styles first; revit_list_line_styles includePatterns=true gives the pattern names. For a family being created, use lineStyles of revit_create_detail_family instead.")]
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
}

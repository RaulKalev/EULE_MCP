using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Text types and dimension types (docs/annotation-types.md): list, create by duplicating, edit, rename.</summary>
internal sealed partial class RevitMcpTools
{
    private const string TextTypesDescription =
        "Text types: [{name, duplicateFrom?, newName?, font?, size? (paper mm), widthFactor?, bold?, italic?, underline?, " +
        "color? (\"#RRGGBB\", \"r,g,b\" or [r,g,b]), background? (opaque | transparent), showBorder?, leaderBorderOffset? (mm), " +
        "tabSize? (mm), lineWeight? (1-16), leaderArrowhead? (arrowhead type name or none)}]. A name the document lacks is " +
        "created from duplicateFrom (default: the default text type); an existing name gets only the fields given; newName renames.";

    private const string DimensionTypesDescription =
        "Dimension types: [{name, style? (linear, angular, radial, diameter, arcLength, spotElevation, …; picks among same-named " +
        "types), duplicateFrom?, newName?, textSize? (paper mm), textFont?, textWidthFactor?, bold?, italic?, underline?, " +
        "textOffset? (mm), textBackground? (opaque | transparent), color?, lineWeight? (1-16), tickMark? (arrowhead type name), " +
        "tickMarkLineWeight?, witnessLineExtension? (mm), witnessLineGap? (mm), dimensionLineExtension? (mm), unitsFormat? " +
        "({useProjectSettings?, unit? (mm, cm, m, ft, in, degrees), accuracy?} or \"project\")}]. A name the document lacks is " +
        "created from duplicateFrom (default: the default type of style, linear when none) and keeps its style; an existing " +
        "name gets only the fields given; newName renames.";

    [McpServerTool(Name = "revit_list_text_types", ReadOnly = true),
     Description("Lists the text types of the document, a project or an open family: id, name, font, size (paper mm), widthFactor, bold, italic, underline, color, background (opaque | transparent), showBorder, leaderBorderOffset and tabSize (mm), lineWeight, leaderArrowhead, isDefault. includeUsage=true adds how many text notes use each type.")]
    public async Task<string> ListTextTypes(
        [Description("Also count the text notes of each type. Default false.")] bool includeUsage = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["includeUsage"] = includeUsage
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_list_text_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_text_types", ReadOnly = true),
     Description("Previews revit_set_text_types without changing the model. Same arguments. Per type: action (create | edit | unchanged), duplicateFrom, usage (text notes of an edited type) and changes [{field, from, to}]; ready and problems to fix first.")]
    public async Task<string> PreviewSetTextTypes(
        [Description(TextTypesDescription)] object[] types,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["types"] = ToJToken(types)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_text_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_text_types"),
     Description("Creates text types (by duplicating) and edits or renames existing ones in the document: a project, or a family open in the Family Editor. Requires approval. One transaction, one undo; any problem leaves the document unchanged. Editing a type changes every text note that uses it. Run revit_preview_set_text_types first.")]
    public async Task<string> SetTextTypes(
        [Description(TextTypesDescription)] object[] types,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["types"] = ToJToken(types)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_text_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_set_dimension_types", ReadOnly = true),
     Description("Previews revit_set_dimension_types without changing the model. Same arguments. Per type: action (create | edit | unchanged), style, duplicateFrom, usage (dimensions of an edited type) and changes [{field, from, to}]; ready and problems to fix first.")]
    public async Task<string> PreviewSetDimensionTypes(
        [Description(DimensionTypesDescription)] object[] types,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["types"] = ToJToken(types)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_preview_set_dimension_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_set_dimension_types"),
     Description("Creates dimension types (by duplicating, keeping the source's style) and edits or renames existing ones in the document: a project, or a family open in the Family Editor. Requires approval. One transaction, one undo; any problem leaves the document unchanged. Editing a type changes every dimension that uses it. Run revit_preview_set_dimension_types first; revit_list_dimension_types details=true shows the current values.")]
    public async Task<string> SetDimensionTypes(
        [Description(DimensionTypesDescription)] object[] types,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["types"] = ToJToken(types)
        };
        args["document"] = document ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync("revit_set_dimension_types", args, cancellationToken));
    }
}

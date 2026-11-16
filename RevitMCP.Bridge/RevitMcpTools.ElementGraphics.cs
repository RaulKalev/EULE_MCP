using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Per-element hide and graphic overrides in one view (#160, docs/element-graphics.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ElementGraphicsViewDescription = "View id. Give viewId or useActiveView, not both. Never a view template.";
    private const string ElementGraphicsGraphicsDescription =
        "For operation=override: {halftone?, transparency? (0-100), projectionLines?|cutLines? {color \"#RRGGBB\"|[r,g,b], weight 1-16, pattern name|solid|none}, " +
        "surfaceForeground?|surfaceBackground?|cutForeground?|cutBackground? {color, pattern, visible}}. Only the fields given change.";

    [McpServerTool(Name = "revit_get_element_graphics", ReadOnly = true),
     Description("Reads, for elements in ONE view, whether each is hidden (Hide in View > Elements), whether it can be hidden, its element-level graphic overrides (projection/cut lines, surface and cut patterns, halftone, transparency), and every known reason it is not drawn anyway: category off, a view filter with visibility off, hidden workset, temporary hide/isolate, not in the view. Elements by elementIds or useSelection (max 500 listed). An empty notDrawnBecause list is not a guarantee of visibility. Distinct from revit_apply_view_filters (category/filter scope) and revit_set_link_visibility (linked models).")]
    public Task<string> GetElementGraphics(
        [Description(ElementGraphicsViewDescription)] long viewId = 0,
        [Description("Use the active view of the active document.")] bool useActiveView = false,
        [Description("Element ids.")] long[]? elementIds = null,
        [Description("Use the current selection (active document only).")] bool useSelection = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendElementGraphics("revit_get_element_graphics", viewId, useActiveView, elementIds, useSelection, null, null, 0, document, cancellationToken);

    [McpServerTool(Name = "revit_preview_set_element_graphics", ReadOnly = true),
     Description("Previews hide, unhide, override or resetOverrides of individual elements in ONE view WITHOUT changing the model. Same arguments as revit_set_element_graphics. Returns counts (willChange, unchanged, ineligible with reason codes notFound, notInView, cannotBeHidden, alreadyHidden, notHidden, noOverrides, sameOverrides), sampled examples, the view's limits (template, temporary hide/isolate, ownership) and which elements would still not be drawn afterwards.")]
    public Task<string> PreviewSetElementGraphics(
        [Description("hide | unhide | override | resetOverrides")] string operation,
        [Description(ElementGraphicsViewDescription)] long viewId = 0,
        [Description("Use the active view of the active document.")] bool useActiveView = false,
        [Description("Element ids.")] long[]? elementIds = null,
        [Description("Use the current selection (active document only).")] bool useSelection = false,
        [Description(ElementGraphicsGraphicsDescription)] object? graphics = null,
        [Description("Example rows to return (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendElementGraphics("revit_preview_set_element_graphics", viewId, useActiveView, elementIds, useSelection, operation, graphics, sampleSize, document, cancellationToken);

    [McpServerTool(Name = "revit_set_element_graphics"),
     Description("Per-element view graphics in ONE view, one transaction (one undo step). Requires approval. operation: hide, unhide (undoes Hide in View > Elements only), override (graphics, changes only the fields given) or resetOverrides (clears all element overrides). Elements by elementIds or useSelection; view by viewId or useActiveView. Does not change view templates, categories, filters or worksets, and does not promise visibility: the result lists elements that remain undrawn and why. Refused while the view is in temporary hide/isolate mode or checked out by another user. Run revit_preview_set_element_graphics first; revit_checkpoint_create before to roll back.")]
    public Task<string> SetElementGraphics(
        [Description("hide | unhide | override | resetOverrides")] string operation,
        [Description(ElementGraphicsViewDescription)] long viewId = 0,
        [Description("Use the active view of the active document.")] bool useActiveView = false,
        [Description("Element ids.")] long[]? elementIds = null,
        [Description("Use the current selection (active document only).")] bool useSelection = false,
        [Description(ElementGraphicsGraphicsDescription)] object? graphics = null,
        [Description("Example rows to return (default 20, max 200).")] int sampleSize = 20,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendElementGraphics("revit_set_element_graphics", viewId, useActiveView, elementIds, useSelection, operation, graphics, sampleSize, document, cancellationToken);

    private async Task<string> SendElementGraphics(string toolName, long viewId, bool useActiveView, long[]? elementIds, bool useSelection,
        string? operation, object? graphics, int sampleSize, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["viewId"] = viewId,
            ["useActiveView"] = useActiveView,
            ["elementIds"] = elementIds ?? Array.Empty<long>(),
            ["useSelection"] = useSelection,
            ["operation"] = operation ?? string.Empty,
            ["graphics"] = graphics == null ? null : ToJToken(graphics),
            ["sampleSize"] = sampleSize,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

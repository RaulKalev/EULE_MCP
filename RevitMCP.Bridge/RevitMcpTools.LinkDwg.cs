using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Linking DWG files into views (docs/link-dwg.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string DwgLinksDescription =
        "DWG files to link: [{path, viewId? | view? (view name), thisViewOnly?, unit?, placement?, colors?, layers?, correctLinesOffAxis?, pin?}] or plain paths. Per-file values override the top-level ones. Up to 50.";

    [McpServerTool(Name = "revit_preview_link_dwg", ReadOnly = true),
     Description("Previews linking DWG files into views WITHOUT changing the model. Same arguments as revit_link_dwg. Per file: the target view, thisViewOnly (always true in plan, section and drafting views; a 3D view can only take a model-wide link), unit (auto = Revit detects the scale), placement, and willLink | willSkip (already linked into that view) | invalid with the reason.")]
    public Task<string> PreviewLinkDwg(
        [Description(DwgLinksDescription)] object[]? links = null,
        [Description("DWG files, all into viewId / view (instead of links).")] string[]? paths = null,
        [Description("Target view id for files without one. Default: the active view.")] long viewId = 0,
        [Description("Target view name for files without one (instead of viewId).")] string? view = null,
        [Description("Current view only. Default: true in plan, section and drafting views; pass false to link a plan or section model-wide.")] bool? thisViewOnly = null,
        [Description("auto (default: Revit detects the DWG's units) | mm | cm | dm | m | in | ft.")] string unit = "auto",
        [Description("origin (default, origin to origin) | center | shared.")] string placement = "origin",
        [Description("preserve (default) | blackAndWhite | invert.")] string colors = "preserve",
        [Description("all (default) | visible.")] string layers = "all",
        [Description("Correct lines that are slightly off axis (default true).")] bool correctLinesOffAxis = true,
        [Description("Pin the linked instance (default false).")] bool pin = false,
        [Description("The same file is already linked into that view: skip (default) | linkAgain.")] string ifExists = "skip",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLinkDwg("revit_preview_link_dwg", links, paths, viewId, view, thisViewOnly, unit, placement, colors, layers,
            correctLinesOffAxis, pin, ifExists, document, cancellationToken);

    [McpServerTool(Name = "revit_link_dwg"),
     Description("Links DWG files into views (Insert > Link CAD). Requires approval. Into a plan, section or drafting view a DWG is linked current view only (the office rule; thisViewOnly=false is allowed for plans and sections); into a 3D view it is model-wide. unit auto (default) lets Revit detect the scale. placement origin (default) | center | shared. ifExists skip (default) leaves a file already linked into that view. One transaction per file, one undo step. Returns per file {path, view, status: linked | skipped | failed, instanceId, extentsM, reason}, with a warning when the extents suggest wrong units. Run revit_preview_link_dwg first.")]
    public Task<string> LinkDwg(
        [Description(DwgLinksDescription)] object[]? links = null,
        [Description("DWG files, all into viewId / view (instead of links).")] string[]? paths = null,
        [Description("Target view id for files without one. Default: the active view.")] long viewId = 0,
        [Description("Target view name for files without one (instead of viewId).")] string? view = null,
        [Description("Current view only. Default: true in plan, section and drafting views; pass false to link a plan or section model-wide.")] bool? thisViewOnly = null,
        [Description("auto (default: Revit detects the DWG's units) | mm | cm | dm | m | in | ft.")] string unit = "auto",
        [Description("origin (default, origin to origin) | center | shared.")] string placement = "origin",
        [Description("preserve (default) | blackAndWhite | invert.")] string colors = "preserve",
        [Description("all (default) | visible.")] string layers = "all",
        [Description("Correct lines that are slightly off axis (default true).")] bool correctLinesOffAxis = true,
        [Description("Pin the linked instance (default false).")] bool pin = false,
        [Description("The same file is already linked into that view: skip (default) | linkAgain.")] string ifExists = "skip",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLinkDwg("revit_link_dwg", links, paths, viewId, view, thisViewOnly, unit, placement, colors, layers,
            correctLinesOffAxis, pin, ifExists, document, cancellationToken);

    private async Task<string> SendLinkDwg(string toolName, object[]? links, string[]? paths, long viewId, string? view,
        bool? thisViewOnly, string unit, string placement, string colors, string layers, bool correctLinesOffAxis, bool pin,
        string ifExists, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["links"] = ToJToken(links),
            ["paths"] = paths ?? [],
            ["viewId"] = viewId,
            ["view"] = view ?? string.Empty,
            ["thisViewOnly"] = thisViewOnly,
            ["unit"] = unit,
            ["placement"] = placement,
            ["colors"] = colors,
            ["layers"] = layers,
            ["correctLinesOffAxis"] = correctLinesOffAxis,
            ["pin"] = pin,
            ["ifExists"] = ifExists,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

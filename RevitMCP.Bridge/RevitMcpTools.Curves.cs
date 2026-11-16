using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Arcs, circles and polylines as detail or model curves (#161, docs/annotation-and-sheets.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string CurvesDescription =
        "JSON array of shapes in mm. {shape:'line', x1, y1, x2, y2} | {shape:'arc', center:{x,y}, radius, startAngleDeg, endAngleDeg or sweepDeg} " +
        "| {shape:'arc', start:{x,y}, through:{x,y}, end:{x,y}} | {shape:'circle', center:{x,y}, radius} | {shape:'polyline', points:[{x,y},...], closed?}. " +
        "Detail curves use view-plane x, y only; model curves add z (center arcs and circles take plane 'xy'|'xz'|'yz', default xy). Optional lineStyle per entry.";

    [McpServerTool(Name = "revit_preview_create_curves", ReadOnly = true),
     Description("Previews revit_create_curves WITHOUT changing the model: per entry the validation result (collinear points, non-positive radius, short segments, off-plane points, z on detail curves), the Revit curves it becomes (centre, radius, sweep, length), how many elements would be created, and whether the line style exists. Same arguments as revit_create_curves.")]
    public Task<string> PreviewCreateCurves(
        [Description(CurvesDescription)] string curves,
        [Description("'detail' (view-specific) or 'model' (3D). Default 'detail'.")] string kind = "detail",
        [Description("View element ID for detail curves. Defaults to the active view.")] long viewId = 0,
        [Description("Line style name for all entries (e.g. 'Hidden Lines'); an entry may name its own.")] string? lineStyle = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendCurves("revit_preview_create_curves", curves, kind, viewId, lineStyle, document, cancellationToken);

    [McpServerTool(Name = "revit_create_curves"),
     Description("Creates true curved geometry as native Revit detail or model curves, in mm: line, circular arc (centre + radius + angles, or start + through + end), full circle and polyline. A polyline is a chain of separate line elements, not one element; element ids come back in order, per entry, with the entry's errors. kind='detail' draws in a view (viewId, default active) with view-plane x, y, no z; kind='model' draws in 3D with x, y, z on fitting sketch planes. Invalid entries (collinear arc points, radius <= 0, short segments, off-plane points) are reported and skipped, never projected. Optional lineStyle. Requires approval; one transaction, one undo. Run revit_preview_create_curves first. revit_create_lines is unchanged and still draws straight segments.")]
    public Task<string> CreateCurves(
        [Description(CurvesDescription)] string curves,
        [Description("'detail' (view-specific) or 'model' (3D). Default 'detail'.")] string kind = "detail",
        [Description("View element ID for detail curves. Defaults to the active view.")] long viewId = 0,
        [Description("Line style name for all entries (e.g. 'Hidden Lines'); an entry may name its own.")] string? lineStyle = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendCurves("revit_create_curves", curves, kind, viewId, lineStyle, document, cancellationToken);

    private async Task<string> SendCurves(string toolName, string curves, string kind, long viewId, string? lineStyle,
        string? document, CancellationToken cancellationToken)
    {
        if (!TryParseJsonArray(curves, "curves", out var parsed, out var error))
            return FormatBridgeError(error!);
        var args = new Dictionary<string, object?>
        {
            ["curves"] = parsed,
            ["kind"] = kind,
            ["viewId"] = viewId,
            ["lineStyle"] = lineStyle ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

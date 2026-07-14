using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CreateLinesTool : IRevitMcpTool
{
    public string Name => "revit_create_lines";
    public string Description => "Creates straight lines from segments given in mm: kind='detail' draws view-specific detail lines in a view, kind='model' draws model lines in 3D space (each segment gets a fitting sketch plane). Optional lineStyle name applied to all created lines. Requires approval. Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var kind = ToolArguments.GetString(request.Arguments, "kind", "detail").Trim().ToLowerInvariant();
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var lineStyleName = ToolArguments.GetString(request.Arguments, "lineStyle");

        if (kind != "detail" && kind != "model")
            return Task.FromResult(Fail(request, $"Unknown kind '{kind}'. Use 'detail' (view-specific) or 'model' (3D)."));

        if (!request.Arguments.TryGetValue("lines", out var rawLines) || rawLines == null)
            return Task.FromResult(Fail(request, "Provide 'lines': a JSON array of {x1, y1, z1, x2, y2, z2} segments in millimetres."));
        var lines = rawLines as JArray ?? (rawLines is string s ? ToolArguments.TryParseJArray(s) : null);
        if (lines == null || lines.Count == 0)
            return Task.FromResult(Fail(request, "'lines' could not be parsed as a non-empty JSON array."));

        View? view = null;
        if (kind == "detail")
        {
            var (resolvedView, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, viewId);
            if (resolvedView == null)
                return Task.FromResult(Fail(request, viewError!));
            view = resolvedView;
        }

        var shortCurveTolerance = doc.Application.ShortCurveTolerance;
        var created = new List<object>();
        var errors = new List<string>();
        ElementId? resolvedStyleId = null;
        bool styleWarned = false;

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Lines", () =>
        {
            int index = 0;
            foreach (var token in lines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                index++;

                var start = PlacementHelpers.PointFromMm(
                    PlacementHelpers.TokenDouble(token, "x1"),
                    PlacementHelpers.TokenDouble(token, "y1"),
                    PlacementHelpers.TokenDouble(token, "z1"));
                var end = PlacementHelpers.PointFromMm(
                    PlacementHelpers.TokenDouble(token, "x2"),
                    PlacementHelpers.TokenDouble(token, "y2"),
                    PlacementHelpers.TokenDouble(token, "z2"));

                if (start.DistanceTo(end) < shortCurveTolerance)
                {
                    errors.Add($"Segment {index}: too short (below Revit's short curve tolerance).");
                    continue;
                }

                try
                {
                    var geometryLine = Line.CreateBound(start, end);

                    CurveElement curve;
                    if (kind == "detail")
                    {
                        curve = doc.Create.NewDetailCurve(view!, geometryLine);
                    }
                    else
                    {
                        var plane = PlaneContainingLine(start, end);
                        var sketchPlane = SketchPlane.Create(doc, plane);
                        curve = doc.Create.NewModelCurve(geometryLine, sketchPlane);
                    }

                    if (!string.IsNullOrWhiteSpace(lineStyleName))
                    {
                        resolvedStyleId ??= FindLineStyleId(doc, curve, lineStyleName);
                        if (resolvedStyleId != null && resolvedStyleId != ElementId.InvalidElementId)
                        {
                            if (doc.GetElement(resolvedStyleId) is GraphicsStyle style)
                                curve.LineStyle = style;
                        }
                        else if (!styleWarned)
                        {
                            styleWarned = true;
                            errors.Add($"Line style '{lineStyleName}' not found — lines keep the default style.");
                        }
                    }

                    created.Add(new { elementId = curve.Id.Value });
                }
                catch (Exception ex)
                {
                    errors.Add($"Segment {index}: {ex.Message}");
                }
            }
        });

        sw.Stop();

        if (!txSuccess)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed — no lines were created.",
                Errors = errors,
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created.Count > 0,
            Message = created.Count > 0
                ? $"Created {created.Count} {kind} line(s){(view != null ? $" in view '{view.Name}'" : string.Empty)}."
                : "No lines were created.",
            Errors = errors,
            Data = new
            {
                kind,
                viewId = view?.Id.Value,
                viewName = view?.Name,
                createdCount = created.Count,
                created
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// A model curve must lie on a sketch plane. Any plane whose normal is perpendicular
    /// to the segment direction contains the segment, so one is derived per segment.
    /// </summary>
    private static Plane PlaneContainingLine(XYZ start, XYZ end)
    {
        var direction = (end - start).Normalize();
        var normal = direction.CrossProduct(XYZ.BasisZ);
        if (normal.IsZeroLength())
            normal = direction.CrossProduct(XYZ.BasisX);
        return Plane.CreateByNormalAndOrigin(normal.Normalize(), start);
    }

    private static ElementId? FindLineStyleId(Document doc, CurveElement curve, string styleName)
    {
        foreach (var styleId in curve.GetLineStyleIds())
        {
            if (doc.GetElement(styleId) is GraphicsStyle gs &&
                string.Equals(gs.GraphicsStyleCategory?.Name, styleName, StringComparison.OrdinalIgnoreCase))
                return styleId;
        }
        return ElementId.InvalidElementId;
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

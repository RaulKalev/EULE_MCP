using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Annotation;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>Read-only check of a revit_create_curves call: the geometry, element counts and per-entry failures (#161).</summary>
public sealed class PreviewCreateCurvesTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_curves";
    public string Description =>
        "Previews revit_create_curves without changing the model: each entry's validation result, the Revit curves it becomes " +
        "(arc centre, radius and sweep, lengths), how many elements would be created, and whether the line style exists. " +
        "Same arguments as revit_create_curves. Read-only.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var context = CurveDrawing.Prepare(uiapp, request, out var failure);
        if (context == null) return Task.FromResult(failure!);

        var warnings = new List<string>();
        var items = new List<object>();
        var willCreate = 0;
        var failed = 0;
        var styleChecked = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var shape in context.Shapes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = shape.Error;
            if (error == null)
            {
                // Build the Revit geometry without adding it: catches what only Revit refuses.
                try { foreach (var curve in shape.Curves) CurveDrawing.BuildCurve(curve, context); }
                catch (Exception ex) when (ex is not OperationCanceledException) { error = $"Revit refused the geometry: {ex.Message}"; }
            }

            var styleName = shape.LineStyle ?? context.LineStyle;
            if (!string.IsNullOrWhiteSpace(styleName) && !styleChecked.ContainsKey(styleName!))
            {
                var found = CurveDrawing.FindStyle(context.Doc, styleName!) != null;
                styleChecked[styleName!] = found;
                if (!found) warnings.Add($"Line style '{styleName}' not found: those curves would keep the default style.");
            }

            if (error != null) failed++;
            else willCreate += shape.Curves.Count;
            items.Add(CurveDrawing.Describe(shape, error));
        }
        sw.Stop();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{context.Shapes.Count - failed} of {context.Shapes.Count} entr{(context.Shapes.Count == 1 ? "y" : "ies")} valid; {willCreate} {context.Kind} curve element(s) would be created.",
            Data = new
            {
                preview = true,
                kind = context.Kind,
                viewId = context.View?.Id.Value,
                viewName = context.View?.Name,
                coordinateFrame = context.Frame?.Name ?? "modelXYZ",
                entries = context.Shapes.Count,
                valid = context.Shapes.Count - failed,
                failed,
                willCreate,
                items
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>
/// Draws true arcs, circles, lines and polylines (as connected line elements) as detail or model curves (#161).
/// The straight-line tool revit_create_lines is unchanged.
/// </summary>
public sealed class CreateCurvesTool : IRevitMcpTool
{
    public string Name => "revit_create_curves";
    public string Description =>
        "Creates native Revit curves from shapes in mm: line, arc (center + radius + startAngleDeg + endAngleDeg/sweepDeg, or " +
        "start + through + end points), circle (center + radius) and polyline (points, closed). curves = [{shape, ...}]. " +
        "kind='detail' draws view-specific detail curves in viewId (default active) with view-plane x, y (no z; in a plan " +
        "these are model x, y, in a section or elevation along the view's right and up); kind='model' draws model curves in " +
        "3D with x, y, z, each on a fitting sketch plane (center arcs and circles take plane xy|xz|yz). A polyline is a " +
        "chain of separate line elements, not one element; the ids come back in order. Optional lineStyle for all, or per " +
        "entry. Collinear points, non-positive radius, short segments, off-plane points and z on detail curves are " +
        "rejected per entry, not projected. Run revit_preview_create_curves first. Requires approval. One transaction, one undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var context = CurveDrawing.Prepare(uiapp, request, out var failure);
        if (context == null) return Task.FromResult(failure!);
        var doc = context.Doc;

        var errors = new List<string>();
        var warnings = new List<string>();
        var items = new List<object>();
        var createdCount = 0;
        var createdShapes = 0;
        var styles = new Dictionary<string, GraphicsStyle?>(StringComparer.OrdinalIgnoreCase);

        foreach (var shape in context.Shapes.Where(s => s.Error != null))
            errors.Add($"Entry {shape.Index} ({shape.Shape}): {shape.Error}");

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Curves", () =>
        {
            foreach (var shape in context.Shapes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (shape.Error != null)
                {
                    items.Add(CurveDrawing.Describe(shape, shape.Error));
                    continue;
                }

                GraphicsStyle? style = null;
                var styleName = shape.LineStyle ?? context.LineStyle;
                if (!string.IsNullOrWhiteSpace(styleName))
                {
                    if (!styles.TryGetValue(styleName!, out style))
                    {
                        style = CurveDrawing.FindStyle(doc, styleName!);
                        styles[styleName!] = style;
                        if (style == null) warnings.Add($"Line style '{styleName}' not found: those curves keep the default style.");
                    }
                }

                var ids = new List<long>();
                using var sub = new SubTransaction(doc);
                try
                {
                    sub.Start();
                    foreach (var planned in shape.Curves)
                    {
                        var geometry = CurveDrawing.BuildCurve(planned, context);
                        CurveElement element;
                        if (context.Detail)
                        {
                            element = doc.Create.NewDetailCurve(context.View!, geometry);
                        }
                        else
                        {
                            var plane = geometry is Line line
                                ? CreateLinesTool.PlaneContainingLine(line.GetEndPoint(0), line.GetEndPoint(1))
                                : Plane.CreateByNormalAndOrigin(((Arc)geometry).Normal, ((Arc)geometry).Center);
                            element = doc.Create.NewModelCurve(geometry, SketchPlane.Create(doc, plane));
                        }
                        if (style != null) element.LineStyle = style;
                        ids.Add(element.Id.Value);
                    }
                    sub.Commit();
                    createdCount += ids.Count;
                    createdShapes++;
                    items.Add(CurveDrawing.Describe(shape, null, ids, style?.Name ?? styleName));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                    var message = $"Revit refused it: {ex.Message}";
                    errors.Add($"Entry {shape.Index} ({shape.Shape}): {message}");
                    items.Add(CurveDrawing.Describe(shape, message));
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
                Message = diagnostics.OriginalError ?? "Transaction failed: no curves were created.",
                Errors = errors,
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = createdCount > 0,
            Message = createdCount > 0
                ? $"Created {createdCount} {context.Kind} curve element(s) from {createdShapes} of {context.Shapes.Count} entr{(context.Shapes.Count == 1 ? "y" : "ies")}{(context.View != null ? $" in view '{context.View.Name}'" : string.Empty)}."
                : "No curves were created.",
            Errors = errors,
            Warnings = warnings,
            Data = new
            {
                kind = context.Kind,
                viewId = context.View?.Id.Value,
                viewName = context.View?.Name,
                coordinateFrame = context.Frame?.Name ?? "modelXYZ",
                createdCount,
                items
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>What the preview and the apply tool of revit_create_curves share: argument reading, the view, geometry and results.</summary>
internal sealed class CurveDrawingContext
{
    public Document Doc { get; set; } = null!;
    public bool Detail { get; set; }
    public string Kind => Detail ? "detail" : "model";
    public View? View { get; set; }
    public ViewPlaneFrame? Frame { get; set; }
    public string? LineStyle { get; set; }
    public List<PlannedShape> Shapes { get; set; } = new();
}

internal static class CurveDrawing
{
    private const double FeetToMm = 304.8;

    /// <summary>Resolves the document, view and arguments and plans every entry; null with the failure result otherwise.</summary>
    public static CurveDrawingContext? Prepare(UIApplication uiapp, McpToolRequest request, out McpToolResult? failure)
    {
        failure = null;
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) { failure = documentFailure!; return null; }
        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        var args = request.Arguments;

        var kind = ToolArguments.GetString(args, "kind", "detail").Trim().ToLowerInvariant();
        if (kind != "detail" && kind != "model")
        {
            failure = Fail(request, $"Unknown kind '{kind}'. Use 'detail' (view-specific) or 'model' (3D).");
            return null;
        }
        var detail = kind == "detail";

        args.TryGetValue("curves", out var raw);
        var curves = raw as JArray ?? (raw is string s ? ToolArguments.TryParseJArray(s) : null);
        var shapes = CurveShapePlanner.Plan(curves, detail, doc.Application.ShortCurveTolerance * FeetToMm, out var planError);
        if (shapes == null) { failure = Fail(request, planError!); return null; }

        var context = new CurveDrawingContext
        {
            Doc = doc,
            Detail = detail,
            Shapes = shapes,
            LineStyle = NullIfBlank(ToolArguments.GetString(args, "lineStyle"))
        };

        if (detail)
        {
            var (view, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, ToolArguments.GetLong(args, "viewId"));
            if (view == null) { failure = Fail(request, viewError!); return null; }
            if (view is View3D)
            {
                failure = Fail(request, $"View '{view.Name}' is a 3D view: detail curves cannot be drawn in it. Use kind=model or a plan, section or drafting view.");
                return null;
            }
            context.View = view;
            context.Frame = new ViewPlaneFrame(view);
        }
        return context;
    }

    /// <summary>The Revit geometry of one planned curve, in feet; nothing is added to the document.</summary>
    public static Curve BuildCurve(PlannedCurve curve, CurveDrawingContext context)
    {
        XYZ Pt(CurvePoint p) => context.Detail
            ? context.Frame!.ToModel(p.X, p.Y)
            : PlacementHelpers.PointFromMm(p.X, p.Y, p.Z);

        switch (curve.Kind)
        {
            case "line":
                return Line.CreateBound(Pt(curve.Start), Pt(curve.End));
            case "arc":
                return Arc.Create(Pt(curve.Start), Pt(curve.End), Pt(curve.Mid));
            default:
            {
                var center = Pt(curve.Center);
                XYZ xAxis, yAxis;
                if (context.Detail)
                {
                    xAxis = (Pt(curve.Center + curve.XAxis) - center).Normalize();
                    yAxis = (Pt(curve.Center + curve.YAxis) - center).Normalize();
                }
                else
                {
                    xAxis = new XYZ(curve.XAxis.X, curve.XAxis.Y, curve.XAxis.Z);
                    yAxis = new XYZ(curve.YAxis.X, curve.YAxis.Y, curve.YAxis.Z);
                }
                return Arc.Create(center, curve.Radius / FeetToMm, 0, 2 * Math.PI, xAxis, yAxis);
            }
        }
    }

    /// <summary>A line style of the Lines category by name, or null.</summary>
    public static GraphicsStyle? FindStyle(Document doc, string name)
    {
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        if (lines == null) return null;
        foreach (Category sub in lines.SubCategories)
            if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        return null;
    }

    public static object Describe(PlannedShape shape, string? error, IReadOnlyList<long>? elementIds = null, string? lineStyle = null)
    {
        var first = shape.Curves.FirstOrDefault();
        object? Round(CurvePoint p) => new { x = Math.Round(p.X, 2), y = Math.Round(p.Y, 2), z = Math.Round(p.Z, 2) };
        var arc = first != null && first.Kind != "line" ? first : null;
        return new
        {
            index = shape.Index,
            shape = shape.Shape,
            status = error == null ? (elementIds == null ? "valid" : "created") : "error",
            error,
            curveCount = shape.Curves.Count,
            closed = shape.Shape == "polyline" ? shape.Closed : (bool?)null,
            lengthMm = error == null ? Math.Round(shape.TotalLengthMm, 2) : (double?)null,
            sweepDeg = arc != null && error == null ? Math.Round(arc.SweepDeg, 3) : (double?)null,
            radiusMm = arc != null && error == null ? Math.Round(arc.Radius, 3) : (double?)null,
            center = arc != null && error == null ? Round(arc.Center) : null,
            start = first != null && error == null && shape.Shape != "polyline" ? Round(first.Start) : null,
            end = first != null && error == null && shape.Shape != "polyline" ? Round(first.End) : null,
            lineStyle = lineStyle ?? shape.LineStyle,
            elementIds
        };
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

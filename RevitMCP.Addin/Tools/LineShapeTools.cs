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

/// <summary>Changes existing detail and model lines in place; their element ids stay (#140).</summary>
public class SetLineGeometryTool : IRevitMcpTool
{
    private const double FeetToMm = 304.8;
    private const double PlaneToleranceFt = 1.0 / FeetToMm;

    public string Name => "revit_set_line_geometry";
    public string Description =>
        "Sets the start and end points of existing straight detail and model lines in place; element ids, tags and " +
        "dimensions stay. lines = [{elementId, x1, y1, x2, y2, z1?, z2?}] in mm; a missing z keeps the point on the line's " +
        "own sketch plane. Optional lineStyle name for all of them. Points off the line's plane, arcs, pinned lines and " +
        "missing ids are skipped per line with the reason. Requires approval (Direct Edit allowed). One transaction, one undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        request.Arguments.TryGetValue("lines", out var rawLines);
        var parsed = rawLines as JArray ?? (rawLines is string s ? ToolArguments.TryParseJArray(s) : null);
        var edits = LineShapePlanner.ParseEdits(parsed, out var parseError);
        if (edits == null) return Task.FromResult(Fail(request, parseError!));

        var lineStyleName = ToolArguments.GetString(request.Arguments, "lineStyle");
        var shortCurve = doc.Application.ShortCurveTolerance;
        var warnings = new List<string>();
        var results = new List<object>();
        var updated = 0;
        var styleWarned = false;

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Set Line Geometry", () =>
        {
            foreach (var edit in edits)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var curve = doc.GetElement(new ElementId(edit.ElementId)) as CurveElement;
                string? skip = null;
                Line? newLine = null;
                if (curve == null) skip = "not a detail or model line";
                else if (curve.Pinned) skip = "pinned";
                else if (curve.GeometryCurve is not Line oldLine) skip = "not a straight line";
                else
                {
                    var plane = curve.SketchPlane?.GetPlane();
                    var start = OnPlane(edit.X1, edit.Y1, edit.Z1, oldLine.GetEndPoint(0), plane);
                    var end = OnPlane(edit.X2, edit.Y2, edit.Z2, oldLine.GetEndPoint(1), plane);
                    if (start.DistanceTo(end) < shortCurve) skip = "too short (below Revit's short curve tolerance)";
                    else if (plane != null && (OffPlane(start, plane) > PlaneToleranceFt || OffPlane(end, plane) > PlaneToleranceFt))
                        skip = "a point is off the line's sketch plane; pass z1 and z2 on the plane, or redraw the line";
                    else newLine = Line.CreateBound(start, end);
                }

                if (newLine == null)
                {
                    results.Add(new { elementId = edit.ElementId, status = "skipped", reason = skip });
                    continue;
                }

                try
                {
                    // overrideJoins: a line joined to its neighbours would otherwise pull them along or refuse.
                    curve!.SetGeometryCurve(newLine, true);
                    if (!string.IsNullOrWhiteSpace(lineStyleName))
                    {
                        var styleId = CreateLinesTool.FindLineStyleId(doc, curve, lineStyleName);
                        if (styleId != null && styleId != ElementId.InvalidElementId && doc.GetElement(styleId) is GraphicsStyle style)
                            curve.LineStyle = style;
                        else if (!styleWarned)
                        {
                            styleWarned = true;
                            warnings.Add($"Line style '{lineStyleName}' not found: the lines keep their style.");
                        }
                    }
                    updated++;
                    results.Add(new
                    {
                        elementId = edit.ElementId,
                        status = "updated",
                        start = Mm(newLine.GetEndPoint(0)),
                        end = Mm(newLine.GetEndPoint(1)),
                        lengthMm = Math.Round(newLine.Length * FeetToMm, 1)
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    results.Add(new { elementId = edit.ElementId, status = "skipped", reason = $"Revit refused it: {ex.Message}" });
                }
            }
        });
        sw.Stop();

        if (!txSuccess)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed: no lines were changed.",
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = updated > 0,
            Message = $"Changed {updated} of {edits.Count} line(s).",
            Data = new { updated, skipped = edits.Count - updated, lines = results },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>The new point: its z as given, else on a non-vertical plane through x and y, else the old point's z.</summary>
    private static XYZ OnPlane(double xMm, double yMm, double? zMm, XYZ old, Plane? plane)
    {
        var x = xMm / FeetToMm;
        var y = yMm / FeetToMm;
        if (zMm != null) return new XYZ(x, y, zMm.Value / FeetToMm);
        if (plane != null && Math.Abs(plane.Normal.Z) > 1e-6)
        {
            var d = plane.Normal.DotProduct(plane.Origin);
            return new XYZ(x, y, (d - plane.Normal.X * x - plane.Normal.Y * y) / plane.Normal.Z);
        }
        return new XYZ(x, y, old.Z);
    }

    private static double OffPlane(XYZ point, Plane plane) =>
        Math.Abs(plane.Normal.DotProduct(point - plane.Origin));

    private static object Mm(XYZ p) => new
    {
        x = Math.Round(p.X * FeetToMm, 1),
        y = Math.Round(p.Y * FeetToMm, 1),
        z = Math.Round(p.Z * FeetToMm, 1)
    };

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

/// <summary>Draws one rectangle as four detail lines in one call, optionally with centred text and as a group (#140).</summary>
public class CreateRectangleTool : IRevitMcpTool
{
    public string Name => "revit_create_rectangle";
    public string Description =>
        "Draws one rectangle as 4 detail lines in a view (viewId, default active). Give minX, minY, maxX, maxY, or center " +
        "{x, y}, width, height and optional rotationDeg, in mm. In a plan, drafting view or sheet these are model (sheet) " +
        "x and y; in a section or elevation they run along the view's right and up directions. Optional lineStyle, text " +
        "centred in it (textTypeId, default the document's text type) and asGroup=true for a detail group, so later moves " +
        "and deletes are one element. Returns the line ids, text id and group id. Requires approval (Direct Edit allowed). " +
        "One transaction, one undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
        var args = request.Arguments;

        var plan = PlanFrom(args, out var planError);
        if (plan == null) return Task.FromResult(Fail(request, planError!));

        var (view, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, ToolArguments.GetLong(args, "viewId"));
        if (view == null) return Task.FromResult(Fail(request, viewError!));
        if (view is View3D) return Task.FromResult(Fail(request, $"View '{view.Name}' is a 3D view: detail lines cannot be drawn in it."));

        var frame = new ViewPlaneFrame(view);
        var lineStyleName = ToolArguments.GetString(args, "lineStyle");
        var text = ToolArguments.GetString(args, "text");
        var textTypeId = ToolArguments.GetLong(args, "textTypeId");
        var asGroup = ToolArguments.GetBool(args, "asGroup");

        var warnings = new List<string>();
        var lineIds = new List<long>();
        long? textId = null;
        long? groupId = null;

        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Rectangle", () =>
        {
            var corners = plan.Corners.Select(c => frame.ToModel(c.X, c.Y)).ToList();
            var created = new List<ElementId>();
            GraphicsStyle? style = null;
            for (var i = 0; i < corners.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var curve = doc.Create.NewDetailCurve(view, Line.CreateBound(corners[i], corners[(i + 1) % corners.Count]));
                if (!string.IsNullOrWhiteSpace(lineStyleName))
                {
                    if (style == null && i == 0)
                    {
                        var styleId = CreateLinesTool.FindLineStyleId(doc, curve, lineStyleName);
                        style = styleId == null ? null : doc.GetElement(styleId) as GraphicsStyle;
                        if (style == null) warnings.Add($"Line style '{lineStyleName}' not found: the lines keep the default style.");
                    }
                    if (style != null) curve.LineStyle = style;
                }
                created.Add(curve.Id);
                lineIds.Add(curve.Id.Value);
            }

            if (!string.IsNullOrEmpty(text))
            {
                var typeId = textTypeId > 0 ? new ElementId(textTypeId) : doc.GetDefaultElementTypeId(ElementTypeGroup.TextNoteType);
                var options = new TextNoteOptions(typeId)
                {
                    HorizontalAlignment = HorizontalTextAlignment.Center,
                    VerticalAlignment = VerticalTextAlignment.Middle,
                    Rotation = plan.RotationDegrees * Math.PI / 180.0
                };
                var note = TextNote.Create(doc, view.Id, frame.ToModel(plan.CenterX, plan.CenterY), text, options);
                created.Add(note.Id);
                textId = note.Id.Value;
            }

            if (asGroup)
            {
                try { groupId = doc.Create.NewGroup(created).Id.Value; }
                catch (Autodesk.Revit.Exceptions.ApplicationException ex)
                {
                    warnings.Add($"The elements were not grouped: {ex.Message}");
                }
            }
        });
        sw.Stop();

        if (!txSuccess)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed: nothing was drawn.",
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Drew a {plan.Width:0.#} x {plan.Height:0.#} mm rectangle in view '{view.Name}'.",
            Data = new
            {
                viewId = view.Id.Value,
                viewName = view.Name,
                coordinateFrame = frame.Name,
                lineIds,
                textId,
                groupId,
                center = new { x = Math.Round(plan.CenterX, 1), y = Math.Round(plan.CenterY, 1) },
                widthMm = Math.Round(plan.Width, 1),
                heightMm = Math.Round(plan.Height, 1),
                rotationDeg = plan.RotationDegrees,
                corners = plan.Corners.Select(c => new { x = Math.Round(c.X, 1), y = Math.Round(c.Y, 1) }).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static RectanglePlan? PlanFrom(Dictionary<string, object?> args, out string? error)
    {
        double? Optional(string key) =>
            args.TryGetValue(key, out var v) && v != null && !(v is JValue { Type: JTokenType.Null }) && !(v is string { Length: 0 })
                ? ToolArguments.GetDouble(args, key)
                : null;

        args.TryGetValue("center", out var rawCenter);
        var center = rawCenter as JObject ?? (rawCenter is string s ? ToolArguments.TryParseJObject(s) : null);
        return LineShapePlanner.PlanRectangle(
            Optional("minX"), Optional("minY"), Optional("maxX"), Optional("maxY"),
            center?["x"]?.Value<double?>(), center?["y"]?.Value<double?>(), Optional("width"), Optional("height"),
            ToolArguments.GetDouble(args, "rotationDeg"),
            out error);
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>revit_preview_draw_circuit_arcs: which devices would be joined by arcs, no changes.</summary>
public sealed class PreviewDrawCircuitArcsTool : IRevitMcpTool
{
    public string Name => "revit_preview_draw_circuit_arcs";
    public string Description =>
        "Previews drawing arc detail lines between the devices of electrical circuits in a plan view (a stand-in for Revit wires) " +
        "WITHOUT changing anything: per circuit the devices seen in the view, the arcs that would join them and the arcs that would be replaced.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DrawCircuitArcsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_draw_circuit_arcs: draws arc detail lines between the devices of circuits in a plan view.</summary>
public sealed class DrawCircuitArcsTool : IRevitMcpTool
{
    public string Name => "revit_draw_circuit_arcs";
    public string Description =>
        "Draws arc detail lines between the devices of electrical circuits in a plan view, as a stand-in for Revit wires (requires approval). " +
        "Only devices visible in the view are joined. The arcs are marked with their circuit, so drawing a circuit again replaces its arcs " +
        "in that view. Run the preview first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(DrawCircuitArcsExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class DrawCircuitArcsExecutor
{
    // Marks an arc with the circuit it was drawn for, so the circuit's arcs can be found and replaced.
    private static readonly Guid MarkerGuid = new("5d0f6b1e-2c1a-4f6f-9a43-8b0d2e7c4a19");
    private const string MarkerName = "RevitMcpCircuitArc";
    private const string MarkerField = "CircuitUniqueId";

    private sealed class CircuitPlan
    {
        public ElectricalSystem Circuit = null!;
        public int DevicesOnCircuit;
        public List<CircuitArcPlanner.Node> Nodes = new();
        public CircuitArcPlanner.Plan Plan = new();
        public List<ElementId> Existing = new();
        public bool HasHomeRun;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc == null) return Fail(request, "No active document.");

        var (view, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc!, doc, ToolArguments.GetLong(request.Arguments, "viewId"));
        if (view == null) return Fail(request, viewError!);
        if (view is not ViewPlan)
            return Fail(request, $"View '{view.Name}' is a {view.ViewType}; circuit arcs are drawn in floor or ceiling plans. Pass the plan's viewId.");

        var mode = ToolArguments.GetString(request.Arguments, "mode", CircuitArcPlanner.Chain).Trim().ToLowerInvariant();
        if (mode != CircuitArcPlanner.Chain && mode != CircuitArcPlanner.Tree)
            return Fail(request, "mode must be 'chain' (device to nearest device) or 'tree' (shortest total, with branches).");
        var side = ToolArguments.GetString(request.Arguments, "side", "left").Trim().ToLowerInvariant();
        if (side != "left" && side != "right" && side != "alternate")
            return Fail(request, "side must be left, right or alternate.");
        var bulge = ToolArguments.GetDouble(request.Arguments, "bulge", 0.2);
        if (bulge <= 0 || bulge > 1)
            return Fail(request, "bulge is the arc height as a fraction of the distance between two devices: above 0, at most 1 (default 0.2).");
        var minDistance = UnitUtils.ConvertToInternalUnits(Math.Max(1, ToolArguments.GetDouble(request.Arguments, "minDistanceMm", 100)), UnitTypeId.Millimeters);
        var replaceExisting = ToolArguments.GetBool(request.Arguments, "replaceExisting", true);
        // Spline by default: its three points can be dragged in Revit, which an arc's cannot
        var curveKind = ToolArguments.GetString(request.Arguments, "curve", "spline").Trim().ToLowerInvariant();
        if (curveKind.Length == 0) curveKind = "spline";
        if (curveKind != "arc" && curveKind != "spline")
            return Fail(request, "curve must be 'spline' (through the two devices and the bulge point; its points can be dragged in Revit) or 'arc'.");
        var connectToPanel = ToolArguments.GetBool(request.Arguments, "connectToPanel");
        var homeRun = ToolArguments.GetBool(request.Arguments, "homeRun");
        var homeRunLength = UnitUtils.ConvertToInternalUnits(Math.Max(50, ToolArguments.GetDouble(request.Arguments, "homeRunLengthMm", 900)), UnitTypeId.Millimeters);
        var homeRunSide = ToolArguments.GetString(request.Arguments, "homeRunSide", "right").Trim().ToLowerInvariant();
        if (homeRunSide != "left" && homeRunSide != "right")
            return Fail(request, "homeRunSide must be left or right.");

        // Circuits: given ids, or every circuit of a panel
        var errors = new List<string>();
        var circuits = new List<ElectricalSystem>();
        foreach (var id in ToolArguments.GetLongArray(request.Arguments, "circuitIds"))
        {
            if (doc.GetElement(new ElementId(id)) is ElectricalSystem circuit)
            {
                if (circuits.All(c => c.Id != circuit.Id)) circuits.Add(circuit);
            }
            else errors.Add($"{id} is not an electrical circuit.");
        }
        var panelSelector = ToolArguments.GetString(request.Arguments, "panel").Trim();
        if (panelSelector.Length > 0)
        {
            var panel = RevitMCP.Addin.PanelSchematics.PanelSchematicsModel.ResolvePanel(doc, panelSelector, out var panelError);
            if (panel == null) errors.Add(panelError!);
            else
                foreach (var circuit in RevitMCP.Addin.PanelSchematics.PanelSchematicsModel.Circuits(doc, panel.Id))
                    if (circuits.All(c => c.Id != circuit.Id)) circuits.Add(circuit);
        }
        if (errors.Count == 0 && circuits.Count == 0)
            errors.Add("Give circuitIds or panel (all circuits of that panel).");
        if (errors.Count > 0)
            return Fail(request, errors[0], errors);

        var warnings = new List<string>();
        GraphicsStyle? lineStyle = null;
        var lineStyleName = ToolArguments.GetString(request.Arguments, "lineStyle").Trim();
        if (lineStyleName.Length > 0)
        {
            lineStyle = FindLineStyle(doc, lineStyleName);
            if (lineStyle == null)
                return Fail(request, $"Line style '{lineStyleName}' was not found. Styles: {string.Join(", ", LineStyleNames(doc).Take(40))}.");
        }

        // The view's plane, and what the view shows
        var origin = view.Origin;
        var right = view.RightDirection;
        var up = view.UpDirection;
        var normal = view.ViewDirection;
        var visible = new HashSet<ElementId>(new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType().ToElementIds());
        var existingByCircuit = ExistingArcs(doc, view);

        (double U, double V) ToPlane(XYZ point)
        {
            var offset = point - origin;
            return (offset.DotProduct(right), offset.DotProduct(up));
        }
        XYZ FromPlane(double u, double v) => origin + right.Multiply(u) + up.Multiply(v);

        var plans = new List<CircuitPlan>();
        foreach (var circuit in circuits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var plan = new CircuitPlan { Circuit = circuit };
            if (existingByCircuit.TryGetValue(circuit.UniqueId, out var existing)) plan.Existing = existing;

            try
            {
                foreach (Element element in circuit.Elements)
                {
                    if (element == null) continue;
                    plan.DevicesOnCircuit++;
                    if (!visible.Contains(element.Id)) continue;
                    var point = PointOf(element, view);
                    if (point == null) continue;
                    var (u, v) = ToPlane(point);
                    plan.Nodes.Add(new CircuitArcPlanner.Node(element.Id.Value, u, v));
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Circuit {circuit.Id.Value}: its devices could not be read ({ex.Message}).");
            }

            int devicesInView = plan.Nodes.Count;

            // Start at the device nearest to the panel, so the chain runs away from it
            int start = 0;
            XYZ? panelPoint = null;
            try { panelPoint = circuit.BaseEquipment == null ? null : PointOf(circuit.BaseEquipment, view); }
            catch { /* no panel */ }
            CircuitArcPlanner.ArcSegment? homeRunArc = null;
            if (panelPoint != null && plan.Nodes.Count > 0)
            {
                var (pu, pv) = ToPlane(panelPoint);
                start = CircuitArcPlanner.Nearest(plan.Nodes, pu, pv);
                if (connectToPanel && visible.Contains(circuit.BaseEquipment!.Id))
                {
                    plan.Nodes.Insert(0, new CircuitArcPlanner.Node(circuit.BaseEquipment.Id.Value, pu, pv));
                    start = 0;
                }
                else if (homeRun)
                {
                    // From the device nearest the panel, pointing at it
                    homeRunArc = CircuitArcPlanner.HomeRun(plan.Nodes[start], pu, pv, homeRunLength, bulge, homeRunSide);
                }
            }

            plan.Plan = CircuitArcPlanner.Build(plan.Nodes, start, mode, bulge, side, minDistance);
            if (homeRunArc != null)
            {
                plan.Plan.Arcs.Insert(0, homeRunArc);
                plan.HasHomeRun = true;
            }
            plans.Add(plan);

            // A circuit with one device in the view and a home run has that to draw; without one there is nothing
            if (plan.Nodes.Count < 2 && homeRunArc == null)
                warnings.Add($"Circuit {circuit.Id.Value} '{circuit.Name}': {devicesInView} of {plan.DevicesOnCircuit} device(s) are visible in '{view.Name}', nothing to join.");
            else if (devicesInView < plan.DevicesOnCircuit)
                warnings.Add($"Circuit {circuit.Id.Value} '{circuit.Name}': only {devicesInView} of {plan.DevicesOnCircuit} device(s) are visible in '{view.Name}'; the rest are on another level or hidden.");
        }

        int toDraw = plans.Sum(p => p.Plan.Arcs.Count);
        int toReplace = replaceExisting ? plans.Sum(p => p.Existing.Count) : 0;

        object Summary(Dictionary<long, List<long>>? created) => new
        {
            view = new { id = view.Id.Value, name = view.Name },
            mode,
            curve = curveKind,
            arcs = toDraw,
            replaced = toReplace,
            circuits = plans.Select(p => new
            {
                circuitId = p.Circuit.Id.Value,
                name = p.Circuit.Name,
                devicesOnCircuit = p.DevicesOnCircuit,
                devicesInView = p.Nodes.Count,
                homeRun = p.HasHomeRun,
                arcs = p.Plan.Arcs.Select(a => new
                {
                    from = a.FromId,
                    to = a.ToId == 0 ? (long?)null : a.ToId,
                    kind = a.ToId == 0 ? "homeRun" : "link",
                    lengthMm = Math.Round(UnitUtils.ConvertFromInternalUnits(a.Chord, UnitTypeId.Millimeters))
                }),
                stackedDevices = p.Plan.Coincident,
                existingArcs = p.Existing.Count,
                existingArcIds = p.Existing.Select(id => id.Value),
                createdArcIds = created != null && created.TryGetValue(p.Circuit.Id.Value, out var ids) ? ids : null
            })
        };

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Would draw {toDraw} arc(s) for {plans.Count} circuit(s) in '{view.Name}'" + (toReplace > 0 ? $", replacing {toReplace} earlier arc(s)." : "."),
                Data = Summary(null),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (toDraw == 0 && toReplace == 0)
            return new McpToolResult
            {
                RequestId = request.RequestId, Success = true, Message = "Nothing to draw.", Data = Summary(null), Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };

        var shortCurve = doc.Application.ShortCurveTolerance;
        var createdIds = new Dictionary<long, List<long>>();
        int maxSplinePoints = 0;
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - Draw circuit arcs", () =>
        {
            var schema = MarkerSchema();
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (replaceExisting && plan.Existing.Count > 0)
                    doc.Delete(plan.Existing);

                var ids = new List<long>();
                foreach (var arc in plan.Plan.Arcs)
                {
                    if (arc.Chord < shortCurve) continue;
                    var from = FromPlane(arc.X1, arc.Y1);
                    var to = FromPlane(arc.X2, arc.Y2);
                    var middle = FromPlane(arc.MidX, arc.MidY);
                    var curve = curveKind == "spline"
                        ? NewDetailSpline(doc, view, from, middle, to)
                        : doc.Create.NewDetailCurve(view, Arc.Create(from, to, middle));
                    if (curve == null)
                        throw new InvalidOperationException($"Revit did not create a detail {curveKind} between elements {arc.FromId} and {arc.ToId}.");
                    if (curve.GeometryCurve is NurbSpline created)
                        maxSplinePoints = Math.Max(maxSplinePoints, created.CtrlPoints.Count);
                    if (lineStyle != null) curve.LineStyle = lineStyle;

                    var marker = new Entity(schema);
                    marker.Set(MarkerField, plan.Circuit.UniqueId);
                    curve.SetEntity(marker);
                    ids.Add(curve.Id.Value);
                }
                createdIds[plan.Circuit.Id.Value] = ids;
            }
        });

        sw.Stop();
        int drawn = createdIds.Values.Sum(v => v.Count);
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success,
            Status = transaction.Success ? null : "transaction_failed",
            Message = transaction.Success
                ? $"Drew {drawn} arc(s) for {plans.Count} circuit(s) in '{view.Name}'" + (toReplace > 0 ? $", replacing {toReplace} earlier arc(s)." : ".")
                : $"Drawing the arcs failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
            Data = new
            {
                result = Summary(transaction.Success ? createdIds : null),
                // Points a user drags to reshape a spline: 3 means the two ends and one in between
                splineControlPoints = maxSplinePoints > 0 ? maxSplinePoints : (int?)null,
                transaction = transaction.Diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// A detail spline with the fewest points there can be: its two ends and one control point between them, so it
    /// is reshaped in Revit by dragging a single point. A three-point spline only goes halfway to its control
    /// point, so the control point sits twice as far from the chord as <paramref name="apex"/>, the point the
    /// curve should reach.
    /// Never pass a Hermite spline to NewDetailCurve: in Revit 2026 that call creates the line and then throws a
    /// NullReferenceException, which leaves an unmarked duplicate behind when the caller carries on.
    /// </summary>
    private static DetailCurve? NewDetailSpline(Document doc, View view, XYZ from, XYZ apex, XYZ to)
    {
        var chordMiddle = (from + to) / 2;
        var control = chordMiddle + (apex - chordMiddle) * 2;
        var spline = NurbSpline.CreateCurve(new List<XYZ> { from, control, to }, new List<double> { 1, 1, 1 });
        return doc.Create.NewDetailCurve(view, spline);
    }

    /// <summary>Where a device is shown: its insertion point, or the middle of its box in the view.</summary>
    private static XYZ? PointOf(Element element, View view)
    {
        if (element.Location is LocationPoint location) return location.Point;
        var box = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
        return box == null ? null : (box.Min + box.Max) / 2;
    }

    private static Schema MarkerSchema()
    {
        var schema = Schema.Lookup(MarkerGuid);
        if (schema != null) return schema;

        var builder = new SchemaBuilder(MarkerGuid);
        builder.SetSchemaName(MarkerName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.SetDocumentation("Marks a detail arc drawn by Revit MCP for an electrical circuit.");
        builder.AddSimpleField(MarkerField, typeof(string));
        return builder.Finish();
    }

    /// <summary>Arcs this tool drew in the view earlier, by circuit UniqueId.</summary>
    private static Dictionary<string, List<ElementId>> ExistingArcs(Document doc, View view)
    {
        var result = new Dictionary<string, List<ElementId>>(StringComparer.Ordinal);
        var schema = Schema.Lookup(MarkerGuid);
        if (schema == null) return result;

        foreach (var curve in new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement))
                     .WherePasses(new ExtensibleStorageFilter(MarkerGuid)).Cast<CurveElement>())
        {
            if (curve.OwnerViewId != view.Id) continue;
            try
            {
                var entity = curve.GetEntity(schema);
                var circuit = entity.IsValid() ? entity.Get<string>(MarkerField) : null;
                if (string.IsNullOrEmpty(circuit)) continue;
                if (!result.TryGetValue(circuit!, out var list)) result[circuit!] = list = new List<ElementId>();
                list.Add(curve.Id);
            }
            catch
            {
                // A marker that cannot be read is not ours to replace.
            }
        }
        return result;
    }

    private static GraphicsStyle? FindLineStyle(Document doc, string name)
    {
        var lines = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
        foreach (Category sub in lines.SubCategories)
            if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
                return sub.GetGraphicsStyle(GraphicsStyleType.Projection);
        return null;
    }

    private static IEnumerable<string> LineStyleNames(Document doc)
        => doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines).SubCategories.Cast<Category>().Select(c => c.Name).OrderBy(n => n);

    private static McpToolResult Fail(McpToolRequest request, string message, List<string>? errors = null)
    {
        var result = new McpToolResult { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = message };
        if (errors != null) result.Errors.AddRange(errors);
        return result;
    }
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Places dimensions of every kind the Revit API supports creating:
/// aligned/horizontal/vertical linear dimensions, angular, radial, diameter,
/// spot elevations and spot coordinates.
/// </summary>
public class PlaceDimensionsTool : IRevitMcpTool
{
    public string Name => "revit_place_dimensions";
    public string Description =>
        "Places dimensions in a view. Requires approval. " +
        "kind: aligned | horizontal | vertical (2+ elementIds, one dimension across all) | " +
        "angular (exactly 2 linear elements) | radial | diameter | arcLength (arc elements, one dimension each) | " +
        "spotElevation | spotCoordinate (one spot per element, with leader). Spot slope is not supported by the Revit API. " +
        "Required: kind, elementIds. Optional: viewId (default active view), dimensionTypeId (from revit_list_dimension_types), " +
        "offsetMm (distance of the dimension line / leader bend from the elements, default 1000; sign flips the side), " +
        "leaderLengthMm (spot dimensions: horizontal leader segment length, default 600). " +
        "References are auto-extracted: walls/grids/levels (axis lines), reference planes, model/detail lines, family instances (center reference planes). " +
        "Transaction-wrapped and reversible via Revit Undo.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var kind = ToolArguments.GetString(request.Arguments, "kind").Trim().ToLowerInvariant();
        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var viewId = ToolArguments.GetLong(request.Arguments, "viewId");
        var dimensionTypeId = ToolArguments.GetLong(request.Arguments, "dimensionTypeId");
        var offsetMm = ToolArguments.GetDouble(request.Arguments, "offsetMm", 1000.0);
        var leaderLengthMm = ToolArguments.GetDouble(request.Arguments, "leaderLengthMm", 600.0);

        var supported = new[] { "aligned", "horizontal", "vertical", "angular", "radial", "diameter", "arclength", "spotelevation", "spotcoordinate" };
        if (!supported.Contains(kind))
            return Task.FromResult(Fail(request, $"Unknown kind '{kind}'. Supported: aligned, horizontal, vertical, angular, radial, diameter, arcLength, spotElevation, spotCoordinate. (Spot slope dimensions cannot be created through the Revit API.)"));
        if (elementIds.Length == 0)
            return Task.FromResult(Fail(request, "elementIds is required."));

        var (view, viewError) = PlacementHelpers.ResolveGraphicalView(uidoc, doc, viewId);
        if (view == null)
            return Task.FromResult(Fail(request, viewError!));

        var elements = new List<Element>();
        var warnings = new List<string>();
        foreach (var id in elementIds)
        {
            var el = doc.GetElement(new ElementId(id));
            if (el == null) warnings.Add($"Element {id} not found — skipped.");
            else elements.Add(el);
        }
        if (elements.Count == 0)
            return Task.FromResult(Fail(request, "None of the provided element IDs exist in the model."));

        DimensionType? dimensionType = null;
        if (dimensionTypeId > 0)
        {
            dimensionType = doc.GetElement(new ElementId(dimensionTypeId)) as DimensionType;
            if (dimensionType == null)
                return Task.FromResult(Fail(request, $"Element {dimensionTypeId} is not a dimension type. Use revit_list_dimension_types."));
            if (!IsCompatibleDimensionType(kind, dimensionType.StyleType))
                return Task.FromResult(Fail(request,
                    $"Dimension type '{dimensionType.Name}' has style {dimensionType.StyleType}, which is incompatible with kind '{kind}'. " +
                    $"Choose a {ExpectedDimensionStyle(kind)} type from revit_list_dimension_types."));
        }

        double offsetFt = PlacementHelpers.MmToFt(offsetMm);
        double leaderFt = PlacementHelpers.MmToFt(leaderLengthMm);
        var created = new List<object>();

        cancellationToken.ThrowIfCancellationRequested();
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Place Dimensions", () =>
        {
            switch (kind)
            {
                case "aligned":
                case "horizontal":
                case "vertical":
                    CreateLinearDimension(doc, view, kind, elements, dimensionType, offsetFt, created, warnings);
                    break;
                case "angular":
                    CreateAngularDimension(doc, view, elements, dimensionType, offsetFt, created, warnings);
                    break;
                case "radial":
                case "diameter":
                    CreateRadialDimensions(doc, view, kind, elements, dimensionType, offsetFt, created, warnings);
                    break;
                case "arclength":
                    CreateArcLengthDimensions(doc, view, elements, dimensionType, offsetFt, created, warnings);
                    break;
                case "spotelevation":
                case "spotcoordinate":
                    CreateSpotDimensions(doc, view, kind, elements, dimensionType, offsetFt, leaderFt, created, warnings);
                    break;
            }
        });

        sw.Stop();
        if (!txSuccess)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = diagnostics.OriginalError ?? "Transaction failed — no dimensions were created.",
                Warnings = warnings,
                Data = new { transactionDiagnostics = diagnostics },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created.Count > 0,
            Message = created.Count > 0
                ? $"Created {created.Count} {kind} dimension(s) in view '{view.Name}'."
                : "No dimensions were created — see warnings.",
            Warnings = warnings,
            Data = new { kind, viewId = view.Id.Value, viewName = view.Name, createdCount = created.Count, created },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    // ── Linear (aligned / horizontal / vertical) ───────────────────────────────

    private static void CreateLinearDimension(
        Document doc, View view, string kind, List<Element> elements,
        DimensionType? dimensionType, double offsetFt, List<object> created, List<string> warnings)
    {
        if (elements.Count < 2)
        {
            warnings.Add("Linear dimensions need at least 2 elements.");
            return;
        }

        var refs = new List<Reference>();
        var points = new List<XYZ>();
        foreach (var el in elements)
        {
            var (reference, _, error) = DimensionReferenceResolver.ResolveLinear(el, view);
            var point = DimensionReferenceResolver.MeasurePoint(el, view);
            if (reference == null || point == null)
            {
                warnings.Add(error ?? $"Element {el.Id.Value}: no measure point found — skipped.");
                continue;
            }
            refs.Add(reference);
            points.Add(point);
        }
        if (refs.Count < 2)
        {
            warnings.Add("Fewer than 2 elements yielded a dimensionable reference — nothing to dimension.");
            return;
        }

        var viewDir = view.ViewDirection;
        XYZ direction;
        if (kind == "horizontal") direction = view.RightDirection;
        else if (kind == "vertical") direction = view.UpDirection;
        else
        {
            direction = points[^1] - points[0];
            direction -= viewDir * direction.DotProduct(viewDir);
            if (direction.GetLength() < 1e-6)
            {
                warnings.Add("The first and last element project to the same point in this view — cannot derive an aligned direction. Use horizontal/vertical instead.");
                return;
            }
            direction = direction.Normalize();
        }

        var perpendicular = viewDir.CrossProduct(direction).Normalize();
        var centroid = points.Aggregate(XYZ.Zero, (acc, p) => acc + p) / points.Count;
        var origin = centroid + perpendicular * offsetFt;

        double tMin = double.MaxValue, tMax = double.MinValue;
        foreach (var p in points)
        {
            var t = (p - origin).DotProduct(direction);
            tMin = Math.Min(tMin, t);
            tMax = Math.Max(tMax, t);
        }
        if (tMax - tMin < 0.05) { tMin -= 0.5; tMax += 0.5; } // degenerate span — pad so the line is valid

        var line = Line.CreateBound(origin + direction * tMin, origin + direction * tMax);
        var referenceArray = new ReferenceArray();
        foreach (var r in refs) referenceArray.Append(r);

        try
        {
            var dim = dimensionType != null
                ? doc.Create.NewDimension(view, line, referenceArray, dimensionType)
                : doc.Create.NewDimension(view, line, referenceArray);
            created.Add(new { dimensionId = dim.Id.Value, referenceCount = refs.Count });
        }
        catch (Exception ex)
        {
            warnings.Add($"Dimension creation failed: {ex.Message}");
        }
    }

    // ── Angular ────────────────────────────────────────────────────────────────

    private static void CreateAngularDimension(
        Document doc, View view, List<Element> elements,
        DimensionType? dimensionType, double offsetFt, List<object> created, List<string> warnings)
    {
        if (elements.Count != 2)
        {
            warnings.Add("Angular dimensions need exactly 2 linear elements.");
            return;
        }

        var resolved = new List<(Reference Reference, Curve Curve)>();
        foreach (var el in elements)
        {
            var (reference, curve, error) = DimensionReferenceResolver.ResolveLinear(el, view);
            if (reference == null || curve is not Line)
            {
                warnings.Add(error ?? $"Element {el.Id.Value}: no straight axis line found — angular dimensions need linear elements.");
                continue;
            }
            resolved.Add((reference, curve));
        }
        if (resolved.Count != 2) return;

        var viewDir = view.ViewDirection;
        var right = view.RightDirection;
        var up = view.UpDirection;

        XYZ Flatten(XYZ v) => v - viewDir * v.DotProduct(viewDir);

        var d1 = Flatten(((Line)resolved[0].Curve).Direction).Normalize();
        var d2 = Flatten(((Line)resolved[1].Curve).Direction).Normalize();
        var q1 = resolved[0].Curve.Evaluate(0.5, true);
        var q2 = resolved[1].Curve.Evaluate(0.5, true);

        // Intersect the two (infinite) axis lines in 2D view-plane coordinates.
        double a1x = d1.DotProduct(right), a1y = d1.DotProduct(up);
        double a2x = d2.DotProduct(right), a2y = d2.DotProduct(up);
        double cross = a1x * a2y - a1y * a2x;
        if (Math.Abs(cross) < 1e-9)
        {
            warnings.Add("The two elements are parallel in this view — no angle to dimension.");
            return;
        }
        double q1x = q1.DotProduct(right), q1y = q1.DotProduct(up);
        double q2x = q2.DotProduct(right), q2y = q2.DotProduct(up);
        double t = ((q2x - q1x) * a2y - (q2y - q1y) * a2x) / cross;
        var center = q1 + d1 * t;

        // Angles from the intersection toward each element's midpoint pick the measured quadrant.
        double AngleTo(XYZ p)
        {
            var v = Flatten(p - center);
            return Math.Atan2(v.DotProduct(up), v.DotProduct(right));
        }
        double ang1 = AngleTo(q1), ang2 = AngleTo(q2);
        if (ang1 > ang2) (ang1, ang2) = (ang2, ang1);
        if (ang2 - ang1 > Math.PI) (ang1, ang2) = (ang2, ang1 + 2 * Math.PI);

        var type = dimensionType
                   ?? doc.GetElement(doc.GetDefaultElementTypeId(ElementTypeGroup.AngularDimensionType)) as DimensionType;
        if (type == null)
        {
            warnings.Add("No angular dimension type exists in this model — pass dimensionTypeId explicitly.");
            return;
        }

        double radius = Math.Max(Math.Abs(offsetFt), 0.5);
        try
        {
            var arc = Arc.Create(center, radius, ang1, ang2, right, up);
            var dim = AngularDimension.Create(doc, view, arc, new List<Reference> { resolved[0].Reference, resolved[1].Reference }, type);
            created.Add(new { dimensionId = dim.Id.Value });
        }
        catch (Exception ex)
        {
            warnings.Add($"Angular dimension creation failed: {ex.Message}");
        }
    }

    // ── Radial / Diameter ──────────────────────────────────────────────────────

    private static void CreateRadialDimensions(
        Document doc, View view, string kind, List<Element> elements,
        DimensionType? dimensionType, double offsetFt, List<object> created, List<string> warnings)
    {
#if REVIT2024
        warnings.Add($"{kind} dimensions require Revit 2025 or newer (RadialDimension API is not available in Revit 2024).");
#else
        foreach (var el in elements)
        {
            var (reference, arc, error) = DimensionReferenceResolver.ResolveArc(el, view);
            if (reference == null || arc == null)
            {
                warnings.Add(error!);
                continue;
            }

            try
            {
                using (var subTransaction = new SubTransaction(doc))
                {
                    subTransaction.Start();
                    try
                    {
                        var dim = RadialDimension.Create(doc, view, reference, kind == "diameter");
                        var createdId = ApplyDimensionType(dim, dimensionType);
                        var createdDimension = doc.GetElement(createdId) as RadialDimension ?? dim;

                        // "Distance from element": pull the dimension text outward from the arc.
                        var mid = arc.Evaluate(0.5, true);
                        var outward = (mid - arc.Center).Normalize();
                        try { createdDimension.TextPosition = arc.Center + outward * (arc.Radius + offsetFt); } catch { }

                        if (subTransaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("The radial dimension subtransaction did not commit.");
                        created.Add(new { dimensionId = createdId.Value, elementId = el.Id.Value });
                    }
                    catch
                    {
                        if (subTransaction.GetStatus() == TransactionStatus.Started)
                            subTransaction.RollBack();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Element {el.Id.Value}: {kind} dimension failed: {ex.Message}");
            }
        }
#endif
    }

    // ── Arc length ─────────────────────────────────────────────────────────────

    private static void CreateArcLengthDimensions(
        Document doc, View view, List<Element> elements,
        DimensionType? dimensionType, double offsetFt, List<object> created, List<string> warnings)
    {
#if REVIT2024
        warnings.Add("Arc length dimensions require Revit 2025 or newer (ArcLengthDimension API is not available in Revit 2024).");
#else
        foreach (var el in elements)
        {
            var (reference, arc, error) = DimensionReferenceResolver.ResolveArc(el, view);
            if (reference == null || arc == null)
            {
                warnings.Add(error!);
                continue;
            }
            if (!arc.IsBound)
            {
                warnings.Add($"Element {el.Id.Value}: full circles have no arc-length end points — use radial or diameter instead.");
                continue;
            }

            try
            {
                using (var subTransaction = new SubTransaction(doc))
                {
                    subTransaction.Start();
                    try
                    {
                        var endRefs = new List<Reference>();
                        var start = arc.GetEndPointReference(0);
                        var end = arc.GetEndPointReference(1);
                        if (start == null || end == null)
                            throw new InvalidOperationException("The arc exposes no end-point references — arc length cannot be dimensioned.");
                        endRefs.Add(start);
                        endRefs.Add(end);

                        // Dimension line: a concentric arc offset outward from the measured arc.
                        var placementArc = Arc.Create(
                            arc.Center, arc.Radius + Math.Abs(offsetFt),
                            arc.GetEndParameter(0), arc.GetEndParameter(1),
                            arc.XDirection, arc.YDirection);

                        var dim = ArcLengthDimension.Create(doc, view, placementArc, reference, endRefs);
                        var createdId = ApplyDimensionType(dim, dimensionType);
                        if (subTransaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("The arc-length dimension subtransaction did not commit.");
                        created.Add(new { dimensionId = createdId.Value, elementId = el.Id.Value });
                    }
                    catch
                    {
                        if (subTransaction.GetStatus() == TransactionStatus.Started)
                            subTransaction.RollBack();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Element {el.Id.Value}: arc length dimension failed: {ex.Message}");
            }
        }
#endif
    }

    // ── Spot elevation / coordinate ────────────────────────────────────────────

    private static void CreateSpotDimensions(
        Document doc, View view, string kind, List<Element> elements,
        DimensionType? dimensionType, double offsetFt, double leaderFt,
        List<object> created, List<string> warnings)
    {
        var up = view.UpDirection;
        var right = view.RightDirection;

        foreach (var el in elements)
        {
            var point = DimensionReferenceResolver.MeasurePoint(el, view);
            if (point == null)
            {
                warnings.Add($"Element {el.Id.Value}: no measure point found — skipped.");
                continue;
            }

            var bend = point + up * Math.Max(Math.Abs(offsetFt), 0.1) * Math.Sign(offsetFt == 0 ? 1 : offsetFt);
            var end = bend + right * Math.Max(leaderFt, 0.1);
            bool hasLeader = leaderFt > 0.001 || Math.Abs(offsetFt) > 0.001;

            try
            {
                using (var subTransaction = new SubTransaction(doc))
                {
                    subTransaction.Start();
                    try
                    {
                        var reference = new Reference(el);
                        var dim = kind == "spotelevation"
                            ? doc.Create.NewSpotElevation(view, reference, point, bend, end, point, hasLeader)
                            : doc.Create.NewSpotCoordinate(view, reference, point, bend, end, point, hasLeader);
                        var createdId = ApplyDimensionType(dim, dimensionType);
                        if (subTransaction.Commit() != TransactionStatus.Committed)
                            throw new InvalidOperationException("The spot dimension subtransaction did not commit.");
                        created.Add(new { dimensionId = createdId.Value, elementId = el.Id.Value });
                    }
                    catch
                    {
                        if (subTransaction.GetStatus() == TransactionStatus.Started)
                            subTransaction.RollBack();
                        throw;
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Element {el.Id.Value}: {kind} failed: {ex.Message}");
            }
        }
    }

    private static ElementId ApplyDimensionType(Element dimension, DimensionType? dimensionType)
    {
        if (dimensionType == null)
            return dimension.Id;

        var replacementId = dimension.ChangeTypeId(dimensionType.Id);
        return replacementId != ElementId.InvalidElementId ? replacementId : dimension.Id;
    }

    private static bool IsCompatibleDimensionType(string kind, DimensionStyleType style)
    {
        switch (kind)
        {
            case "aligned":
            case "horizontal":
            case "vertical":
                return style == DimensionStyleType.Linear || style == DimensionStyleType.LinearFixed;
            case "angular":
                return style == DimensionStyleType.Angular;
            case "radial":
                return style == DimensionStyleType.Radial;
            case "diameter":
                return style == DimensionStyleType.Diameter;
            case "arclength":
                return style == DimensionStyleType.ArcLength;
            case "spotelevation":
                return style == DimensionStyleType.SpotElevation;
            case "spotcoordinate":
                return style == DimensionStyleType.SpotCoordinate;
            default:
                return false;
        }
    }

    private static string ExpectedDimensionStyle(string kind)
    {
        switch (kind)
        {
            case "aligned":
            case "horizontal":
            case "vertical":
                return "Linear";
            case "angular":
                return "Angular";
            case "radial":
                return "Radial";
            case "diameter":
                return "Diameter";
            case "arclength":
                return "ArcLength";
            case "spotelevation":
                return "SpotElevation";
            case "spotcoordinate":
                return "SpotCoordinate";
            default:
                return "matching";
        }
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

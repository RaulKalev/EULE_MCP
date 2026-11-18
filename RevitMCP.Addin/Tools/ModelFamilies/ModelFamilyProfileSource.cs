using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.CadManagement;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>The curves a profile analysis reads, and what the result says about where they came from.</summary>
internal sealed class ModelFamilyProfileSourceData
{
    public List<ModelFamilyProfileCurve> Curves { get; } = new();
    public JObject Description { get; set; } = new();
}

/// <summary>
/// Reads the source curves of #177 from a family document: the line work of a CAD import (layer from each curve's
/// graphics style, as <see cref="CadLayerResolver"/> does for projects), chosen family curves, or the lines of a view.
/// Lines, arcs and circles keep their geometry; splines, ellipses and elliptical arcs are tessellated
/// (<c>Curve.Tessellate</c>, Revit's display tolerance) and marked approximated.
/// </summary>
internal static class ModelFamilyProfileSource
{
    private const double MmPerFoot = ModelFamilyJson.MmPerFoot;
    private const int MaxDepth = 8;
    /// <summary>A drawing larger than this is a background, not a manufacturer's outline.</summary>
    public const int MaxCurves = 50_000;

    public static List<ImportInstance> Imports(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ImportInstance)).Cast<ImportInstance>().OrderBy(i => i.Id.Value).ToList();

    public static ModelFamilyProfileSourceData? Read(ModelFamilyContext context, ModelFamilyProfileSourceSpec spec, double minSegmentMm)
    {
        return spec.Kind switch
        {
            ModelFamilyProfileSourceKind.Curves => ReadCurves(context, spec, minSegmentMm),
            ModelFamilyProfileSourceKind.View => ReadView(context, spec, minSegmentMm),
            _ => ReadImport(context, spec, minSegmentMm)
        };
    }

    // ── CAD import ───────────────────────────────────────────────────────────

    private static ModelFamilyProfileSourceData? ReadImport(ModelFamilyContext context, ModelFamilyProfileSourceSpec spec, double minSegmentMm)
    {
        var doc = context.FamilyDoc;
        var problems = context.Problems;
        var imports = Imports(doc);
        const string path = "source.import";
        if (imports.Count == 0)
        {
            problems.Add("source: the family has no CAD import. Import the DWG into the family (Insert > Import CAD, in the plan view of the reference level) or pick family curves with source {\"curves\": [ids]}.");
            return null;
        }
        ImportInstance? import;
        if (spec.Import == null)
        {
            if (imports.Count > 1)
            {
                problems.Add($"source.import: the family has {imports.Count} CAD imports; name one: {string.Join(", ", imports.Select(i => $"{i.Id.Value} '{CadPointExtractor.SafeName(i)}'"))}.");
                return null;
            }
            import = imports[0];
        }
        else if (ModelFamilyJson.AsId(spec.Import) is { } id && spec.Import.Type == JTokenType.Integer)
        {
            import = imports.FirstOrDefault(i => i.Id.Value == id);
            if (import == null) problems.Add($"{path}: the family has no CAD import with id {id}. Imports: {string.Join(", ", imports.Select(i => $"{i.Id.Value} '{CadPointExtractor.SafeName(i)}'"))}.");
        }
        else
        {
            var name = (spec.Import.Type == JTokenType.String ? (string?)spec.Import : spec.Import.ToString())?.Trim() ?? string.Empty;
            var matches = imports.Where(i => string.Equals(CadPointExtractor.SafeName(i), name, StringComparison.OrdinalIgnoreCase) ||
                                             string.Equals(i.Category?.Name, name, StringComparison.OrdinalIgnoreCase) ||
                                             ModelFamilyJson.AsId(spec.Import) == i.Id.Value).ToList();
            import = matches.Count == 1 ? matches[0] : null;
            if (matches.Count > 1) problems.Add($"{path}: {matches.Count} imports are called '{name}'; give its id: {string.Join(", ", matches.Select(i => i.Id.Value))}.");
            else if (import == null)
            {
                var close = ModelFamilyJson.Suggest(name, imports.Select(CadPointExtractor.SafeName));
                problems.Add($"{path}: the family has no CAD import '{name}'" + (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : $". Imports: {string.Join(", ", imports.Select(i => $"{i.Id.Value} '{CadPointExtractor.SafeName(i)}'"))}."));
            }
        }
        if (import == null) return null;

        var all = new List<(ModelFamilyProfileCurve Curve, string Layer)>();
        var ignored = new Dictionary<string, int>(StringComparer.Ordinal);
        var warnings = new List<string>();
        var geometry = ImportGeometry(doc, import, warnings);
        var layers = new CadLayerResolver(doc);
        var truncated = false;
        if (geometry != null)
            Walk(geometry, Transform.Identity, 0, layers, (curve, transform, layer) =>
            {
                if (all.Count >= MaxCurves) { truncated = true; return; }
                foreach (var c in Convert(curve, transform, layer, import.Id.Value, minSegmentMm, ignored)) all.Add((c, layer));
            }, (polyline, transform, layer) =>
            {
                if (all.Count >= MaxCurves) { truncated = true; return; }
                IList<XYZ> points;
                try { points = polyline.GetCoordinates(); }
                catch { return; }
                foreach (var c in ModelFamilyProfileCurve.Polyline(points.Select(p => Mm(transform.OfPoint(p))).ToList(), layer, import.Id.Value, approximated: false, minSpacing: 1e-6))
                    all.Add((c, layer));
            }, ignored, context.CancellationToken);
        context.Warnings.AddRange(warnings);
        if (truncated) context.Warnings.Add($"The import has more than {MaxCurves} curves; only the first {MaxCurves} were read. Pick the outline's layers with source.layers.");

        var counts = all.GroupBy(c => c.Layer, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        HashSet<string>? included = null;
        if (spec.Layers != null)
        {
            included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var wanted in spec.Layers)
            {
                var match = counts.Keys.FirstOrDefault(k => string.Equals(k, wanted, StringComparison.OrdinalIgnoreCase));
                if (match != null) { included.Add(match); continue; }
                var close = ModelFamilyJson.Suggest(wanted, counts.Keys);
                problems.Add($"source.layers: the import has no layer '{wanted}' with curves" +
                             (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : $". Layers: {string.Join(", ", counts.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).Take(30).Select(n => $"'{n}'"))}."));
            }
        }

        var data = new ModelFamilyProfileSourceData();
        data.Curves.AddRange(all.Where(c => included == null || included.Contains(c.Layer)).Select(c => c.Curve));
        var description = DescribeImport(doc, import);
        description["layers"] = new JArray(counts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => new JObject
        {
            ["name"] = k.Key,
            ["curves"] = k.Value,
            ["included"] = included == null || included.Contains(k.Key)
        }));
        if (ignored.Count > 0) description["ignored"] = JObject.FromObject(ignored);
        data.Description = description;
        return data;
    }

    /// <summary>
    /// The import's geometry. A view-specific import ("current view only") may have no geometry without its view, so
    /// its owner view is tried next.
    /// </summary>
    internal static GeometryElement? ImportGeometry(Document doc, ImportInstance import, List<string> warnings)
    {
        GeometryElement? geometry = null;
        try { geometry = import.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = false }); }
        catch (Exception ex) { warnings.Add($"Could not read the geometry of import {import.Id.Value} without a view: {ex.Message.TrimEnd('.')}."); }
        if (geometry != null && geometry.Any()) return geometry;
        if (import.OwnerViewId != ElementId.InvalidElementId && doc.GetElement(import.OwnerViewId) is View view)
        {
            try { geometry = import.get_Geometry(new Options { ComputeReferences = false, View = view, IncludeNonVisibleObjects = false }); }
            catch (Exception ex) { warnings.Add($"Could not read the geometry of import {import.Id.Value} in view '{view.Name}': {ex.Message.TrimEnd('.')}."); }
        }
        if (geometry == null || !geometry.Any()) warnings.Add($"Import {import.Id.Value} '{CadPointExtractor.SafeName(import)}' has no readable geometry in this family.");
        return geometry;
    }

    /// <summary>Walks nested blocks, composing their transforms so every curve lands in family coordinates.</summary>
    internal static void Walk(GeometryElement geometry, Transform transform, int depth, CadLayerResolver layers,
        Action<Curve, Transform, string> curve, Action<PolyLine, Transform, string> polyline, Dictionary<string, int> ignored,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDepth) return;
        foreach (var obj in geometry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (obj)
            {
                case GeometryInstance instance:
                    GeometryElement? symbol;
                    try { symbol = instance.GetSymbolGeometry(); }
                    catch { continue; }
                    if (symbol != null) Walk(symbol, transform.Multiply(instance.Transform), depth + 1, layers, curve, polyline, ignored, cancellationToken);
                    break;
                case Curve c:
                    curve(c, transform, layers.LayerOf(c) ?? CadLayerResolver.NoLayer);
                    break;
                case PolyLine p:
                    polyline(p, transform, layers.LayerOf(p) ?? CadLayerResolver.NoLayer);
                    break;
                default:
                    var kind = obj.GetType().Name;
                    ignored[kind] = ignored.TryGetValue(kind, out var n) ? n + 1 : 1;
                    break;
            }
        }
    }

    // ── Family curves ────────────────────────────────────────────────────────

    private static ModelFamilyProfileSourceData? ReadCurves(ModelFamilyContext context, ModelFamilyProfileSourceSpec spec, double minSegmentMm)
    {
        var doc = context.FamilyDoc;
        var data = new ModelFamilyProfileSourceData();
        var rows = new JArray();
        var ignored = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < spec.CurveIds.Count; i++)
        {
            var id = spec.CurveIds[i];
            var element = doc.GetElement(new ElementId(id));
            if (element is not CurveElement curveElement)
            {
                context.Problems.Add(element == null
                    ? $"source.curves[{i}]: the family has no element {id}."
                    : $"source.curves[{i}]: element {id} is {ModelFamilyLookup.Describe(element)}, not a model, symbolic or reference line.");
                continue;
            }
            if (IsSketchLine(curveElement))
            {
                context.Problems.Add($"source.curves[{i}]: element {id} is a sketch line of a form; pick model, symbolic or reference lines.");
                continue;
            }
            AddCurveElement(curveElement, data, rows, minSegmentMm, ignored);
        }
        data.Description = new JObject { ["kind"] = "curves", ["curves"] = rows };
        if (ignored.Count > 0) data.Description["ignored"] = JObject.FromObject(ignored);
        return context.Problems.Count > 0 ? null : data;
    }

    private static ModelFamilyProfileSourceData? ReadView(ModelFamilyContext context, ModelFamilyProfileSourceSpec spec, double minSegmentMm)
    {
        var doc = context.FamilyDoc;
        var view = ModelFamilyLookup.View(doc, spec.View, "source.view", context.Problems);
        if (view == null) return null;
        var curves = new FilteredElementCollector(doc, view.Id).OfClass(typeof(CurveElement)).Cast<CurveElement>()
            .Where(c => !IsSketchLine(c) && c is ModelCurve or SymbolicCurve or DetailCurve)
            .OrderBy(c => c.Id.Value).ToList();
        var styles = curves.Select(LayerOf).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (spec.Subcategory.Length > 0)
        {
            var match = styles.FirstOrDefault(s => string.Equals(s, spec.Subcategory, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                var close = ModelFamilyJson.Suggest(spec.Subcategory, styles);
                context.Problems.Add($"source.subcategory: no line in view '{view.Name}' is in subcategory or line style '{spec.Subcategory}'" +
                                     (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : styles.Count > 0 ? $". It has {string.Join(", ", styles.Take(20).Select(n => $"'{n}'"))}." : "."));
                return null;
            }
            curves = curves.Where(c => string.Equals(LayerOf(c), match, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (curves.Count == 0)
        {
            context.Problems.Add($"source.view: view '{view.Name}' shows no model, symbolic or detail lines{(spec.Subcategory.Length > 0 ? $" in '{spec.Subcategory}'" : string.Empty)}.");
            return null;
        }
        var data = new ModelFamilyProfileSourceData();
        var rows = new JArray();
        var ignored = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var curve in curves.Take(MaxCurves)) AddCurveElement(curve, data, rows, minSegmentMm, ignored);
        if (curves.Count > MaxCurves) context.Warnings.Add($"View '{view.Name}' has {curves.Count} lines; only the first {MaxCurves} were read.");
        data.Description = new JObject
        {
            ["kind"] = "view",
            ["view"] = new JObject { ["id"] = view.Id.Value, ["name"] = view.Name },
            ["subcategory"] = spec.Subcategory.Length > 0 ? spec.Subcategory : null,
            ["subcategories"] = new JArray(styles),
            ["curves"] = rows
        };
        if (ignored.Count > 0) data.Description["ignored"] = JObject.FromObject(ignored);
        return data;
    }

    private static void AddCurveElement(CurveElement element, ModelFamilyProfileSourceData data, JArray rows, double minSegmentMm, Dictionary<string, int> ignored)
    {
        var layer = LayerOf(element);
        Curve? curve = null;
        try { curve = element.GeometryCurve; }
        catch { /* reported below */ }
        var row = new JObject
        {
            ["id"] = element.Id.Value,
            ["class"] = element.GetType().Name,
            ["subcategory"] = layer,
            ["referenceLine"] = element is ModelCurve model && SafeReferenceLine(model)
        };
        if (curve == null) row["error"] = "no geometry";
        else data.Curves.AddRange(Convert(curve, Transform.Identity, layer, element.Id.Value, minSegmentMm, ignored));
        rows.Add(row);
    }

    private static bool SafeReferenceLine(ModelCurve curve)
    {
        try { return curve.IsReferenceLine; }
        catch { return false; }
    }

    private static bool IsSketchLine(CurveElement curve)
    {
        try { return curve.Category?.Id.Value == (long)BuiltInCategory.OST_SketchLines; }
        catch { return false; }
    }

    /// <summary>A family curve's "layer": its line style's subcategory name.</summary>
    internal static string LayerOf(CurveElement curve)
    {
        try { return (curve.LineStyle as GraphicsStyle)?.GraphicsStyleCategory?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    // ── Curve conversion ─────────────────────────────────────────────────────

    public static double[] Mm(XYZ p) => new[] { p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot };

    private static double[] Direction(XYZ v) => new[] { v.X, v.Y, v.Z };

    /// <summary>
    /// A Revit curve as profile curves in family millimetres: a line, an arc (a full circle as two half arcs), or
    /// approximating lines for anything else (spline, ellipse, elliptical arc, helix).
    /// </summary>
    internal static List<ModelFamilyProfileCurve> Convert(Curve curve, Transform transform, string layer, long sourceId, double minSegmentMm, Dictionary<string, int> ignored)
    {
        var result = new List<ModelFamilyProfileCurve>();
        try
        {
            switch (curve)
            {
                case Line line when line.IsBound:
                    result.Add(ModelFamilyProfileCurve.Line(Mm(transform.OfPoint(line.GetEndPoint(0))), Mm(transform.OfPoint(line.GetEndPoint(1))), layer, sourceId));
                    return result;
                case Arc arc when !arc.IsBound:
                {
                    var center = Mm(transform.OfPoint(arc.Center));
                    var x = transform.OfVector(arc.XDirection);
                    var y = transform.OfVector(arc.YDirection);
                    var radius = arc.Radius * x.GetLength() * MmPerFoot;
                    result.AddRange(ModelFamilyProfileCurve.Circle(center, Direction(x.Normalize()), Direction(y.Normalize()), radius, layer, sourceId));
                    return result;
                }
                case Arc arc:
                    result.Add(ModelFamilyProfileCurve.Arc(Mm(transform.OfPoint(arc.GetEndPoint(0))), Mm(transform.OfPoint(arc.Evaluate(0.5, true))),
                        Mm(transform.OfPoint(arc.GetEndPoint(1))), layer, sourceId));
                    return result;
            }
            // Splines, ellipses and other curves: Revit's display tessellation. An unbound (closed) curve is split in two halves first.
            var pieces = new List<Curve>();
            if (curve.IsBound) pieces.Add(curve);
            else if (curve.IsCyclic)
            {
                var period = curve.Period;
                var first = curve.Clone();
                first.MakeBound(0, period / 2);
                var second = curve.Clone();
                second.MakeBound(period / 2, period);
                pieces.Add(first);
                pieces.Add(second);
            }
            else
            {
                ignored["unboundCurve"] = ignored.TryGetValue("unboundCurve", out var n) ? n + 1 : 1;
                return result;
            }
            foreach (var piece in pieces)
            {
                var points = piece.Tessellate().Select(p => Mm(transform.OfPoint(p))).ToList();
                result.AddRange(ModelFamilyProfileCurve.Polyline(points, layer, sourceId, approximated: true, minSpacing: minSegmentMm));
            }
        }
        catch
        {
            var kind = curve.GetType().Name;
            ignored[kind] = ignored.TryGetValue(kind, out var n) ? n + 1 : 1;
        }
        return result;
    }

    // ── Description ──────────────────────────────────────────────────────────

    /// <summary>An import's identity, units and placement, as far as the API exposes them.</summary>
    public static JObject DescribeImport(Document doc, ImportInstance import)
    {
        var row = new JObject
        {
            ["kind"] = "import",
            ["id"] = import.Id.Value,
            ["name"] = CadPointExtractor.SafeName(import),
            ["linked"] = Safe(() => import.IsLinked),
            ["pinned"] = Safe(() => import.Pinned),
            ["viewSpecific"] = Safe(() => import.ViewSpecific)
        };
        if (Safe(() => import.ViewSpecific) == true && doc.GetElement(import.OwnerViewId) is View owner)
            row["ownerView"] = new JObject { ["id"] = owner.Id.Value, ["name"] = owner.Name };
        var type = doc.GetElement(import.GetTypeId());
        var units = new JObject();
        foreach (var (name, parameter) in new[] { ("importUnits", BuiltInParameter.IMPORT_DISPLAY_UNITS), ("scaleFactor", BuiltInParameter.IMPORT_SCALE), ("instanceScale", BuiltInParameter.IMPORT_INSTANCE_SCALE) })
            if ((ParameterValue(import, parameter) ?? (type == null ? null : ParameterValue(type, parameter))) is { } value) units[name] = value;
        if (units.Count > 0) row["units"] = units;
        try
        {
            var transform = import.GetTotalTransform();
            row["transform"] = new JObject
            {
                ["origin"] = ModelFamilyLookup.Mm(transform.Origin),
                ["rotation"] = Math.Round(Math.Atan2(transform.BasisX.Y, transform.BasisX.X) * 180 / Math.PI, 4),
                ["scale"] = Math.Round(transform.BasisX.GetLength(), 6),
                ["mirrored"] = transform.HasReflection,
                ["xAxis"] = ModelFamilyLookup.Direction(transform.BasisX.Normalize()),
                ["zAxis"] = ModelFamilyLookup.Direction(transform.BasisZ.Normalize())
            };
        }
        catch { /* no transform */ }
        return row;
    }

    private static JToken? ParameterValue(Element element, BuiltInParameter builtIn)
    {
        try
        {
            var parameter = element.get_Parameter(builtIn);
            if (parameter == null || !parameter.HasValue) return null;
            return parameter.StorageType switch
            {
                StorageType.Double => Math.Round(parameter.AsDouble(), 6),
                StorageType.Integer => (JToken?)(parameter.AsValueString() ?? parameter.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture)),
                StorageType.String => parameter.AsString(),
                _ => parameter.AsValueString()
            };
        }
        catch { return null; }
    }

    private static bool? Safe(Func<bool> read)
    {
        try { return read(); }
        catch { return null; }
    }

    /// <summary>The <c>imports</c> inspection section: every CAD import of the family with its layers.</summary>
    public static JToken ImportsSection(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var rows = new JArray();
        foreach (var import in Imports(doc).Take(limit))
        {
            var row = DescribeImport(doc, import);
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var ignored = new Dictionary<string, int>(StringComparer.Ordinal);
            var warnings = new List<string>();
            if (ImportGeometry(doc, import, warnings) is { } geometry)
            {
                var resolver = new CadLayerResolver(doc);
                void Count(string layer) => counts[layer] = counts.TryGetValue(layer, out var n) ? n + 1 : 1;
                Walk(geometry, Transform.Identity, 0, resolver, (_, _, layer) => Count(layer), (_, _, layer) => Count(layer), ignored, context.CancellationToken);
            }
            row["layers"] = new JArray(counts.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase).Select(k => new JObject { ["name"] = k.Key, ["curves"] = k.Value }));
            if (warnings.Count > 0) row["warnings"] = new JArray(warnings);
            rows.Add(row);
        }
        return rows;
    }
}

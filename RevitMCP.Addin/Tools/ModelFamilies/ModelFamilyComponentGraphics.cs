using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Symbolic lines, model lines and subcategory assignment of a model family (#175). A symbolic line is 2D and shows
/// only in views parallel to its plane (the plan, or one elevation); a model line is 3D on a work plane.
/// </summary>
internal static class ModelFamilyComponentGraphics
{
    /// <summary>
    /// The plane symbolic lines of a view are drawn on: the view's own plane. A plan is at the reference level; an
    /// elevation or section through the family origin. Null with a reason for a 3D or non-graphical view.
    /// </summary>
    public static Plane? ViewPlane(Document doc, View view, out string? error)
    {
        error = null;
        if (view is View3D || view.ViewType is not (ViewType.FloorPlan or ViewType.CeilingPlan or ViewType.Elevation or ViewType.Section or ViewType.Detail))
        {
            error = $"view '{view.Name}' cannot hold symbolic lines: they are 2D and need a plan or an elevation. Name a plan, front, back, left or right view.";
            return null;
        }
        var origin = XYZ.Zero;
        if (view is ViewPlan plan && plan.GenLevel != null) origin = new XYZ(0, 0, plan.GenLevel.Elevation);
        return Plane.CreateByOriginAndBasis(origin, view.RightDirection, view.UpDirection);
    }

    /// <summary>A plane as millimetre origin and unit normal arrays for the pure projection.</summary>
    public static (double[] Origin, double[] Normal) Frame(Plane plane) =>
        (new[] { ModelFamilyJson.ToMm(plane.Origin.X), ModelFamilyJson.ToMm(plane.Origin.Y), ModelFamilyJson.ToMm(plane.Origin.Z) },
         new[] { plane.Normal.X, plane.Normal.Y, plane.Normal.Z });

    /// <summary>The Revit curve of a spec already projected onto <paramref name="plane"/>.</summary>
    public static Curve BuildCurve(ComponentCurveSpec spec, Plane plane)
    {
        switch (spec.Kind)
        {
            case "line":
                return Line.CreateBound(ModelFamilyLookup.Xyz(spec.Points[0]), ModelFamilyLookup.Xyz(spec.Points[1]));
            case "arc":
                return Arc.Create(ModelFamilyLookup.Xyz(spec.Points[0]), ModelFamilyLookup.Xyz(spec.Points[2]), ModelFamilyLookup.Xyz(spec.Points[1]));
            default:
                return Arc.Create(ModelFamilyLookup.Xyz(spec.Points[0]), ModelFamilyJson.ToFeet(spec.Radius), 0, 2 * Math.PI, plane.XVec, plane.YVec);
        }
    }

    private static List<Curve> ProjectAll(ComponentLineGroupSpec group, Plane plane)
    {
        var (origin, normal) = Frame(plane);
        var curves = new List<Curve>();
        foreach (var spec in group.Curves)
        {
            var projected = spec.Project(origin, normal);
            var error = projected.Validate();
            if (error != null) throw new InvalidOperationException($"{spec.Source}: {error}");
            curves.Add(BuildCurve(projected, plane));
        }
        return curves;
    }

    public static JArray SymbolicLines(ModelFamilyContext context, List<ComponentLineGroupSpec> groups, Dictionary<string, List<long>> created)
    {
        var rows = new JArray();
        var doc = context.FamilyDoc;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var problems = new List<string>();
            var view = ModelFamilyLookup.View(doc, group.View, $"{group.Source}.view", problems);
            Plane? plane = null;
            if (view != null)
            {
                plane = ViewPlane(doc, view, out var planeError);
                if (plane == null) problems.Add($"{group.Source}.view: {planeError}");
            }
            if (problems.Count > 0 || view == null || plane == null)
            {
                context.Problems.AddRange(problems);
                continue;
            }

            var ids = new List<long>();
            var rowsForGroup = new JArray();
            var ok = context.TryTransact($"Symbolic lines {i + 1}", () =>
            {
                var style = ResolveGroupStyle(doc, group);
                var sketch = SketchPlane.Create(doc, plane);
                foreach (var curve in ProjectAll(group, plane))
                {
                    var line = doc.FamilyCreate.NewSymbolicCurve(curve, sketch);
                    if (style != null) line.Subcategory = style;
                    ids.Add(line.Id.Value);
                }
                if (group.Visibility != null)
                    foreach (var id in ids)
                        ModelFamilyComponentVisibilityRevit.Apply(doc, doc.GetElement(new ElementId(id)), group.Visibility, group.Source, context.Warnings);
            }, out var error);
            if (!ok)
            {
                context.Problems.Add($"{group.Source}: {error}");
                continue;
            }
            created[ModelFamilyComponentRefs.Format("symbolicLines", i)] = ids;
            foreach (var id in ids) rowsForGroup.Add(GraphicRow(doc, doc.GetElement(new ElementId(id)), view.Name));
            rows.Add(new JObject { ["source"] = group.Source, ["view"] = view.Name, ["viewId"] = view.Id.Value, ["created"] = rowsForGroup });
        }
        return rows;
    }

    public static JArray ModelLines(ModelFamilyContext context, List<ComponentLineGroupSpec> groups, Dictionary<string, List<long>> created)
    {
        var rows = new JArray();
        var doc = context.FamilyDoc;
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var problems = new List<string>();
            var plane = ModelFamilyLookup.WorkPlane(doc, group.WorkPlane, $"{group.Source}.workPlane", problems, out _);
            if (plane == null || problems.Count > 0)
            {
                context.Problems.AddRange(problems);
                continue;
            }
            var ids = new List<long>();
            string planeName = group.WorkPlane == null ? "level" : group.WorkPlane.ToString(Newtonsoft.Json.Formatting.None).Trim('"');
            var ok = context.TryTransact($"Model lines {i + 1}", () =>
            {
                var style = ResolveGroupStyle(doc, group);
                var sketchProblems = new List<string>();
                var sketch = ModelFamilyLookup.SketchPlane(doc, group.WorkPlane, $"{group.Source}.workPlane", sketchProblems)
                             ?? throw new InvalidOperationException(sketchProblems.FirstOrDefault() ?? "no work plane.");
                foreach (var curve in ProjectAll(group, plane))
                {
                    var line = doc.FamilyCreate.NewModelCurve(curve, sketch);
                    if (style != null) line.Subcategory = style;
                    ids.Add(line.Id.Value);
                }
                if (group.Visibility != null)
                    foreach (var id in ids)
                        ModelFamilyComponentVisibilityRevit.Apply(doc, doc.GetElement(new ElementId(id)), group.Visibility, group.Source, context.Warnings);
            }, out var error);
            if (!ok)
            {
                context.Problems.Add($"{group.Source}: {error}");
                continue;
            }
            created[ModelFamilyComponentRefs.Format("modelLines", i)] = ids;
            var rowsForGroup = new JArray();
            foreach (var id in ids) rowsForGroup.Add(GraphicRow(doc, doc.GetElement(new ElementId(id)), planeName));
            rows.Add(new JObject { ["source"] = group.Source, ["workPlane"] = planeName, ["created"] = rowsForGroup });
        }
        return rows;
    }

    private static GraphicsStyle? ResolveGroupStyle(Document doc, ComponentLineGroupSpec group)
    {
        if (group.Subcategory == null) return null;
        return ModelFamilyComponentSupport.ResolveStyle(doc, group.Subcategory, out var error)
               ?? throw new InvalidOperationException($"{group.Source}.subcategory: {error}");
    }

    /// <summary>A line as a row: id, kind, geometry in millimetres, subcategory.</summary>
    public static JObject GraphicRow(Document doc, Element element, string? plane)
    {
        var row = ModelFamilyComponentSupport.Row(element);
        if (element is CurveElement curve)
        {
            row["geometry"] = CurveJson(curve.GeometryCurve);
            row["plane"] = plane;
        }
        row["subcategory"] = ModelFamilyComponentSupport.SubcategoryNameOf(element);
        return row;
    }

    /// <summary>A curve as {kind, points} in millimetres.</summary>
    public static JObject CurveJson(Curve? curve)
    {
        switch (curve)
        {
            case Line line:
                return new JObject { ["kind"] = "line", ["start"] = ModelFamilyLookup.Mm(line.GetEndPoint(0)), ["end"] = ModelFamilyLookup.Mm(line.GetEndPoint(1)) };
            case Arc arc when !arc.IsBound:
                return new JObject { ["kind"] = "circle", ["center"] = ModelFamilyLookup.Mm(arc.Center), ["radius"] = ModelFamilyJson.ToMm(arc.Radius) };
            case Arc arc:
                return new JObject
                {
                    ["kind"] = "arc",
                    ["start"] = ModelFamilyLookup.Mm(arc.GetEndPoint(0)),
                    ["mid"] = ModelFamilyLookup.Mm(arc.Evaluate(0.5, true)),
                    ["end"] = ModelFamilyLookup.Mm(arc.GetEndPoint(1))
                };
            case null:
                return new JObject { ["kind"] = null };
            default:
                return new JObject { ["kind"] = curve.GetType().Name };
        }
    }

    // ── setSubcategory ────────────────────────────────────────────────────────

    public static JArray SetSubcategory(ModelFamilyContext context, List<ComponentSetSubcategorySpec> specs, Dictionary<string, List<long>> created)
    {
        var rows = new JArray();
        var doc = context.FamilyDoc;
        foreach (var spec in specs)
        {
            var ids = ModelFamilyComponentSupport.ResolveIds(spec.Elements, created, context.Problems);
            if (ids.Count == 0) continue;
            var done = new JArray();
            var ok = context.TryTransact("Set subcategory", () =>
            {
                Category category;
                if (spec.Subcategory == null)
                    category = doc.OwnerFamily?.FamilyCategory
                               ?? throw new InvalidOperationException($"{spec.Source}: the family has no category to put the elements back on.");
                else
                    category = ModelFamilyComponentSupport.ResolveCategory(doc, spec.Subcategory, out var styleError)
                               ?? throw new InvalidOperationException($"{spec.Source}.subcategory: {styleError}");
                var style = category.GetGraphicsStyle(GraphicsStyleType.Projection)
                            ?? throw new InvalidOperationException($"{spec.Source}: '{category.Name}' has no graphics style to assign.");
                foreach (var id in ids)
                {
                    var element = doc.GetElement(new ElementId(id))
                                  ?? throw new InvalidOperationException($"{spec.Source}: the family has no element {id}.");
                    switch (element)
                    {
                        case GenericForm form: form.Subcategory = category; break;
                        case SymbolicCurve symbolic: symbolic.Subcategory = style; break;
                        case ModelCurve model: model.Subcategory = style; break;
                        default:
                            throw new InvalidOperationException($"{spec.Source}: {ModelFamilyLookup.Describe(element)} has no subcategory. Forms, symbolic lines and model lines do.");
                    }
                    var name = ModelFamilyComponentSupport.SubcategoryNameOf(element);
                    if (!string.Equals(name, category.Name, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"{spec.Source}: Revit kept subcategory '{name}' on {ModelFamilyLookup.Describe(element)}.");
                    var row = ModelFamilyComponentSupport.Row(element);
                    row["subcategory"] = name;
                    done.Add(row);
                }
            }, out var error);
            if (!ok) context.Problems.Add($"{spec.Source}: {error}");
            else foreach (var row in done) rows.Add(row);
        }
        return rows;
    }
}

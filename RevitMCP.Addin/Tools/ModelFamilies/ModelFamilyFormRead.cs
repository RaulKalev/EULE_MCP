using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Reads the forms of a family (#171): the <c>forms</c> and <c>combinations</c> sections of revit_get_model_family, and
/// the helpers the edit uses to check and describe forms (kind, sketch plane, profile loops, cuts, volume).
/// </summary>
internal static class ModelFamilyFormRead
{
    private const double MmPerFoot = ModelFamilyJson.MmPerFoot;

    public static List<GenericForm> Forms(Document doc) =>
        new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().OfType<GenericForm>().ToList();

    public static List<GeomCombination> Combinations(Document doc) =>
        new FilteredElementCollector(doc).WhereElementIsNotElementType().ToElements().OfType<GeomCombination>().ToList();

    public static string KindOf(Element element) => element switch
    {
        Extrusion => "extrusion",
        Blend => "blend",
        Revolution => "revolve",
        Sweep => "sweep",
        SweptBlend => "sweptBlend",
        GeomCombination => "combination",
        _ => "other"
    };

    public static SketchPlane? SketchPlaneOf(GenericForm form)
    {
        try
        {
            return form switch
            {
                Extrusion e => e.Sketch?.SketchPlane,
                Revolution r => r.Sketch?.SketchPlane,
                Blend b => b.BottomSketch?.SketchPlane,
                Sweep s => s.PathSketch?.SketchPlane,
                SweptBlend sb => sb.PathSketch?.SketchPlane,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A sketch plane: id, name, origin, normal, and the reference plane it follows when it was made from one.</summary>
    public static JObject DescribeSketchPlane(Document doc, SketchPlane sketchPlane)
    {
        var row = new JObject { ["id"] = sketchPlane.Id.Value };
        try { row["name"] = sketchPlane.Name; }
        catch { /* unnamed */ }
        try
        {
            var plane = sketchPlane.GetPlane();
            row["origin"] = ModelFamilyLookup.Mm(plane.Origin);
            row["normal"] = ModelFamilyLookup.Direction(plane.Normal);
        }
        catch { /* no plane */ }
        try
        {
            if (sketchPlane.GetPlaneReference() is { } reference && doc.GetElement(reference) is ReferencePlane referencePlane)
                row["referencePlane"] = new JObject { ["id"] = referencePlane.Id.Value, ["name"] = referencePlane.Name };
            else if (sketchPlane.GetPlaneReference() is { } other && doc.GetElement(other) is Level level)
                row["level"] = new JObject { ["id"] = level.Id.Value, ["name"] = level.Name };
        }
        catch { /* a plane not made from a reference */ }
        return row;
    }

    public static JObject Box(BoundingBoxXYZ box) => new()
    {
        ["min"] = ModelFamilyLookup.Mm(box.Min),
        ["max"] = ModelFamilyLookup.Mm(box.Max),
        ["size"] = ModelFamilyLookup.Mm(box.Max - box.Min)
    };

    /// <summary>A curve in the wire format: line, arc (start, mid, end), circle, or another curve type with its ends.</summary>
    public static JObject CurveJson(Curve curve)
    {
        try
        {
            if (curve is Line line)
                return new JObject { ["line"] = new JObject { ["start"] = ModelFamilyLookup.Mm(line.GetEndPoint(0)), ["end"] = ModelFamilyLookup.Mm(line.GetEndPoint(1)) } };
            if (curve is Arc arc)
            {
                if (!arc.IsBound)
                    return new JObject { ["circle"] = new JObject { ["center"] = ModelFamilyLookup.Mm(arc.Center), ["radius"] = ModelFamilyJson.ToMm(arc.Radius) } };
                return new JObject
                {
                    ["arc"] = new JObject
                    {
                        ["start"] = ModelFamilyLookup.Mm(arc.GetEndPoint(0)),
                        ["mid"] = ModelFamilyLookup.Mm(arc.Evaluate(0.5, true)),
                        ["end"] = ModelFamilyLookup.Mm(arc.GetEndPoint(1))
                    }
                };
            }
            return new JObject
            {
                ["curve"] = curve.GetType().Name,
                ["start"] = curve.IsBound ? ModelFamilyLookup.Mm(curve.GetEndPoint(0)) : null,
                ["end"] = curve.IsBound ? ModelFamilyLookup.Mm(curve.GetEndPoint(1)) : null
            };
        }
        catch
        {
            return new JObject { ["curve"] = curve.GetType().Name };
        }
    }

    public static JArray Loops(CurveArrArray? loops)
    {
        var result = new JArray();
        if (loops == null) return result;
        foreach (CurveArray loop in loops) result.Add(Loop(loop));
        return result;
    }

    public static JArray Loop(CurveArray? loop)
    {
        var result = new JArray();
        if (loop == null) return result;
        foreach (Curve curve in loop) result.Add(CurveJson(curve));
        return result;
    }

    private static T? Safe<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch { return null; }
    }

    /// <summary>The family parameter driving a built-in parameter of an element, by name.</summary>
    private static string? DrivenBy(Document doc, Element element, BuiltInParameter builtIn)
    {
        try
        {
            var parameter = element.get_Parameter(builtIn);
            return parameter == null ? null : ModelFamilyAssociations.Associated(doc, parameter)?.Definition.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The ids of the geometry combinations a form belongs to.</summary>
    public static List<long> CombinationIds(Element element)
    {
        var ids = new List<long>();
        try
        {
            if (element is CombinableElement combinable)
                foreach (GeomCombination combination in combinable.Combinations) ids.Add(combination.Id.Value);
        }
        catch { /* not combinable */ }
        return ids;
    }

    public static List<Element> Members(GeomCombination combination)
    {
        var members = new List<Element>();
        try { foreach (CombinableElement member in combination.AllMembers) members.Add(member); }
        catch { /* deleted */ }
        return members;
    }

    /// <summary>
    /// What cuts a form and what it cuts: solid-solid cuts (SolidSolidCutUtils) and geometry combinations that mix
    /// voids and solids (a void member cuts the solid members).
    /// </summary>
    public static (List<long> CutBy, List<long> Cuts) CutRelations(Document doc, Element element)
    {
        var cutBy = new HashSet<long>();
        var cuts = new HashSet<long>();
        try { foreach (var id in SolidSolidCutUtils.GetCuttingSolids(element)) cutBy.Add(id.Value); }
        catch { /* not eligible */ }
        try { foreach (var id in SolidSolidCutUtils.GetSolidsBeingCut(element)) cuts.Add(id.Value); }
        catch { /* not eligible */ }
        var isSolid = element is GenericForm form ? ModelFamilyFormBuilder.SafeIsSolid(form) ?? true : true;
        foreach (var combinationId in CombinationIds(element))
        {
            if (doc.GetElement(new ElementId(combinationId)) is not GeomCombination combination) continue;
            foreach (var member in Members(combination))
            {
                if (member.Id == element.Id || member is not GenericForm other) continue;
                var otherSolid = ModelFamilyFormBuilder.SafeIsSolid(other) ?? true;
                if (isSolid && !otherSolid) cutBy.Add(member.Id.Value);
                if (!isSolid && otherSolid) cuts.Add(member.Id.Value);
            }
        }
        return (cutBy.OrderBy(i => i).ToList(), cuts.OrderBy(i => i).ToList());
    }

    /// <summary>The volume of an element's solids in mm³ (0 when it has none, as a void often has).</summary>
    public static double VolumeMm3(Element element)
    {
        try
        {
            var geometry = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine });
            if (geometry == null) return 0;
            return ModelFamilyFormFaces.Solids(geometry).Sum(s => Math.Max(0, s.Volume)) * MmPerFoot * MmPerFoot * MmPerFoot;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>One row of the <c>forms</c> section.</summary>
    public static JObject Describe(Document doc, GenericForm form)
    {
        var row = new JObject
        {
            ["id"] = form.Id.Value,
            ["kind"] = KindOf(form),
            ["solid"] = ModelFamilyFormBuilder.SafeIsSolid(form)
        };
        if (SketchPlaneOf(form) is { } sketchPlane) row["sketchPlane"] = DescribeSketchPlane(doc, sketchPlane);
        var drivenBy = new JObject();
        void Driven(string key, BuiltInParameter builtIn)
        {
            if (DrivenBy(doc, form, builtIn) is { } name) drivenBy[key] = name;
        }
        try
        {
            switch (form)
            {
                case Extrusion e:
                    row["start"] = ModelFamilyJson.ToMm(e.StartOffset);
                    row["end"] = ModelFamilyJson.ToMm(e.EndOffset);
                    row["profile"] = Loops(Safe(() => e.Sketch?.Profile));
                    Driven("start", BuiltInParameter.EXTRUSION_START_PARAM);
                    Driven("end", BuiltInParameter.EXTRUSION_END_PARAM);
                    break;
                case Blend b:
                    row["start"] = ModelFamilyJson.ToMm(b.BottomOffset);
                    row["end"] = ModelFamilyJson.ToMm(b.TopOffset);
                    row["bottomProfile"] = Loops(Safe(() => b.BottomProfile));
                    row["topProfile"] = Loops(Safe(() => b.TopProfile));
                    Driven("start", BuiltInParameter.BLEND_START_PARAM);
                    Driven("end", BuiltInParameter.BLEND_END_PARAM);
                    break;
                case Revolution r:
                    row["startAngle"] = Math.Round(r.StartAngle * 180 / Math.PI, 4);
                    row["endAngle"] = Math.Round(r.EndAngle * 180 / Math.PI, 4);
                    row["profile"] = Loops(Safe(() => r.Sketch?.Profile));
                    if (Safe(() => r.Axis) is { } axisCurve && Safe(() => axisCurve.GeometryCurve) is { } axis && axis.IsBound)
                        row["axis"] = new JObject { ["start"] = ModelFamilyLookup.Mm(axis.GetEndPoint(0)), ["end"] = ModelFamilyLookup.Mm(axis.GetEndPoint(1)) };
                    Driven("startAngle", BuiltInParameter.REVOLUTION_START_ANGLE);
                    Driven("endAngle", BuiltInParameter.REVOLUTION_END_ANGLE);
                    break;
                case Sweep s:
                    row["path"] = Loops(Safe(() => s.PathSketch?.Profile));
                    row["profile"] = Loops(Safe(() => s.ProfileSketch?.Profile));
                    break;
                case SweptBlend sb:
                    row["path"] = Loops(Safe(() => sb.PathSketch?.Profile));
                    row["bottomProfile"] = Loops(Safe(() => sb.BottomSketch?.Profile));
                    row["topProfile"] = Loops(Safe(() => sb.TopSketch?.Profile));
                    break;
            }
        }
        catch
        {
            row["note"] = "Revit could not describe this form's sketch.";
        }
        if (ModelFamilyFormBuilder.SafeBox(form) is { } box) row["boundingBox"] = Box(box);
        row["visible"] = ModelFamilyFormBuilder.SafeVisible(form);
        Driven("visible", BuiltInParameter.IS_VISIBLE_PARAM);
        row["subcategory"] = ModelFamilyFormBuilder.SafeSubcategory(form);
        row["material"] = MaterialName(doc, form);
        Driven("material", BuiltInParameter.MATERIAL_ID_PARAM);
        if (drivenBy.Count > 0) row["drivenBy"] = drivenBy;
        var (cutBy, cuts) = CutRelations(doc, form);
        row["cutBy"] = new JArray(cutBy);
        row["cuts"] = new JArray(cuts);
        var combinations = CombinationIds(form);
        row["combination"] = combinations.Count == 0 ? null : combinations.Count == 1 ? combinations[0] : new JArray(combinations);
        row["faces"] = ModelFamilyFormFaces.Describe(doc, form);
        return row;
    }

    private static string? MaterialName(Document doc, Element element)
    {
        try
        {
            var parameter = element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM);
            if (parameter == null) return null;
            var id = parameter.AsElementId();
            if (id == null || id == ElementId.InvalidElementId) return "<By Category>";
            return doc.GetElement(id)?.Name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The <c>forms</c> section.</summary>
    public static JToken FormsSection(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var forms = Forms(doc);
        var rows = new JArray(forms.Take(Math.Max(0, limit)).Select(f => Describe(doc, f)));
        return forms.Count > limit
            ? new JObject { ["items"] = rows, ["truncated"] = $"{forms.Count - limit} more form(s); raise limit to see them." }
            : rows;
    }

    /// <summary>The <c>combinations</c> section: GeomCombination elements (joins and cuts) and their members.</summary>
    public static JToken CombinationsSection(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var combinations = Combinations(doc);
        var rows = new JArray(combinations.Take(Math.Max(0, limit)).Select(c => new JObject
        {
            ["id"] = c.Id.Value,
            ["members"] = new JArray(Members(c).Select(m => new JObject
            {
                ["id"] = m.Id.Value,
                ["kind"] = KindOf(m),
                ["solid"] = m is GenericForm g ? ModelFamilyFormBuilder.SafeIsSolid(g) : null
            }))
        }));
        return combinations.Count > limit
            ? new JObject { ["items"] = rows, ["truncated"] = $"{combinations.Count - limit} more combination(s); raise limit to see them." }
            : rows;
    }
}

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Reads reference planes, reference lines, dimensions, alignments and the origin of a family (#170): the rows the
/// edit returns and the <c>revit_get_model_family</c> sections this feature owns. Millimetres, unit normals.
/// </summary>
internal static class ModelFamilyReferenceInspect
{
    public static void RegisterSections()
    {
        ModelFamilyInspect.AddSection("referencePlanes",
            "Reference planes: id, name, isReference, definesOrigin, bubbleEnd/freeEnd (mm), normal; unnamed ones flagged.",
            (context, limit) => Limited(ModelFamilyLookup.ReferencePlanes(context.FamilyDoc).OrderBy(p => p.Id.Value).Select(PlaneRow), limit));
        ModelFamilyInspect.AddSection("referenceLines",
            "Reference lines: id, start, end (mm), isReference.",
            (context, limit) => Limited(ReferenceLines(context.FamilyDoc).Select(LineRow), limit));
        ModelFamilyInspect.AddSection("dimensions",
            "Dimensions: id, view, kind, references (description + stable reference), value or segment values (mm/degrees), label, locked, equal.",
            (context, limit) => Limited(AllDimensions(context.FamilyDoc).Where(d => !IsAlignment(d)).Select(d => DimensionRow(context.FamilyDoc, d)), limit));
        ModelFamilyInspect.AddSection("alignments",
            "Alignment constraints (locked alignments): id, view, the two references, locked.",
            (context, limit) => Limited(AllDimensions(context.FamilyDoc).Where(IsAlignment).Select(d => DimensionRow(context.FamilyDoc, d)), limit));
        ModelFamilyInspect.AddSection("origin",
            "The reference planes that define the insertion point, and where it is.",
            (context, _) => Origin(context.FamilyDoc));
    }

    private static JArray Limited(IEnumerable<JObject> rows, int limit) => new(rows.Take(Math.Max(1, limit)));

    // ── Planes ───────────────────────────────────────────────────────────────

    public static JObject PlaneRow(ReferencePlane plane)
    {
        var name = plane.Name ?? string.Empty;
        var row = new JObject
        {
            ["id"] = plane.Id.Value,
            ["name"] = name,
            ["isReference"] = IsReferenceOf(plane),
            ["definesOrigin"] = DefinesOrigin(plane),
            ["bubbleEnd"] = ModelFamilyLookup.Mm(plane.BubbleEnd),
            ["freeEnd"] = ModelFamilyLookup.Mm(plane.FreeEnd),
            ["normal"] = ModelFamilyLookup.Direction(plane.Normal.Normalize()),
            ["offsetMm"] = ModelFamilyJson.ToMm(plane.Normal.Normalize().DotProduct(plane.BubbleEnd))
        };
        if (name.Trim().Length == 0)
        {
            row["unnamed"] = true;
            row["hint"] = "Unnamed: name it (referencePlanes [{plane: id, rename}]) so it can be found by name.";
        }
        return row;
    }

    /// <summary>The plane's "Is Reference": wire name, stored integer and the label Revit shows.</summary>
    public static JObject? IsReferenceOf(Element element)
    {
        var parameter = IsReferenceParameter(element);
        if (parameter == null) return null;
        int value;
        try { value = parameter.AsInteger(); }
        catch { return null; }
        string? label = null;
        try { label = parameter.AsValueString(); }
        catch { /* the label is diagnostics only */ }
        return new JObject
        {
            ["name"] = ModelFamilyIsReference.FromParameterValue(value)?.Name ?? "unknown",
            ["value"] = value,
            ["label"] = label
        };
    }

    public static Parameter? IsReferenceParameter(Element element)
    {
        try { return element.get_Parameter(BuiltInParameter.ELEM_REFERENCE_NAME); }
        catch { return null; }
    }

    public static Parameter? DefinesOriginParameter(ReferencePlane plane)
    {
        try { return plane.get_Parameter(BuiltInParameter.DATUM_PLANE_DEFINES_ORIGIN); }
        catch { return null; }
    }

    public static bool DefinesOrigin(ReferencePlane plane)
    {
        try { return DefinesOriginParameter(plane)?.AsInteger() == 1; }
        catch { return false; }
    }

    /// <summary>
    /// The origin-defining planes and the insertion point they give: a plane facing X fixes x, Y fixes y, Z fixes z;
    /// a direction no plane fixes falls back to the family origin (z: the reference level).
    /// </summary>
    public static JObject Origin(Document doc)
    {
        var planes = ModelFamilyLookup.ReferencePlanes(doc).Where(DefinesOrigin).ToList();
        var rows = new JArray();
        var axes = new Dictionary<string, (string Plane, double Mm)>();
        foreach (var plane in planes)
        {
            var n = plane.Normal.Normalize();
            var offset = ModelFamilyJson.ToMm(n.DotProduct(plane.BubbleEnd));
            string? axis = null;
            if (Math.Abs(Math.Abs(n.X) - 1) < 1e-6) axis = "x";
            else if (Math.Abs(Math.Abs(n.Y) - 1) < 1e-6) axis = "y";
            else if (Math.Abs(Math.Abs(n.Z) - 1) < 1e-6) axis = "z";
            var coordinate = axis == null ? (double?)null : offset * Math.Sign(axis == "x" ? n.X : axis == "y" ? n.Y : n.Z);
            rows.Add(new JObject
            {
                ["id"] = plane.Id.Value,
                ["name"] = plane.Name,
                ["normal"] = ModelFamilyLookup.Direction(n),
                ["axis"] = axis,
                ["coordinateMm"] = coordinate
            });
            if (axis != null && coordinate != null && !axes.ContainsKey(axis)) axes[axis] = (ModelFamilyReferenceResolver.PlaneName(plane), coordinate.Value);
        }
        var level = ModelFamilyLookup.ReferenceLevel(doc);
        string Part(string axis) => axes.TryGetValue(axis, out var a)
            ? $"{axis} = {a.Mm:0.###} mm from {a.Plane}"
            : axis == "z" && level != null ? $"z = {ModelFamilyJson.ToMm(level.Elevation):0.###} mm from level '{level.Name}'" : $"{axis} = 0 (no plane defines it)";
        var missing = new[] { "x", "y" }.Where(a => !axes.ContainsKey(a)).ToList();
        return new JObject
        {
            ["planes"] = rows,
            ["insertionPoint"] = $"Insertion point at {Part("x")}, {Part("y")}, {Part("z")}.",
            ["hint"] = missing.Count == 0 ? null
                : $"No reference plane defines the origin along {string.Join(" and ", missing)}; set definesOrigin on one plane facing that axis."
        };
    }

    // ── Reference lines ──────────────────────────────────────────────────────

    public static List<ModelCurve> ReferenceLines(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).OfType<ModelCurve>()
            .Where(c => SafeIsReferenceLine(c)).OrderBy(c => c.Id.Value).ToList();

    public static bool SafeIsReferenceLine(ModelCurve curve)
    {
        try { return curve.IsReferenceLine; }
        catch { return false; }
    }

    public static JObject LineRow(ModelCurve curve)
    {
        var row = new JObject { ["id"] = curve.Id.Value };
        try
        {
            var geometry = curve.GeometryCurve;
            row["kind"] = geometry is Line ? "line" : geometry.GetType().Name.ToLowerInvariant();
            if (geometry.IsBound)
            {
                row["start"] = ModelFamilyLookup.Mm(geometry.GetEndPoint(0));
                row["end"] = ModelFamilyLookup.Mm(geometry.GetEndPoint(1));
            }
        }
        catch { /* a curve without geometry */ }
        row["isReference"] = IsReferenceOf(curve);
        try { row["workPlane"] = curve.SketchPlane?.Name; }
        catch { /* no sketch plane */ }
        return row;
    }

    // ── Dimensions and alignments ────────────────────────────────────────────

    /// <summary>Every dimension of the family, alignment constraints included (they are Dimension elements too).</summary>
    public static List<Dimension> AllDimensions(Document doc)
    {
        var byClass = new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>().ToList();
        try
        {
            var constraints = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Constraints).WhereElementIsNotElementType()
                .OfType<Dimension>().ToList();
            byClass.AddRange(constraints.Where(c => byClass.All(d => d.Id != c.Id)));
        }
        catch { /* the category may hold no dimensions */ }
        return byClass.OrderBy(d => d.Id.Value).ToList();
    }

    /// <summary>
    /// True for a locked alignment: Revit keeps it as a Dimension in the Constraints category. Ordinary dimensions,
    /// labelled or locked, are in the Dimensions category.
    /// </summary>
    public static bool IsAlignment(Dimension dimension)
    {
        try { return dimension.Category?.BuiltInCategory == BuiltInCategory.OST_Constraints; }
        catch { return false; }
    }

    public static List<Reference> References(Dimension dimension)
    {
        var list = new List<Reference>();
        try { foreach (Reference reference in dimension.References) list.Add(reference); }
        catch { /* a dimension whose references are gone */ }
        return list;
    }

    public static JObject DimensionRow(Document doc, Dimension dimension)
    {
        var alignment = IsAlignment(dimension);
        var shape = SafeShape(dimension);
        var row = new JObject
        {
            ["id"] = dimension.Id.Value,
            ["kind"] = alignment ? "alignment" : shape.ToString().ToLowerInvariant()
        };
        try { row["view"] = dimension.View?.Name; }
        catch { /* a dimension without a view */ }
        row["references"] = new JArray(References(dimension).Take(20).Select(r => ModelFamilyReferenceResolver.Describe(doc, r)));

        var segments = SafeSegments(dimension);
        if (!alignment)
        {
            var angular = shape == DimensionShape.Angular;
            if (segments > 1)
            {
                var values = new JArray();
                foreach (DimensionSegment segment in dimension.Segments) values.Add(Value(segment.Value, angular));
                row["segments"] = values;
            }
            else row["value"] = Value(SafeValue(dimension), angular);
            row["unit"] = angular ? "degrees" : "mm";
            try { row["label"] = dimension.FamilyLabel?.Definition.Name; }
            catch { row["label"] = null; }
            if (segments > 1)
            {
                try { row["equal"] = dimension.AreSegmentsEqual; }
                catch { row["equal"] = null; }
            }
        }
        row["locked"] = Locked(dimension);
        return row;
    }

    private static JToken Value(double? feetOrRadians, bool angular) => feetOrRadians == null
        ? JValue.CreateNull()
        : new JValue(angular ? Math.Round(feetOrRadians.Value * 180 / Math.PI, 4) : ModelFamilyJson.ToMm(feetOrRadians.Value));

    public static int SafeSegments(Dimension dimension)
    {
        try { return dimension.NumberOfSegments; }
        catch { return 0; }
    }

    public static DimensionShape SafeShape(Dimension dimension)
    {
        try { return dimension.DimensionShape; }
        catch { return DimensionShape.Unknown; }
    }

    private static double? SafeValue(Dimension dimension)
    {
        try { return dimension.Value; }
        catch { return null; }
    }

    /// <summary>Locked: the dimension's own lock, or every segment's for a multi-segment dimension.</summary>
    public static bool? Locked(Dimension dimension)
    {
        try
        {
            if (SafeSegments(dimension) > 1)
            {
                var all = true;
                foreach (DimensionSegment segment in dimension.Segments) all &= segment.IsLocked;
                return all;
            }
            return dimension.IsLocked;
        }
        catch
        {
            return null;
        }
    }
}

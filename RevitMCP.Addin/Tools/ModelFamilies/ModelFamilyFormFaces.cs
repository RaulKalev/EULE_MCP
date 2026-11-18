using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>A planar face of a form with the reference other tools place or dimension against.</summary>
internal sealed class ModelFamilyFormFace
{
    public PlanarFace Face { get; init; } = null!;
    public Reference? Reference { get; init; }
    /// <summary><c>left|right|front|back|top|bottom</c>, or null for a face not square to the family axes.</summary>
    public string? Label { get; init; }
    public XYZ Normal { get; init; } = XYZ.BasisZ;
    /// <summary>A point on the face, feet.</summary>
    public XYZ Point { get; init; } = XYZ.Zero;
    /// <summary>Area in mm².</summary>
    public double AreaMm2 { get; init; }

    public JObject ToJson(Document doc)
    {
        string? stable = null;
        try { stable = Reference?.ConvertToStableRepresentation(doc); }
        catch { /* a face without a stable reference */ }
        return new JObject
        {
            ["face"] = Label,
            ["normal"] = ModelFamilyLookup.Direction(Normal),
            ["point"] = ModelFamilyLookup.Mm(Point),
            ["area"] = Math.Round(AreaMm2, 1),
            ["reference"] = stable
        };
    }
}

/// <summary>
/// The planar faces of a form and their references (#171), for downstream placement: other features accept
/// <c>{"element": id, "face": "top"}</c> or <c>{"reference": "&lt;stable representation&gt;"}</c> and resolve it with
/// <see cref="Resolve"/>. The label rule is the pure <see cref="ModelFamilyFormFaceLabels.LabelOf(double, double, double)"/>.
/// </summary>
internal static class ModelFamilyFormFaces
{
    /// <summary><c>left|right|front|back|top|bottom</c> when the normal is within 1° of −X/+X/−Y/+Y/+Z/−Z, else null.</summary>
    public static string? LabelOf(XYZ normal) => ModelFamilyFormFaceLabels.LabelOf(normal.X, normal.Y, normal.Z);

    /// <summary>
    /// The planar faces of an element's solids, with references (geometry computed with <c>ComputeReferences</c>).
    /// Non-planar faces are left out.
    /// </summary>
    public static List<ModelFamilyFormFace> PlanarFaces(Element element)
    {
        var result = new List<ModelFamilyFormFace>();
        GeometryElement? geometry;
        try
        {
            geometry = element.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = true });
        }
        catch
        {
            return result;
        }
        if (geometry == null) return result;
        foreach (var solid in Solids(geometry))
        {
            foreach (Face face in solid.Faces)
            {
                if (face is not PlanarFace planar) continue;
                XYZ normal;
                try { normal = planar.FaceNormal; }
                catch { continue; }
                result.Add(new ModelFamilyFormFace
                {
                    Face = planar,
                    Reference = SafeReference(planar),
                    Label = LabelOf(normal),
                    Normal = normal,
                    Point = PointOn(planar),
                    AreaMm2 = SafeArea(planar) * ModelFamilyJson.MmPerFoot * ModelFamilyJson.MmPerFoot
                });
            }
        }
        return result;
    }

    /// <summary>
    /// The face of <paramref name="element"/> with <paramref name="label"/>. When several faces share it (a stepped
    /// form), the outermost along the label's direction wins, then the largest. Null when none has it.
    /// </summary>
    public static ModelFamilyFormFace? Find(Element element, string label)
    {
        if (!ModelFamilyFormFaceLabels.IsLabel(label)) return null;
        var axis = ModelFamilyFormFaceLabels.NormalOf(label);
        var direction = new XYZ(axis[0], axis[1], axis[2]);
        // Faces without a reference cannot be placed or dimensioned against; leave them out before choosing, as
        // ModelFamilyReferenceResolver does, so both pick the same face.
        return PlanarFaces(element)
            .Where(f => f.Reference != null && string.Equals(f.Label, label.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => Math.Round(f.Point.DotProduct(direction) * ModelFamilyJson.MmPerFoot, 3))
            .ThenByDescending(f => f.AreaMm2)
            .FirstOrDefault();
    }

    /// <summary>
    /// A face reference from <c>{"element": id, "face": "top"}</c> or <c>{"reference": "…"}</c> (a stable representation
    /// that revit_get_model_family lists). Problems name <paramref name="source"/>; null when it cannot be resolved.
    /// </summary>
    public static Reference? Resolve(Document doc, JToken? token, string source, List<string> problems)
    {
        if (token is not JObject obj)
        {
            problems.Add($"{source}: give {{\"element\": id, \"face\": \"top\"}} or {{\"reference\": \"<stable reference>\"}}.");
            return null;
        }
        ModelFamilyJson.CheckKeys(obj, source, problems, "element", "face", "reference");
        var stable = ModelFamilyJson.Str(obj, "reference");
        if (stable.Length > 0)
        {
            try
            {
                var parsed = Reference.ParseFromStableRepresentation(doc, stable);
                if (parsed != null) return parsed;
            }
            catch
            {
                // reported below
            }
            problems.Add($"{source}.reference: '{stable}' is not a reference of this family; read the current one with revit_get_model_family include=[\"forms\"].");
            return null;
        }
        var element = ModelFamilyLookup.Element(doc, ModelFamilyJson.Get(obj, "element"));
        var label = ModelFamilyJson.Str(obj, "face");
        if (element == null)
        {
            problems.Add($"{source}.element: give the id of a form of this family.");
            return null;
        }
        if (!ModelFamilyFormFaceLabels.IsLabel(label))
        {
            problems.Add($"{source}.face: '{label}' is not one of {string.Join(", ", ModelFamilyFormFaceLabels.Labels)}.");
            return null;
        }
        var face = Find(element, label);
        if (face?.Reference != null) return face.Reference;
        var labels = PlanarFaces(element).Select(f => f.Label).Where(l => l != null).Distinct().ToList();
        problems.Add($"{source}: element {element.Id.Value} has no '{label}' face" +
                     (labels.Count > 0 ? $"; it has {string.Join(", ", labels)}." : "; pass a reference from revit_get_model_family include=[\"forms\"]."));
        return null;
    }

    /// <summary>The planar faces as result rows.</summary>
    public static JArray Describe(Document doc, Element element) =>
        new(PlanarFaces(element).Select(f => f.ToJson(doc)));

    internal static IEnumerable<Solid> Solids(GeometryElement geometry)
    {
        foreach (var obj in geometry)
        {
            switch (obj)
            {
                case Solid solid when solid.Faces.Size > 0:
                    yield return solid;
                    break;
                case GeometryInstance instance:
                    GeometryElement? inner = null;
                    try { inner = instance.GetSymbolGeometry(); }
                    catch { /* no symbol geometry */ }
                    if (inner != null)
                        foreach (var nested in Solids(inner)) yield return nested;
                    break;
            }
        }
    }

    private static Reference? SafeReference(Face face)
    {
        try { return face.Reference; }
        catch { return null; }
    }

    private static double SafeArea(Face face)
    {
        try { return face.Area; }
        catch { return 0; }
    }

    private static XYZ PointOn(PlanarFace face)
    {
        try
        {
            var box = face.GetBoundingBox();
            var middle = new UV((box.Min.U + box.Max.U) / 2, (box.Min.V + box.Max.V) / 2);
            if (face.IsInside(middle)) return face.Evaluate(middle);
            foreach (EdgeArray loop in face.EdgeLoops)
                foreach (Edge edge in loop)
                    return edge.Evaluate(0.5);
        }
        catch
        {
            // the origin below
        }
        return face.Origin;
    }
}

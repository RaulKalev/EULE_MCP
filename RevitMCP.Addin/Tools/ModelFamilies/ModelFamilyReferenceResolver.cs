using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>A selector turned into a Revit reference, with the plane it lies in where that is known.</summary>
internal sealed class ModelFamilyResolvedReference
{
    public Reference Reference { get; init; } = null!;
    public Element? Element { get; init; }
    /// <summary>The unit normal of the referenced plane or face; null for a line.</summary>
    public XYZ? Normal { get; set; }
    /// <summary>A point on the referenced plane, face or line (feet); null when unknown.</summary>
    public XYZ? Point { get; set; }
    /// <summary>True when <see cref="Point"/> is only the nested instance's insertion point.</summary>
    public bool PointApproximate { get; set; }
    /// <summary>The direction of a referenced line; null for planes.</summary>
    public XYZ? LineDirection { get; set; }
    public string Description { get; init; } = string.Empty;
    public ModelFamilyReferenceSelector Selector { get; init; } = null!;
}

/// <summary>
/// Resolves the shared reference selectors (<see cref="ModelFamilyReferenceSelector"/>, #170) in a family document:
/// reference planes, reference lines and levels by name or id, form faces by outward normal, nested-instance references
/// and stable representations. Other model-family features use it for their own references.
/// </summary>
internal static class ModelFamilyReferenceResolver
{
    /// <summary>Faces within this angle of the wanted normal count as facing that way.</summary>
    public const double FaceToleranceDegrees = 1.0;

    public static ModelFamilyResolvedReference? Resolve(Document doc, JToken? token, string source, List<string> problems)
    {
        var selector = ModelFamilyReferenceSelector.Parse(token, source, problems);
        return selector == null ? null : Resolve(doc, selector, problems);
    }

    public static ModelFamilyResolvedReference? Resolve(Document doc, ModelFamilyReferenceSelector selector, List<string> problems)
    {
        var source = selector.Source;
        switch (selector.Kind)
        {
            case ModelFamilyReferenceKind.Level:
            {
                var level = ModelFamilyLookup.ReferenceLevel(doc);
                if (level == null)
                {
                    problems.Add($"{source}: the family has no level.");
                    return null;
                }
                return FromLevel(level, selector);
            }

            case ModelFamilyReferenceKind.Element:
            {
                Element? element = null;
                if (selector.ElementId != null) element = doc.GetElement(new ElementId(selector.ElementId.Value));
                if (element == null && selector.Name.Length > 0)
                {
                    var planeProblems = new List<string>();
                    element = ModelFamilyLookup.ReferencePlane(doc, new JValue(selector.Name), source, planeProblems);
                    if (element == null)
                    {
                        element = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                            .FirstOrDefault(l => string.Equals(l.Name, selector.Name, StringComparison.OrdinalIgnoreCase));
                        if (element == null)
                        {
                            problems.AddRange(planeProblems);
                            return null;
                        }
                    }
                }
                if (element == null)
                {
                    problems.Add($"{source}: the family has no element {selector.ElementId}.");
                    return null;
                }
                return FromElement(element, selector, problems);
            }

            case ModelFamilyReferenceKind.Face:
                return FromFace(doc, selector, problems);

            case ModelFamilyReferenceKind.InstanceReference:
                return FromInstance(doc, selector, problems);

            default:
                return FromStable(doc, selector, problems);
        }
    }

    private static ModelFamilyResolvedReference FromLevel(Level level, ModelFamilyReferenceSelector selector) => new()
    {
        Reference = level.GetPlaneReference(),
        Element = level,
        Normal = XYZ.BasisZ,
        Point = new XYZ(0, 0, level.Elevation),
        Description = $"level '{level.Name}'",
        Selector = selector
    };

    private static ModelFamilyResolvedReference? FromElement(Element element, ModelFamilyReferenceSelector selector, List<string> problems)
    {
        switch (element)
        {
            case ReferencePlane plane:
                return new ModelFamilyResolvedReference
                {
                    Reference = plane.GetReference(),
                    Element = plane,
                    Normal = plane.Normal.Normalize(),
                    Point = plane.BubbleEnd,
                    Description = PlaneName(plane),
                    Selector = selector
                };
            case Level level:
                return FromLevel(level, selector);
            case ModelCurve curve when curve.GeometryCurve is Line line:
            {
                var reference = line.Reference ?? new Reference(curve);
                return new ModelFamilyResolvedReference
                {
                    Reference = reference,
                    Element = curve,
                    Point = line.Evaluate(0.5, true),
                    LineDirection = line.Direction.Normalize(),
                    Description = $"{(curve.IsReferenceLine ? "reference line" : "model line")} {curve.Id.Value}",
                    Selector = selector
                };
            }
            default:
                problems.Add($"{selector.Source}: element {element.Id.Value} is a {element.GetType().Name}; give a reference plane, a straight reference line or a level, or use {{element, face}} / {{element, instanceReference}}.");
                return null;
        }
    }

    private static ModelFamilyResolvedReference? FromFace(Document doc, ModelFamilyReferenceSelector selector, List<string> problems)
    {
        var element = doc.GetElement(new ElementId(selector.ElementId!.Value));
        if (element == null)
        {
            problems.Add($"{selector.Source}.element: the family has no element {selector.ElementId}.");
            return null;
        }
        var wanted = new XYZ(selector.FaceNormal![0], selector.FaceNormal[1], selector.FaceNormal[2]);
        var faces = PlanarFaces(element);
        if (faces.Count == 0)
        {
            problems.Add($"{selector.Source}: element {element.Id.Value} ({element.GetType().Name}) has no planar face with a reference.");
            return null;
        }
        var cosine = Math.Cos(FaceToleranceDegrees * Math.PI / 180);
        var candidates = faces.Where(f => f.Normal.DotProduct(wanted) >= cosine).ToList();
        if (candidates.Count == 0)
        {
            var normals = faces.Select(f => $"[{Round(f.Normal.X)}, {Round(f.Normal.Y)}, {Round(f.Normal.Z)}]").Distinct().Take(12);
            problems.Add($"{selector.Source}: element {element.Id.Value} has no planar face within {FaceToleranceDegrees}° of {selector.Describe().Replace($" of element {element.Id.Value}", string.Empty)}. Its planar face normals: {string.Join(", ", normals)}; pass face as one of them.");
            return null;
        }
        // Several faces facing that way (a stepped form, several solids): the outermost one, as documented.
        var best = candidates.OrderByDescending(f => f.Origin.DotProduct(wanted)).First();
        return new ModelFamilyResolvedReference
        {
            Reference = best.Reference,
            Element = element,
            Normal = best.Normal,
            Point = best.Origin,
            Description = $"{(selector.FaceName.Length > 0 ? selector.FaceName + " face" : "face")} of {ModelFamilyLookup.Describe(element)}",
            Selector = selector
        };
    }

    /// <summary>A planar face with its reference, normal and a point, in family coordinates.</summary>
    internal sealed class FaceInfo
    {
        public Reference Reference { get; init; } = null!;
        public XYZ Normal { get; init; } = XYZ.BasisZ;
        public XYZ Origin { get; init; } = XYZ.Zero;
    }

    /// <summary>The element's planar faces that carry a reference (solids of forms; instance geometry of nested instances).</summary>
    internal static List<FaceInfo> PlanarFaces(Element element)
    {
        var result = new List<FaceInfo>();
        GeometryElement? geometry;
        try { geometry = element.get_Geometry(new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine }); }
        catch { return result; }
        if (geometry == null) return result;
        Collect(geometry, result);
        return result;
    }

    private static void Collect(GeometryElement geometry, List<FaceInfo> result)
    {
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid solid:
                    foreach (Face face in solid.Faces)
                    {
                        if (face is not PlanarFace planar || planar.Reference == null) continue;
                        result.Add(new FaceInfo { Reference = planar.Reference, Normal = planar.FaceNormal.Normalize(), Origin = planar.Origin });
                    }
                    break;
                case GeometryInstance instance:
                    try { Collect(instance.GetInstanceGeometry(), result); }
                    catch { /* a nested instance without geometry */ }
                    break;
            }
        }
    }

    private static ModelFamilyResolvedReference? FromInstance(Document doc, ModelFamilyReferenceSelector selector, List<string> problems)
    {
        if (doc.GetElement(new ElementId(selector.ElementId!.Value)) is not FamilyInstance instance)
        {
            problems.Add($"{selector.Source}.element: element {selector.ElementId} is not a nested family instance.");
            return null;
        }
        Reference? reference = null;
        if (selector.InstanceReferenceType != null && Enum.TryParse<FamilyInstanceReferenceType>(selector.InstanceReferenceType, out var type))
        {
            var found = SafeReferences(instance, type);
            if (found.Count == 0)
            {
                problems.Add($"{selector.Source}: nested instance {instance.Id.Value} has no {selector.InstanceReferenceType} reference. Its references: {ReferenceNames(instance)}.");
                return null;
            }
            if (found.Count > 1)
            {
                problems.Add($"{selector.Source}: nested instance {instance.Id.Value} has {found.Count} {selector.InstanceReferenceType} references; name one: {string.Join(", ", found.Select(r => $"'{SafeName(instance, r)}'"))}.");
                return null;
            }
            reference = found[0];
        }
        else
        {
            try { reference = instance.GetReferenceByName(selector.InstanceReferenceName); }
            catch { reference = null; }
            if (reference == null)
            {
                problems.Add($"{selector.Source}: nested instance {instance.Id.Value} has no reference called '{selector.InstanceReferenceName}'. Its references: {ReferenceNames(instance)}.");
                return null;
            }
        }

        var resolved = new ModelFamilyResolvedReference
        {
            Reference = reference,
            Element = instance,
            Description = $"reference '{SafeName(instance, reference)}' of nested {ModelFamilyLookup.Describe(instance)}",
            Selector = selector
        };
        Locate(doc, instance, reference, resolved);
        return resolved;
    }

    /// <summary>
    /// The plane of a nested-instance reference. Tried in order: the geometry object it names; a temporary sketch plane
    /// on it (inside the step's transaction, deleted again); the instance's axes for left…top references.
    /// </summary>
    private static void Locate(Document doc, FamilyInstance instance, Reference reference, ModelFamilyResolvedReference resolved)
    {
        try
        {
            switch (instance.GetGeometryObjectFromReference(reference))
            {
                case PlanarFace face:
                    resolved.Normal = face.FaceNormal.Normalize();
                    resolved.Point = face.Origin;
                    return;
                case Line line:
                    resolved.LineDirection = line.Direction.Normalize();
                    resolved.Point = line.Evaluate(0.5, true);
                    return;
            }
        }
        catch { /* not every instance reference names a geometry object */ }

        if (doc.IsModifiable)
        {
            try
            {
                using var sub = new SubTransaction(doc);
                sub.Start();
                var sketch = SketchPlane.Create(doc, reference);
                var plane = sketch.GetPlane();
                resolved.Normal = plane.Normal.Normalize();
                resolved.Point = plane.Origin;
                sub.RollBack();
                return;
            }
            catch { /* not a planar reference Revit can sketch on */ }
        }

        XYZ? axis = null;
        try
        {
            var transform = instance.GetTransform();
            axis = instance.GetReferenceType(reference) switch
            {
                FamilyInstanceReferenceType.Left or FamilyInstanceReferenceType.CenterLeftRight or FamilyInstanceReferenceType.Right => transform.BasisX,
                FamilyInstanceReferenceType.Front or FamilyInstanceReferenceType.CenterFrontBack or FamilyInstanceReferenceType.Back => transform.BasisY,
                FamilyInstanceReferenceType.Bottom or FamilyInstanceReferenceType.CenterElevation or FamilyInstanceReferenceType.Top => transform.BasisZ,
                _ => null
            };
            if (axis != null)
            {
                resolved.Normal = axis.Normalize();
                resolved.Point = transform.Origin;
                resolved.PointApproximate = true;
            }
        }
        catch { /* the plane stays unknown */ }
    }

    private static ModelFamilyResolvedReference? FromStable(Document doc, ModelFamilyReferenceSelector selector, List<string> problems)
    {
        Reference reference;
        try { reference = Reference.ParseFromStableRepresentation(doc, selector.StableRepresentation); }
        catch (Exception ex)
        {
            problems.Add($"{selector.Source}.reference: '{selector.StableRepresentation}' is not a reference of this family ({ex.Message.TrimEnd('.')}). Use a representation returned by these tools for this family.");
            return null;
        }
        var element = doc.GetElement(reference.ElementId);
        var resolved = new ModelFamilyResolvedReference
        {
            Reference = reference,
            Element = element,
            Description = element == null ? $"reference '{selector.StableRepresentation}'" : $"reference of {ModelFamilyLookup.Describe(element)}",
            Selector = selector
        };
        switch (element)
        {
            case ReferencePlane plane when reference.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_NONE:
                resolved.Normal = plane.Normal.Normalize();
                resolved.Point = plane.BubbleEnd;
                return resolved;
            case Level level when reference.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_NONE:
                resolved.Normal = XYZ.BasisZ;
                resolved.Point = new XYZ(0, 0, level.Elevation);
                return resolved;
            case FamilyInstance instance:
                Locate(doc, instance, reference, resolved);
                return resolved;
        }
        try
        {
            switch (element?.GetGeometryObjectFromReference(reference))
            {
                case PlanarFace face:
                    resolved.Normal = face.FaceNormal.Normalize();
                    resolved.Point = face.Origin;
                    break;
                case Line line:
                    resolved.LineDirection = line.Direction.Normalize();
                    resolved.Point = line.Evaluate(0.5, true);
                    break;
            }
        }
        catch { /* the plane stays unknown: an explicit view is then needed */ }
        return resolved;
    }

    // ── Describing references ────────────────────────────────────────────────

    /// <summary>A reference for a result row: what it is in words and its stable representation (usable as {reference}).</summary>
    public static JObject Describe(Document doc, Reference reference)
    {
        string? stable = null;
        try { stable = reference.ConvertToStableRepresentation(doc); }
        catch { /* some references have none */ }
        var element = doc.GetElement(reference.ElementId);
        string description;
        switch (element)
        {
            case null:
                description = $"element {reference.ElementId.Value}";
                break;
            case ReferencePlane plane:
                description = PlaneName(plane);
                break;
            case Level level:
                description = $"level '{level.Name}'";
                break;
            case FamilyInstance instance:
                description = $"reference '{SafeName(instance, reference)}' of nested {ModelFamilyLookup.Describe(instance)}";
                break;
            case ModelCurve curve:
                description = $"{(curve.IsReferenceLine ? "reference line" : "model line")} {curve.Id.Value}";
                break;
            default:
                description = reference.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_SURFACE
                    ? $"face of {ModelFamilyLookup.Describe(element)}{FaceSide(element, reference)}"
                    : $"{reference.ElementReferenceType.ToString().Replace("REFERENCE_TYPE_", string.Empty).ToLowerInvariant()} of {ModelFamilyLookup.Describe(element)}";
                break;
        }
        return new JObject
        {
            ["description"] = description,
            ["elementId"] = reference.ElementId.Value,
            ["reference"] = stable
        };
    }

    private static string FaceSide(Element element, Reference reference)
    {
        try
        {
            if (element.GetGeometryObjectFromReference(reference) is not PlanarFace face) return string.Empty;
            var n = face.FaceNormal.Normalize();
            foreach (var pair in ModelFamilyReferenceSelector.FaceDirections)
                if (n.DotProduct(new XYZ(pair.Value[0], pair.Value[1], pair.Value[2])) >= Math.Cos(FaceToleranceDegrees * Math.PI / 180))
                    return $" ({pair.Key})";
            return $" (normal [{Round(n.X)}, {Round(n.Y)}, {Round(n.Z)}])";
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string PlaneName(ReferencePlane plane) =>
        string.IsNullOrEmpty(plane.Name) ? $"unnamed reference plane {plane.Id.Value}" : $"reference plane '{plane.Name}'";

    private static List<Reference> SafeReferences(FamilyInstance instance, FamilyInstanceReferenceType type)
    {
        try { return instance.GetReferences(type)?.ToList() ?? new List<Reference>(); }
        catch { return new List<Reference>(); }
    }

    private static string SafeName(FamilyInstance instance, Reference reference)
    {
        try { return instance.GetReferenceName(reference) ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>Every named reference of a nested instance with its type, for messages.</summary>
    private static string ReferenceNames(FamilyInstance instance)
    {
        var names = new List<string>();
        foreach (FamilyInstanceReferenceType type in Enum.GetValues(typeof(FamilyInstanceReferenceType)))
        {
            if (type == FamilyInstanceReferenceType.NotAReference) continue;
            foreach (var reference in SafeReferences(instance, type))
                names.Add($"'{SafeName(instance, reference)}' ({type})");
        }
        return names.Count == 0 ? "none (its family's planes are all Not a Reference)" : string.Join(", ", names.Distinct().Take(20));
    }

    private static double Round(double value) => Math.Round(value, 4);
}

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>The family elements a flex check names, resolved once before the cases run (#176).</summary>
internal sealed class ModelFamilyFlexTargets
{
    public long? Element { get; set; }
    public long? Plane { get; set; }
    public long? OnPlane { get; set; }
    public long? Connector { get; set; }
    public long? OnFaceElement { get; set; }
    public long? Instance { get; set; }
}

/// <summary>
/// The Revit side of the flex checks (#176): resolves what a check names in the family document and measures it in
/// each case, as plain <see cref="ModelFamilyFlexObservation"/>s for the pure evaluator. Reuses the readers of the
/// sibling features: faces (#171), reference planes (#170), parameters (#172), materials (#173), connectors (#174),
/// nested instances (#175).
/// </summary>
internal static class ModelFamilyFlexMeasure
{
    // ── Resolving ────────────────────────────────────────────────────────────

    public static Dictionary<string, ModelFamilyFlexTargets> Resolve(Document doc, List<ModelFamilyFlexCheck> checks, List<string> problems)
    {
        var result = new Dictionary<string, ModelFamilyFlexTargets>(StringComparer.Ordinal);
        foreach (var check in checks)
        {
            var targets = new ModelFamilyFlexTargets();
            var source = check.Source;
            switch (check.Kind)
            {
                case "boundingBox":
                case "centred":
                    if (check.Element != null) targets.Element = Existing(doc, check.Element, $"{source}.element", "a form", problems)?.Id.Value;
                    break;
                case "plane":
                    targets.Plane = ModelFamilyLookup.ReferencePlane(doc, check.Plane, $"{source}.plane", problems)?.Id.Value;
                    break;
                case "face":
                    targets.Element = Existing(doc, check.Element, $"{source}.element", "a form", problems)?.Id.Value;
                    if (check.OnPlane != null) targets.OnPlane = ModelFamilyLookup.ReferencePlane(doc, check.OnPlane, $"{source}.onPlane", problems)?.Id.Value;
                    break;
                case "material":
                {
                    var element = Existing(doc, check.Element, $"{source}.element", "a form", problems);
                    if (element != null && ModelFamilyMaterialRevit.MaterialParameter(element) == null)
                        problems.Add($"{source}.element: {ModelFamilyLookup.Describe(element)} has no Material property.");
                    targets.Element = element?.Id.Value;
                    break;
                }
                case "connector":
                    targets.Connector = ResolveConnector(doc, check.Connector, $"{source}.connector", problems)?.Id.Value;
                    if (check.OnFaceElement != null) targets.OnFaceElement = Existing(doc, check.OnFaceElement, $"{source}.onFace.element", "a form", problems)?.Id.Value;
                    break;
                case "symbol":
                {
                    var element = Existing(doc, check.Instance, $"{source}.instance", "a nested instance", problems);
                    if (element != null && element is not FamilyInstance)
                        problems.Add($"{source}.instance: {ModelFamilyLookup.Describe(element)} is not a nested family instance.");
                    targets.Instance = element?.Id.Value;
                    break;
                }
                case "exists":
                    for (var i = 0; i < check.Elements.Count; i++)
                        if (doc.GetElement(new ElementId(check.Elements[i])) == null)
                            problems.Add($"{source}.elements[{i}]: the family has no element {check.Elements[i]} before the flex.");
                    break;
                case "cut":
                    foreach (var (id, key) in new[] { (check.Void, "void"), (check.Solid, "solid") })
                        if (doc.GetElement(new ElementId(id)) is not GenericForm)
                            problems.Add($"{source}.{key}: element {id} is not a form of the family.");
                    break;
                case "parameter":
                    ModelFamilyLookup.Parameter(doc, check.Parameter ?? string.Empty, $"{source}.parameter", problems);
                    break;
            }
            foreach (var name in check.ReferencedParameters())
                if (!(check.Kind == "parameter" && string.Equals(name, check.Parameter, StringComparison.OrdinalIgnoreCase)))
                    ModelFamilyLookup.Parameter(doc, name, $"{source} (an expectation)", problems);
            result[check.Id] = targets;
        }
        return result;
    }

    private static Element? Existing(Document doc, JToken? token, string source, string what, List<string> problems)
    {
        var id = ModelFamilyJson.AsId(token);
        if (id == null)
        {
            problems.Add($"{source}: give the element id of {what} (revit_get_model_family lists them).");
            return null;
        }
        var element = doc.GetElement(new ElementId(id.Value));
        if (element == null) problems.Add($"{source}: the family has no element {id}.");
        return element;
    }

    /// <summary>A connector of the family by element id, or by its description (unique, case-insensitive).</summary>
    public static ConnectorElement? ResolveConnector(Document doc, JToken? token, string source, List<string> problems)
    {
        var all = ModelFamilyConnectorRevit.All(doc);
        if (token?.Type == JTokenType.Integer && ModelFamilyJson.AsId(token) is { } id)
        {
            var byId = all.FirstOrDefault(c => c.Id.Value == id);
            if (byId == null) problems.Add($"{source}: the family has no connector with id {id}. Connectors: {Listing(all)}.");
            return byId;
        }
        var text = (token?.Type == JTokenType.String ? (string?)token : token?.ToString())?.Trim() ?? string.Empty;
        var matches = all.Where(c => string.Equals(Description(c), text, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        if (ModelFamilyJson.AsId(token) is { } numeric && all.FirstOrDefault(c => c.Id.Value == numeric) is { } numbered) return numbered;
        problems.Add(matches.Count > 1
            ? $"{source}: {matches.Count} connectors are described '{text}'; use an element id ({string.Join(", ", matches.Select(c => c.Id.Value))})."
            : $"{source}: no connector of the family is described '{text}'. Connectors: {Listing(all)}.");
        return null;
    }

    private static string Listing(List<ConnectorElement> all) =>
        all.Count == 0 ? "none" : string.Join(", ", all.Select(c => $"{c.Id.Value}{(Description(c) is { Length: > 0 } d ? $" '{d}'" : string.Empty)}"));

    public static string Description(ConnectorElement connector)
    {
        try { return connector.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    // ── Measuring ────────────────────────────────────────────────────────────

    /// <summary>Measures one check in the current (regenerated) state of the family.</summary>
    public static ModelFamilyFlexObservation Measure(Document doc, ModelFamilyFlexCheck check, ModelFamilyFlexTargets targets,
        IReadOnlyDictionary<string, JToken?> values)
    {
        var observation = new ModelFamilyFlexObservation();
        try
        {
            switch (check.Kind)
            {
                case "boundingBox":
                case "centred":
                {
                    var elements = targets.Element is { } id
                        ? new List<Element?> { doc.GetElement(new ElementId(id)) }.OfType<Element>().ToList()
                        : ModelFamilyFormRead.Forms(doc).Where(f => ModelFamilyFormBuilder.SafeIsSolid(f) != false).Cast<Element>().ToList();
                    if (elements.Count == 0)
                    {
                        observation.Missing = targets.Element != null ? $"Element {targets.Element} no longer exists." : "The family has no solid forms.";
                        break;
                    }
                    var box = Box(elements, null);
                    if (box != null)
                    {
                        observation.Min = box.Value.Min;
                        observation.Max = box.Value.Max;
                    }
                    break;
                }
                case "plane":
                    observation.Plane = targets.Plane is { } planeId && doc.GetElement(new ElementId(planeId)) is ReferencePlane plane ? PlaneOf(plane) : null;
                    break;
                case "face":
                {
                    if (targets.Element is not { } formId || doc.GetElement(new ElementId(formId)) is not { } form)
                    {
                        observation.Missing = $"Element {targets.Element} no longer exists.";
                        break;
                    }
                    observation.Plane = FaceOf(form, check.Face!);
                    if (targets.OnPlane is { } onPlaneId)
                    {
                        if (doc.GetElement(new ElementId(onPlaneId)) is ReferencePlane onPlane) observation.Target = PlaneOf(onPlane);
                        else observation.TargetMissing = $"The reference plane {onPlaneId} no longer exists.";
                    }
                    break;
                }
                case "parameter":
                    observation.Value = ModelFamilyFlexExpr.TryGet(values, check.Parameter!, out var value) ? value?.DeepClone() : null;
                    break;
                case "material":
                {
                    var element = targets.Element is { } id ? doc.GetElement(new ElementId(id)) : null;
                    var parameter = element == null ? null : ModelFamilyMaterialRevit.MaterialParameter(element);
                    if (parameter == null)
                    {
                        observation.Missing = $"Element {targets.Element} has no Material property in this case.";
                        break;
                    }
                    var name = ModelFamilyMaterialRevit.MaterialName(doc, ModelFamilyMaterialRevit.SafeId(parameter));
                    observation.Value = name.Length == 0 ? JValue.CreateNull() : new JValue(name);
                    break;
                }
                case "connector":
                    MeasureConnector(doc, check, targets, observation);
                    break;
                case "symbol":
                {
                    if (targets.Instance is not { } id || doc.GetElement(new ElementId(id)) is not FamilyInstance instance)
                    {
                        observation.Missing = $"The nested instance {targets.Instance} no longer exists.";
                        break;
                    }
                    observation.Point = PositionOf(instance);
                    break;
                }
                case "exists":
                {
                    observation.Exists = true;
                    foreach (var id in check.Elements)
                    {
                        var element = doc.GetElement(new ElementId(id));
                        if (element == null)
                        {
                            observation.Exists = false;
                            continue;
                        }
                        if (element is GenericForm form && ModelFamilyFormBuilder.SafeIsSolid(form) == true && ModelFamilyFormRead.VolumeMm3(form) <= 0)
                            observation.Empty.Add(id);
                    }
                    break;
                }
                case "cut":
                {
                    if (doc.GetElement(new ElementId(check.Solid)) is not { } solid)
                    {
                        observation.Missing = $"The solid {check.Solid} no longer exists.";
                        break;
                    }
                    observation.CutBy = ModelFamilyFormRead.CutRelations(doc, solid).CutBy;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            observation.Missing = $"Revit could not measure it: {ex.Message.TrimEnd('.')}.";
        }
        return observation;
    }

    private static void MeasureConnector(Document doc, ModelFamilyFlexCheck check, ModelFamilyFlexTargets targets, ModelFamilyFlexObservation observation)
    {
        if (targets.Connector is not { } id || doc.GetElement(new ElementId(id)) is not ConnectorElement connector)
        {
            observation.Missing = $"The connector {targets.Connector} no longer exists.";
            return;
        }
        observation.ConnectorId = connector.Id.Value;
        try { observation.Point = Mm(connector.Origin); } catch { /* reported by the evaluator */ }
        try { observation.Direction = Vector(connector.CoordinateSystem.BasisZ); } catch { /* reported by the evaluator */ }
        var classification = ModelFamilyConnectorRevit.Classification(connector);
        observation.SystemType = classification.Length == 0 ? null : ModelFamilyConnectorTables.ClassificationWireName(classification);
        observation.Domain = ModelFamilyConnectorRevit.DomainWireName(connector);
        foreach (var pair in check.Values)
        {
            var parameter = ModelFamilyAssociations.ElementParameter(connector, pair.Key);
            if (parameter != null) observation.Values[pair.Key] = ModelFamilyConnectorRevit.ReadValue(doc, parameter).Value;
        }
        if (targets.OnFaceElement is { } formId)
        {
            if (doc.GetElement(new ElementId(formId)) is { } form)
            {
                observation.Target = FaceOf(form, check.OnFaceLabel!);
                if (observation.Target == null) observation.TargetMissing = $"Element {formId} has no '{check.OnFaceLabel}' face in this case.";
            }
            else observation.TargetMissing = $"Element {formId} no longer exists.";
        }
    }

    public static ModelFamilyFlexPlane PlaneOf(ReferencePlane plane)
    {
        var geometry = plane.GetPlane();
        return new ModelFamilyFlexPlane { Point = Mm(geometry.Origin), Normal = Vector(geometry.Normal) };
    }

    public static ModelFamilyFlexPlane? FaceOf(Element element, string label)
    {
        var face = ModelFamilyFormFaces.Find(element, label);
        return face == null ? null : new ModelFamilyFlexPlane { Point = Mm(face.Point), Normal = Vector(face.Normal) };
    }

    /// <summary>A nested instance's insertion point (its location point, else its transform's origin), mm.</summary>
    public static double[] PositionOf(FamilyInstance instance) =>
        instance.Location is LocationPoint point ? Mm(point.Point) : Mm(instance.GetTotalTransform().Origin);

    public static double[] Mm(XYZ point) => new[] { ModelFamilyJson.ToMm(point.X), ModelFamilyJson.ToMm(point.Y), ModelFamilyJson.ToMm(point.Z) };

    public static double[] Vector(XYZ vector) => new[] { Math.Round(vector.X, 9), Math.Round(vector.Y, 9), Math.Round(vector.Z, 9) };

    /// <summary>
    /// The bounding box of the solids of <paramref name="elements"/>, from the tessellated edges of every solid with
    /// volume (nested symbol geometry transformed into place), optionally taken into another coordinate system by
    /// <paramref name="toLocal"/>. Millimetres; null when there is no solid.
    /// </summary>
    public static (double[] Min, double[] Max)? Box(IEnumerable<Element> elements, Transform? toLocal)
    {
        var min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
        var max = new[] { double.MinValue, double.MinValue, double.MinValue };
        var any = false;
        foreach (var element in elements)
        {
            GeometryElement? geometry;
            try { geometry = element.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }); }
            catch { continue; }
            if (geometry == null) continue;
            foreach (var (solid, transform) in Solids(geometry, Transform.Identity))
            {
                foreach (Edge edge in solid.Edges)
                {
                    IList<XYZ> points;
                    try { points = edge.Tessellate(); }
                    catch { continue; }
                    foreach (var raw in points)
                    {
                        var point = transform.OfPoint(raw);
                        if (toLocal != null) point = toLocal.OfPoint(point);
                        var mm = new[] { point.X * ModelFamilyJson.MmPerFoot, point.Y * ModelFamilyJson.MmPerFoot, point.Z * ModelFamilyJson.MmPerFoot };
                        for (var a = 0; a < 3; a++)
                        {
                            min[a] = Math.Min(min[a], mm[a]);
                            max[a] = Math.Max(max[a], mm[a]);
                        }
                        any = true;
                    }
                }
            }
        }
        if (!any) return null;
        return (min.Select(v => Math.Round(v, 4)).ToArray(), max.Select(v => Math.Round(v, 4)).ToArray());
    }

    /// <summary>The solids with volume of a geometry element, each with the transform that puts it in place.</summary>
    public static IEnumerable<(Solid Solid, Transform Transform)> Solids(GeometryElement geometry, Transform transform)
    {
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid solid when SafeVolume(solid) > 1e-9:
                    yield return (solid, transform);
                    break;
                case GeometryInstance instance:
                {
                    GeometryElement? symbol = null;
                    Transform? inner = null;
                    try
                    {
                        symbol = instance.GetSymbolGeometry();
                        inner = transform.Multiply(instance.Transform);
                    }
                    catch { /* no symbol geometry */ }
                    if (symbol != null && inner != null)
                        foreach (var nested in Solids(symbol, inner)) yield return nested;
                    break;
                }
            }
        }
    }

    private static double SafeVolume(Solid solid)
    {
        try { return solid.Volume; }
        catch { return 0; }
    }

    // ── Revit failures of a refused case ────────────────────────────────────

    /// <summary>
    /// The failures of a case as the result reports them: message, severity, element ids with descriptions, and the
    /// dimensions and alignments among them with their labels (the constraints Revit could not satisfy).
    /// </summary>
    public static JArray DescribeFailures(Document doc, IEnumerable<ModelFamilyFailure> failures)
    {
        var rows = new JArray();
        foreach (var failure in failures)
        {
            var elements = new JArray();
            var constraints = new JArray();
            foreach (var id in failure.ElementIds.Take(20))
            {
                Element? element = null;
                try { element = doc.GetElement(new ElementId(id)); }
                catch { /* gone */ }
                if (element == null)
                {
                    elements.Add(new JObject { ["id"] = id, ["element"] = null });
                    continue;
                }
                elements.Add(new JObject { ["id"] = id, ["element"] = ModelFamilyLookup.Describe(element) });
                if (element is Dimension dimension)
                {
                    try { constraints.Add(ModelFamilyReferenceInspect.DimensionRow(doc, dimension)); }
                    catch { constraints.Add(new JObject { ["id"] = id }); }
                }
            }
            rows.Add(new JObject
            {
                ["message"] = failure.Message,
                ["severity"] = failure.Severity,
                ["elementIds"] = new JArray(failure.ElementIds),
                ["elements"] = elements,
                ["constraints"] = constraints
            });
        }
        return rows;
    }
}

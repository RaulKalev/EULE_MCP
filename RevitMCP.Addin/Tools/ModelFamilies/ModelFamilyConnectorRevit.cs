using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>A host a connector is created on: the face (or plane) reference and how to describe it.</summary>
internal sealed class ModelFamilyConnectorHost
{
    public Reference Reference { get; init; } = null!;
    public JObject Describe { get; init; } = new();
    public bool IsPlane { get; init; }
}

/// <summary>
/// The Revit side of the connector feature (#174): host faces, creation, parameter values in display units, and the
/// read-back rows the edit result and the <c>connectors</c> inspection section return.
/// </summary>
internal static class ModelFamilyConnectorRevit
{
    /// <summary>0.3 mm in feet: how close an origin must be to a face or plane to count as hosted on it.</summary>
    private const double OnPlaneTolerance = 0.001;

    // ── Hosts ────────────────────────────────────────────────────────────────

    /// <summary>The planar faces of an element's solids that have a reference, with outward normal and origin.</summary>
    public static List<PlanarFace> PlanarFaces(Element element)
    {
        var faces = new List<PlanarFace>();
        GeometryElement? geometry;
        try { geometry = element.get_Geometry(new Options { ComputeReferences = true, IncludeNonVisibleObjects = false }); }
        catch { return faces; }
        if (geometry == null) return faces;
        foreach (var obj in geometry)
        {
            if (obj is not Solid solid || solid.Faces.IsEmpty) continue;
            foreach (Face face in solid.Faces)
                if (face is PlanarFace planar && planar.Reference != null) faces.Add(planar);
        }
        return faces;
    }

    private static double[] Arr(XYZ v) => new[] { v.X, v.Y, v.Z };

    /// <summary>Resolves a host selector to a reference; problems name the element's faces when none fits.</summary>
    public static ModelFamilyConnectorHost? ResolveHost(Document doc, ConnectorHostSpec spec, string source, List<string> problems, List<string> warnings)
    {
        switch (spec.Kind)
        {
            case ConnectorHostKind.ElementFace:
            {
                var element = doc.GetElement(new ElementId(spec.ElementId));
                if (element == null)
                {
                    problems.Add($"{source}.element: the family has no element {spec.ElementId}.");
                    return null;
                }
                var faces = PlanarFaces(element);
                if (faces.Count == 0)
                {
                    problems.Add($"{source}.element: {ModelFamilyLookup.Describe(element)} has no planar faces to host a connector; give a solid form (revit_get_model_family lists them).");
                    return null;
                }
                var candidates = faces.Select(f => (Arr(f.FaceNormal), Arr(f.Origin))).ToList();
                var (index, matches) = ModelFamilyConnectorTables.PickFace(candidates, spec.FaceNormal);
                var wanted = spec.FaceLabel ?? $"[{string.Join(", ", spec.FaceNormal.Select(n => n.ToString("0.###", CultureInfo.InvariantCulture)))}]";
                if (index < 0)
                {
                    var listed = faces.Select(f => ModelFamilyConnectorTables.LabelOf(Arr(f.FaceNormal)) is { } label
                            ? label
                            : $"[{string.Join(", ", Arr(f.FaceNormal).Select(n => n.ToString("0.###", CultureInfo.InvariantCulture)))}]")
                        .Distinct().ToList();
                    problems.Add($"{source}.face: {ModelFamilyLookup.Describe(element)} has no planar face facing {wanted} (within {ModelFamilyConnectorTables.FaceToleranceDegrees}°). Its planar faces face: {string.Join(", ", listed)}.");
                    return null;
                }
                var face = faces[index];
                if (matches > 1)
                    warnings.Add($"{source}: {matches} faces of element {spec.ElementId} face {wanted}; the outermost one was used. Pass {{\"reference\": …}} to choose another.");
                return new ModelFamilyConnectorHost
                {
                    Reference = face.Reference,
                    Describe = new JObject
                    {
                        ["kind"] = "face",
                        ["element"] = element.Id.Value,
                        ["face"] = ModelFamilyConnectorTables.LabelOf(Arr(face.FaceNormal)),
                        ["normal"] = ModelFamilyLookup.Direction(face.FaceNormal),
                        ["reference"] = Stable(doc, face.Reference)
                    }
                };
            }
            case ConnectorHostKind.StableReference:
            {
                Reference? reference = null;
                try { reference = Reference.ParseFromStableRepresentation(doc, spec.StableReference); }
                catch { /* reported below */ }
                var owner = reference == null ? null : doc.GetElement(reference.ElementId);
                GeometryObject? geometry = null;
                try { geometry = owner?.GetGeometryObjectFromReference(reference); }
                catch { /* reported below */ }
                if (reference == null || geometry is not PlanarFace planar)
                {
                    problems.Add($"{source}.reference: '{spec.StableReference}' is not a planar face of this family; use {{\"element\": <form id>, \"face\": \"back\"}}.");
                    return null;
                }
                return new ModelFamilyConnectorHost
                {
                    Reference = reference,
                    Describe = new JObject
                    {
                        ["kind"] = "face",
                        ["element"] = owner!.Id.Value,
                        ["face"] = ModelFamilyConnectorTables.LabelOf(Arr(planar.FaceNormal)),
                        ["normal"] = ModelFamilyLookup.Direction(planar.FaceNormal),
                        ["reference"] = spec.StableReference
                    }
                };
            }
            default:
            {
                var plane = ModelFamilyLookup.ReferencePlane(doc, spec.Plane, source, problems);
                if (plane == null) return null;
                return new ModelFamilyConnectorHost
                {
                    Reference = plane.GetReference(),
                    IsPlane = true,
                    Describe = new JObject { ["kind"] = "plane", ["plane"] = plane.Name, ["id"] = plane.Id.Value }
                };
            }
        }
    }

    private static string? Stable(Document doc, Reference reference)
    {
        try { return reference.ConvertToStableRepresentation(doc); }
        catch { return null; }
    }

    /// <summary>Creates the connector the spec asks for on <paramref name="host"/>. Call inside a transaction; Revit's refusal is thrown.</summary>
    public static ConnectorElement Create(Document doc, ConnectorCreateSpec spec, Reference host)
    {
        return spec.Domain switch
        {
            ConnectorDomain.Electrical => ConnectorElement.CreateElectricalConnector(doc, ParseEnum<ElectricalSystemType>(spec.System!.CreateMember), host),
            ConnectorDomain.Duct => ConnectorElement.CreateDuctConnector(doc, ParseEnum<DuctSystemType>(spec.System!.CreateMember), ParseEnum<ConnectorProfileType>(spec.Shape!), host),
            ConnectorDomain.Pipe => ConnectorElement.CreatePipeConnector(doc, ParseEnum<PipeSystemType>(spec.System!.CreateMember), host),
            ConnectorDomain.Conduit => ConnectorElement.CreateConduitConnector(doc, host),
            _ => ConnectorElement.CreateCableTrayConnector(doc, host)
        };
    }

    public static T ParseEnum<T>(string member) where T : struct => (T)Enum.Parse(typeof(T), member, ignoreCase: false);

    // ── Domain and identity ──────────────────────────────────────────────────

    public static string ShapeName(ConnectorElement connector)
    {
        try { return connector.Shape.ToString(); }
        catch { return "Invalid"; }
    }

    public static string DomainWireName(ConnectorElement connector)
    {
        string domain;
        try { domain = connector.Domain.ToString(); }
        catch { domain = "DomainUndefined"; }
        return ModelFamilyConnectorTables.DomainWireName(domain, ShapeName(connector));
    }

    public static ConnectorDomain? Domain(ConnectorElement connector) =>
        ModelFamilyConnectorTables.Domains.TryGetValue(DomainWireName(connector), out var domain) ? domain : null;

    public static string Classification(ConnectorElement connector)
    {
        try { return connector.SystemClassification.ToString(); }
        catch { return string.Empty; }
    }

    public static List<ConnectorElement> All(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ConnectorElement)).Cast<ConnectorElement>().OrderBy(c => c.Id.Value).ToList();

    public static ConnectorElement? Linked(ConnectorElement connector)
    {
        try { return connector.GetLinkedConnectorElement(); }
        catch { return null; }
    }

    // ── Parameter values ─────────────────────────────────────────────────────

    /// <summary>The spec key of <see cref="ModelFamilyConnectorUnits"/> for a parameter's data type, or null.</summary>
    public static string? SpecKey(Parameter parameter)
    {
        ForgeTypeId? type;
        try { type = parameter.Definition.GetDataType(); }
        catch { return null; }
        if (type == null || string.IsNullOrEmpty(type.TypeId)) return null;
        if (type.Equals(SpecTypeId.Length)) return "length";
        if (type.Equals(SpecTypeId.ElectricalPotential)) return "electricalPotential";
        if (type.Equals(SpecTypeId.ApparentPower)) return "apparentPower";
        if (type.Equals(SpecTypeId.ElectricalPower)) return "electricalPower";
        if (type.Equals(SpecTypeId.Current)) return "current";
        if (type.Equals(SpecTypeId.AirFlow)) return "airFlow";
        if (type.Equals(SpecTypeId.Flow)) return "flow";
        if (type.Equals(SpecTypeId.Angle)) return "angle";
        if (type.Equals(SpecTypeId.HvacPressure)) return "hvacPressure";
        if (type.Equals(SpecTypeId.PipingPressure)) return "pipingPressure";
        if (type.Equals(SpecTypeId.Number) || type.Equals(SpecTypeId.Int.Integer)) return "number";
        return null;
    }

    private static ForgeTypeId? UnitOf(string key) => key switch
    {
        "length" => UnitTypeId.Millimeters,
        "electricalPotential" => UnitTypeId.Volts,
        "apparentPower" => UnitTypeId.VoltAmperes,
        "electricalPower" => UnitTypeId.Watts,
        "current" => UnitTypeId.Amperes,
        "airFlow" or "flow" => UnitTypeId.LitersPerSecond,
        "angle" => UnitTypeId.Degrees,
        "hvacPressure" or "pipingPressure" => UnitTypeId.Pascals,
        _ => null
    };

    private static bool IsYesNo(Parameter parameter)
    {
        try { return parameter.Definition.GetDataType().Equals(SpecTypeId.Boolean.YesNo); }
        catch { return false; }
    }

    public static string BuiltInName(Parameter parameter)
    {
        try
        {
            var builtIn = (parameter.Definition as InternalDefinition)?.BuiltInParameter ?? BuiltInParameter.INVALID;
            return builtIn == BuiltInParameter.INVALID ? string.Empty : builtIn.ToString();
        }
        catch { return string.Empty; }
    }

    public static string NameOf(Parameter parameter)
    {
        try { return parameter.Definition?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    /// <summary>A parameter's value in its display unit: numbers converted, yes/no as true/false, enums and load classifications by name.</summary>
    public static (JToken Value, string? Unit) ReadValue(Document doc, Parameter parameter)
    {
        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                {
                    var key = SpecKey(parameter);
                    if (key == "number") return (Round(parameter.AsDouble()), null);
                    if (key != null && UnitOf(key) is { } unit)
                        return (Round(UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), unit)), ModelFamilyConnectorUnits.DisplayUnit(key));
                    return (new JValue(parameter.AsValueString()), null);
                }
                case StorageType.Integer:
                {
                    var value = parameter.AsInteger();
                    if (IsYesNo(parameter)) return (new JValue(value != 0), null);
                    if (ModelFamilyConnectorTables.EnumParameters.TryGetValue(BuiltInName(parameter), out var table))
                    {
                        var member = EnumMemberName(table.Enum, value);
                        var wire = table.Values.FirstOrDefault(v => string.Equals(v.Value, member, StringComparison.Ordinal)).Key;
                        return (new JValue(wire ?? member ?? value.ToString(CultureInfo.InvariantCulture)), null);
                    }
                    return (new JValue(value), null);
                }
                case StorageType.String:
                    return (new JValue(parameter.AsString()), null);
                case StorageType.ElementId:
                {
                    var id = parameter.AsElementId();
                    if (id == null || id == ElementId.InvalidElementId) return (JValue.CreateNull(), null);
                    var element = doc.GetElement(id);
                    return element is ElectricalLoadClassification classification ? (new JValue(classification.Name), null) : (new JValue(id.Value), null);
                }
            }
        }
        catch
        {
            // A value Revit cannot give is left empty.
        }
        return (JValue.CreateNull(), null);
    }

    private static JValue Round(double value) => new(Math.Round(value, 6));

    private static string? EnumMemberName(string enumName, int value)
    {
        try
        {
            return enumName switch
            {
                "FlowDirectionType" => ((FlowDirectionType)value).ToString(),
                "DuctFlowConfigurationType" => ((DuctFlowConfigurationType)value).ToString(),
                "PipeFlowConfigurationType" => ((PipeFlowConfigurationType)value).ToString(),
                _ => null
            };
        }
        catch { return null; }
    }

    private static int? EnumValue(string enumName, string member)
    {
        try
        {
            return enumName switch
            {
                "FlowDirectionType" => (int)ParseEnum<FlowDirectionType>(member),
                "DuctFlowConfigurationType" => (int)ParseEnum<DuctFlowConfigurationType>(member),
                "PipeFlowConfigurationType" => (int)ParseEnum<PipeFlowConfigurationType>(member),
                _ => null
            };
        }
        catch { return null; }
    }

    /// <summary>The electrical load classifications of the family document, by name.</summary>
    public static List<ElectricalLoadClassification> LoadClassifications(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ElectricalLoadClassification)).Cast<ElectricalLoadClassification>().ToList();

    /// <summary>
    /// Sets a connector parameter from a wire value (display unit, or <c>{value, unit}</c>). Call inside a transaction.
    /// Returns the row to report (requested, stored, unit) or an error that says what to pass instead.
    /// </summary>
    public static (JObject? Row, string? Error) SetValue(Document doc, Parameter parameter, ConnectorValue value)
    {
        var name = NameOf(parameter);
        if (parameter.IsReadOnly) return (null, $"'{name}' is read-only on this connector");
        string? unit = null;
        double? requestedDisplay = null;
        bool ok;
        try
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                {
                    var number = value.Number ?? (value.Text != null && double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : (double?)null);
                    if (number == null) return (null, $"'{name}' takes a number{(UnitHint(parameter) is { } hint ? $" in {hint}" : string.Empty)}, or {{\"value\": number, \"unit\": \"…\"}}");
                    var key = SpecKey(parameter);
                    var (display, error) = ModelFamilyConnectorUnits.ToDefault(key, number.Value, value.Unit);
                    if (error != null) return (null, $"'{name}': {error}");
                    requestedDisplay = display;
                    unit = ModelFamilyConnectorUnits.DisplayUnit(key);
                    var internalValue = key == "number" || UnitOf(key!) is not { } unitId ? display : UnitUtils.ConvertToInternalUnits(display, unitId);
                    ok = parameter.Set(internalValue);
                    break;
                }
                case StorageType.Integer:
                {
                    int? integer = null;
                    if (value.Flag != null) integer = value.Flag.Value ? 1 : 0;
                    else if (value.Number != null) integer = (int)Math.Round(value.Number.Value);
                    else if (value.Text != null)
                    {
                        if (ModelFamilyConnectorTables.EnumParameters.TryGetValue(BuiltInName(parameter), out var table))
                        {
                            if (!table.Values.TryGetValue(value.Text, out var member) || EnumValue(table.Enum, member) is not { } enumValue)
                                return (null, $"'{name}' takes one of {string.Join(", ", table.Values.Keys)}");
                            integer = enumValue;
                        }
                        else if (int.TryParse(value.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt)) integer = parsedInt;
                        else if (bool.TryParse(value.Text, out var parsedBool)) integer = parsedBool ? 1 : 0;
                    }
                    if (integer == null) return (null, $"'{name}' takes {(IsYesNo(parameter) ? "true or false" : "a whole number")}");
                    ok = parameter.Set(integer.Value);
                    break;
                }
                case StorageType.String:
                    ok = parameter.Set(value.Text ?? (value.Number != null ? value.Number.Value.ToString(CultureInfo.InvariantCulture) : value.Flag?.ToString() ?? string.Empty));
                    break;
                case StorageType.ElementId:
                {
                    ElementId? id = null;
                    if (BuiltInName(parameter) == nameof(BuiltInParameter.RBS_ELEC_LOAD_CLASSIFICATION) && value.Text != null)
                    {
                        var classifications = LoadClassifications(doc);
                        var match = classifications.FirstOrDefault(c => string.Equals(c.Name, value.Text, StringComparison.OrdinalIgnoreCase));
                        if (match == null)
                            return (null, classifications.Count == 0
                                ? $"'{name}': the family document has no load classifications, so '{value.Text}' cannot be set; leave it to the project, or use a family template that has them"
                                : $"'{name}': '{value.Text}' is not a load classification of the family; use {string.Join(", ", classifications.Select(c => $"'{c.Name}'"))}");
                        id = match.Id;
                    }
                    else if (value.Number != null) id = new ElementId((long)Math.Round(value.Number.Value));
                    if (id == null) return (null, $"'{name}' takes an element id{(BuiltInName(parameter) == nameof(BuiltInParameter.RBS_ELEC_LOAD_CLASSIFICATION) ? " or a load classification name" : string.Empty)}");
                    ok = parameter.Set(id);
                    break;
                }
                default:
                    return (null, $"'{name}' cannot be set");
            }
        }
        catch (Exception ex)
        {
            return (null, $"Revit refused '{name}': {ex.Message.TrimEnd('.')}");
        }
        if (!ok) return (null, $"Revit did not take the value of '{name}'");

        var (stored, storedUnit) = ReadValue(doc, parameter);
        var row = new JObject
        {
            ["requested"] = value.ToJson(),
            ["stored"] = stored,
            ["unit"] = storedUnit ?? unit
        };
        if (requestedDisplay != null && stored.Type is JTokenType.Float or JTokenType.Integer)
        {
            var storedNumber = stored.Value<double>();
            row["matches"] = Math.Abs(storedNumber - requestedDisplay.Value) <= 1e-6 * Math.Max(1, Math.Abs(requestedDisplay.Value));
        }
        return (row, null);
    }

    private static string? UnitHint(Parameter parameter) =>
        ModelFamilyConnectorUnits.DisplayUnit(SpecKey(parameter)) is { Length: > 0 } unit ? unit : null;

    /// <summary>Sets a length parameter in millimetres; an error when the connector has no such settable parameter.</summary>
    public static string? SetLength(ConnectorElement connector, BuiltInParameter builtIn, double mm)
    {
        Parameter? parameter;
        try { parameter = connector.get_Parameter(builtIn); }
        catch { parameter = null; }
        if (parameter == null) return $"the connector has no {builtIn} parameter";
        if (parameter.IsReadOnly) return $"{builtIn} is read-only on this connector{(ModelFamilyAssociations.Associated(connector.Document, parameter) is { } driver ? $" (it is associated with '{driver.Definition.Name}')" : string.Empty)}";
        try { return parameter.Set(ModelFamilyJson.ToFeet(mm)) ? null : $"Revit did not take {builtIn} = {mm} mm"; }
        catch (Exception ex) { return $"Revit refused {builtIn} = {mm} mm: {ex.Message.TrimEnd('.')}"; }
    }

    /// <summary>The names of a connector's settable parameters, for a problem that lists what can be set.</summary>
    public static List<string> SettableNames(Element element)
    {
        var names = new List<string>();
        foreach (Parameter parameter in element.Parameters)
        {
            try
            {
                if (parameter.IsReadOnly) continue;
                var builtIn = BuiltInName(parameter);
                names.Add(builtIn.Length > 0 ? $"{NameOf(parameter)} ({builtIn})" : NameOf(parameter));
            }
            catch { /* a parameter without a definition */ }
        }
        return names.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> AssociableNames(Document doc, Element element) =>
        ModelFamilyAssociations.Associable(doc, element)
            .Select(r => (string?)r["builtIn"] is { } builtIn ? $"{(string?)r["name"]} ({builtIn})" : (string?)r["name"] ?? string.Empty)
            .Where(n => n.Length > 0).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    // ── Read-back ────────────────────────────────────────────────────────────

    /// <summary>Faces and planes a connector origin can be matched against, built once per read.</summary>
    internal sealed class HostIndex
    {
        private readonly Document _doc;
        private List<(Element Element, PlanarFace Face)>? _faces;
        private List<ReferencePlane>? _planes;

        public HostIndex(Document doc) => _doc = doc;

        private List<(Element, PlanarFace)> Faces => _faces ??= new FilteredElementCollector(_doc).WhereElementIsNotElementType()
            .Where(e => e is GenericForm or FreeFormElement)
            .SelectMany(e => PlanarFaces(e).Select(f => (e, f))).ToList();

        private List<ReferencePlane> Planes => _planes ??= ModelFamilyLookup.ReferencePlanes(_doc);

        /// <summary>
        /// The face or reference plane a connector sits on, found from its origin and direction (Revit does not
        /// expose a connector's host): <c>{kind: face, element, face, normal}</c>, <c>{kind: plane, plane, id}</c>
        /// or <c>{kind: unknown}</c>. <c>inferred</c> is always true.
        /// </summary>
        public JObject Find(XYZ origin, XYZ direction)
        {
            foreach (var (element, face) in Faces)
            {
                try
                {
                    var normal = face.FaceNormal;
                    if (Math.Abs(normal.DotProduct(direction)) < Math.Cos(ModelFamilyConnectorTables.FaceToleranceDegrees * Math.PI / 180)) continue;
                    if (Math.Abs((origin - face.Origin).DotProduct(normal)) > OnPlaneTolerance) continue;
                    var projection = face.Project(origin);
                    if (projection == null || projection.Distance > OnPlaneTolerance) continue;
                    return new JObject
                    {
                        ["kind"] = "face",
                        ["element"] = element.Id.Value,
                        ["face"] = ModelFamilyConnectorTables.LabelOf(Arr(normal)),
                        ["normal"] = ModelFamilyLookup.Direction(normal),
                        ["inferred"] = true
                    };
                }
                catch { /* a face Revit cannot measure */ }
            }
            foreach (var plane in Planes)
            {
                try
                {
                    var geometry = plane.GetPlane();
                    if (Math.Abs(geometry.Normal.DotProduct(direction)) < Math.Cos(ModelFamilyConnectorTables.FaceToleranceDegrees * Math.PI / 180)) continue;
                    if (Math.Abs((origin - geometry.Origin).DotProduct(geometry.Normal)) > OnPlaneTolerance) continue;
                    return new JObject { ["kind"] = "plane", ["plane"] = plane.Name, ["id"] = plane.Id.Value, ["inferred"] = true };
                }
                catch { /* a plane Revit cannot measure */ }
            }
            return new JObject { ["kind"] = "unknown", ["inferred"] = true };
        }
    }

    /// <summary>
    /// One connector as the edit result and the inspection report it: id, description, domain, system type,
    /// shape, size (mm), origin (mm), direction, host, primary, linked connector, settable property values and
    /// associations.
    /// </summary>
    public static JObject Row(Document doc, ConnectorElement connector, HostIndex hosts)
    {
        var row = new JObject { ["id"] = connector.Id.Value };
        var description = connector.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
        row["description"] = string.IsNullOrEmpty(description) ? null : description;
        row["domain"] = DomainWireName(connector);
        var classification = Classification(connector);
        row["systemType"] = classification.Length == 0 ? null : ModelFamilyConnectorTables.ClassificationWireName(classification);
        row["classification"] = classification.Length == 0 ? null : classification;
        var shape = ShapeName(connector);
        row["shape"] = shape == "Invalid" ? null : shape.ToLowerInvariant();
        var size = new JObject();
        try { if (connector.Radius > 0) size["diameter"] = ModelFamilyJson.ToMm(connector.Radius * 2); } catch { /* not round */ }
        try { if (connector.Width > 0) size["width"] = ModelFamilyJson.ToMm(connector.Width); } catch { /* round */ }
        try { if (connector.Height > 0) size["height"] = ModelFamilyJson.ToMm(connector.Height); } catch { /* round */ }
        row["size"] = size.Count == 0 ? null : size;
        XYZ? origin = null, direction = null;
        try { origin = connector.Origin; } catch { /* reported as null */ }
        try { direction = connector.CoordinateSystem.BasisZ; } catch { /* reported as null */ }
        row["origin"] = origin == null ? null : ModelFamilyLookup.Mm(origin);
        row["direction"] = direction == null ? null : ModelFamilyLookup.Direction(direction);
        row["host"] = origin != null && direction != null ? hosts.Find(origin, direction) : null;
        try { row["isPrimary"] = connector.IsPrimary; } catch { row["isPrimary"] = null; }
        row["linkedConnector"] = Linked(connector)?.Id.Value;

        var properties = new JObject();
        foreach (Parameter parameter in connector.Parameters)
        {
            try
            {
                // A parameter driven by a family parameter may read as read-only; its value (the current type's) is
                // still what a flex check needs, so it stays in the row.
                if (parameter.IsReadOnly && ModelFamilyAssociations.Associated(doc, parameter) == null) continue;
                var name = NameOf(parameter);
                if (name.Length == 0 || properties.ContainsKey(name)) continue;
                var (value, unit) = ReadValue(doc, parameter);
                var item = new JObject { ["value"] = value };
                if (!string.IsNullOrEmpty(unit)) item["unit"] = unit;
                var builtIn = BuiltInName(parameter);
                if (builtIn.Length > 0) item["builtIn"] = builtIn;
                properties[name] = item;
            }
            catch { /* a parameter Revit cannot describe */ }
        }
        row["properties"] = properties;

        var associations = new JObject();
        var eligible = new List<string>();
        foreach (var associable in ModelFamilyAssociations.Associable(doc, connector))
        {
            if ((string?)associable["name"] is not { Length: > 0 } name) continue;
            eligible.Add((string?)associable["builtIn"] is { } builtIn ? $"{name} ({builtIn})" : name);
            if ((string?)associable["associatedWith"] is { } family) associations[name] = family;
        }
        row["associations"] = associations;
        // The issue asks for the eligible associations too: what this connector's parameters can be driven by.
        row["associable"] = new JArray(eligible.Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        return row;
    }

    /// <summary>The <c>connectors</c> inspection section: every connector of the family, up to <paramref name="limit"/>.</summary>
    public static JToken Inspect(Document doc, int limit)
    {
        var all = All(doc);
        var hosts = new HostIndex(doc);
        return new JObject
        {
            ["count"] = all.Count,
            ["truncated"] = all.Count > limit,
            ["connectors"] = new JArray(all.Take(Math.Max(0, limit)).Select(c => Row(doc, c, hosts)))
        };
    }
}

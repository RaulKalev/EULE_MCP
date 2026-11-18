using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// revit_check_model_family_instances (#176): checks placed instances of a loaded family in a project against the same
/// expectations as a flex. Geometry is read in the instance's own coordinate system (the inverse of
/// <c>GetTotalTransform</c>), so expectations are written in family coordinates whatever the instance's position and
/// rotation. Reads only.
/// </summary>
internal static class ModelFamilyFlexInstances
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        if (doc.IsFamilyDocument)
            return Result(request, sw, false, "validation_failed",
                $"'{doc.Title}' is a family document; this checks placed instances in a project. Pass document=<project title>, or flex the family itself with revit_flex_model_family.", null, new List<string>());

        var args = request.Arguments;
        var problems = new List<string>();
        var familyName = ModelFamilyJson.Token(args, ModelFamilyFlexArgs.Family) is { Type: JTokenType.String } familyToken ? ((string?)familyToken ?? string.Empty).Trim() : string.Empty;
        var ids = Ids(ModelFamilyJson.Token(args, ModelFamilyFlexArgs.ElementIds), problems);
        var types = ModelFamilyFlexSpec.Names(args, ModelFamilyFlexArgs.Types, problems);
        var checks = ModelFamilyFlexChecks.Parse(ModelFamilyJson.Objects(args, ModelFamilyFlexArgs.Checks, problems), ModelFamilyFlexScope.Instance, problems);
        if (checks.Count == 0 && !problems.Any(p => p.StartsWith("checks", StringComparison.Ordinal)))
            problems.Add("checks is required: what to verify on each instance (boundingBox, centred, parameter, material, connector, hosted, count).");
        if (familyName.Length == 0 && ids.Count == 0)
            problems.Add("family or elementIds is required: the loaded family whose instances to check, or the instances themselves.");

        Family? family = null;
        if (familyName.Length > 0)
        {
            var loaded = FamilyInventory.LoadedByName(doc);
            if (!loaded.TryGetValue(familyName, out family))
            {
                family = loaded.FirstOrDefault(p => string.Equals(p.Key, familyName, StringComparison.OrdinalIgnoreCase)).Value;
                if (family == null)
                {
                    var close = ModelFamilyJson.Suggest(familyName, loaded.Keys);
                    problems.Add($"family: '{familyName}' is not loaded in '{doc.Title}'" +
                                 (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". Use revit_list_families to find it."));
                }
            }
        }

        var instances = new List<FamilyInstance>();
        if (ids.Count > 0)
        {
            foreach (var id in ids)
            {
                var element = doc.GetElement(new ElementId(id));
                if (element is not FamilyInstance instance)
                {
                    problems.Add(element == null ? $"elementIds: '{doc.Title}' has no element {id}." : $"elementIds: {id} is a {element.GetType().Name}, not a family instance.");
                    continue;
                }
                if (family != null && instance.Symbol?.Family?.Id != family.Id)
                {
                    problems.Add($"elementIds: {id} is an instance of '{instance.Symbol?.Family?.Name}', not of '{familyName}'.");
                    continue;
                }
                instances.Add(instance);
            }
        }
        else if (family != null)
        {
            instances = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                .Where(i => i.Symbol?.Family?.Id == family.Id).OrderBy(i => i.Id.Value).ToList();
        }

        var familyTypes = family != null
            ? FamilyInventory.Symbols(doc, family).Select(s => FamilyTypeSupport.SafeName(s)).ToList()
            : instances.Select(i => i.Symbol == null ? string.Empty : FamilyTypeSupport.SafeName(i.Symbol)).Distinct().ToList();
        if (types != null && family != null)
        {
            for (var i = 0; i < types.Count; i++)
                if (!familyTypes.Contains(types[i], StringComparer.OrdinalIgnoreCase))
                {
                    var close = ModelFamilyJson.Suggest(types[i], familyTypes);
                    problems.Add($"types[{i}]: '{familyName}' has no type '{types[i]}'" +
                                 (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : $". Types: {string.Join(", ", familyTypes.Select(n => $"'{n}'"))}."));
                }
        }
        foreach (var check in checks.Where(c => c.Kind == "count" && family != null))
            if (!familyTypes.Contains(check.Type!, StringComparer.OrdinalIgnoreCase))
                problems.Add($"{check.Source}.type: '{familyName}' has no type '{check.Type}'.");

        if (problems.Count > 0)
            return Result(request, sw, false, "validation_failed", $"Nothing was checked: {problems.Count} problem(s). {string.Join(" ", problems.Take(3))}", null, problems);

        string TypeOf(FamilyInstance i) => i.Symbol == null ? string.Empty : FamilyTypeSupport.SafeName(i.Symbol);
        if (types != null) instances = instances.Where(i => types.Contains(TypeOf(i), StringComparer.OrdinalIgnoreCase)).ToList();

        var rows = new JArray();
        var allFailures = new List<(string Instance, ModelFamilyFlexFailure Failure)>();
        var perInstanceChecks = checks.Where(c => c.Kind != "count").ToList();
        var passed = 0;
        foreach (var instance in instances)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Transform toFamily;
            try { toFamily = instance.GetTotalTransform().Inverse; }
            catch { toFamily = Transform.Identity; }
            var values = new Dictionary<string, JToken?>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in perInstanceChecks.SelectMany(c => c.ReferencedParameters()).Distinct(StringComparer.OrdinalIgnoreCase))
                if (ParameterOf(instance, name) is { } parameter) values[name] = ReadValue(doc, parameter);

            var failures = new List<ModelFamilyFlexFailure>();
            var measurements = new JObject();
            foreach (var check in perInstanceChecks)
            {
                var observation = Observe(doc, instance, toFamily, check, values);
                measurements[check.Id] = observation.ToJson();
                failures.AddRange(ModelFamilyFlexEvaluator.Evaluate(check, observation, values));
            }
            if (failures.Count == 0) passed++;
            allFailures.AddRange(failures.Select(f => (instance.Id.Value.ToString(), f)));

            var row = new JObject
            {
                ["id"] = instance.Id.Value,
                ["family"] = instance.Symbol?.Family?.Name,
                ["type"] = TypeOf(instance),
                ["level"] = LevelName(doc, instance),
                ["location"] = instance.Location is LocationPoint point ? ModelFamilyFlexMath.Json(ModelFamilyFlexMeasure.Mm(point.Point)) : null,
                ["rotation"] = instance.Location is LocationPoint rotated ? Math.Round(rotated.Rotation * 180 / Math.PI, 4) : null,
                ["status"] = failures.Count == 0 ? "ok" : "failed",
                ["failedChecks"] = new JArray(failures.Select(f => f.ToJson())),
                ["measurements"] = measurements
            };
            if (values.Count > 0)
            {
                var parameterValues = new JObject();
                foreach (var pair in values) parameterValues[pair.Key] = pair.Value?.DeepClone();
                row["parameters"] = parameterValues;
            }
            rows.Add(row);
        }

        var counts = new JArray();
        foreach (var check in checks.Where(c => c.Kind == "count"))
        {
            var count = instances.Count(i => string.Equals(TypeOf(i), check.Type, StringComparison.OrdinalIgnoreCase));
            var failures = ModelFamilyFlexEvaluator.EvaluateCount(check, count);
            counts.Add(new JObject
            {
                ["id"] = check.Id,
                ["type"] = check.Type,
                ["count"] = count,
                ["status"] = failures.Count == 0 ? "ok" : "failed",
                ["failedChecks"] = new JArray(failures.Select(f => f.ToJson()))
            });
        }

        var named = types ?? familyTypes;
        var missingTypes = named.Where(t => !instances.Any(i => string.Equals(TypeOf(i), t, StringComparison.OrdinalIgnoreCase))).ToList();
        var countFailures = counts.Count(c => (string?)c["status"] == "failed");
        var summary = new JObject
        {
            ["instances"] = instances.Count,
            ["passed"] = passed,
            ["failed"] = instances.Count - passed,
            ["countChecksFailed"] = countFailures,
            ["checks"] = new JArray(perInstanceChecks.Select(c => new JObject
            {
                ["id"] = c.Id,
                ["kind"] = c.Kind,
                ["failedInstances"] = allFailures.Where(f => f.Failure.Id == c.Id).Select(f => f.Instance).Distinct().Count()
            }))
        };
        var message = instances.Count == 0
            ? $"No instance{(family != null ? $" of '{family.Name}'" : string.Empty)}{(types != null ? " of the named types" : string.Empty)} is placed in '{doc.Title}'."
            : $"{passed} of {instances.Count} instance(s) pass every check{(countFailures > 0 ? $"; {countFailures} count check(s) failed" : string.Empty)}{(missingTypes.Count > 0 ? $"; no instance of {string.Join(", ", missingTypes.Select(t => $"'{t}'"))}" : string.Empty)}.";
        var data = new JObject
        {
            ["document"] = doc.Title,
            ["family"] = family?.Name,
            ["instances"] = rows,
            ["counts"] = counts,
            ["summary"] = summary,
            ["missingTypes"] = new JArray(missingTypes),
            ["coordinates"] = "Geometry, connector origins and directions are in the instance's own (family) coordinates: the inverse of its total transform; location is in project coordinates.",
            ["units"] = "lengths in mm, angles in degrees, electrical values in volts / VA / amperes / watts"
        };
        return Result(request, sw, true, null, message, data, new List<string>());
    }

    private static List<long> Ids(JToken? token, List<string> problems)
    {
        var ids = new List<long>();
        if (token == null) return ids;
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        for (var i = 0; i < items.Count; i++)
        {
            var id = ModelFamilyJson.AsId(items[i]);
            if (id == null) problems.Add($"elementIds[{i}]: must be an element id.");
            else if (!ids.Contains(id.Value)) ids.Add(id.Value);
        }
        return ids;
    }

    private static ModelFamilyFlexObservation Observe(Document doc, FamilyInstance instance, Transform toFamily, ModelFamilyFlexCheck check,
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
                    var box = ModelFamilyFlexMeasure.Box(new[] { (Element)instance }, toFamily);
                    if (box != null)
                    {
                        observation.Min = box.Value.Min;
                        observation.Max = box.Value.Max;
                    }
                    break;
                }
                case "parameter":
                {
                    var parameter = ParameterOf(instance, check.Parameter!);
                    if (parameter == null) observation.Missing = $"The instance and its type have no parameter '{check.Parameter}'.";
                    else observation.Value = ReadValue(doc, parameter);
                    break;
                }
                case "material":
                    if (check.Parameter != null)
                    {
                        var parameter = ParameterOf(instance, check.Parameter);
                        if (parameter == null) observation.Missing = $"The instance and its type have no parameter '{check.Parameter}'.";
                        else observation.Value = ReadValue(doc, parameter);
                    }
                    else observation.Materials = SolidMaterials(doc, instance);
                    break;
                case "connector":
                    ObserveConnector(instance, toFamily, check, observation);
                    break;
                case "hosted":
                {
                    Element? host = null;
                    try { host = instance.Host; }
                    catch { /* no host */ }
                    observation.Hosted = host != null && host is not Level;
                    observation.Host = host == null ? null : ModelFamilyLookup.Describe(host);
                    try
                    {
                        var placement = instance.Symbol?.Family?.FamilyPlacementType;
                        observation.Placement = placement?.ToString();
                        observation.NeedsHost = placement == FamilyPlacementType.OneLevelBasedHosted;
                    }
                    catch { /* an in-place family */ }
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

    private static void ObserveConnector(FamilyInstance instance, Transform toFamily, ModelFamilyFlexCheck check, ModelFamilyFlexObservation observation)
    {
        var connectors = new List<Connector>();
        try
        {
            var set = instance.MEPModel?.ConnectorManager?.Connectors;
            if (set != null) foreach (Connector connector in set) connectors.Add(connector);
        }
        catch { /* no MEP model */ }
        if (connectors.Count == 0)
        {
            observation.Missing = "The instance has no MEP connectors.";
            return;
        }
        var selector = check.Connector!;
        List<Connector> matches;
        if (selector.Type == JTokenType.Integer)
        {
            var id = selector.Value<long>();
            matches = connectors.Where(c => SafeId(c) == id).ToList();
        }
        else
        {
            var text = (selector.Type == JTokenType.String ? (string?)selector ?? string.Empty : selector.ToString()).Trim();
            matches = connectors.Where(c => string.Equals(SafeDescription(c), text, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) matches = connectors.Where(c => string.Equals(SystemTypeOf(c), text, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (matches.Count != 1)
        {
            var shown = check.Connector?.ToString(Newtonsoft.Json.Formatting.None) ?? "(none)";
            var listing = string.Join(", ", connectors.Select(c => $"{SafeId(c)}{(SafeDescription(c).Length > 0 ? $" '{SafeDescription(c)}'" : string.Empty)} ({SystemTypeOf(c) ?? DomainOf(c)})"));
            observation.Missing = matches.Count == 0
                ? $"No connector of the instance matches {shown}. Connectors: {listing}."
                : $"{matches.Count} connectors match {shown}; use the connector id. Connectors: {listing}.";
            return;
        }
        var match = matches[0];
        observation.ConnectorId = SafeId(match);
        try { observation.Point = ModelFamilyFlexMeasure.Mm(toFamily.OfPoint(match.Origin)); } catch { /* reported by the evaluator */ }
        try { observation.Direction = ModelFamilyFlexMeasure.Vector(toFamily.OfVector(match.CoordinateSystem.BasisZ)); } catch { /* reported by the evaluator */ }
        observation.Domain = DomainOf(match);
        observation.SystemType = SystemTypeOf(match);
        MEPSystem? system = null;
        try { system = match.MEPSystem; }
        catch { /* not in a system */ }
        observation.System = system?.Name;
        var connected = false;
        try { connected = match.IsConnected; }
        catch { /* a logical connector */ }
        observation.Connected = connected || system != null;
        foreach (var pair in check.Values)
        {
            var value = ConnectorValue(match, pair.Key) ?? (ParameterOf(instance, pair.Key) is { } parameter ? ReadValue(instance.Document, parameter) : null);
            if (value != null) observation.Values[pair.Key] = value;
        }
    }

    /// <summary>
    /// A connector parameter of a placed connector by BuiltInParameter name (<c>RBS_ELEC_VOLTAGE</c>,
    /// <c>RBS_ELEC_APPARENT_LOAD</c>), from the family connector information; voltage, apparent load, current and
    /// power in their display units (V, VA, A, W).
    /// </summary>
    private static JToken? ConnectorValue(Connector connector, string name)
    {
        if (!Enum.TryParse<BuiltInParameter>(name.Trim(), ignoreCase: true, out var builtIn) || builtIn == BuiltInParameter.INVALID) return null;
        try
        {
            if (connector.GetMEPConnectorInfo() is not MEPFamilyConnectorInfo info) return null;
            var value = info.GetConnectorParameterValue(new ElementId(builtIn));
            switch (value)
            {
                case DoubleParameterValue number:
                {
                    var text = builtIn.ToString();
                    ForgeTypeId? unit = text.Contains("VOLTAGE") ? UnitTypeId.Volts
                        : text.Contains("APPARENT") ? UnitTypeId.VoltAmperes
                        : text.Contains("CURRENT") ? UnitTypeId.Amperes
                        : text.Contains("POWER") ? UnitTypeId.Watts
                        : null;
                    return new JValue(Math.Round(unit == null ? number.Value : UnitUtils.ConvertFromInternalUnits(number.Value, unit), 6));
                }
                case IntegerParameterValue whole:
                    return new JValue(whole.Value);
                case StringParameterValue text:
                    return new JValue(text.Value);
                default:
                    return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static long SafeId(Connector connector)
    {
        try { return connector.Id; }
        catch { return -1; }
    }

    private static string SafeDescription(Connector connector)
    {
        try { return connector.Description ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static string DomainOf(Connector connector)
    {
        string domain, shape;
        try { domain = connector.Domain.ToString(); }
        catch { domain = "DomainUndefined"; }
        try { shape = connector.Shape.ToString(); }
        catch { shape = string.Empty; }
        return ModelFamilyConnectorTables.DomainWireName(domain, shape);
    }

    /// <summary>The connector's system type as the connector tools name it (<c>powerBalanced</c>, <c>data</c>, <c>supplyAir</c>…).</summary>
    private static string? SystemTypeOf(Connector connector)
    {
        try
        {
            var member = connector.Domain switch
            {
                Autodesk.Revit.DB.Domain.DomainElectrical => connector.ElectricalSystemType.ToString(),
                Autodesk.Revit.DB.Domain.DomainHvac => connector.DuctSystemType.ToString(),
                Autodesk.Revit.DB.Domain.DomainPiping => connector.PipeSystemType.ToString(),
                _ => null
            };
            return member == null ? null : ModelFamilyFlexChecks.SystemTypeWireName(member);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>An instance parameter by name or BuiltInParameter name, else the type's.</summary>
    private static Parameter? ParameterOf(FamilyInstance instance, string name) =>
        ModelFamilyAssociations.ElementParameter(instance, name) ??
        (instance.Symbol == null ? null : ModelFamilyAssociations.ElementParameter(instance.Symbol, name));

    /// <summary>A project parameter value in the wire form: mm, degrees, display units for electrical values, yes/no, names.</summary>
    private static JToken? ReadValue(Document doc, Parameter parameter)
    {
        try
        {
            if (parameter.StorageType == StorageType.ElementId)
            {
                var id = parameter.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) return JValue.CreateNull();
                return doc.GetElement(id) is { } element ? new JValue(element.Name) : new JValue(id.Value);
            }
        }
        catch
        {
            return null;
        }
        return ModelFamilyConnectorRevit.ReadValue(doc, parameter).Value;
    }

    private static List<string> SolidMaterials(Document doc, FamilyInstance instance)
    {
        var names = new List<string>();
        GeometryElement? geometry;
        try { geometry = instance.get_Geometry(new Options { DetailLevel = ViewDetailLevel.Fine }); }
        catch { return names; }
        if (geometry == null) return names;
        foreach (var (solid, _) in ModelFamilyFlexMeasure.Solids(geometry, Transform.Identity))
            foreach (Face face in solid.Faces)
            {
                try
                {
                    if (doc.GetElement(face.MaterialElementId) is Material material && !names.Contains(material.Name, StringComparer.OrdinalIgnoreCase))
                        names.Add(material.Name);
                }
                catch { /* a face without a material */ }
            }
        return names;
    }

    private static string? LevelName(Document doc, FamilyInstance instance)
    {
        try
        {
            if (instance.LevelId != ElementId.InvalidElementId && doc.GetElement(instance.LevelId) is Level level) return level.Name;
            if (instance.Host is Level host) return host.Name;
        }
        catch { /* no level */ }
        return null;
    }

    private static McpToolResult Result(McpToolRequest request, Stopwatch sw, bool success, string? status, string message, JObject? data, List<string> errors)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = success,
            Status = status,
            Message = message,
            Data = data,
            Errors = errors,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}

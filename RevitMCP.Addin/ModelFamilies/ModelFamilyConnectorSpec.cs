using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>What a connector is hosted on (#174).</summary>
public enum ConnectorHostKind
{
    /// <summary>A planar face of a form, chosen by label (<c>back</c>) or normal.</summary>
    ElementFace,
    /// <summary>A face reference in Revit's stable representation.</summary>
    StableReference,
    /// <summary>A reference plane, by name or id: a plane-hosted connector, where Revit allows it.</summary>
    ReferencePlane
}

/// <summary>A host selector: <c>{element, face}</c>, <c>{reference}</c>, or a reference plane name or id.</summary>
public sealed class ConnectorHostSpec
{
    public ConnectorHostKind Kind { get; set; }
    public long ElementId { get; set; }
    /// <summary>left, right, front, back, top or bottom; null when <see cref="FaceNormal"/> is given.</summary>
    public string? FaceLabel { get; set; }
    /// <summary>The wanted outward normal (unit-less), from the label or given.</summary>
    public double[] FaceNormal { get; set; } = Array.Empty<double>();
    public string StableReference { get; set; } = string.Empty;
    /// <summary>The reference plane's name or id as given.</summary>
    public JToken? Plane { get; set; }
}

/// <summary>A connector named by element id, or one created earlier in the same call (<c>{"created": "connectors[0]"}</c>).</summary>
public sealed class ConnectorRef
{
    public long? Id { get; set; }
    public int? CreatedIndex { get; set; }

    public override string ToString() => Id != null ? $"connector {Id}" : $"connectors[{CreatedIndex}]";
}

/// <summary>A property value: a number (in the parameter's display unit, or <see cref="Unit"/>), a text or a yes/no.</summary>
public sealed class ConnectorValue
{
    public double? Number { get; set; }
    public string? Unit { get; set; }
    public string? Text { get; set; }
    public bool? Flag { get; set; }

    public JToken ToJson() => Number != null
        ? Unit != null ? new JObject { ["value"] = Number, ["unit"] = Unit } : new JValue(Number)
        : Flag != null ? new JValue(Flag) : new JValue(Text);
}

/// <summary>The settings a created and an edited connector share.</summary>
public class ConnectorSettings
{
    /// <summary>Where the operation came from, for messages: <c>connectors[0]</c>, <c>edit[1]</c>.</summary>
    public string Source { get; set; } = string.Empty;
    public string? SystemType { get; set; }
    /// <summary>A ConnectorProfileType member (Round, Rectangular, Oval), or null.</summary>
    public string? Shape { get; set; }
    public double? Diameter { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    /// <summary>Millimetres from the family origin.</summary>
    public double[]? Position { get; set; }
    public bool Flip { get; set; }
    public double? Angle { get; set; }
    public bool? Primary { get; set; }
    /// <summary>True when linkTo was given; <see cref="LinkTo"/> null then means "remove the link" (edit).</summary>
    public bool LinkGiven { get; set; }
    public ConnectorRef? LinkTo { get; set; }
    public List<(string Name, ConnectorValue Value)> Properties { get; } = new();
    /// <summary>Connector parameter â†’ family parameter; null dissociates (edit).</summary>
    public List<(string Name, string? FamilyParameter)> Associations { get; } = new();

    public bool HasSize => Diameter != null || Width != null || Height != null;
}

/// <summary>One connector to create.</summary>
public sealed class ConnectorCreateSpec : ConnectorSettings
{
    public int Index { get; set; }
    /// <summary>The caller's label, echoed in the result and written to the connector's description.</summary>
    public string Name { get; set; } = string.Empty;
    public ConnectorDomain Domain { get; set; }
    public ConnectorHostSpec Host { get; set; } = new();
    public ConnectorSystemType? System { get; set; }
}

/// <summary>One existing connector to change.</summary>
public sealed class ConnectorEditSpec : ConnectorSettings
{
    public long ConnectorId { get; set; }
}

/// <summary>The operations of one call, in the order they run: remove, create (connectors), edit.</summary>
public sealed class ConnectorEditPlan
{
    public List<long> Remove { get; } = new();
    public List<ConnectorCreateSpec> Create { get; } = new();
    public List<ConnectorEditSpec> Edit { get; } = new();

    public int Count => Remove.Count + Create.Count + Edit.Count;
}

/// <summary>Parses the arguments of revit_(preview_)edit_model_family_connectors (#174). Pure; bad input becomes problems.</summary>
public static class ModelFamilyConnectorParser
{
    public static readonly string[] CreateKeys =
    {
        "name", "domain", "host", "systemType", "shape", "diameter", "width", "height", "position", "flip", "angle",
        "primary", "linkTo", "properties", "associations"
    };

    public static readonly string[] EditKeys =
    {
        "connector", "systemType", "shape", "diameter", "width", "height", "position", "flip", "angle", "primary",
        "linkTo", "properties", "associations"
    };

    private static readonly Regex CreatedPattern = new(@"^\s*connectors\s*\[\s*(\d+)\s*\]\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads the three operation lists. <paramref name="removeKey"/>, <paramref name="createKey"/> and
    /// <paramref name="editKey"/> are the argument names (remove, connectors, edit).
    /// </summary>
    public static ConnectorEditPlan Parse(IReadOnlyDictionary<string, object?> args, string removeKey, string createKey, string editKey, List<string> problems)
    {
        var plan = new ConnectorEditPlan();
        ParseRemove(ModelFamilyJson.Token(args, removeKey), removeKey, plan.Remove, problems);

        var creates = ModelFamilyJson.Objects(args, createKey, problems);
        for (var i = 0; i < creates.Count; i++)
            if (ParseCreate(creates[i], i, $"{createKey}[{i}]", creates.Count, problems) is { } spec) plan.Create.Add(spec);

        var edits = ModelFamilyJson.Objects(args, editKey, problems);
        for (var i = 0; i < edits.Count; i++)
            if (ParseEdit(edits[i], $"{editKey}[{i}]", creates.Count, problems) is { } spec) plan.Edit.Add(spec);

        foreach (var group in plan.Create.Where(c => c.Primary == true).GroupBy(c => c.Domain).Where(g => g.Count() > 1))
            problems.Add($"{string.Join(", ", group.Select(c => c.Source))}: only one {ModelFamilyConnectorTables.WireName(group.Key)} connector of the family can be primary.");
        foreach (var group in plan.Edit.GroupBy(e => e.ConnectorId).Where(g => g.Count() > 1))
            problems.Add($"{string.Join(", ", group.Select(e => e.Source))}: connector {group.Key} is edited more than once; merge the edits.");
        foreach (var edit in plan.Edit.Where(e => plan.Remove.Contains(e.ConnectorId)))
            problems.Add($"{edit.Source}.connector: connector {edit.ConnectorId} is removed in the same call.");
        foreach (var spec in plan.Create.Concat<ConnectorSettings>(plan.Edit))
            if (spec.LinkTo?.Id is { } linked && plan.Remove.Contains(linked))
                problems.Add($"{spec.Source}.linkTo: connector {linked} is removed in the same call.");

        if (plan.Count == 0 && problems.Count == 0)
            problems.Add($"Nothing to do: give {createKey} (connectors to create), {editKey} (connectors to change) or {removeKey} (connector ids to delete).");
        return plan;
    }

    private static void ParseRemove(JToken? token, string key, List<long> ids, List<string> problems)
    {
        if (token == null) return;
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        for (var i = 0; i < items.Count; i++)
        {
            var id = ModelFamilyJson.AsId(items[i] is JObject obj ? ModelFamilyJson.Get(obj, "connector") ?? ModelFamilyJson.Get(obj, "id") : items[i]);
            if (id == null) problems.Add($"{key}[{i}]: give a connector element id.");
            else if (!ids.Contains(id.Value)) ids.Add(id.Value);
        }
    }

    public static ConnectorCreateSpec? ParseCreate(JObject obj, int index, string source, int createCount, List<string> problems)
    {
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, CreateKeys);
        var spec = new ConnectorCreateSpec { Index = index, Source = source, Name = ModelFamilyJson.Str(obj, "name") };

        var domainText = ModelFamilyJson.Str(obj, "domain");
        if (domainText.Length == 0)
            problems.Add($"{source}.domain is required: {string.Join(", ", ModelFamilyConnectorTables.Domains.Keys)}.");
        else if (!ModelFamilyConnectorTables.Domains.TryGetValue(domainText, out var domain))
            problems.Add($"{source}.domain: '{domainText}' is not a connector domain; use {string.Join(", ", ModelFamilyConnectorTables.Domains.Keys)}.");
        else spec.Domain = domain;
        var domainKnown = problems.Count == before;

        if (ParseHost(ModelFamilyJson.Get(obj, "host"), $"{source}.host", problems) is { } host) spec.Host = host;

        ReadSettings(obj, spec, source, createCount, index, problems);
        if (domainKnown)
        {
            spec.System = ResolveSystemType(spec.Domain, spec.SystemType, source, problems, required: true);
            spec.Shape = ModelFamilyConnectorTables.CheckShapeAndSize(spec.Domain, spec.Shape, spec.Diameter, spec.Width, spec.Height, source, problems);
            if (spec.Angle != null && spec.Domain == ConnectorDomain.Electrical)
                problems.Add($"{source}.angle: an electrical connector has no angle.");
        }
        foreach (var (name, family) in spec.Associations)
            if (family == null) problems.Add($"{source}.associations.{name}: give the family parameter to associate (null only dissociates in edit).");
        if (spec.LinkGiven && spec.LinkTo == null)
            problems.Add($"{source}.linkTo: a new connector has no link to remove; give a connector id or {{\"created\": \"connectors[i]\"}}.");
        return problems.Count == before ? spec : null;
    }

    public static ConnectorEditSpec? ParseEdit(JObject obj, string source, int createCount, List<string> problems)
    {
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, EditKeys);
        var spec = new ConnectorEditSpec { Source = source };
        var id = ModelFamilyJson.AsId(ModelFamilyJson.Get(obj, "connector"));
        if (id == null) problems.Add($"{source}.connector is required: the element id of the connector (revit_get_model_family lists them under connectors).");
        else spec.ConnectorId = id.Value;
        ReadSettings(obj, spec, source, createCount, null, problems);
        if (problems.Count == before && spec.SystemType == null && spec.Shape == null && !spec.HasSize && spec.Position == null && !spec.Flip &&
            spec.Angle == null && spec.Primary == null && !spec.LinkGiven && spec.Properties.Count == 0 && spec.Associations.Count == 0)
            problems.Add($"{source}: nothing to change; give systemType, shape, diameter, width, height, position, flip, angle, primary, linkTo, properties or associations.");
        return problems.Count == before ? spec : null;
    }

    /// <summary>A domain's system type by wire name, or a problem listing the valid ones (none for conduit and cable tray).</summary>
    public static ConnectorSystemType? ResolveSystemType(ConnectorDomain domain, string? wireName, string source, List<string> problems, bool required)
    {
        var valid = ModelFamilyConnectorTables.SystemTypes(domain);
        var domainName = ModelFamilyConnectorTables.WireName(domain);
        var aConnector = ModelFamilyConnectorTables.AConnector(domain);
        if (valid.Count == 0)
        {
            if (wireName != null) problems.Add($"{source}.systemType: {aConnector} has no system type; remove it.");
            return null;
        }
        if (wireName == null)
        {
            if (required) problems.Add($"{source}.systemType is required for {aConnector}: {string.Join(", ", valid.Select(v => v.WireName))}.");
            return null;
        }
        var found = ModelFamilyConnectorTables.SystemType(domain, wireName);
        if (found == null)
            problems.Add($"{source}.systemType: '{wireName}' is not a system type for {domainName} connectors; use {string.Join(", ", valid.Select(v => v.WireName))}.");
        return found;
    }

    private static void ReadSettings(JObject obj, ConnectorSettings spec, string source, int createCount, int? selfIndex, List<string> problems)
    {
        var systemType = ModelFamilyJson.Str(obj, "systemType");
        spec.SystemType = systemType.Length > 0 ? systemType : null;

        var shape = ModelFamilyJson.Str(obj, "shape");
        if (shape.Length > 0)
        {
            spec.Shape = ModelFamilyConnectorTables.ParseShape(shape);
            if (spec.Shape == null) problems.Add($"{source}.shape: '{shape}' is not a shape; use round, rectangular or oval.");
        }
        spec.Diameter = ModelFamilyJson.Number(obj, "diameter", source, problems);
        spec.Width = ModelFamilyJson.Number(obj, "width", source, problems);
        spec.Height = ModelFamilyJson.Number(obj, "height", source, problems);
        if (ModelFamilyJson.Get(obj, "position") is { } position)
            spec.Position = ModelFamilyJson.Point(position, $"{source}.position", problems, allow2D: false);
        spec.Flip = ModelFamilyJson.Bool(obj, "flip", source, problems) ?? false;
        spec.Angle = ModelFamilyJson.Number(obj, "angle", source, problems);
        spec.Primary = ModelFamilyJson.Bool(obj, "primary", source, problems);

        var link = obj.GetValue("linkTo", StringComparison.OrdinalIgnoreCase);
        if (link != null)
        {
            spec.LinkGiven = true;
            if (link.Type != JTokenType.Null) spec.LinkTo = ParseRef(link, $"{source}.linkTo", createCount, selfIndex, problems);
        }

        if (ModelFamilyJson.Get(obj, "properties") is { } properties)
        {
            if (properties is not JObject map) problems.Add($"{source}.properties: must be an object {{\"<parameter>\": value}}.");
            else
                foreach (var property in map.Properties())
                    if (ParseValue(property.Value, $"{source}.properties.{property.Name}", problems) is { } value)
                        spec.Properties.Add((property.Name, value));
        }

        if (ModelFamilyJson.Get(obj, "associations") is { } associations)
        {
            if (associations is not JObject map) problems.Add($"{source}.associations: must be an object {{\"<connector parameter>\": \"<family parameter>\"}}.");
            else
                foreach (var property in map.Properties())
                {
                    if (property.Value is null || property.Value.Type == JTokenType.Null) spec.Associations.Add((property.Name, null));
                    else if (property.Value.Type == JTokenType.String && ((string?)property.Value)?.Trim() is { Length: > 0 } family)
                        spec.Associations.Add((property.Name, family));
                    else problems.Add($"{source}.associations.{property.Name}: give the family parameter's name (or null to dissociate).");
                }
        }
        foreach (var (name, _) in spec.Properties)
            if (spec.Associations.Any(a => a.FamilyParameter != null && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                problems.Add($"{source}.properties.{name}: it is also associated with a family parameter, which drives its value; set the value on the family parameter instead.");
    }

    /// <summary>A connector id, or <c>{"created": "connectors[i]"}</c> / <c>{"created": i}</c> for one made earlier in the call.</summary>
    public static ConnectorRef? ParseRef(JToken token, string source, int createCount, int? selfIndex, List<string> problems)
    {
        if (token is JObject obj)
        {
            var created = ModelFamilyJson.Get(obj, "created");
            int? index = null;
            if (created?.Type == JTokenType.Integer) index = created.Value<int>();
            else if (created?.Type == JTokenType.String && CreatedPattern.Match((string?)created ?? string.Empty) is { Success: true } m)
                index = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (index == null || obj.Count != 1)
            {
                problems.Add($"{source}: give a connector id or {{\"created\": \"connectors[i]\"}}.");
                return null;
            }
            if (index < 0 || index >= createCount)
            {
                problems.Add($"{source}: connectors[{index}] is not created in this call ({createCount} connector(s) are).");
                return null;
            }
            if (index == selfIndex)
            {
                problems.Add($"{source}: a connector cannot be linked to itself.");
                return null;
            }
            return new ConnectorRef { CreatedIndex = index };
        }
        var id = ModelFamilyJson.AsId(token);
        if (id == null)
        {
            problems.Add($"{source}: give a connector id or {{\"created\": \"connectors[i]\"}}.");
            return null;
        }
        return new ConnectorRef { Id = id };
    }

    /// <summary>A property value: number, text, true/false, or <c>{value, unit}</c>.</summary>
    public static ConnectorValue? ParseValue(JToken token, string source, List<string> problems)
    {
        switch (token.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                return ModelFamilyJson.AsNumber(token) is { } number ? new ConnectorValue { Number = number } : Bad();
            case JTokenType.Boolean:
                return new ConnectorValue { Flag = (bool)token };
            case JTokenType.String:
                return new ConnectorValue { Text = ((string?)token)?.Trim() ?? string.Empty };
            case JTokenType.Object:
                var obj = (JObject)token;
                var before = problems.Count;
                ModelFamilyJson.CheckKeys(obj, source, problems, "value", "unit");
                var value = ModelFamilyJson.Number(obj, "value", source, problems);
                if (value == null && problems.Count == before) problems.Add($"{source}.value is required: a number.");
                var unit = ModelFamilyJson.Str(obj, "unit");
                return problems.Count == before ? new ConnectorValue { Number = value, Unit = unit.Length > 0 ? unit : null } : null;
            default:
                return Bad();
        }

        ConnectorValue? Bad()
        {
            problems.Add($"{source}: give a number, a text, true/false or {{\"value\": number, \"unit\": \"â€¦\"}}.");
            return null;
        }
    }

    /// <summary>
    /// A host selector: <c>{"element": id, "face": "back" | [nx, ny, nz]}</c>, <c>{"reference": "&lt;stable&gt;"}</c>,
    /// or a reference plane name or id.
    /// </summary>
    public static ConnectorHostSpec? ParseHost(JToken? token, string source, List<string> problems)
    {
        const string hint = "give {\"element\": <form id>, \"face\": \"left|right|front|back|top|bottom\" or [nx, ny, nz]}, {\"reference\": \"<stable reference>\"} or a reference plane name or id";
        if (token == null)
        {
            problems.Add($"{source} is required: {hint}.");
            return null;
        }
        if (token is JObject obj)
        {
            if (ModelFamilyJson.Has(obj, "reference"))
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "reference");
                var stable = ModelFamilyJson.Str(obj, "reference");
                if (stable.Length == 0)
                {
                    problems.Add($"{source}.reference: give the face reference's stable representation.");
                    return null;
                }
                return new ConnectorHostSpec { Kind = ConnectorHostKind.StableReference, StableReference = stable };
            }
            var before = problems.Count;
            ModelFamilyJson.CheckKeys(obj, source, problems, "element", "face");
            var element = ModelFamilyJson.AsId(ModelFamilyJson.Get(obj, "element"));
            if (element == null) problems.Add($"{source}.element is required: the id of the form whose face hosts the connector ({hint}).");
            var spec = new ConnectorHostSpec { Kind = ConnectorHostKind.ElementFace, ElementId = element ?? 0 };
            var face = ModelFamilyJson.Get(obj, "face");
            if (face == null) problems.Add($"{source}.face is required: left, right, front, back, top, bottom or a normal [nx, ny, nz].");
            else if (face.Type == JTokenType.String)
            {
                var label = ((string?)face)?.Trim() ?? string.Empty;
                if (ModelFamilyConnectorTables.FaceLabels.TryGetValue(label, out var normal))
                {
                    spec.FaceLabel = ModelFamilyConnectorTables.FaceLabels.Keys.First(k => string.Equals(k, label, StringComparison.OrdinalIgnoreCase));
                    spec.FaceNormal = normal;
                }
                else problems.Add($"{source}.face: '{label}' is not a face; use left, right, front, back, top, bottom or a normal [nx, ny, nz].");
            }
            else if (ModelFamilyJson.Point(face, $"{source}.face", problems, allow2D: false) is { } normal)
            {
                if (Math.Sqrt(normal[0] * normal[0] + normal[1] * normal[1] + normal[2] * normal[2]) < 1e-9)
                    problems.Add($"{source}.face: the normal must not be zero.");
                else
                {
                    spec.FaceNormal = normal;
                    spec.FaceLabel = ModelFamilyConnectorTables.LabelOf(normal);
                }
            }
            return problems.Count == before ? spec : null;
        }
        if (token.Type is JTokenType.String or JTokenType.Integer)
        {
            var text = token.Type == JTokenType.String ? ((string?)token)?.Trim() ?? string.Empty : token.ToString();
            if (text.Length == 0)
            {
                problems.Add($"{source}: {hint}.");
                return null;
            }
            return new ConnectorHostSpec { Kind = ConnectorHostKind.ReferencePlane, Plane = token };
        }
        problems.Add($"{source}: {hint}.");
        return null;
    }
}

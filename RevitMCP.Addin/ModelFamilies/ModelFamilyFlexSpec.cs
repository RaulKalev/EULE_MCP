using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>The argument names of revit_flex_model_family and revit_check_model_family_instances (#176).</summary>
public static class ModelFamilyFlexArgs
{
    public const string Types = "types";
    public const string Ranges = "ranges";
    public const string Cases = "cases";
    public const string Combine = "combine";
    public const string MaxCases = "maxCases";
    public const string Checks = "checks";
    public const string ReopenCopy = "reopenCopy";
    public const string Family = "family";
    public const string ElementIds = "elementIds";

    /// <summary>What the flex tool reads besides target and document.</summary>
    public static readonly string[] FlexOrder = { Types, Ranges, Cases, Combine, MaxCases, Checks, ReopenCopy };

    /// <summary>What the instance check reads besides document.</summary>
    public static readonly string[] InstanceOrder = { Family, ElementIds, Types, Checks };

    public const string OneAtATime = "oneAtATime";
    public const string Extremes = "extremes";
    public const int DefaultMaxCases = 60;
    public const int MaxMaxCases = 300;
}

/// <summary>
/// An expected number or value of a check: a literal, or <c>{"parameter": "Width", "factor"?: 0.5, "offset"?: 0}</c>
/// evaluated against the family parameter values of each case (formula results included), so one expectation holds
/// for every type and every flexed value. Pure.
/// </summary>
public sealed class ModelFamilyFlexExpr
{
    public JToken? Literal { get; init; }
    public string? Parameter { get; init; }
    public double Factor { get; init; } = 1;
    public double Offset { get; init; }

    /// <summary>True when the parameter's value is scaled or shifted, so the result is a number.</summary>
    public bool Scaled => Factor != 1 || Offset != 0;

    public static ModelFamilyFlexExpr Of(double value) => new() { Literal = new JValue(value) };

    public static ModelFamilyFlexExpr Of(string parameter, double factor = 1, double offset = 0) =>
        new() { Parameter = parameter, Factor = factor, Offset = offset };

    /// <summary>
    /// Reads an expectation. <paramref name="numeric"/> requires a number literal (a length in mm, an angle in
    /// degrees). Null token gives null without a problem: the caller decides whether it was required.
    /// </summary>
    public static ModelFamilyFlexExpr? Parse(JToken? token, string source, List<string> problems, bool numeric)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is JObject obj)
        {
            ModelFamilyJson.CheckKeys(obj, source, problems, "parameter", "factor", "offset");
            var name = ModelFamilyJson.Str(obj, "parameter");
            if (name.Length == 0)
            {
                problems.Add($"{source}.parameter is required: the name of a family parameter, e.g. {{\"parameter\": \"Width\", \"factor\": 0.5}}.");
                return null;
            }
            var factor = ModelFamilyJson.Number(obj, "factor", source, problems) ?? 1;
            var offset = ModelFamilyJson.Number(obj, "offset", source, problems) ?? 0;
            return new ModelFamilyFlexExpr { Parameter = name, Factor = factor, Offset = offset };
        }
        if (token is JArray)
        {
            problems.Add($"{source}: must be a value or {{\"parameter\": \"<name>\", \"factor\"?, \"offset\"?}}, not a list.");
            return null;
        }
        if (numeric && ModelFamilyJson.AsNumber(token) == null)
        {
            problems.Add($"{source}: must be a number (millimetres for a length) or {{\"parameter\": \"<name>\", \"factor\"?, \"offset\"?}}.");
            return null;
        }
        return new ModelFamilyFlexExpr { Literal = token.DeepClone() };
    }

    /// <summary>Three entries <c>[x, y, z]</c>; a null entry is not checked.</summary>
    public static ModelFamilyFlexExpr?[]? ParseTriple(JToken? token, string source, List<string> problems)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is not JArray array || array.Count != 3)
        {
            problems.Add($"{source}: must be [x, y, z]; each entry a number in mm, {{\"parameter\": \"<name>\", \"factor\"?, \"offset\"?}} or null (not checked).");
            return null;
        }
        var result = new ModelFamilyFlexExpr?[3];
        for (var i = 0; i < 3; i++) result[i] = Parse(array[i], $"{source}[{i}]", problems, numeric: true);
        return result;
    }

    /// <summary>The value as a number: a literal, or the parameter's value × factor + offset. Yes/no counts as 1/0.</summary>
    public (double? Value, string? Error) Number(IReadOnlyDictionary<string, JToken?> values)
    {
        if (Parameter == null)
        {
            var literal = ToNumber(Literal);
            return literal == null ? (null, $"{ModelFamilyParameterValues.Describe(Literal)} is not a number") : (literal, null);
        }
        if (!TryGet(values, Parameter, out var token))
            return (null, $"the parameter '{Parameter}' has no value here");
        var number = ToNumber(token);
        if (number == null) return (null, $"'{Parameter}' = {ModelFamilyParameterValues.Describe(token)} is not a number");
        return (Math.Round(number.Value * Factor + Offset, 6), null);
    }

    /// <summary>The value as a token: a literal as given, a parameter's value (scaled when factor or offset is set).</summary>
    public (JToken? Value, string? Error) Value(IReadOnlyDictionary<string, JToken?> values)
    {
        if (Parameter == null) return (Literal, null);
        if (Scaled)
        {
            var (number, error) = Number(values);
            return number == null ? (null, error) : (new JValue(number.Value), null);
        }
        return TryGet(values, Parameter, out var token) ? (token, null) : (null, $"the parameter '{Parameter}' has no value here");
    }

    /// <summary>The expression as text for a result: <c>Width × 0.5 + 10</c>, or the literal.</summary>
    public string Text()
    {
        if (Parameter == null) return ModelFamilyParameterValues.Describe(Literal);
        var text = Parameter;
        if (Factor != 1) text += " × " + Factor.ToString("0.######", CultureInfo.InvariantCulture);
        if (Offset > 0) text += " + " + Offset.ToString("0.######", CultureInfo.InvariantCulture);
        if (Offset < 0) text += " − " + (-Offset).ToString("0.######", CultureInfo.InvariantCulture);
        return text;
    }

    public static double? ToNumber(JToken? token) =>
        token?.Type == JTokenType.Boolean ? ((bool)token ? 1 : 0) : ModelFamilyJson.AsNumber(token);

    public static bool TryGet(IReadOnlyDictionary<string, JToken?> values, string name, out JToken? token)
    {
        if (values.TryGetValue(name, out token)) return true;
        foreach (var pair in values)
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                token = pair.Value;
                return true;
            }
        token = null;
        return false;
    }
}

/// <summary>Where a check runs: in the family document during a flex (#176), or on a placed instance in a project.</summary>
public enum ModelFamilyFlexScope
{
    Family,
    Instance
}

/// <summary>One expectation of a flex or an instance check, parsed. Which fields are set depends on <see cref="Kind"/>.</summary>
public sealed class ModelFamilyFlexCheck
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    /// <summary>Millimetres for geometry; the parameter's own unit for a value.</summary>
    public double Tolerance { get; set; } = 0.5;

    /// <summary>A form of the family (boundingBox, centred, face, material); null = every solid form together.</summary>
    public JToken? Element { get; set; }
    public ModelFamilyFlexExpr?[] Size { get; set; } = new ModelFamilyFlexExpr?[3];
    public ModelFamilyFlexExpr?[]? Min { get; set; }
    public ModelFamilyFlexExpr?[]? Max { get; set; }

    /// <summary>plane: a reference plane name or id. face: the onPlane plane.</summary>
    public JToken? Plane { get; set; }
    public JToken? OnPlane { get; set; }
    public string? Face { get; set; }
    /// <summary>at.axis (0 = x, 1 = y, 2 = z) or -1.</summary>
    public int AtAxis { get; set; } = -1;
    public ModelFamilyFlexExpr? AtValue { get; set; }
    public bool Fixed { get; set; }

    /// <summary>centred: the axis (0..2) and the centre value.</summary>
    public int Axis { get; set; } = -1;
    public ModelFamilyFlexExpr? About { get; set; }

    public string? Parameter { get; set; }
    public ModelFamilyFlexExpr? EqualsValue { get; set; }
    public ModelFamilyFlexExpr? MinValue { get; set; }
    public ModelFamilyFlexExpr? MaxValue { get; set; }

    /// <summary>A connector: element id (family), connector id (instance) or description / system type text.</summary>
    public JToken? Connector { get; set; }
    public ModelFamilyFlexExpr?[]? Origin { get; set; }
    public bool OriginFixed { get; set; }
    public JToken? OnFaceElement { get; set; }
    public string? OnFaceLabel { get; set; }
    public double[]? Direction { get; set; }
    public string? SystemType { get; set; }
    public string? Domain { get; set; }
    public bool? Connected { get; set; }
    public string? System { get; set; }
    public List<KeyValuePair<string, ModelFamilyFlexExpr>> Values { get; } = new();

    /// <summary>symbol: the nested instance and where it must be.</summary>
    public JToken? Instance { get; set; }
    public ModelFamilyFlexExpr?[]? At { get; set; }

    public List<long> Elements { get; } = new();
    public long Void { get; set; }
    public long Solid { get; set; }

    /// <summary>hosted: the expected state (default false: placed without a host).</summary>
    public bool Hosted { get; set; }

    public string? Type { get; set; }
    public int? CountMin { get; set; }
    public int? CountMax { get; set; }

    /// <summary>True when the check compares a position across cases.</summary>
    public bool HasFixed => Fixed || OriginFixed;

    /// <summary>The family parameters the check's expectations read.</summary>
    public IEnumerable<string> ReferencedParameters()
    {
        var all = new List<ModelFamilyFlexExpr?>();
        all.AddRange(Size);
        if (Min != null) all.AddRange(Min);
        if (Max != null) all.AddRange(Max);
        if (Origin != null) all.AddRange(Origin);
        if (At != null) all.AddRange(At);
        all.AddRange(new[] { AtValue, About, EqualsValue, MinValue, MaxValue });
        all.AddRange(Values.Select(v => v.Value));
        var names = all.Where(e => e?.Parameter != null).Select(e => e!.Parameter!).ToList();
        if (Kind == "parameter" && Parameter != null) names.Add(Parameter);
        return names.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Parses the <c>checks</c> list of the flex and instance tools (#176). Pure; bad input becomes problems.</summary>
public static class ModelFamilyFlexChecks
{
    public static readonly string[] FamilyKinds =
        { "boundingBox", "plane", "face", "centred", "parameter", "material", "connector", "symbol", "exists", "cut" };

    public static readonly string[] InstanceKinds =
        { "boundingBox", "centred", "parameter", "material", "connector", "hosted", "count" };

    public static readonly string[] Axes = { "x", "y", "z" };

    public const double DefaultTolerance = 0.5;
    public const double DefaultValueTolerance = 0.001;

    public static List<ModelFamilyFlexCheck> Parse(List<JObject> items, ModelFamilyFlexScope scope, List<string> problems)
    {
        var result = new List<ModelFamilyFlexCheck>();
        var kinds = scope == ModelFamilyFlexScope.Family ? FamilyKinds : InstanceKinds;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var obj = items[i];
            var source = $"checks[{i}]";
            var kind = ModelFamilyJson.Str(obj, "kind");
            var match = kinds.FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase))
                        ?? (string.Equals(kind, "centered", StringComparison.OrdinalIgnoreCase) ? "centred" : null);
            if (match == null)
            {
                var other = (scope == ModelFamilyFlexScope.Family ? InstanceKinds : FamilyKinds)
                    .FirstOrDefault(k => string.Equals(k, kind, StringComparison.OrdinalIgnoreCase));
                problems.Add(kind.Length == 0
                    ? $"{source}.kind is required: one of {string.Join(", ", kinds)}."
                    : other != null
                        ? $"{source}.kind: '{other}' applies to {(scope == ModelFamilyFlexScope.Family ? "placed instances (revit_check_model_family_instances)" : "the family document (revit_flex_model_family)")}; here use one of {string.Join(", ", kinds)}."
                        : $"{source}.kind: '{kind}' is not a check. Use one of {string.Join(", ", kinds)}.");
                continue;
            }
            var check = new ModelFamilyFlexCheck { Kind = match, Source = source };
            check.Id = ModelFamilyJson.Str(obj, "id");
            if (check.Id.Length == 0) check.Id = source;
            if (!ids.Add(check.Id)) problems.Add($"{source}.id: '{check.Id}' is used by another check; ids must be unique.");
            var before = problems.Count;
            ParseKind(obj, check, scope, source, problems);
            if (problems.Count == before) result.Add(check);
        }
        return result;
    }

    private static void ParseKind(JObject obj, ModelFamilyFlexCheck check, ModelFamilyFlexScope scope, string source, List<string> problems)
    {
        var family = scope == ModelFamilyFlexScope.Family;
        switch (check.Kind)
        {
            case "boundingBox":
            {
                if (family) ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "element", "size", "min", "max", "tolerance");
                else ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "size", "min", "max", "tolerance");
                if (family) check.Element = ModelFamilyJson.Get(obj, "element");
                if (ModelFamilyJson.Get(obj, "size") is { } size)
                {
                    if (size is JObject sizeObj)
                    {
                        ModelFamilyJson.CheckKeys(sizeObj, $"{source}.size", problems, "x", "y", "z");
                        for (var a = 0; a < 3; a++)
                            check.Size[a] = ModelFamilyFlexExpr.Parse(ModelFamilyJson.Get(sizeObj, Axes[a]), $"{source}.size.{Axes[a]}", problems, numeric: true);
                    }
                    else problems.Add($"{source}.size: must be {{x?, y?, z?}} in millimetres.");
                }
                check.Min = ModelFamilyFlexExpr.ParseTriple(ModelFamilyJson.Get(obj, "min"), $"{source}.min", problems);
                check.Max = ModelFamilyFlexExpr.ParseTriple(ModelFamilyJson.Get(obj, "max"), $"{source}.max", problems);
                check.Tolerance = Tolerance(obj, source, problems, DefaultTolerance);
                if (check.Size.All(s => s == null) && (check.Min?.All(s => s == null) ?? true) && (check.Max?.All(s => s == null) ?? true))
                    problems.Add($"{source}: give size, min or max to check.");
                break;
            }
            case "plane":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "plane", "at", "fixed", "tolerance");
                check.Plane = ModelFamilyJson.Get(obj, "plane");
                if (check.Plane == null) problems.Add($"{source}.plane is required: a reference plane name or element id.");
                ParseAt(obj, check, source, problems);
                check.Fixed = ModelFamilyJson.Bool(obj, "fixed", source, problems) ?? false;
                check.Tolerance = Tolerance(obj, source, problems, DefaultTolerance);
                if (check.AtValue == null && !check.Fixed) problems.Add($"{source}: give at {{axis, value}} or fixed: true.");
                break;
            }
            case "face":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "element", "face", "at", "fixed", "onPlane", "tolerance");
                check.Element = ModelFamilyJson.Get(obj, "element");
                if (ModelFamilyJson.AsId(check.Element) == null) problems.Add($"{source}.element is required: the element id of a form.");
                check.Face = ModelFamilyJson.Str(obj, "face");
                if (!ModelFamilyFormFaceLabels.IsLabel(check.Face))
                    problems.Add($"{source}.face: '{check.Face}' is not one of {string.Join(", ", ModelFamilyFormFaceLabels.Labels)}.");
                else check.Face = check.Face.Trim().ToLowerInvariant();
                ParseAt(obj, check, source, problems);
                check.Fixed = ModelFamilyJson.Bool(obj, "fixed", source, problems) ?? false;
                check.OnPlane = ModelFamilyJson.Get(obj, "onPlane");
                check.Tolerance = Tolerance(obj, source, problems, DefaultTolerance);
                if (check.AtValue == null && !check.Fixed && check.OnPlane == null) problems.Add($"{source}: give at {{axis, value}}, fixed: true or onPlane.");
                break;
            }
            case "centred":
            {
                if (family) ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "element", "axis", "about", "tolerance");
                else ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "axis", "about", "tolerance");
                if (family) check.Element = ModelFamilyJson.Get(obj, "element");
                check.Axis = ParseAxis(ModelFamilyJson.Str(obj, "axis"), $"{source}.axis", problems);
                check.About = ModelFamilyFlexExpr.Parse(ModelFamilyJson.Get(obj, "about"), $"{source}.about", problems, numeric: true) ?? ModelFamilyFlexExpr.Of(0);
                check.Tolerance = Tolerance(obj, source, problems, DefaultTolerance);
                break;
            }
            case "parameter":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "parameter", "equals", "min", "max", "tolerance");
                check.Parameter = ModelFamilyJson.Str(obj, "parameter");
                if (check.Parameter.Length == 0) problems.Add($"{source}.parameter is required: the name of a {(family ? "family" : "instance or type")} parameter.");
                check.EqualsValue = ModelFamilyFlexExpr.Parse(ModelFamilyJson.Get(obj, "equals"), $"{source}.equals", problems, numeric: false);
                check.MinValue = ModelFamilyFlexExpr.Parse(ModelFamilyJson.Get(obj, "min"), $"{source}.min", problems, numeric: true);
                check.MaxValue = ModelFamilyFlexExpr.Parse(ModelFamilyJson.Get(obj, "max"), $"{source}.max", problems, numeric: true);
                check.Tolerance = Tolerance(obj, source, problems, DefaultValueTolerance);
                if (check.EqualsValue == null && check.MinValue == null && check.MaxValue == null)
                    problems.Add($"{source}: give equals, min or max.");
                break;
            }
            case "material":
            {
                if (family)
                {
                    ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "element", "equals");
                    check.Element = ModelFamilyJson.Get(obj, "element");
                    if (ModelFamilyJson.AsId(check.Element) == null) problems.Add($"{source}.element is required: the element id of a form.");
                }
                else
                {
                    ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "parameter", "equals");
                    var name = ModelFamilyJson.Str(obj, "parameter");
                    check.Parameter = name.Length == 0 ? null : name;
                }
                check.EqualsValue = ModelFamilyFlexExpr.Parse(ModelFamilyJson.Get(obj, "equals"), $"{source}.equals", problems, numeric: false);
                if (check.EqualsValue == null) problems.Add($"{source}.equals is required: a material name or {{\"parameter\": \"<material parameter>\"}}.");
                break;
            }
            case "connector":
            {
                if (family)
                    ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "connector", "origin", "originFixed", "onFace", "direction", "systemType", "domain", "values", "tolerance");
                else
                    ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "connector", "origin", "direction", "systemType", "domain", "values", "connected", "system", "tolerance");
                check.Connector = ModelFamilyJson.Get(obj, "connector");
                if (check.Connector == null || check.Connector is JContainer)
                    problems.Add($"{source}.connector is required: {(family ? "the connector's element id" : "the connector's id on the instance")} or its description.");
                check.Origin = ModelFamilyFlexExpr.ParseTriple(ModelFamilyJson.Get(obj, "origin"), $"{source}.origin", problems);
                if (family) check.OriginFixed = ModelFamilyJson.Bool(obj, "originFixed", source, problems) ?? false;
                if (family && ModelFamilyJson.Get(obj, "onFace") is { } onFace)
                {
                    if (onFace is JObject faceObj)
                    {
                        ModelFamilyJson.CheckKeys(faceObj, $"{source}.onFace", problems, "element", "face");
                        check.OnFaceElement = ModelFamilyJson.Get(faceObj, "element");
                        check.OnFaceLabel = ModelFamilyJson.Str(faceObj, "face");
                        if (ModelFamilyJson.AsId(check.OnFaceElement) == null) problems.Add($"{source}.onFace.element is required: the element id of a form.");
                        if (!ModelFamilyFormFaceLabels.IsLabel(check.OnFaceLabel))
                            problems.Add($"{source}.onFace.face: '{check.OnFaceLabel}' is not one of {string.Join(", ", ModelFamilyFormFaceLabels.Labels)}.");
                        else check.OnFaceLabel = check.OnFaceLabel.Trim().ToLowerInvariant();
                    }
                    else problems.Add($"{source}.onFace: must be {{element, face}}.");
                }
                if (ModelFamilyJson.Get(obj, "direction") is { } direction)
                {
                    var d = ModelFamilyJson.Point(direction, $"{source}.direction", problems, allow2D: false);
                    if (d != null && Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]) < 1e-9) problems.Add($"{source}.direction: must not be zero.");
                    else check.Direction = d;
                }
                check.SystemType = Optional(ModelFamilyJson.Str(obj, "systemType"));
                check.Domain = Optional(ModelFamilyJson.Str(obj, "domain"));
                if (!family)
                {
                    check.Connected = ModelFamilyJson.Bool(obj, "connected", source, problems);
                    check.System = Optional(ModelFamilyJson.Str(obj, "system"));
                }
                if (ModelFamilyJson.Get(obj, "values") is { } values)
                {
                    if (values is JObject valuesObj)
                        foreach (var property in valuesObj.Properties())
                        {
                            var expr = ModelFamilyFlexExpr.Parse(property.Value, $"{source}.values.{property.Name}", problems, numeric: false);
                            if (expr != null) check.Values.Add(new KeyValuePair<string, ModelFamilyFlexExpr>(property.Name, expr));
                        }
                    else problems.Add($"{source}.values: must be {{\"<connector parameter>\": value or {{\"parameter\": \"<family parameter>\"}}}}.");
                }
                check.Tolerance = Tolerance(obj, source, problems, DefaultTolerance);
                if (check.Origin == null && !check.OriginFixed && check.OnFaceElement == null && check.Direction == null && check.SystemType == null &&
                    check.Domain == null && check.Connected == null && check.System == null && check.Values.Count == 0)
                    problems.Add($"{source}: give at least one of origin, {(family ? "originFixed, onFace, " : "connected, system, ")}direction, systemType, domain or values.");
                break;
            }
            case "symbol":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "instance", "at", "fixed", "tolerance");
                check.Instance = ModelFamilyJson.Get(obj, "instance");
                if (ModelFamilyJson.AsId(check.Instance) == null) problems.Add($"{source}.instance is required: the element id of the nested instance.");
                check.At = ModelFamilyFlexExpr.ParseTriple(ModelFamilyJson.Get(obj, "at"), $"{source}.at", problems);
                check.Fixed = ModelFamilyJson.Bool(obj, "fixed", source, problems) ?? false;
                check.Tolerance = Tolerance(obj, source, problems, DefaultTolerance);
                if (check.At == null && !check.Fixed) problems.Add($"{source}: give at [x, y, z] or fixed: true.");
                break;
            }
            case "exists":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "elements");
                if (ModelFamilyJson.Get(obj, "elements") is JArray elements && elements.Count > 0)
                {
                    for (var e = 0; e < elements.Count; e++)
                    {
                        var id = ModelFamilyJson.AsId(elements[e]);
                        if (id == null) problems.Add($"{source}.elements[{e}]: must be an element id.");
                        else check.Elements.Add(id.Value);
                    }
                }
                else problems.Add($"{source}.elements is required: element ids of the family that must exist (and have geometry) in every case.");
                break;
            }
            case "cut":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "void", "solid");
                var voidId = ModelFamilyJson.AsId(ModelFamilyJson.Get(obj, "void"));
                var solidId = ModelFamilyJson.AsId(ModelFamilyJson.Get(obj, "solid"));
                if (voidId == null) problems.Add($"{source}.void is required: the element id of the void form.");
                if (solidId == null) problems.Add($"{source}.solid is required: the element id of the solid form it must cut.");
                check.Void = voidId ?? 0;
                check.Solid = solidId ?? 0;
                break;
            }
            case "hosted":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "hosted");
                check.Hosted = ModelFamilyJson.Bool(obj, "hosted", source, problems) ?? false;
                break;
            }
            case "count":
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "id", "kind", "type", "min", "max");
                check.Type = Optional(ModelFamilyJson.Str(obj, "type"));
                if (check.Type == null) problems.Add($"{source}.type is required: a type name of the family.");
                check.CountMin = WholeNumber(obj, "min", source, problems);
                check.CountMax = WholeNumber(obj, "max", source, problems);
                if (check.CountMin == null && check.CountMax == null) problems.Add($"{source}: give min or max (number of instances of the type).");
                break;
            }
        }
    }

    private static void ParseAt(JObject obj, ModelFamilyFlexCheck check, string source, List<string> problems)
    {
        if (ModelFamilyJson.Get(obj, "at") is not { } at) return;
        if (at is not JObject atObj)
        {
            problems.Add($"{source}.at: must be {{axis: \"x\"|\"y\"|\"z\", value}}.");
            return;
        }
        ModelFamilyJson.CheckKeys(atObj, $"{source}.at", problems, "axis", "value");
        check.AtAxis = ParseAxis(ModelFamilyJson.Str(atObj, "axis"), $"{source}.at.axis", problems);
        check.AtValue = ModelFamilyFlexExpr.Parse(ModelFamilyJson.Get(atObj, "value"), $"{source}.at.value", problems, numeric: true);
        if (check.AtValue == null && ModelFamilyJson.Get(atObj, "value") == null)
            problems.Add($"{source}.at.value is required: a number in mm or {{\"parameter\": \"<name>\"}}.");
    }

    /// <summary>
    /// The connector tools' wire name (#174) of a placed connector's system type member: ElectricalSystemType
    /// <c>PowerBalanced</c> → <c>powerBalanced</c>, <c>Data</c> → <c>data</c>, DuctSystemType <c>SupplyAir</c> →
    /// <c>supplyAir</c>; an unknown member camel-cased.
    /// </summary>
    public static string SystemTypeWireName(string member)
    {
        foreach (var domain in new[] { ConnectorDomain.Electrical, ConnectorDomain.Duct, ConnectorDomain.Pipe })
            if (ModelFamilyConnectorTables.SystemTypes(domain).FirstOrDefault(t =>
                    string.Equals(t.CreateMember, member, StringComparison.Ordinal) || string.Equals(t.Classification, member, StringComparison.Ordinal)) is { } found)
                return found.WireName;
        return ModelFamilyConnectorTables.ClassificationWireName(member);
    }

    public static int ParseAxis(string text, string source, List<string> problems)
    {
        var index = Array.FindIndex(Axes, a => string.Equals(a, text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (index < 0) problems.Add($"{source}: '{text}' is not an axis; use x, y or z.");
        return index;
    }

    private static double Tolerance(JObject obj, string source, List<string> problems, double fallback)
    {
        var value = ModelFamilyJson.Number(obj, "tolerance", source, problems);
        if (value is < 0)
        {
            problems.Add($"{source}.tolerance: must not be negative.");
            return fallback;
        }
        return value ?? fallback;
    }

    private static int? WholeNumber(JObject obj, string key, string source, List<string> problems)
    {
        var value = ModelFamilyJson.Number(obj, key, source, problems);
        if (value == null) return null;
        if (value < 0 || Math.Abs(value.Value - Math.Round(value.Value)) > 1e-9)
        {
            problems.Add($"{source}.{key}: must be a whole number, 0 or more.");
            return null;
        }
        return (int)Math.Round(value.Value);
    }

    private static string? Optional(string text) => text.Length == 0 ? null : text;
}

/// <summary>A <c>ranges</c> entry: a writable parameter flexed from min to max in evenly spaced steps.</summary>
public sealed class ModelFamilyFlexRange
{
    public string Parameter { get; set; } = string.Empty;
    public JToken Min { get; set; } = JValue.CreateNull();
    public JToken Max { get; set; } = JValue.CreateNull();
    public int Steps { get; set; } = 2;
    public List<string>? Types { get; set; }
    public string Source { get; set; } = string.Empty;

    /// <summary>The values the range takes: min and max for 2 steps, evenly spaced between them for up to 5; false and true for yes/no.</summary>
    public List<JToken> Values()
    {
        if (Min.Type == JTokenType.Boolean)
            return (bool)Min == (bool)Max ? new List<JToken> { Min.DeepClone() } : new List<JToken> { new JValue(false), new JValue(true) };
        var min = ModelFamilyJson.AsNumber(Min)!.Value;
        var max = ModelFamilyJson.AsNumber(Max)!.Value;
        var result = new List<JToken>();
        for (var i = 0; i < Steps; i++)
        {
            var value = i == Steps - 1 ? max : min + (max - min) * i / (Steps - 1);
            result.Add(new JValue(Math.Round(value, 6)));
        }
        return result;
    }
}

/// <summary>A <c>cases</c> entry: an explicit combination of values on one type.</summary>
public sealed class ModelFamilyFlexCaseSpec
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public List<KeyValuePair<string, JToken>> Values { get; } = new();
    public string Source { get; set; } = string.Empty;
}

/// <summary>Everything revit_flex_model_family is asked to do, parsed without Revit (#176).</summary>
public sealed class ModelFamilyFlexSpec
{
    public List<string>? Types { get; set; }
    public List<ModelFamilyFlexRange> Ranges { get; } = new();
    public List<ModelFamilyFlexCaseSpec> Cases { get; } = new();
    public string Combine { get; set; } = ModelFamilyFlexArgs.OneAtATime;
    public int MaxCases { get; set; } = ModelFamilyFlexArgs.DefaultMaxCases;
    public List<ModelFamilyFlexCheck> Checks { get; set; } = new();
    public bool ReopenCopy { get; set; }

    public static ModelFamilyFlexSpec Parse(IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var spec = new ModelFamilyFlexSpec { Types = Names(args, ModelFamilyFlexArgs.Types, problems) };

        var ranges = ModelFamilyJson.Objects(args, ModelFamilyFlexArgs.Ranges, problems);
        for (var i = 0; i < ranges.Count; i++)
        {
            var obj = ranges[i];
            var source = $"ranges[{i}]";
            ModelFamilyJson.CheckKeys(obj, source, problems, "parameter", "min", "max", "steps", "types");
            var range = new ModelFamilyFlexRange { Source = source, Parameter = ModelFamilyJson.Str(obj, "parameter") };
            if (range.Parameter.Length == 0) problems.Add($"{source}.parameter is required: the name of a writable family parameter.");
            var min = ModelFamilyJson.Get(obj, "min");
            var max = ModelFamilyJson.Get(obj, "max");
            if (min == null || max == null)
            {
                problems.Add($"{source}: min and max are required (mm for lengths, degrees for angles, true/false for yes/no).");
                continue;
            }
            if (min.Type == JTokenType.Boolean && max.Type == JTokenType.Boolean)
            {
                range.Min = min;
                range.Max = max;
            }
            else
            {
                var minNumber = ModelFamilyJson.AsNumber(min);
                var maxNumber = ModelFamilyJson.AsNumber(max);
                if (minNumber == null || maxNumber == null)
                {
                    problems.Add($"{source}: min and max must both be numbers (or both true/false); use cases for text and material values.");
                    continue;
                }
                if (minNumber >= maxNumber)
                {
                    problems.Add($"{source}: min ({minNumber}) must be less than max ({maxNumber}); for one value use a case.");
                    continue;
                }
                range.Min = new JValue(minNumber.Value);
                range.Max = new JValue(maxNumber.Value);
            }
            var steps = ModelFamilyJson.Number(obj, "steps", source, problems);
            if (steps != null)
            {
                if (steps < 2 || steps > 5 || Math.Abs(steps.Value - Math.Round(steps.Value)) > 1e-9)
                    problems.Add($"{source}.steps: must be a whole number from 2 (min and max) to 5.");
                else range.Steps = (int)Math.Round(steps.Value);
            }
            range.Types = NamesOf(ModelFamilyJson.Get(obj, "types"), $"{source}.types", problems);
            spec.Ranges.Add(range);
        }

        var cases = ModelFamilyJson.Objects(args, ModelFamilyFlexArgs.Cases, problems);
        for (var i = 0; i < cases.Count; i++)
        {
            var obj = cases[i];
            var source = $"cases[{i}]";
            ModelFamilyJson.CheckKeys(obj, source, problems, "name", "type", "values");
            var item = new ModelFamilyFlexCaseSpec { Source = source };
            var name = ModelFamilyJson.Str(obj, "name");
            item.Name = name.Length == 0 ? null : name;
            var type = ModelFamilyJson.Str(obj, "type");
            item.Type = type.Length == 0 ? null : type;
            if (ModelFamilyJson.Get(obj, "values") is JObject values && values.Count > 0)
            {
                foreach (var property in values.Properties()) item.Values.Add(new KeyValuePair<string, JToken>(property.Name, property.Value.DeepClone()));
            }
            else problems.Add($"{source}.values is required: {{\"<parameter>\": value}} in mm, degrees, true/false, text or a material name.");
            spec.Cases.Add(item);
        }

        if (ModelFamilyJson.Token(args, ModelFamilyFlexArgs.Combine) is { } combine)
        {
            var text = combine.Type == JTokenType.String ? ((string?)combine ?? string.Empty).Trim() : combine.ToString();
            if (string.Equals(text, ModelFamilyFlexArgs.OneAtATime, StringComparison.OrdinalIgnoreCase)) spec.Combine = ModelFamilyFlexArgs.OneAtATime;
            else if (string.Equals(text, ModelFamilyFlexArgs.Extremes, StringComparison.OrdinalIgnoreCase)) spec.Combine = ModelFamilyFlexArgs.Extremes;
            else problems.Add($"combine: '{text}' is not known; use \"{ModelFamilyFlexArgs.OneAtATime}\" (each range value alone) or \"{ModelFamilyFlexArgs.Extremes}\" (also all-min and all-max per type).");
        }

        if (ModelFamilyJson.Token(args, ModelFamilyFlexArgs.MaxCases) is { } maxToken)
        {
            var max = ModelFamilyJson.AsNumber(maxToken);
            if (max == null || max < 1 || max > ModelFamilyFlexArgs.MaxMaxCases || Math.Abs(max.Value - Math.Round(max.Value)) > 1e-9)
                problems.Add($"maxCases: must be a whole number from 1 to {ModelFamilyFlexArgs.MaxMaxCases}.");
            else spec.MaxCases = (int)Math.Round(max.Value);
        }

        spec.Checks = ModelFamilyFlexChecks.Parse(ModelFamilyJson.Objects(args, ModelFamilyFlexArgs.Checks, problems), ModelFamilyFlexScope.Family, problems);

        if (ModelFamilyJson.Token(args, ModelFamilyFlexArgs.ReopenCopy) is { } reopen)
        {
            if (reopen.Type == JTokenType.Boolean) spec.ReopenCopy = (bool)reopen;
            else if (reopen.Type == JTokenType.String && bool.TryParse(((string?)reopen)?.Trim(), out var parsed)) spec.ReopenCopy = parsed;
            else problems.Add("reopenCopy: must be true or false.");
        }
        return spec;
    }

    /// <summary>A list of names (a single string counts as a list of one), or null when the argument is absent.</summary>
    public static List<string>? Names(IReadOnlyDictionary<string, object?> args, string key, List<string> problems) =>
        NamesOf(ModelFamilyJson.Token(args, key), key, problems);

    public static List<string>? NamesOf(JToken? token, string source, List<string> problems)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.String)
        {
            var single = ((string?)token ?? string.Empty).Trim();
            return single.Length == 0 ? null : new List<string> { single };
        }
        if (token is not JArray array)
        {
            problems.Add($"{source}: must be a list of type names.");
            return null;
        }
        var names = new List<string>();
        for (var i = 0; i < array.Count; i++)
        {
            var name = array[i].Type == JTokenType.String ? ((string?)array[i] ?? string.Empty).Trim() : string.Empty;
            if (name.Length == 0) problems.Add($"{source}[{i}]: must be a type name.");
            else if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }
}

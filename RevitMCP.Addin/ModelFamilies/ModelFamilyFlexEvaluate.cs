using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>A planar thing measured in a case: a point on it and its normal, in mm (family coordinates).</summary>
public sealed class ModelFamilyFlexPlane
{
    public double[] Point { get; set; } = new double[3];
    public double[] Normal { get; set; } = { 0, 0, 1 };

    /// <summary>The axis the normal follows within 1°, or -1 for a skewed plane.</summary>
    public int Axis
    {
        get
        {
            var unit = ModelFamilyFlexMath.Unit(Normal);
            for (var a = 0; a < 3; a++)
                if (Math.Abs(unit[a]) >= ModelFamilyFlexMath.CosOneDegree) return a;
            return -1;
        }
    }

    /// <summary>
    /// Where the plane is along its own normal (sign made positive on the dominant axis), so a plane is compared with
    /// itself across cases whatever way its normal points.
    /// </summary>
    public double Coordinate
    {
        get
        {
            var unit = ModelFamilyFlexMath.Unit(Normal);
            var dominant = 0;
            for (var a = 1; a < 3; a++) if (Math.Abs(unit[a]) > Math.Abs(unit[dominant])) dominant = a;
            var sign = unit[dominant] < 0 ? -1 : 1;
            return Math.Round(sign * ModelFamilyFlexMath.Dot(Point, unit), 4);
        }
    }

    public JObject ToJson() => new()
    {
        ["point"] = ModelFamilyFlexMath.Json(Point),
        ["normal"] = new JArray(Normal.Select(v => Math.Round(v, 6))),
        ["axis"] = Axis < 0 ? null : ModelFamilyFlexChecks.Axes[Axis],
        ["coordinate"] = Coordinate
    };
}

/// <summary>
/// What the Revit side measured for one check in one case (or on one instance), as plain data, so evaluation is pure.
/// Lengths in mm in family coordinates.
/// </summary>
public sealed class ModelFamilyFlexObservation
{
    /// <summary>Why the check could not be measured (an element that is gone, a face that no longer exists).</summary>
    public string? Missing { get; set; }
    public double[]? Min { get; set; }
    public double[]? Max { get; set; }
    public ModelFamilyFlexPlane? Plane { get; set; }
    /// <summary>The plane a face must lie on (face.onPlane), or the face a connector must sit on (connector.onFace).</summary>
    public ModelFamilyFlexPlane? Target { get; set; }
    public string? TargetMissing { get; set; }
    public JToken? Value { get; set; }
    /// <summary>The materials of an instance's solids, when no material parameter is named.</summary>
    public List<string>? Materials { get; set; }
    /// <summary>The connector a connector check found (element id in a family, connector id on an instance).</summary>
    public long? ConnectorId { get; set; }
    public double[]? Point { get; set; }
    public double[]? Direction { get; set; }
    public string? SystemType { get; set; }
    public string? Domain { get; set; }
    public string? System { get; set; }
    public bool? Connected { get; set; }
    public Dictionary<string, JToken?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool? Exists { get; set; }
    public List<long> Empty { get; } = new();
    public List<long>? CutBy { get; set; }
    public bool? Hosted { get; set; }
    public string? Host { get; set; }
    public bool? NeedsHost { get; set; }
    public string? Placement { get; set; }

    public JObject ToJson()
    {
        var row = new JObject();
        if (Missing != null) row["missing"] = Missing;
        if (Min != null && Max != null)
        {
            row["min"] = ModelFamilyFlexMath.Json(Min);
            row["max"] = ModelFamilyFlexMath.Json(Max);
            row["size"] = ModelFamilyFlexMath.Json(new[] { Max[0] - Min[0], Max[1] - Min[1], Max[2] - Min[2] });
            row["centre"] = ModelFamilyFlexMath.Json(new[] { (Max[0] + Min[0]) / 2, (Max[1] + Min[1]) / 2, (Max[2] + Min[2]) / 2 });
        }
        if (Plane != null) row["plane"] = Plane.ToJson();
        if (Target != null) row["target"] = Target.ToJson();
        if (TargetMissing != null) row["targetMissing"] = TargetMissing;
        if (Value != null) row["value"] = Value.DeepClone();
        if (Materials != null) row["materials"] = new JArray(Materials);
        if (ConnectorId != null) row["connectorId"] = ConnectorId;
        if (Point != null) row["point"] = ModelFamilyFlexMath.Json(Point);
        if (Direction != null) row["direction"] = new JArray(Direction.Select(v => Math.Round(v, 6)));
        if (SystemType != null) row["systemType"] = SystemType;
        if (Domain != null) row["domain"] = Domain;
        if (Connected != null) row["connected"] = Connected;
        if (System != null) row["system"] = System;
        if (Values.Count > 0)
        {
            var values = new JObject();
            foreach (var pair in Values) values[pair.Key] = pair.Value?.DeepClone();
            row["values"] = values;
        }
        if (Exists != null) row["exists"] = Exists;
        if (Empty.Count > 0) row["withoutGeometry"] = new JArray(Empty);
        if (CutBy != null) row["cutBy"] = new JArray(CutBy);
        if (Hosted != null) row["hosted"] = Hosted;
        if (Host != null) row["host"] = Host;
        if (NeedsHost != null) row["needsHost"] = NeedsHost;
        if (Placement != null) row["placement"] = Placement;
        return row;
    }
}

/// <summary>One failed expectation: which check, what was expected and what Revit gave.</summary>
public sealed class ModelFamilyFlexFailure
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    /// <summary>The part of the check that failed: <c>size.x</c>, <c>min[0]</c>, <c>origin</c>, <c>fixed</c>…</summary>
    public string What { get; set; } = string.Empty;
    public JToken? Expected { get; set; }
    public string? Expression { get; set; }
    public JToken? Actual { get; set; }
    public double? Difference { get; set; }
    public string Message { get; set; } = string.Empty;

    public JObject ToJson() => new()
    {
        ["id"] = Id,
        ["kind"] = Kind,
        ["what"] = What,
        ["expected"] = Expected?.DeepClone(),
        ["expression"] = Expression,
        ["actual"] = Actual?.DeepClone(),
        ["difference"] = Difference,
        ["message"] = Message
    };
}

/// <summary>The vector helpers the checks need. Pure.</summary>
public static class ModelFamilyFlexMath
{
    public static readonly double CosOneDegree = Math.Cos(Math.PI / 180);

    public static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];

    public static double Length(double[] a) => Math.Sqrt(Dot(a, a));

    public static double[] Unit(double[] a)
    {
        var length = Length(a);
        return length < 1e-12 ? new double[] { 0, 0, 0 } : new[] { a[0] / length, a[1] / length, a[2] / length };
    }

    public static double Distance(double[] a, double[] b) => Length(new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] });

    public static double AngleDegrees(double[] a, double[] b)
    {
        var dot = Math.Max(-1, Math.Min(1, Dot(Unit(a), Unit(b))));
        return Math.Round(Math.Acos(dot) * 180 / Math.PI, 4);
    }

    public static JArray Json(double[] point) => new(point.Select(v => Math.Round(v, 4)));
}

/// <summary>
/// Evaluates checks against observations (#176): one case of a flex, or one placed instance. Pure: the Revit side
/// measures, this decides. Expectations with <c>{parameter…}</c> read <c>values</c>, the case's or instance's
/// parameter values after regeneration.
/// </summary>
public static class ModelFamilyFlexEvaluator
{
    public const double DirectionToleranceDegrees = 1;

    public static List<ModelFamilyFlexFailure> Evaluate(ModelFamilyFlexCheck check, ModelFamilyFlexObservation observation,
        IReadOnlyDictionary<string, JToken?> values)
    {
        var failures = new List<ModelFamilyFlexFailure>();
        ModelFamilyFlexFailure Fail(string what, string message, JToken? expected = null, JToken? actual = null, double? difference = null, string? expression = null)
        {
            var failure = new ModelFamilyFlexFailure
            {
                Id = check.Id, Kind = check.Kind, What = what, Message = message, Expected = expected, Actual = actual,
                Difference = difference == null ? null : Math.Round(difference.Value, 4), Expression = expression
            };
            failures.Add(failure);
            return failure;
        }

        void Number(string what, ModelFamilyFlexExpr? expr, double? actual, double tolerance)
        {
            if (expr == null) return;
            var (expected, error) = expr.Number(values);
            if (expected == null)
            {
                Fail(what, $"{what}: the expectation cannot be evaluated: {error}.", expression: expr.Text());
                return;
            }
            if (actual == null)
            {
                Fail(what, $"{what}: nothing was measured.", new JValue(expected.Value), expression: expr.Parameter != null ? expr.Text() : null);
                return;
            }
            var difference = actual.Value - expected.Value;
            if (Math.Abs(difference) > tolerance)
                Fail(what, $"{what} is {Format(actual.Value)}, expected {Format(expected.Value)}{(expr.Parameter != null ? $" ({expr.Text()})" : string.Empty)}; off by {Format(difference)} (tolerance {Format(tolerance)}).",
                    new JValue(Math.Round(expected.Value, 4)), new JValue(Math.Round(actual.Value, 4)), difference, expr.Parameter != null ? expr.Text() : null);
        }

        void Point(string what, ModelFamilyFlexExpr?[]? exprs, double[]? actual, double tolerance)
        {
            if (exprs == null) return;
            for (var a = 0; a < 3; a++) Number($"{what}.{ModelFamilyFlexChecks.Axes[a]}", exprs[a], actual?[a], tolerance);
        }

        if (observation.Missing != null)
        {
            Fail("measure", observation.Missing);
            return failures;
        }

        switch (check.Kind)
        {
            case "boundingBox":
                if (observation.Min == null || observation.Max == null)
                {
                    Fail("measure", "No solid geometry was found to measure.");
                    break;
                }
                for (var a = 0; a < 3; a++)
                    Number($"size.{ModelFamilyFlexChecks.Axes[a]}", check.Size[a], observation.Max[a] - observation.Min[a], check.Tolerance);
                Point("min", check.Min, observation.Min, check.Tolerance);
                Point("max", check.Max, observation.Max, check.Tolerance);
                break;

            case "centred":
                if (observation.Min == null || observation.Max == null)
                {
                    Fail("measure", "No solid geometry was found to measure.");
                    break;
                }
                Number($"centre.{ModelFamilyFlexChecks.Axes[check.Axis]}", check.About, (observation.Min[check.Axis] + observation.Max[check.Axis]) / 2, check.Tolerance);
                break;

            case "plane":
            case "face":
            {
                var plane = observation.Plane;
                if (plane == null)
                {
                    Fail("measure", check.Kind == "face" ? $"The form has no '{check.Face}' face in this case." : "The reference plane could not be measured.");
                    break;
                }
                if (check.AtValue != null)
                {
                    var axis = ModelFamilyFlexChecks.Axes[check.AtAxis];
                    if (plane.Axis != check.AtAxis)
                        Fail($"at.{axis}", $"The {check.Kind} is not perpendicular to {axis} (normal {ModelFamilyParameterValues.Describe(new JArray(plane.Normal.Select(v => Math.Round(v, 4))))}); at cannot place it on {axis}.",
                            actual: new JArray(plane.Normal.Select(v => Math.Round(v, 4))));
                    else Number($"at.{axis}", check.AtValue, plane.Point[check.AtAxis], check.Tolerance);
                }
                if (check.Kind == "face" && check.OnPlane != null)
                {
                    if (observation.Target == null)
                        Fail("onPlane", observation.TargetMissing ?? "The reference plane could not be measured.");
                    else OnPlane("onPlane", plane.Point, plane.Normal, observation.Target, check.Tolerance, requireSameDirection: false);
                }
                break;
            }

            case "parameter":
            {
                var actual = observation.Value;
                if (check.EqualsValue != null) Equal("equals", check.EqualsValue, actual, check.Tolerance);
                var number = ModelFamilyFlexExpr.ToNumber(actual);
                if (check.MinValue != null)
                {
                    var (min, error) = check.MinValue.Number(values);
                    if (min == null) Fail("min", $"min: the expectation cannot be evaluated: {error}.", expression: check.MinValue.Text());
                    else if (number == null) Fail("min", $"'{check.Parameter}' = {ModelFamilyParameterValues.Describe(actual)} is not a number.", new JValue(min.Value), actual?.DeepClone());
                    else if (number < min - check.Tolerance) Fail("min", $"'{check.Parameter}' = {Format(number.Value)} is below the minimum {Format(min.Value)}.", new JValue(min.Value), new JValue(number.Value), number - min);
                }
                if (check.MaxValue != null)
                {
                    var (max, error) = check.MaxValue.Number(values);
                    if (max == null) Fail("max", $"max: the expectation cannot be evaluated: {error}.", expression: check.MaxValue.Text());
                    else if (number == null) Fail("max", $"'{check.Parameter}' = {ModelFamilyParameterValues.Describe(actual)} is not a number.", new JValue(max.Value), actual?.DeepClone());
                    else if (number > max + check.Tolerance) Fail("max", $"'{check.Parameter}' = {Format(number.Value)} is above the maximum {Format(max.Value)}.", new JValue(max.Value), new JValue(number.Value), number - max);
                }
                break;
            }

            case "material":
            {
                var (expected, error) = check.EqualsValue!.Value(values);
                if (error != null)
                {
                    Fail("equals", $"equals: the expectation cannot be evaluated: {error}.", expression: check.EqualsValue.Text());
                    break;
                }
                var wanted = Text(expected);
                if (observation.Materials != null)
                {
                    if (!observation.Materials.Any(m => string.Equals(m, wanted, StringComparison.OrdinalIgnoreCase)))
                        Fail("equals", $"No solid of the instance has the material '{wanted}'; it has {(observation.Materials.Count == 0 ? "none" : string.Join(", ", observation.Materials.Select(m => $"'{m}'")))}.",
                            expected?.DeepClone(), new JArray(observation.Materials), expression: check.EqualsValue.Parameter != null ? check.EqualsValue.Text() : null);
                }
                else if (!string.Equals(Text(observation.Value), wanted, StringComparison.OrdinalIgnoreCase))
                    Fail("equals", $"The material is {Quoted(observation.Value)}, expected {Quoted(expected)}{(check.EqualsValue.Parameter != null ? $" ({check.EqualsValue.Text()})" : string.Empty)}.",
                        expected?.DeepClone(), observation.Value?.DeepClone(), expression: check.EqualsValue.Parameter != null ? check.EqualsValue.Text() : null);
                break;
            }

            case "connector":
            {
                Point("origin", check.Origin, observation.Point, check.Tolerance);
                if (check.Direction != null)
                {
                    if (observation.Direction == null) Fail("direction", "The connector's direction could not be read.", new JArray(check.Direction));
                    else
                    {
                        var angle = ModelFamilyFlexMath.AngleDegrees(check.Direction, observation.Direction);
                        if (angle > DirectionToleranceDegrees)
                            Fail("direction", $"The connector points {Describe(observation.Direction)}, expected {Describe(check.Direction)}: {angle:0.###}° apart.",
                                new JArray(check.Direction), new JArray(observation.Direction.Select(v => Math.Round(v, 6))), angle);
                    }
                }
                if (check.OnFaceElement != null)
                {
                    if (observation.Target == null) Fail("onFace", observation.TargetMissing ?? $"The form has no '{check.OnFaceLabel}' face in this case.");
                    else if (observation.Point == null) Fail("onFace", "The connector's origin could not be read.");
                    else OnPlane("onFace", observation.Point, observation.Direction, observation.Target, check.Tolerance, requireSameDirection: true);
                }
                TextEqual("systemType", check.SystemType, observation.SystemType);
                TextEqual("domain", check.Domain, observation.Domain);
                TextEqual("system", check.System, observation.System);
                if (check.Connected != null && observation.Connected != check.Connected)
                    Fail("connected", check.Connected.Value
                            ? $"The connector is not connected to a system{(observation.System != null ? string.Empty : " (no system)")}."
                            : $"The connector is connected{(observation.System != null ? $" to '{observation.System}'" : string.Empty)}.",
                        new JValue(check.Connected.Value), observation.Connected == null ? null : new JValue(observation.Connected.Value));
                foreach (var pair in check.Values)
                {
                    if (!observation.Values.TryGetValue(pair.Key, out var actual))
                    {
                        Fail($"values.{pair.Key}", $"The connector has no parameter '{pair.Key}'.");
                        continue;
                    }
                    Equal($"values.{pair.Key}", pair.Value, actual, Math.Min(check.Tolerance, ModelFamilyFlexChecks.DefaultTolerance));
                }
                break;
            }

            case "symbol":
                if (observation.Point == null) Fail("measure", "The nested instance's position could not be read.");
                else Point("at", check.At, observation.Point, check.Tolerance);
                break;

            case "exists":
                if (observation.Exists == false) Fail("exists", "An element of the check no longer exists in this case.");
                if (observation.Empty.Count > 0)
                    Fail("geometry", $"Element(s) {string.Join(", ", observation.Empty)} have no solid geometry in this case (zero volume).", actual: new JArray(observation.Empty));
                break;

            case "cut":
                if (observation.CutBy == null) Fail("measure", "What cuts the solid could not be read.");
                else if (!observation.CutBy.Contains(check.Void))
                    Fail("cutBy", $"The void {check.Void} does not cut the solid {check.Solid} in this case; it is cut by {(observation.CutBy.Count == 0 ? "nothing" : string.Join(", ", observation.CutBy))}.",
                        new JValue(check.Void), new JArray(observation.CutBy));
                break;

            case "hosted":
            {
                var hosted = observation.Hosted == true || observation.NeedsHost == true;
                if (hosted != check.Hosted)
                    Fail("hosted", check.Hosted
                            ? "The instance has no host."
                            : observation.Hosted == true
                                ? $"The instance is hosted by {observation.Host ?? "an element"}."
                                : $"The family's placement type ({observation.Placement}) needs a host.",
                        new JValue(check.Hosted), new JValue(hosted));
                break;
            }
        }
        return failures;

        void TextEqual(string what, string? expected, string? actual)
        {
            if (expected == null) return;
            if (!string.Equals(expected.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase))
                Fail(what, $"{what} is {(actual == null ? "not set" : $"'{actual}'")}, expected '{expected}'.", new JValue(expected), actual == null ? null : new JValue(actual));
        }

        void Equal(string what, ModelFamilyFlexExpr expr, JToken? actual, double tolerance)
        {
            var (expected, error) = expr.Value(values);
            if (error != null)
            {
                Fail(what, $"{what}: the expectation cannot be evaluated: {error}.", expression: expr.Text());
                return;
            }
            if (!Same(expected, actual, tolerance, out var difference))
                Fail(what, $"{what} is {Quoted(actual)}, expected {Quoted(expected)}{(expr.Parameter != null ? $" ({expr.Text()})" : string.Empty)}.",
                    expected?.DeepClone(), actual?.DeepClone(), difference, expr.Parameter != null ? expr.Text() : null);
        }

        void OnPlane(string what, double[] point, double[]? direction, ModelFamilyFlexPlane target, double tolerance, bool requireSameDirection)
        {
            var normal = ModelFamilyFlexMath.Unit(target.Normal);
            var offset = new[] { point[0] - target.Point[0], point[1] - target.Point[1], point[2] - target.Point[2] };
            var distance = Math.Abs(ModelFamilyFlexMath.Dot(offset, normal));
            if (distance > tolerance)
                Fail(what, $"{what}: {Format(distance)} mm off the {(check.Kind == "face" ? "reference plane" : "face")} (tolerance {Format(tolerance)}).", new JValue(0), new JValue(Math.Round(distance, 4)), distance);
            if (direction == null) return;
            var angle = ModelFamilyFlexMath.AngleDegrees(direction, normal);
            var off = requireSameDirection ? angle : Math.Min(angle, 180 - angle);
            if (off > DirectionToleranceDegrees)
                Fail($"{what}.direction", requireSameDirection
                        ? $"{what}: the connector points {Describe(direction)}, but the face faces {Describe(normal)} ({angle:0.###}° apart)."
                        : $"{what}: the face is not parallel to the reference plane ({off:0.###}° apart).",
                    new JArray(normal.Select(v => Math.Round(v, 6))), new JArray(direction.Select(v => Math.Round(v, 6))), off);
        }
    }

    /// <summary>A <c>count</c> check over a set of instances.</summary>
    public static List<ModelFamilyFlexFailure> EvaluateCount(ModelFamilyFlexCheck check, int count)
    {
        var failures = new List<ModelFamilyFlexFailure>();
        if (check.CountMin != null && count < check.CountMin)
            failures.Add(new ModelFamilyFlexFailure { Id = check.Id, Kind = check.Kind, What = "min", Expected = check.CountMin, Actual = count, Difference = count - check.CountMin.Value, Message = $"{count} instance(s) of '{check.Type}', expected at least {check.CountMin}." });
        if (check.CountMax != null && count > check.CountMax)
            failures.Add(new ModelFamilyFlexFailure { Id = check.Id, Kind = check.Kind, What = "max", Expected = check.CountMax, Actual = count, Difference = count - check.CountMax.Value, Message = $"{count} instance(s) of '{check.Type}', expected at most {check.CountMax}." });
        return failures;
    }

    /// <summary>
    /// Two values as a check compares them: numbers (yes/no as 1/0) within <paramref name="tolerance"/>, text ignoring
    /// case and surrounding spaces, null only with null.
    /// </summary>
    public static bool Same(JToken? expected, JToken? actual, double tolerance, out double? difference)
    {
        difference = null;
        var expectedNull = expected == null || expected.Type == JTokenType.Null;
        var actualNull = actual == null || actual.Type == JTokenType.Null;
        if (expectedNull || actualNull) return expectedNull && actualNull;
        if (expected!.Type == JTokenType.Boolean || actual!.Type == JTokenType.Boolean)
            return ModelFamilyFlexExpr.ToNumber(expected) is { } eb && ModelFamilyFlexExpr.ToNumber(actual) is { } ab && Math.Abs(eb - ab) < 1e-9;
        var e = expected.Type is JTokenType.Integer or JTokenType.Float ? ModelFamilyJson.AsNumber(expected) : null;
        var a = ModelFamilyJson.AsNumber(actual);
        if (e != null && a != null)
        {
            difference = a.Value - e.Value;
            return Math.Abs(difference.Value) <= tolerance;
        }
        return string.Equals(Text(expected), Text(actual), StringComparison.OrdinalIgnoreCase);
    }

    private static string Text(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? string.Empty
        : token.Type == JTokenType.String ? ((string?)token ?? string.Empty).Trim()
        : token.ToString(Newtonsoft.Json.Formatting.None);

    private static string Quoted(JToken? token) =>
        token == null || token.Type == JTokenType.Null ? "(none)" : token.Type == JTokenType.String ? $"'{(string?)token}'" : ModelFamilyParameterValues.Describe(token);

    private static string Describe(double[] vector) => "[" + string.Join(", ", vector.Select(v => Math.Round(v, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture))) + "]";

    public static string Format(double value) => Math.Round(value, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>The result of one flex case, before it is written out.</summary>
public sealed class ModelFamilyFlexCaseResult
{
    public ModelFamilyFlexCase Case { get; set; } = new();
    /// <summary>Revit committed the case's values; false = it refused to regenerate them (the case was rolled back).</summary>
    public bool Committed { get; set; }
    public string? Error { get; set; }
    public JArray RevitFailures { get; set; } = new();
    public List<ModelFamilyFlexFailure> Failures { get; } = new();
    public Dictionary<string, ModelFamilyFlexObservation> Observations { get; } = new(StringComparer.Ordinal);
    /// <summary>The values of the parameters the checks read, after regeneration.</summary>
    public JObject Parameters { get; set; } = new();

    public string Status => !Committed ? "refused" : Failures.Count > 0 ? "failed" : "ok";

    public JObject ToJson()
    {
        var measurements = new JObject();
        foreach (var pair in Observations) measurements[pair.Key] = pair.Value.ToJson();
        var row = new JObject
        {
            ["case"] = Case.Name,
            ["kind"] = Case.Kind,
            ["type"] = Case.Type.Length == 0 ? null : Case.Type,
            ["inputs"] = Case.Inputs(),
            ["status"] = Status,
            ["failedChecks"] = new JArray(Failures.Select(f => f.ToJson())),
            ["revitFailures"] = RevitFailures.DeepClone()
        };
        if (Error != null) row["error"] = Error;
        if (Committed)
        {
            row["measurements"] = measurements;
            row["parameters"] = Parameters.DeepClone();
        }
        return row;
    }
}

/// <summary>What happens across cases (#176): positions that must not move, the summary, and two runs compared. Pure.</summary>
public static class ModelFamilyFlexRun
{
    /// <summary>
    /// The <c>fixed</c> and <c>originFixed</c> parts of the checks: the first committed case that measured the position
    /// is the reference, and every other case where it moved by more than the tolerance gets a failure.
    /// </summary>
    public static void ApplyFixed(IReadOnlyList<ModelFamilyFlexCaseResult> results, IEnumerable<ModelFamilyFlexCheck> checks)
    {
        foreach (var check in checks.Where(c => c.HasFixed))
        {
            ModelFamilyFlexCaseResult? reference = null;
            double[]? referencePoint = null;
            double? referenceCoordinate = null;
            foreach (var result in results)
            {
                if (!result.Committed || !result.Observations.TryGetValue(check.Id, out var observation) || observation.Missing != null) continue;
                var usesPoint = check.Kind is "connector" or "symbol";
                var point = usesPoint ? observation.Point : null;
                var coordinate = usesPoint ? null : observation.Plane?.Coordinate;
                if (point == null && coordinate == null) continue;
                if (reference == null)
                {
                    reference = result;
                    referencePoint = point;
                    referenceCoordinate = coordinate;
                    continue;
                }
                var what = check.Kind == "connector" ? "originFixed" : "fixed";
                if (point != null && referencePoint != null)
                {
                    var distance = ModelFamilyFlexMath.Distance(point, referencePoint);
                    if (distance > check.Tolerance)
                        result.Failures.Add(new ModelFamilyFlexFailure
                        {
                            Id = check.Id, Kind = check.Kind, What = what,
                            Expected = ModelFamilyFlexMath.Json(referencePoint), Actual = ModelFamilyFlexMath.Json(point), Difference = Math.Round(distance, 4),
                            Message = $"Moved {ModelFamilyFlexEvaluator.Format(distance)} mm from where it is in case '{reference.Case.Name}' (tolerance {ModelFamilyFlexEvaluator.Format(check.Tolerance)})."
                        });
                }
                else if (coordinate != null && referenceCoordinate != null)
                {
                    var difference = coordinate.Value - referenceCoordinate.Value;
                    if (Math.Abs(difference) > check.Tolerance)
                        result.Failures.Add(new ModelFamilyFlexFailure
                        {
                            Id = check.Id, Kind = check.Kind, What = what,
                            Expected = referenceCoordinate.Value, Actual = coordinate.Value, Difference = Math.Round(difference, 4),
                            Message = $"Moved {ModelFamilyFlexEvaluator.Format(difference)} mm along its normal from where it is in case '{reference.Case.Name}' (tolerance {ModelFamilyFlexEvaluator.Format(check.Tolerance)})."
                        });
                }
            }
        }
    }

    public static JObject Summarize(IReadOnlyList<ModelFamilyFlexCaseResult> results, IEnumerable<ModelFamilyFlexCheck> checks)
    {
        var rows = new JArray();
        foreach (var check in checks)
        {
            var failed = results.Where(r => r.Failures.Any(f => f.Id == check.Id)).Select(r => r.Case.Name).ToList();
            rows.Add(new JObject
            {
                ["id"] = check.Id,
                ["kind"] = check.Kind,
                ["failedCases"] = failed.Count,
                ["cases"] = new JArray(failed.Take(10))
            });
        }
        return new JObject
        {
            ["cases"] = results.Count,
            ["passed"] = results.Count(r => r.Status == "ok"),
            ["failed"] = results.Count(r => r.Status == "failed"),
            ["refused"] = results.Count(r => r.Status == "refused"),
            ["checks"] = rows
        };
    }

    /// <summary>
    /// Compares the type cases (<c>kind = type</c>) of two runs as written by <see cref="ModelFamilyFlexCaseResult.ToJson"/>:
    /// the first on the family, the second on its saved and reopened copy. Per type: equal, or the differences
    /// (status, failed checks, measured values beyond <paramref name="tolerance"/>).
    /// </summary>
    public static JArray Compare(JArray first, JArray second, double tolerance = 0.01)
    {
        var rows = new JArray();
        var copy = second.OfType<JObject>().Where(c => (string?)c["kind"] == "type").ToList();
        foreach (var original in first.OfType<JObject>().Where(c => (string?)c["kind"] == "type"))
        {
            var type = (string?)original["type"];
            var match = copy.FirstOrDefault(c => string.Equals((string?)c["type"], type, StringComparison.OrdinalIgnoreCase));
            var differences = new List<string>();
            if (match == null) differences.Add("the type was not exercised in the reopened copy");
            else
            {
                var statusA = (string?)original["status"];
                var statusB = (string?)match["status"];
                if (statusA != statusB) differences.Add($"status: {statusA} before, {statusB} after reopening");
                var failedA = FailedIds(original);
                var failedB = FailedIds(match);
                foreach (var id in failedB.Except(failedA)) differences.Add($"check '{id}' fails only after reopening");
                foreach (var id in failedA.Except(failedB)) differences.Add($"check '{id}' fails only before reopening");
                Diff(original["measurements"], match["measurements"], "measurements", tolerance, differences);
                // Only the parameters both runs read: the first also reads those its ranges and cases set, which the
                // copy (type cases only) does not.
                if (original["parameters"] is JObject paramsA && match["parameters"] is JObject paramsB)
                    foreach (var key in paramsA.Properties().Select(p => p.Name).Where(k => paramsB.ContainsKey(k)))
                        Diff(paramsA[key], paramsB[key], $"parameters.{key}", tolerance, differences);
                else Diff(original["parameters"], match["parameters"], "parameters", tolerance, differences);
            }
            rows.Add(new JObject
            {
                ["type"] = type,
                ["equal"] = differences.Count == 0,
                ["differences"] = new JArray(differences.Take(20))
            });
        }
        return rows;
    }

    private static List<string> FailedIds(JObject row) =>
        (row["failedChecks"] as JArray ?? new JArray()).Select(f => (string?)f["id"] ?? string.Empty).Distinct().ToList();

    /// <summary>Structural difference of two JSON values; numbers within <paramref name="tolerance"/> are equal.</summary>
    public static void Diff(JToken? a, JToken? b, string path, double tolerance, List<string> differences, int max = 20)
    {
        if (differences.Count >= max) return;
        var aNull = a == null || a.Type == JTokenType.Null;
        var bNull = b == null || b.Type == JTokenType.Null;
        if (aNull || bNull)
        {
            if (aNull != bNull) differences.Add($"{path}: {Show(a)} before, {Show(b)} after reopening");
            return;
        }
        if (a is JObject oa && b is JObject ob)
        {
            foreach (var key in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
                Diff(oa[key], ob[key], $"{path}.{key}", tolerance, differences, max);
            return;
        }
        if (a is JArray aa && b is JArray ab)
        {
            if (aa.Count != ab.Count)
            {
                differences.Add($"{path}: {Show(a)} before, {Show(b)} after reopening");
                return;
            }
            for (var i = 0; i < aa.Count; i++) Diff(aa[i], ab[i], $"{path}[{i}]", tolerance, differences, max);
            return;
        }
        if (!ModelFamilyFlexEvaluator.Same(a, b, tolerance, out _))
            differences.Add($"{path}: {Show(a)} before, {Show(b)} after reopening");
    }

    private static string Show(JToken? token) => token == null || token.Type == JTokenType.Null ? "(none)" : token.ToString(Newtonsoft.Json.Formatting.None);
}

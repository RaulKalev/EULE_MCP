using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

public enum ModelFamilyFormKind { Extrusion, Blend, Revolve, Sweep, SweptBlend }

/// <summary>
/// What every form to create shares (#171): solid or void, the work plane, and optional subcategory and visibility.
/// <see cref="Source"/> is the argument path (<c>extrusions[0]</c>), used in messages and by <c>{"created": …}</c>.
/// </summary>
public abstract class ModelFamilyFormSpec
{
    public abstract ModelFamilyFormKind Kind { get; }
    public string Source { get; set; } = string.Empty;
    /// <summary>True for a solid, false for a void.</summary>
    public bool Solid { get; set; } = true;
    /// <summary>The <c>workPlane</c> argument as given: a reference plane name or id, <c>"level"</c> (null), or {origin, normal}.</summary>
    public JToken? WorkPlane { get; set; }
    /// <summary>A subcategory of the family's category, by name; empty for none.</summary>
    public string Subcategory { get; set; } = string.Empty;
    public bool? Visible { get; set; }

    /// <summary>The keys every form accepts besides its own.</summary>
    internal static readonly string[] CommonKeys = { "solid", "workPlane", "subcategory", "visible" };

    internal void ReadCommon(JObject obj, List<string> problems)
    {
        Solid = ModelFamilyJson.Bool(obj, "solid", Source, problems) ?? true;
        WorkPlane = ModelFamilyJson.Get(obj, "workPlane");
        Subcategory = ModelFamilyJson.Str(obj, "subcategory");
        Visible = ModelFamilyJson.Bool(obj, "visible", Source, problems);
    }
}

/// <summary><c>extrusions[i]</c>: a profile extruded from <see cref="Start"/> to <see cref="End"/> along the work plane's normal.</summary>
public sealed class ModelFamilyFormExtrusionSpec : ModelFamilyFormSpec
{
    public override ModelFamilyFormKind Kind => ModelFamilyFormKind.Extrusion;
    public ModelFamilyFormProfile Profile { get; set; } = null!;
    /// <summary>Offset of the start face from the work plane, mm.</summary>
    public double Start { get; set; }
    /// <summary>Offset of the end face from the work plane, mm.</summary>
    public double End { get; set; }
}

/// <summary><c>blends[i]</c>: one bottom and one top loop, both given on the work plane; the top is lifted to <see cref="End"/>.</summary>
public sealed class ModelFamilyFormBlendSpec : ModelFamilyFormSpec
{
    public override ModelFamilyFormKind Kind => ModelFamilyFormKind.Blend;
    public ModelFamilyFormProfile Bottom { get; set; } = null!;
    public ModelFamilyFormProfile Top { get; set; } = null!;
    public double Start { get; set; }
    public double End { get; set; }
}

/// <summary><c>revolves[i]</c>: a profile turned about an axis on the work plane from <see cref="StartAngle"/> to <see cref="EndAngle"/> (degrees).</summary>
public sealed class ModelFamilyFormRevolveSpec : ModelFamilyFormSpec
{
    public override ModelFamilyFormKind Kind => ModelFamilyFormKind.Revolve;
    public ModelFamilyFormProfile Profile { get; set; } = null!;
    public double[] AxisStart { get; set; } = null!;
    public double[] AxisEnd { get; set; } = null!;
    public double StartAngle { get; set; }
    public double EndAngle { get; set; } = 360;
}

/// <summary>
/// <c>sweeps[i]</c>: a path on the work plane and a profile in the profile plane's own 2D coordinates <c>[u, v]</c>
/// (Revit maps the XY plane onto the plane perpendicular to the path).
/// </summary>
public sealed class ModelFamilyFormSweepSpec : ModelFamilyFormSpec
{
    public override ModelFamilyFormKind Kind => ModelFamilyFormKind.Sweep;
    public ModelFamilyFormPath Path { get; set; } = null!;
    public ModelFamilyFormProfile Profile { get; set; } = null!;
    /// <summary>Where on <see cref="ProfileCurve"/> the profile plane sits: start, middle or end.</summary>
    public string ProfilePlane { get; set; } = "start";
    /// <summary>Index of the path segment the profile plane is on.</summary>
    public int ProfileCurve { get; set; }
}

/// <summary><c>sweptBlends[i]</c>: one path curve and a bottom and top loop in the profile planes' 2D coordinates.</summary>
public sealed class ModelFamilyFormSweptBlendSpec : ModelFamilyFormSpec
{
    public override ModelFamilyFormKind Kind => ModelFamilyFormKind.SweptBlend;
    public ModelFamilyFormPath Path { get; set; } = null!;
    public ModelFamilyFormProfile Bottom { get; set; } = null!;
    public ModelFamilyFormProfile Top { get; set; } = null!;
}

/// <summary>
/// A form named by an operation: an element id, or <c>{"created": "extrusions[0]"}</c> for a form this call creates
/// (resolved after creation from the source path to the new id).
/// </summary>
public sealed class ModelFamilyFormRef
{
    public string Source { get; init; } = string.Empty;
    public long? Id { get; init; }
    /// <summary>The normalised source path of a form created in this call, e.g. <c>extrusions[0]</c>.</summary>
    public string? Created { get; init; }

    private static readonly Regex CreatedPattern = new(@"^\s*(extrusions|blends|revolves|sweeps|sweptBlends)\s*\[\s*(\d+)\s*\]\s*$", RegexOptions.IgnoreCase);

    public override string ToString() => Created != null ? $"the form created by {Created}" : $"element {Id}";

    public static ModelFamilyFormRef? Parse(JToken? token, string source, List<string> problems, bool allowCreated = true)
    {
        if (token == null)
        {
            problems.Add($"{source} is required: an element id, or {{\"created\": \"extrusions[0]\"}} for a form this call creates.");
            return null;
        }
        if (token is JObject obj)
        {
            ModelFamilyJson.CheckKeys(obj, source, problems, "created");
            var path = ModelFamilyJson.Str(obj, "created");
            var match = CreatedPattern.Match(path);
            if (!match.Success)
            {
                problems.Add($"{source}.created: '{path}' is not a form list entry; use extrusions[i], blends[i], revolves[i], sweeps[i] or sweptBlends[i].");
                return null;
            }
            if (!allowCreated)
            {
                problems.Add($"{source}: remove runs before anything is created, so it cannot name {path}; give an element id.");
                return null;
            }
            var list = ModelFamilyFormPlan.FormLists.First(l => l.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
            return new ModelFamilyFormRef { Source = source, Created = $"{list}[{int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)}]" };
        }
        var id = ModelFamilyJson.AsId(token);
        if (id is null or <= 0)
        {
            problems.Add($"{source}: '{token.ToString(Newtonsoft.Json.Formatting.None)}' is not an element id; give the id revit_get_model_family lists, or {{\"created\": \"extrusions[0]\"}}.");
            return null;
        }
        return new ModelFamilyFormRef { Source = source, Id = id };
    }

    /// <summary>The element id: <see cref="Id"/>, or the id the form <see cref="Created"/> got (null when it was not created).</summary>
    public long? Resolve(IReadOnlyDictionary<string, long> created) =>
        Id ?? (Created != null && created.TryGetValue(Created, out var id) ? id : null);
}

/// <summary><c>edit[i]</c>: changes an existing form's offsets, angles, visibility or subcategory.</summary>
public sealed class ModelFamilyFormEditSpec
{
    public string Source { get; set; } = string.Empty;
    public ModelFamilyFormRef Form { get; set; } = null!;
    public double? Start { get; set; }
    public double? End { get; set; }
    public double? StartAngle { get; set; }
    public double? EndAngle { get; set; }
    public bool? Visible { get; set; }
    public string? Subcategory { get; set; }
}

/// <summary><c>cuts[i]</c>: a void cuts a solid, or (remove) stops cutting it.</summary>
public sealed class ModelFamilyFormCutSpec
{
    public string Source { get; set; } = string.Empty;
    public ModelFamilyFormRef Void { get; set; } = null!;
    public ModelFamilyFormRef Solid { get; set; } = null!;
    public bool Remove { get; set; }
}

/// <summary><c>joins[i]</c>: joins forms into one geometry combination, or (unjoin) dissolves a combination.</summary>
public sealed class ModelFamilyFormJoinSpec
{
    public string Source { get; set; } = string.Empty;
    public List<ModelFamilyFormRef> Elements { get; set; } = new();
    public ModelFamilyFormRef? Combination { get; set; }
    public bool Unjoin { get; set; }
}

/// <summary>
/// The operations of one <c>revit_edit_model_family_forms</c> call, parsed and checked without Revit. They run in
/// <see cref="Order"/>: remove, extrusions, blends, revolves, sweeps, sweptBlends, edit, cuts, joins — so an edit, a
/// cut or a join can name a form created in the same call with <c>{"created": "extrusions[0]"}</c>.
/// </summary>
public sealed class ModelFamilyFormPlan
{
    public const string RemoveKey = "remove";
    public const string ExtrusionsKey = "extrusions";
    public const string BlendsKey = "blends";
    public const string RevolvesKey = "revolves";
    public const string SweepsKey = "sweeps";
    public const string SweptBlendsKey = "sweptBlends";
    public const string EditKey = "edit";
    public const string CutsKey = "cuts";
    public const string JoinsKey = "joins";

    /// <summary>The order the operation lists run in.</summary>
    public static readonly string[] Order = { RemoveKey, ExtrusionsKey, BlendsKey, RevolvesKey, SweepsKey, SweptBlendsKey, EditKey, CutsKey, JoinsKey };

    /// <summary>The lists that create forms, in order.</summary>
    public static readonly string[] FormLists = { ExtrusionsKey, BlendsKey, RevolvesKey, SweepsKey, SweptBlendsKey };

    /// <summary>Why <c>edit[i].profile</c> is refused, and what to do instead.</summary>
    public const string ProfileEditLimitation =
        "replacing a form's profile is not supported: Revit's sketch editing (SketchEditScope) works only in project documents, " +
        "not in family documents. Remove the form and create it again with the new profile (dimensions, labels and associations on " +
        "the old form are lost and must be added again), or change its offsets and angles here.";

    public List<ModelFamilyFormRef> Remove { get; } = new();
    public List<ModelFamilyFormExtrusionSpec> Extrusions { get; } = new();
    public List<ModelFamilyFormBlendSpec> Blends { get; } = new();
    public List<ModelFamilyFormRevolveSpec> Revolves { get; } = new();
    public List<ModelFamilyFormSweepSpec> Sweeps { get; } = new();
    public List<ModelFamilyFormSweptBlendSpec> SweptBlends { get; } = new();
    public List<ModelFamilyFormEditSpec> Edits { get; } = new();
    public List<ModelFamilyFormCutSpec> Cuts { get; } = new();
    public List<ModelFamilyFormJoinSpec> Joins { get; } = new();

    /// <summary>Every form to create, in creation order.</summary>
    public IEnumerable<ModelFamilyFormSpec> Forms =>
        Extrusions.Cast<ModelFamilyFormSpec>().Concat(Blends).Concat(Revolves).Concat(Sweeps).Concat(SweptBlends);

    public int OperationCount => Remove.Count + Forms.Count() + Edits.Count + Cuts.Count + Joins.Count;

    /// <summary>One line: "2 extrusions, 1 cut".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        void Add(int count, string one, string many) { if (count > 0) parts.Add($"{count} {(count == 1 ? one : many)}"); }
        Add(Remove.Count, "form removed", "forms removed");
        Add(Extrusions.Count, "extrusion", "extrusions");
        Add(Blends.Count, "blend", "blends");
        Add(Revolves.Count, "revolve", "revolves");
        Add(Sweeps.Count, "sweep", "sweeps");
        Add(SweptBlends.Count, "swept blend", "swept blends");
        Add(Edits.Count, "form edit", "form edits");
        Add(Cuts.Count(c => !c.Remove), "cut", "cuts");
        Add(Cuts.Count(c => c.Remove), "cut removed", "cuts removed");
        Add(Joins.Count(j => !j.Unjoin), "join", "joins");
        Add(Joins.Count(j => j.Unjoin), "unjoin", "unjoins");
        return parts.Count == 0 ? "nothing" : string.Join(", ", parts);
    }

    /// <summary>Reads every operation list; bad input goes to <paramref name="problems"/> with its source path.</summary>
    public static ModelFamilyFormPlan Parse(IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var plan = new ModelFamilyFormPlan();
        ParseRemove(ModelFamilyJson.Token(args, RemoveKey), plan, problems);

        var extrusions = ModelFamilyJson.Objects(args, ExtrusionsKey, problems);
        for (var i = 0; i < extrusions.Count; i++)
            if (ParseExtrusion(extrusions[i], $"{ExtrusionsKey}[{i}]", problems) is { } spec) plan.Extrusions.Add(spec);
        var blends = ModelFamilyJson.Objects(args, BlendsKey, problems);
        for (var i = 0; i < blends.Count; i++)
            if (ParseBlend(blends[i], $"{BlendsKey}[{i}]", problems) is { } spec) plan.Blends.Add(spec);
        var revolves = ModelFamilyJson.Objects(args, RevolvesKey, problems);
        for (var i = 0; i < revolves.Count; i++)
            if (ParseRevolve(revolves[i], $"{RevolvesKey}[{i}]", problems) is { } spec) plan.Revolves.Add(spec);
        var sweeps = ModelFamilyJson.Objects(args, SweepsKey, problems);
        for (var i = 0; i < sweeps.Count; i++)
            if (ParseSweep(sweeps[i], $"{SweepsKey}[{i}]", problems) is { } spec) plan.Sweeps.Add(spec);
        var sweptBlends = ModelFamilyJson.Objects(args, SweptBlendsKey, problems);
        for (var i = 0; i < sweptBlends.Count; i++)
            if (ParseSweptBlend(sweptBlends[i], $"{SweptBlendsKey}[{i}]", problems) is { } spec) plan.SweptBlends.Add(spec);

        var edits = ModelFamilyJson.Objects(args, EditKey, problems);
        for (var i = 0; i < edits.Count; i++)
            if (ParseEdit(edits[i], $"{EditKey}[{i}]", problems) is { } spec) plan.Edits.Add(spec);
        var cuts = ModelFamilyJson.Objects(args, CutsKey, problems);
        for (var i = 0; i < cuts.Count; i++)
            if (ParseCut(cuts[i], $"{CutsKey}[{i}]", problems) is { } spec) plan.Cuts.Add(spec);
        var joins = ModelFamilyJson.Objects(args, JoinsKey, problems);
        for (var i = 0; i < joins.Count; i++)
            if (ParseJoin(joins[i], $"{JoinsKey}[{i}]", problems) is { } spec) plan.Joins.Add(spec);

        // Counts from the raw lists, so a reference to a form whose own spec has a problem is not reported twice.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [ExtrusionsKey] = extrusions.Count, [BlendsKey] = blends.Count, [RevolvesKey] = revolves.Count,
            [SweepsKey] = sweeps.Count, [SweptBlendsKey] = sweptBlends.Count
        };
        foreach (var reference in plan.References())
        {
            if (reference.Created == null) continue;
            var bracket = reference.Created.IndexOf('[');
            var list = reference.Created.Substring(0, bracket);
            var index = int.Parse(reference.Created.Substring(bracket + 1, reference.Created.Length - bracket - 2), System.Globalization.CultureInfo.InvariantCulture);
            if (index >= counts[list])
                problems.Add($"{reference.Source}: {reference.Created} does not exist; this call has {counts[list]} {list} entr{(counts[list] == 1 ? "y" : "ies")}.");
        }
        if (problems.Count == 0 && plan.OperationCount == 0)
            problems.Add($"Nothing to do: give at least one of {string.Join(", ", Order)}.");
        return plan;
    }

    /// <summary>Every form reference of the edit, cut and join operations.</summary>
    public IEnumerable<ModelFamilyFormRef> References() =>
        Edits.Select(e => e.Form)
            .Concat(Cuts.SelectMany(c => new[] { c.Void, c.Solid }))
            .Concat(Joins.SelectMany(j => j.Elements.Concat(j.Combination == null ? Enumerable.Empty<ModelFamilyFormRef>() : new[] { j.Combination })));

    private static void ParseRemove(JToken? token, ModelFamilyFormPlan plan, List<string> problems)
    {
        if (token == null) return;
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        for (var i = 0; i < items.Count; i++)
        {
            var source = $"{RemoveKey}[{i}]";
            var item = items[i];
            if (item is JObject obj && ModelFamilyJson.Has(obj, "form"))
            {
                ModelFamilyJson.CheckKeys(obj, source, problems, "form");
                item = ModelFamilyJson.Get(obj, "form")!;
                source += ".form";
            }
            if (ModelFamilyFormRef.Parse(item, source, problems, allowCreated: false) is { } reference) plan.Remove.Add(reference);
        }
    }

    private static (double Start, double End)? Offsets(JObject obj, string source, List<string> problems)
    {
        var start = ModelFamilyJson.Number(obj, "start", source, problems);
        var end = ModelFamilyJson.Number(obj, "end", source, problems);
        var depth = ModelFamilyJson.Number(obj, "depth", source, problems);
        if (ModelFamilyJson.Has(obj, "depth"))
        {
            if (ModelFamilyJson.Has(obj, "start") || ModelFamilyJson.Has(obj, "end"))
            {
                problems.Add($"{source}: give depth, or start and end, not both (depth means start 0, end depth).");
                return null;
            }
            if (depth == null) return null;
            start = 0;
            end = depth;
        }
        if (end == null)
        {
            if (!ModelFamilyJson.Has(obj, "end")) problems.Add($"{source}.end is required: the end offset from the work plane in mm (or give depth).");
            return null;
        }
        start ??= 0;
        if (Math.Abs(end.Value - start.Value) < ModelFamilyFormCheck.RevitShortCurveMm)
        {
            problems.Add($"{source}: start ({ModelFamilyFormGeometry.Mm(start.Value)}) and end ({ModelFamilyFormGeometry.Mm(end.Value)}) must differ by at least {ModelFamilyFormGeometry.Mm(ModelFamilyFormCheck.RevitShortCurveMm)} mm.");
            return null;
        }
        return (start.Value, end.Value);
    }

    private static ModelFamilyFormExtrusionSpec? ParseExtrusion(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, ModelFamilyFormSpec.CommonKeys.Concat(new[] { "profile", "start", "end", "depth" }).ToArray());
        var before = problems.Count;
        var spec = new ModelFamilyFormExtrusionSpec { Source = source };
        spec.ReadCommon(obj, problems);
        var profile = ModelFamilyFormProfile.Parse(ModelFamilyJson.Get(obj, "profile"), $"{source}.profile", problems);
        var offsets = Offsets(obj, source, problems);
        if (problems.Count > before || profile == null || offsets == null) return null;
        spec.Profile = profile;
        (spec.Start, spec.End) = offsets.Value;
        return spec;
    }

    private static ModelFamilyFormBlendSpec? ParseBlend(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, ModelFamilyFormSpec.CommonKeys.Concat(new[] { "bottomProfile", "topProfile", "start", "end", "depth" }).ToArray());
        var before = problems.Count;
        var spec = new ModelFamilyFormBlendSpec { Source = source };
        spec.ReadCommon(obj, problems);
        var bottom = ModelFamilyFormProfile.Parse(ModelFamilyJson.Get(obj, "bottomProfile"), $"{source}.bottomProfile", problems);
        var top = ModelFamilyFormProfile.Parse(ModelFamilyJson.Get(obj, "topProfile"), $"{source}.topProfile", problems);
        if (bottom != null && bottom.Loops.Count != 1) problems.Add($"{source}.bottomProfile: a blend takes exactly one loop (got {bottom.Loops.Count}).");
        if (top != null && top.Loops.Count != 1) problems.Add($"{source}.topProfile: a blend takes exactly one loop (got {top.Loops.Count}).");
        var offsets = Offsets(obj, source, problems);
        if (problems.Count > before || bottom == null || top == null || offsets == null) return null;
        spec.Bottom = bottom;
        spec.Top = top;
        (spec.Start, spec.End) = offsets.Value;
        return spec;
    }

    /// <summary>Checks a start/end angle pair in degrees: each within ±360, different, at most a full turn apart.</summary>
    public static bool CheckAngles(double start, double end, string source, List<string> problems)
    {
        var ok = true;
        if (Math.Abs(start) > 360) { problems.Add($"{source}.startAngle: must be between -360 and 360 degrees (got {ModelFamilyFormGeometry.Mm(start)})."); ok = false; }
        if (Math.Abs(end) > 360) { problems.Add($"{source}.endAngle: must be between -360 and 360 degrees (got {ModelFamilyFormGeometry.Mm(end)})."); ok = false; }
        if (!ok) return false;
        var span = Math.Abs(end - start);
        if (span < 0.01)
        {
            problems.Add($"{source}: startAngle and endAngle are both {ModelFamilyFormGeometry.Mm(start)}°; a revolve must turn through an angle.");
            return false;
        }
        if (span > 360 + 1e-9)
        {
            problems.Add($"{source}: from {ModelFamilyFormGeometry.Mm(start)}° to {ModelFamilyFormGeometry.Mm(end)}° is more than a full turn ({ModelFamilyFormGeometry.Mm(span)}°).");
            return false;
        }
        return true;
    }

    private static ModelFamilyFormRevolveSpec? ParseRevolve(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, ModelFamilyFormSpec.CommonKeys.Concat(new[] { "profile", "axis", "startAngle", "endAngle" }).ToArray());
        var before = problems.Count;
        var spec = new ModelFamilyFormRevolveSpec { Source = source };
        spec.ReadCommon(obj, problems);
        var profile = ModelFamilyFormProfile.Parse(ModelFamilyJson.Get(obj, "profile"), $"{source}.profile", problems);
        double[]? axisStart = null, axisEnd = null;
        if (ModelFamilyJson.Get(obj, "axis") is JObject axis)
        {
            ModelFamilyJson.CheckKeys(axis, $"{source}.axis", problems, "start", "end");
            axisStart = ModelFamilyJson.Point(axis, "start", $"{source}.axis", problems);
            axisEnd = ModelFamilyJson.Point(axis, "end", $"{source}.axis", problems);
            if (axisStart != null && axisEnd != null && ModelFamilyFormGeometry.Distance(axisStart, axisEnd) < ModelFamilyFormCheck.RevitShortCurveMm)
                problems.Add($"{source}.axis: start and end must be at least {ModelFamilyFormGeometry.Mm(ModelFamilyFormCheck.RevitShortCurveMm)} mm apart.");
        }
        else problems.Add($"{source}.axis is required: {{\"start\": [x,y,z], \"end\": [x,y,z]}} on the work plane.");
        var startAngle = ModelFamilyJson.Number(obj, "startAngle", source, problems) ?? 0;
        var endAngle = ModelFamilyJson.Number(obj, "endAngle", source, problems) ?? 360;
        CheckAngles(startAngle, endAngle, source, problems);
        if (problems.Count > before || profile == null || axisStart == null || axisEnd == null) return null;
        spec.Profile = profile;
        spec.AxisStart = axisStart;
        spec.AxisEnd = axisEnd;
        spec.StartAngle = startAngle;
        spec.EndAngle = endAngle;
        return spec;
    }

    /// <summary>The profile plane locations a sweep accepts.</summary>
    public static readonly string[] ProfilePlanes = { "start", "middle", "end" };

    private static ModelFamilyFormSweepSpec? ParseSweep(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, ModelFamilyFormSpec.CommonKeys.Concat(new[] { "path", "profile", "profilePlane", "profileCurve" }).ToArray());
        var before = problems.Count;
        var spec = new ModelFamilyFormSweepSpec { Source = source };
        spec.ReadCommon(obj, problems);
        var path = ModelFamilyFormPath.Parse(ModelFamilyJson.Get(obj, "path"), $"{source}.path", problems);
        var profile = ModelFamilyFormProfile.Parse(ModelFamilyJson.Get(obj, "profile"), $"{source}.profile", problems);
        var plane = ModelFamilyJson.Str(obj, "profilePlane", "start").ToLowerInvariant();
        if (plane == "mid" || plane == "midpoint") plane = "middle";
        if (!ProfilePlanes.Contains(plane)) problems.Add($"{source}.profilePlane: '{plane}' is not one of {string.Join(", ", ProfilePlanes)}.");
        var curve = ModelFamilyJson.Number(obj, "profileCurve", source, problems) ?? 0;
        if (curve < 0 || Math.Abs(curve - Math.Round(curve)) > 1e-9 || path != null && curve >= path.Segments.Count)
            problems.Add($"{source}.profileCurve: must be the index of a path segment (0 to {(path?.Segments.Count ?? 1) - 1}).");
        if (problems.Count > before || path == null || profile == null) return null;
        spec.Path = path;
        spec.Profile = profile;
        spec.ProfilePlane = plane;
        spec.ProfileCurve = (int)Math.Round(curve);
        return spec;
    }

    private static ModelFamilyFormSweptBlendSpec? ParseSweptBlend(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, ModelFamilyFormSpec.CommonKeys.Concat(new[] { "path", "bottomProfile", "topProfile" }).ToArray());
        var before = problems.Count;
        var spec = new ModelFamilyFormSweptBlendSpec { Source = source };
        spec.ReadCommon(obj, problems);
        var path = ModelFamilyFormPath.Parse(ModelFamilyJson.Get(obj, "path"), $"{source}.path", problems);
        if (path != null && path.Segments.Count != 1) problems.Add($"{source}.path: a swept blend follows one curve (a line or an arc), got {path.Segments.Count}.");
        var bottom = ModelFamilyFormProfile.Parse(ModelFamilyJson.Get(obj, "bottomProfile"), $"{source}.bottomProfile", problems);
        var top = ModelFamilyFormProfile.Parse(ModelFamilyJson.Get(obj, "topProfile"), $"{source}.topProfile", problems);
        if (bottom != null && bottom.Loops.Count != 1) problems.Add($"{source}.bottomProfile: a swept blend takes exactly one loop (got {bottom.Loops.Count}).");
        if (top != null && top.Loops.Count != 1) problems.Add($"{source}.topProfile: a swept blend takes exactly one loop (got {top.Loops.Count}).");
        if (problems.Count > before || path == null || bottom == null || top == null) return null;
        spec.Path = path;
        spec.Bottom = bottom;
        spec.Top = top;
        return spec;
    }

    private static ModelFamilyFormEditSpec? ParseEdit(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, "form", "start", "end", "startAngle", "endAngle", "visible", "subcategory", "profile");
        var before = problems.Count;
        var form = ModelFamilyFormRef.Parse(ModelFamilyJson.Get(obj, "form"), $"{source}.form", problems);
        if (ModelFamilyJson.Has(obj, "profile")) problems.Add($"{source}.profile: {ProfileEditLimitation}");
        var spec = new ModelFamilyFormEditSpec
        {
            Source = source,
            Start = ModelFamilyJson.Number(obj, "start", source, problems),
            End = ModelFamilyJson.Number(obj, "end", source, problems),
            StartAngle = ModelFamilyJson.Number(obj, "startAngle", source, problems),
            EndAngle = ModelFamilyJson.Number(obj, "endAngle", source, problems),
            Visible = ModelFamilyJson.Bool(obj, "visible", source, problems),
            Subcategory = ModelFamilyJson.Has(obj, "subcategory") ? ModelFamilyJson.Str(obj, "subcategory") : null
        };
        if (spec.Start == null && spec.End == null && spec.StartAngle == null && spec.EndAngle == null && spec.Visible == null && spec.Subcategory == null && !ModelFamilyJson.Has(obj, "profile"))
            problems.Add($"{source}: nothing to change; give start, end, startAngle, endAngle, visible or subcategory.");
        if (spec.Start != null && spec.End != null && Math.Abs(spec.Start.Value - spec.End.Value) < ModelFamilyFormCheck.RevitShortCurveMm)
            problems.Add($"{source}: start and end must differ by at least {ModelFamilyFormGeometry.Mm(ModelFamilyFormCheck.RevitShortCurveMm)} mm.");
        if (spec.StartAngle != null && Math.Abs(spec.StartAngle.Value) > 360) problems.Add($"{source}.startAngle: must be between -360 and 360 degrees.");
        if (spec.EndAngle != null && Math.Abs(spec.EndAngle.Value) > 360) problems.Add($"{source}.endAngle: must be between -360 and 360 degrees.");
        if (spec.StartAngle != null && spec.EndAngle != null) CheckAngles(spec.StartAngle.Value, spec.EndAngle.Value, source, problems);
        if (problems.Count > before || form == null) return null;
        spec.Form = form;
        return spec;
    }

    private static ModelFamilyFormCutSpec? ParseCut(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, "void", "solid", "remove");
        var before = problems.Count;
        var cutter = ModelFamilyFormRef.Parse(ModelFamilyJson.Get(obj, "void"), $"{source}.void", problems);
        var target = ModelFamilyFormRef.Parse(ModelFamilyJson.Get(obj, "solid"), $"{source}.solid", problems);
        var remove = ModelFamilyJson.Bool(obj, "remove", source, problems) ?? false;
        if (cutter != null && target != null && (cutter.Id != null && cutter.Id == target.Id || cutter.Created != null && cutter.Created == target.Created))
            problems.Add($"{source}: void and solid are the same form.");
        if (problems.Count > before || cutter == null || target == null) return null;
        return new ModelFamilyFormCutSpec { Source = source, Void = cutter, Solid = target, Remove = remove };
    }

    private static ModelFamilyFormJoinSpec? ParseJoin(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, "elements", "combination", "unjoin");
        var before = problems.Count;
        var unjoin = ModelFamilyJson.Bool(obj, "unjoin", source, problems) ?? false;
        var spec = new ModelFamilyFormJoinSpec { Source = source, Unjoin = unjoin };
        if (unjoin)
        {
            if (ModelFamilyJson.Has(obj, "elements")) problems.Add($"{source}: unjoin takes combination (the id of a geometry combination), not elements.");
            spec.Combination = ModelFamilyFormRef.Parse(ModelFamilyJson.Get(obj, "combination"), $"{source}.combination", problems);
            if (spec.Combination?.Created != null) problems.Add($"{source}.combination: give the id of a geometry combination (revit_get_model_family include=[\"combinations\"] lists them).");
        }
        else
        {
            if (ModelFamilyJson.Has(obj, "combination")) problems.Add($"{source}: combination is only for unjoin=true; to join, list elements.");
            if (ModelFamilyJson.Get(obj, "elements") is JArray elements)
            {
                for (var i = 0; i < elements.Count; i++)
                    if (ModelFamilyFormRef.Parse(elements[i], $"{source}.elements[{i}]", problems) is { } reference) spec.Elements.Add(reference);
                if (elements.Count < 2) problems.Add($"{source}.elements: a join needs at least two forms (got {elements.Count}).");
            }
            else problems.Add($"{source}.elements is required: at least two form ids (or {{\"created\": …}}) to join.");
        }
        return problems.Count > before ? null : spec;
    }
}

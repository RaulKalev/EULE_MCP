using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using static RevitMCP.Addin.ModelFamilies.ModelFamilyFormGeometry;

namespace RevitMCP.Addin.ModelFamilies;

public enum ModelFamilyProfileSourceKind { Import, Curves, View }

/// <summary>
/// The <c>source</c> argument (#177): a CAD import in the family (<c>{"import": id | name, "layers"?: [...]}</c>, default
/// the only one), family curves by id (<c>{"curves": [ids]}</c>), or the model and symbolic lines of a view
/// (<c>{"view": name | id, "subcategory"?: name}</c>).
/// </summary>
public sealed class ModelFamilyProfileSourceSpec
{
    public const string ImportKey = "import";
    public const string LayersKey = "layers";
    public const string CurvesKey = "curves";
    public const string ViewKey = "view";
    public const string SubcategoryKey = "subcategory";

    public ModelFamilyProfileSourceKind Kind { get; init; } = ModelFamilyProfileSourceKind.Import;
    /// <summary>The import's id or name; null for the family's only import.</summary>
    public JToken? Import { get; init; }
    /// <summary>CAD layers to use; null for every layer.</summary>
    public List<string>? Layers { get; init; }
    public List<long> CurveIds { get; init; } = new();
    public JToken? View { get; init; }
    public string Subcategory { get; init; } = string.Empty;

    /// <summary>Family curves (which removeSourceCurves may delete), not a CAD import.</summary>
    public bool FamilyCurves => Kind != ModelFamilyProfileSourceKind.Import;

    public string Describe() => Kind switch
    {
        ModelFamilyProfileSourceKind.Curves => $"{CurveIds.Count} family curve(s)",
        ModelFamilyProfileSourceKind.View => $"the lines of view {View}{(Subcategory.Length > 0 ? $" in '{Subcategory}'" : string.Empty)}",
        _ => (Import == null ? "the CAD import" : $"CAD import {Import}") + (Layers == null ? string.Empty : $" (layers {string.Join(", ", Layers)})")
    };

    public static ModelFamilyProfileSourceSpec? Parse(JToken? token, List<string> problems)
    {
        const string source = ModelFamilyProfileRequest.SourceKey;
        if (token == null) return new ModelFamilyProfileSourceSpec();
        if (token is not JObject obj)
        {
            problems.Add($"{source}: must be {{\"import\": id | name, \"layers\"?: [...]}}, {{\"curves\": [ids]}} or {{\"view\": name, \"subcategory\"?: name}}.");
            return null;
        }
        ModelFamilyJson.CheckKeys(obj, source, problems, ImportKey, LayersKey, CurvesKey, ViewKey, SubcategoryKey);
        var given = new[] { ImportKey, CurvesKey, ViewKey }.Where(k => obj.Properties().Any(p => p.Name.Equals(k, StringComparison.OrdinalIgnoreCase))).ToList();
        if (given.Count > 1)
        {
            problems.Add($"{source}: give only one of import, curves or view (got {string.Join(", ", given)}).");
            return null;
        }
        var before = problems.Count;
        if (given.Count == 1 && given[0] == CurvesKey)
        {
            if (ModelFamilyJson.Has(obj, LayersKey)) problems.Add($"{source}.layers: only for an import; pick family curves by id.");
            if (ModelFamilyJson.Has(obj, SubcategoryKey)) problems.Add($"{source}.subcategory: only with view; pick family curves by id.");
            var ids = new List<long>();
            if (ModelFamilyJson.Get(obj, CurvesKey) is JArray array && array.Count > 0)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    if (ModelFamilyJson.AsId(array[i]) is { } id and > 0) { if (!ids.Contains(id)) ids.Add(id); }
                    else problems.Add($"{source}.curves[{i}]: must be the element id of a model, symbolic or reference line.");
                }
            }
            else problems.Add($"{source}.curves: must be a non-empty list of curve element ids.");
            return problems.Count > before ? null : new ModelFamilyProfileSourceSpec { Kind = ModelFamilyProfileSourceKind.Curves, CurveIds = ids };
        }
        if (given.Count == 1 && given[0] == ViewKey)
        {
            if (ModelFamilyJson.Has(obj, LayersKey)) problems.Add($"{source}.layers: only for an import; use subcategory to pick a view's lines.");
            var view = ModelFamilyJson.Get(obj, ViewKey);
            if (view == null) problems.Add($"{source}.view: give the view's name or id.");
            return problems.Count > before ? null : new ModelFamilyProfileSourceSpec
            {
                Kind = ModelFamilyProfileSourceKind.View, View = view, Subcategory = ModelFamilyJson.Str(obj, SubcategoryKey)
            };
        }
        if (ModelFamilyJson.Has(obj, SubcategoryKey)) problems.Add($"{source}.subcategory: only with view; an import's curves are picked by layers.");
        List<string>? layers = null;
        if (ModelFamilyJson.Get(obj, LayersKey) is { } layerToken)
        {
            var items = layerToken is JArray layerArray ? layerArray.ToList() : new List<JToken> { layerToken };
            layers = items.Select(t => (t.Type == JTokenType.String ? (string?)t : t.ToString())?.Trim() ?? string.Empty).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (layers.Count == 0) problems.Add($"{source}.layers: give at least one layer name (or leave layers out for every layer).");
        }
        return problems.Count > before ? null : new ModelFamilyProfileSourceSpec { Kind = ModelFamilyProfileSourceKind.Import, Import = ModelFamilyJson.Get(obj, ImportKey), Layers = layers };
    }
}

/// <summary>The arguments both profile tools share: source, workPlane, tolerance (and limit for the read tool).</summary>
public sealed class ModelFamilyProfileRequest
{
    public const string SourceKey = "source";
    public const string WorkPlaneKey = "workPlane";
    public const string ToleranceKey = "tolerance";
    public const string LimitKey = "limit";

    public ModelFamilyProfileSourceSpec Source { get; init; } = new();
    /// <summary>The workPlane argument as given; null to use the plane the curves lie in.</summary>
    public JToken? WorkPlane { get; init; }
    public double Tolerance { get; init; } = ModelFamilyProfileOptions.DefaultToleranceMm;
    public int Limit { get; init; } = 200;

    public ModelFamilyProfileOptions Options(double minSegmentMm) => new() { ToleranceMm = Tolerance, MinSegmentMm = minSegmentMm };

    public static ModelFamilyProfileRequest? Parse(IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var before = problems.Count;
        var source = ModelFamilyProfileSourceSpec.Parse(ModelFamilyJson.Token(args, SourceKey), problems);
        var tolerance = ModelFamilyProfileOptions.DefaultToleranceMm;
        if (ModelFamilyJson.Token(args, ToleranceKey) is { } toleranceToken)
        {
            var value = ModelFamilyJson.AsNumber(toleranceToken);
            if (value is not > 0 || value > ModelFamilyProfileOptions.MaxToleranceMm)
                problems.Add($"tolerance: must be a number of millimetres greater than 0 and at most {Mm(ModelFamilyProfileOptions.MaxToleranceMm)} (default {Mm(ModelFamilyProfileOptions.DefaultToleranceMm)}).");
            else tolerance = value.Value;
        }
        var limit = ModelFamilyDocumentSpec.ParseLimit(ModelFamilyJson.Token(args, LimitKey), problems);
        if (problems.Count > before || source == null) return null;
        return new ModelFamilyProfileRequest { Source = source, WorkPlane = ModelFamilyJson.Token(args, WorkPlaneKey), Tolerance = tolerance, Limit = limit };
    }
}

public enum ModelFamilyProfileHoles { Auto, None, List }

/// <summary>What a form's measured loop must match before anything is created.</summary>
public sealed class ModelFamilyProfileExpected
{
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double[]? Centre { get; init; }
    public double Tolerance { get; init; } = 0.5;
}

/// <summary><c>forms[i]</c>: which loop to extrude, its holes, solid or void, offsets, and an optional cut.</summary>
public sealed class ModelFamilyProfileFormSpec
{
    public const string LoopKey = "loop";
    public const string AtKey = "at";
    public const string HolesKey = "holes";
    public const string SolidKey = "solid";
    public const string DepthKey = "depth";
    public const string StartKey = "start";
    public const string EndKey = "end";
    public const string SubcategoryKey = "subcategory";
    public const string VisibleKey = "visible";
    public const string CutKey = "cut";
    public const string NameKey = "name";
    public const string ExpectedKey = "expected";
    public const string CreatedKey = "created";

    public static readonly string[] Keys = { LoopKey, HolesKey, SolidKey, DepthKey, StartKey, EndKey, SubcategoryKey, VisibleKey, CutKey, NameKey, ExpectedKey };

    public string Source { get; set; } = string.Empty;
    public int? Loop { get; set; }
    /// <summary>A family point: the innermost loop containing it is used.</summary>
    public double[]? At { get; set; }
    public ModelFamilyProfileHoles HolesMode { get; set; } = ModelFamilyProfileHoles.Auto;
    public List<int> Holes { get; } = new();
    public bool Solid { get; set; } = true;
    public double Start { get; set; }
    public double End { get; set; }
    public string Subcategory { get; set; } = string.Empty;
    public bool? Visible { get; set; }
    /// <summary>The solid a void cuts: an existing form id …</summary>
    public long? CutId { get; set; }
    /// <summary>… or the index of a form this call creates (<c>{"created": "forms[0]"}</c>).</summary>
    public int? CutCreated { get; set; }
    public string Name { get; set; } = string.Empty;
    public ModelFamilyProfileExpected? Expected { get; set; }

    public string Label => Name.Length > 0 ? $"{Source} '{Name}'" : Source;

    private static readonly Regex CreatedPattern = new(@"^\s*forms\s*\[\s*(\d+)\s*\]\s*$", RegexOptions.IgnoreCase);

    internal static ModelFamilyProfileFormSpec? Parse(JObject obj, string source, List<string> problems)
    {
        ModelFamilyJson.CheckKeys(obj, source, problems, Keys);
        var before = problems.Count;
        var spec = new ModelFamilyProfileFormSpec { Source = source };

        var loop = ModelFamilyJson.Get(obj, LoopKey);
        if (loop == null) problems.Add($"{source}.loop is required: the loop index from revit_get_model_family_profiles, or {{\"at\": [x, y, z]}} for the innermost loop around a point.");
        else if (loop is JObject at)
        {
            ModelFamilyJson.CheckKeys(at, $"{source}.loop", problems, AtKey);
            spec.At = ModelFamilyJson.Point(at, AtKey, $"{source}.loop", problems);
        }
        else if (ModelFamilyJson.AsNumber(loop) is { } index && index >= 0 && Math.Abs(index - Math.Round(index)) < 1e-9) spec.Loop = (int)Math.Round(index);
        else problems.Add($"{source}.loop: '{loop.ToString(Newtonsoft.Json.Formatting.None)}' is not a loop index (0, 1, …) or {{\"at\": [x, y, z]}}.");

        var holes = ModelFamilyJson.Get(obj, HolesKey);
        if (holes is JArray holeArray)
        {
            spec.HolesMode = ModelFamilyProfileHoles.List;
            for (var i = 0; i < holeArray.Count; i++)
            {
                if (ModelFamilyJson.AsNumber(holeArray[i]) is { } h && h >= 0 && Math.Abs(h - Math.Round(h)) < 1e-9) spec.Holes.Add((int)Math.Round(h));
                else problems.Add($"{source}.holes[{i}]: must be a loop index.");
            }
            if (spec.Holes.Distinct().Count() != spec.Holes.Count) problems.Add($"{source}.holes: lists a loop twice.");
        }
        else if (holes != null)
        {
            var mode = ModelFamilyJson.Str(obj, HolesKey).ToLowerInvariant();
            if (mode == "auto") spec.HolesMode = ModelFamilyProfileHoles.Auto;
            else if (mode == "none") spec.HolesMode = ModelFamilyProfileHoles.None;
            else problems.Add($"{source}.holes: '{mode}' is not \"auto\" (the loops directly inside become holes), \"none\" or a list of loop indices.");
        }

        spec.Solid = ModelFamilyJson.Bool(obj, SolidKey, source, problems) ?? true;
        spec.Subcategory = ModelFamilyJson.Str(obj, SubcategoryKey);
        spec.Visible = ModelFamilyJson.Bool(obj, VisibleKey, source, problems);
        spec.Name = ModelFamilyJson.Str(obj, NameKey);
        if (Offsets(obj, source, problems) is { } offsets) (spec.Start, spec.End) = offsets;

        if (ModelFamilyJson.Get(obj, CutKey) is { } cut)
        {
            if (spec.Solid) problems.Add($"{source}.cut: only a void (solid: false) cuts; this form is a solid.");
            if (cut is JObject created)
            {
                ModelFamilyJson.CheckKeys(created, $"{source}.cut", problems, CreatedKey);
                var match = CreatedPattern.Match(ModelFamilyJson.Str(created, CreatedKey));
                if (match.Success) spec.CutCreated = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                else problems.Add($"{source}.cut.created: must be \"forms[i]\", a form this call creates.");
            }
            else if (ModelFamilyJson.AsId(cut) is { } id and > 0) spec.CutId = id;
            else problems.Add($"{source}.cut: must be the id of a solid form, or {{\"created\": \"forms[0]\"}}.");
        }

        if (ModelFamilyJson.Get(obj, ExpectedKey) is { } expectedToken)
        {
            if (expectedToken is JObject expected)
            {
                var path = $"{source}.expected";
                ModelFamilyJson.CheckKeys(expected, path, problems, "width", "height", "centre", "tolerance");
                var width = ModelFamilyJson.Number(expected, "width", path, problems);
                var height = ModelFamilyJson.Number(expected, "height", path, problems);
                var centre = ModelFamilyJson.Point(expected, "centre", path, problems, required: false);
                var tolerance = ModelFamilyJson.Number(expected, "tolerance", path, problems) ?? 0.5;
                if (tolerance <= 0) problems.Add($"{path}.tolerance: must be greater than 0 mm.");
                if (width == null && height == null && centre == null) problems.Add($"{path}: give width, height or centre to check.");
                spec.Expected = new ModelFamilyProfileExpected { Width = width, Height = height, Centre = centre, Tolerance = tolerance };
            }
            else problems.Add($"{source}.expected: must be {{\"width\"?, \"height\"?, \"centre\"?: [x,y,z], \"tolerance\"?}} in mm.");
        }
        return problems.Count > before ? null : spec;
    }

    /// <summary>depth, or start and end (mm from the work plane along its normal), as #171's extrusions take them.</summary>
    private static (double Start, double End)? Offsets(JObject obj, string source, List<string> problems)
    {
        var start = ModelFamilyJson.Number(obj, StartKey, source, problems);
        var end = ModelFamilyJson.Number(obj, EndKey, source, problems);
        var depth = ModelFamilyJson.Number(obj, DepthKey, source, problems);
        if (ModelFamilyJson.Has(obj, DepthKey))
        {
            if (ModelFamilyJson.Has(obj, StartKey) || ModelFamilyJson.Has(obj, EndKey))
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
            if (!ModelFamilyJson.Has(obj, EndKey)) problems.Add($"{source}.depth is required (mm along the work plane's normal), or start and end.");
            return null;
        }
        start ??= 0;
        if (Math.Abs(end.Value - start.Value) < ModelFamilyFormCheck.RevitShortCurveMm)
        {
            problems.Add($"{source}: start ({Mm(start.Value)}) and end ({Mm(end.Value)}) must differ by at least {Mm(ModelFamilyFormCheck.RevitShortCurveMm)} mm.");
            return null;
        }
        return (start.Value, end.Value);
    }
}

/// <summary>
/// The arguments of revit_create_model_family_form_from_profile: the shared request, the forms, and
/// removeSourceCurves. Parsed and cross-checked without Revit.
/// </summary>
public sealed class ModelFamilyProfilePlan
{
    public const string FormsKey = "forms";
    public const string RemoveSourceCurvesKey = "removeSourceCurves";

    public ModelFamilyProfileRequest Request { get; private set; } = new();
    public List<ModelFamilyProfileFormSpec> Forms { get; } = new();
    public bool RemoveSourceCurves { get; private set; }

    /// <summary>One line: "2 forms (1 solid, 1 void), 1 cut, source curves removed".</summary>
    public string Describe()
    {
        if (Forms.Count == 0) return "nothing";
        var solids = Forms.Count(f => f.Solid);
        var text = $"{Forms.Count} form{(Forms.Count == 1 ? string.Empty : "s")} ({solids} solid, {Forms.Count - solids} void)";
        var cuts = Forms.Count(f => f.CutId != null || f.CutCreated != null);
        if (cuts > 0) text += $", {cuts} cut{(cuts == 1 ? string.Empty : "s")}";
        if (RemoveSourceCurves) text += ", source curves removed";
        return $"{text} from {Request.Source.Describe()}";
    }

    public static ModelFamilyProfilePlan Parse(IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var plan = new ModelFamilyProfilePlan();
        if (ModelFamilyProfileRequest.Parse(args, problems) is { } request) plan.Request = request;
        var forms = ModelFamilyJson.Objects(args, FormsKey, problems);
        for (var i = 0; i < forms.Count; i++)
            if (ModelFamilyProfileFormSpec.Parse(forms[i], $"{FormsKey}[{i}]", problems) is { } form) plan.Forms.Add(form);
        if (forms.Count == 0 && ModelFamilyJson.Token(args, FormsKey) == null)
            problems.Add("forms is required: [{\"loop\": <index>, \"depth\": <mm>}, …]; revit_get_model_family_profiles lists the loops.");
        if (ModelFamilyJson.Token(args, RemoveSourceCurvesKey) is { } remove)
        {
            if (remove.Type == JTokenType.Boolean) plan.RemoveSourceCurves = (bool)remove;
            else if (remove.Type == JTokenType.String && bool.TryParse((string?)remove, out var parsed)) plan.RemoveSourceCurves = parsed;
            else problems.Add("removeSourceCurves: must be true or false.");
        }
        if (plan.RemoveSourceCurves && !plan.Request.Source.FamilyCurves)
            problems.Add("removeSourceCurves: only for family curves (source curves or view); a CAD import is not deleted. Delete or hide the import yourself.");

        // Cuts between forms of this call.
        foreach (var form in plan.Forms.Where(f => f.CutCreated != null))
        {
            var target = form.CutCreated!.Value;
            if (target >= forms.Count) problems.Add($"{form.Source}.cut: forms[{target}] does not exist; this call has {forms.Count} form(s).");
            else if ($"{FormsKey}[{target}]" == form.Source) problems.Add($"{form.Source}.cut: a form cannot cut itself.");
            else if (plan.Forms.FirstOrDefault(f => f.Source == $"{FormsKey}[{target}]") is { Solid: false })
                problems.Add($"{form.Source}.cut: forms[{target}] is a void; a void cuts a solid.");
        }
        return plan;
    }
}

/// <summary>A form's chosen loops: the outer loop and its holes.</summary>
public sealed class ModelFamilyProfileChoice
{
    public ModelFamilyProfileFormSpec Spec { get; init; } = null!;
    public ModelFamilyProfileLoop Outer { get; init; } = null!;
    public List<ModelFamilyProfileLoop> Holes { get; init; } = new();
}

/// <summary>
/// Picks the loops of a form from an analysis and checks them, before anything is created (#177): a loop that is
/// open, ambiguous, crosses itself or is shorter than Revit allows is refused here, so an invalid outline never reaches
/// a transaction and the family stays unchanged.
/// </summary>
public static class ModelFamilyProfileSelection
{
    public static ModelFamilyProfileChoice? Choose(ModelFamilyProfileAnalysis analysis, ModelFamilyProfileFormSpec spec, List<string> problems)
    {
        var before = problems.Count;
        var loops = analysis.Loops;
        ModelFamilyProfileLoop? outer;
        if (spec.At != null)
        {
            outer = analysis.LoopAt(spec.At);
            if (outer == null)
            {
                problems.Add($"{spec.Source}.loop.at: no closed loop contains {Format(spec.At)}" + Hint(analysis));
                return null;
            }
        }
        else if (spec.Loop is { } index && index < loops.Count) outer = loops[index];
        else
        {
            problems.Add($"{spec.Source}.loop: there is no loop {spec.Loop}; the curves make {loops.Count} closed loop(s){(loops.Count > 0 ? $" (0 to {loops.Count - 1})" : string.Empty)}" + Hint(analysis));
            return null;
        }
        if (!outer.Usable)
            problems.Add($"{spec.Source}.loop: loop {outer.Index} cannot be built: {string.Join("; ", outer.Reasons)}.");

        var holes = new List<ModelFamilyProfileLoop>();
        switch (spec.HolesMode)
        {
            case ModelFamilyProfileHoles.Auto:
                holes.AddRange(outer.Children.Select(i => loops[i]));
                var bad = holes.Where(h => !h.Usable).ToList();
                if (bad.Count > 0)
                    problems.Add($"{spec.Source}.holes: loop(s) {string.Join(", ", bad.Select(h => h.Index))} inside loop {outer.Index} cannot be built ({string.Join("; ", bad.SelectMany(h => h.Reasons).Distinct())}); list the holes you want, or pass holes \"none\".");
                break;
            case ModelFamilyProfileHoles.List:
                for (var i = 0; i < spec.Holes.Count; i++)
                {
                    var index = spec.Holes[i];
                    if (index >= loops.Count) { problems.Add($"{spec.Source}.holes[{i}]: there is no loop {index}."); continue; }
                    var hole = loops[index];
                    if (hole.Index == outer.Index) problems.Add($"{spec.Source}.holes[{i}]: loop {index} is the outer loop itself.");
                    else if (hole.Parent != outer.Index)
                        problems.Add($"{spec.Source}.holes[{i}]: loop {index} is not directly inside loop {outer.Index} ({(hole.Parent == null ? "it is an outer loop" : $"it is inside loop {hole.Parent}")}); a hole must be one of loop {outer.Index}'s children: {(outer.Children.Count == 0 ? "it has none" : string.Join(", ", outer.Children))}.");
                    else if (!hole.Usable) problems.Add($"{spec.Source}.holes[{i}]: loop {index} cannot be built: {string.Join("; ", hole.Reasons)}.");
                    else holes.Add(hole);
                }
                break;
        }

        if (spec.Expected is { } expected)
        {
            void Compare(string name, double wanted, double measured)
            {
                if (Math.Abs(wanted - measured) > expected.Tolerance)
                    problems.Add($"{spec.Source}.expected.{name}: loop {outer.Index} measures {Mm(measured)} mm, expected {Mm(wanted)} ± {Mm(expected.Tolerance)} mm. Is it the right loop, layer or import scale? Nothing was created.");
            }
            if (expected.Width != null) Compare("width", expected.Width.Value, outer.Width);
            if (expected.Height != null) Compare("height", expected.Height.Value, outer.Height);
            if (expected.Centre != null)
            {
                var centre = analysis.Frame.ToWorld(outer.CentreUv);
                var distance = Distance(centre, Copy(expected.Centre));
                if (distance > expected.Tolerance)
                    problems.Add($"{spec.Source}.expected.centre: loop {outer.Index}'s centre is {Format(centre)}, {Mm(distance)} mm from the expected {Format(expected.Centre)} (tolerance {Mm(expected.Tolerance)} mm). Nothing was created.");
            }
        }
        return problems.Count > before ? null : new ModelFamilyProfileChoice { Spec = spec, Outer = outer, Holes = holes };
    }

    private static string Hint(ModelFamilyProfileAnalysis analysis)
    {
        var hint = ". revit_get_model_family_profiles lists them";
        if (analysis.Open.Count > 0)
        {
            var gaps = analysis.Open.SelectMany(o => o.Ends).Where(e => e.Gap != null).Select(e => e.Gap!.Value).ToList();
            hint += $"; {analysis.Open.Count} chain(s) do not close" + (gaps.Count > 0 ? $" (smallest gap {Mm(gaps.Min())} mm; a tolerance above it joins it)" : string.Empty);
        }
        return hint + ".";
    }

    /// <summary>
    /// The #171 extrusion for a choice: the outer loop and its holes as segment loops in family millimetres, sketched
    /// on <paramref name="workPlane"/> (the token #171's work plane lookup reads).
    /// </summary>
    public static ModelFamilyFormExtrusionSpec ToExtrusion(ModelFamilyProfileChoice choice, JToken? workPlane)
    {
        var spec = choice.Spec;
        var loops = new[] { choice.Outer }.Concat(choice.Holes).Select(l => new ModelFamilyFormLoop
        {
            Kind = ModelFamilyFormLoopKind.Segments,
            Source = $"{spec.Source} loop {l.Index}",
            Segments = l.World.Select(s => s.Map(Copy)).ToList()
        }).ToList();
        return new ModelFamilyFormExtrusionSpec
        {
            Source = spec.Source,
            Solid = spec.Solid,
            WorkPlane = workPlane,
            Subcategory = spec.Subcategory,
            Visible = spec.Visible,
            Profile = new ModelFamilyFormProfile { Source = $"{spec.Source}.loop", Loops = loops },
            Start = spec.Start,
            End = spec.End
        };
    }
}

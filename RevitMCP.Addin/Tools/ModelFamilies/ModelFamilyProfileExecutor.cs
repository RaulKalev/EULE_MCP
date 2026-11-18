using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>The source read, the plane chosen and the analysis on it: what both profile tools start from.</summary>
internal sealed class ModelFamilyProfilePrepared
{
    public ModelFamilyProfileSourceData Source { get; init; } = null!;
    public ModelFamilyProfileAnalysis Analysis { get; init; } = null!;
    /// <summary>The work plane forms are sketched on, as #171's workPlane argument: null (the reference level), a reference plane id, or {origin, normal}.</summary>
    public JToken? WorkPlaneToken { get; init; }
    public JObject WorkPlane { get; init; } = new();
}

/// <summary>
/// The bodies of revit_get_model_family_profiles and revit_preview_/revit_create_model_family_form_from_profile (#177).
/// Both run the same <see cref="Prepare"/> (source curves, work plane, <see cref="ModelFamilyProfileAnalysis"/>), so a
/// loop index means the same in both. The create body checks every form (loop choice, holes, expected size, #171's
/// profile and work-plane checks, cut targets) before its first transaction, so an invalid outline changes nothing.
/// </summary>
internal static class ModelFamilyProfileExecutor
{
    public const string ReadWhat = "analyse profile curves";
    public const string CreateWhat = "create forms from profiles";

    private const double MmPerFoot = ModelFamilyJson.MmPerFoot;

    /// <summary>The plan of a request in one line, for the approval summary.</summary>
    public static string Plan(McpToolRequest request) =>
        ModelFamilyProfilePlan.Parse(request.Arguments ?? new Dictionary<string, object?>(), new List<string>()).Describe();

    // ── Read ─────────────────────────────────────────────────────────────────

    public static object? Analyse(ModelFamilyContext context)
    {
        var request = ModelFamilyProfileRequest.Parse(context.Args, context.Problems);
        if (request == null) return null;
        var result = new JObject();
        var prepared = Prepare(context, request, result);
        if (prepared == null) return result;
        var analysis = prepared.Analysis;
        foreach (var property in analysis.ToJson(request.Limit).Properties()) result[property.Name] = property.Value;
        result["next"] = analysis.Loops.Any(l => l.Usable)
            ? "Create a form with revit_preview_create_model_family_form_from_profile / revit_create_model_family_form_from_profile: the same source, workPlane and tolerance, and forms [{\"loop\": <index>, \"depth\": <mm>}]."
            : analysis.Open.Count > 0
                ? "No usable loop: open lists the chains that do not close and the gap at each end; raise tolerance (up to 1 mm) to join small gaps, or fix the drawing."
                : "No usable loop: check the issues, the layers included, or the work plane.";
        context.Summary = $"{analysis.Loops.Count} loop(s) ({analysis.Loops.Count(l => l.Usable)} usable), {analysis.Open.Count} open chain(s), {analysis.Issues.Count} issue(s)";
        return result;
    }

    /// <summary>
    /// Reads the source and analyses it on the work plane (the given one, or the plane the curves lie in, matched to
    /// the reference level or a reference plane when they coincide). Writes source and workPlane into <paramref name="result"/>.
    /// </summary>
    internal static ModelFamilyProfilePrepared? Prepare(ModelFamilyContext context, ModelFamilyProfileRequest request, JObject result)
    {
        var doc = context.FamilyDoc;
        var problems = context.Problems;
        var check = ModelFamilyFormBuilder.Check(context);
        var source = ModelFamilyProfileSource.Read(context, request.Source, check.MinSegmentMm);
        if (source == null) return null;
        result["source"] = source.Description;
        result["tolerance"] = request.Tolerance;
        if (source.Curves.Count == 0)
        {
            problems.Add($"source: {request.Source.Describe()} has no lines, arcs or curves to analyse" +
                         (request.Source.Layers != null ? "; check source.layers (source.layers lists each layer's curve count)." : "."));
            return null;
        }
        var options = request.Options(check.MinSegmentMm);
        var points = source.Curves.SelectMany(c => c.Points()).ToList();

        JToken? token;
        ModelFamilyFormFrame frame;
        var plane = new JObject();
        if (request.WorkPlane != null)
        {
            var revitPlane = ModelFamilyLookup.WorkPlane(doc, request.WorkPlane, ModelFamilyProfileRequest.WorkPlaneKey, problems, out var referencePlane);
            if (revitPlane == null) return null;
            token = request.WorkPlane;
            frame = ModelFamilyProfilePlane.FrameFor(ModelFamilyFormBuilder.Mm(revitPlane.Origin), Direction(revitPlane.Normal));
            plane["given"] = true;
            plane["normal"] = ModelFamilyLookup.Direction(revitPlane.Normal);
            if (referencePlane != null) plane["coincidesWith"] = new JObject { ["kind"] = "referencePlane", ["id"] = referencePlane.Id.Value, ["name"] = referencePlane.Name };
            else if (IsLevel(request.WorkPlane) && ModelFamilyLookup.ReferenceLevel(doc) is { } level)
                plane["coincidesWith"] = new JObject { ["kind"] = "level", ["id"] = level.Id.Value, ["name"] = level.Name };
        }
        else
        {
            var fit = ModelFamilyProfilePlane.Fit(points, out var spread);
            if (fit == null)
            {
                problems.Add("source: every curve lies on one straight line, so they enclose nothing and span no plane.");
                return null;
            }
            var (origin, normal) = fit.Value;
            if (spread > options.OffPlaneMm)
            {
                problems.Add($"source: the curves are not on one plane: they spread {ModelFamilyFormGeometry.Mm(spread)} mm along the normal {ModelFamilyFormGeometry.Format(normal)} (more than {ModelFamilyFormGeometry.Mm(options.OffPlaneMm)} mm). Pass workPlane (a reference plane, \"level\" or {{origin, normal}}) to analyse the curves on it; curves off it are listed as issues.");
                result["workPlane"] = new JObject { ["fitted"] = true, ["normal"] = new JArray(normal.Select(v => Math.Round(v, 6))), ["spread"] = Math.Round(spread, 3) };
                return null;
            }
            (token, frame) = Coinciding(doc, points, origin, normal, options.OffPlaneMm, plane);
            plane["fitted"] = true;
            plane["spread"] = Math.Round(spread, 3);
        }
        plane["origin"] = ModelFamilyProfileAnalysis.Point(frame.Origin);
        plane["xAxis"] = new JArray(frame.XAxis.Select(v => Math.Round(v, 6)));
        plane["yAxis"] = new JArray(frame.YAxis.Select(v => Math.Round(v, 6)));
        plane["argument"] = token?.DeepClone() ?? "level";
        plane["note"] = "width is measured along xAxis, height along yAxis; a form's depth runs along normal (the normal of the plane it is sketched on).";
        result["workPlane"] = plane;

        var analysis = ModelFamilyProfileAnalysis.Run(source.Curves, frame, options);
        return new ModelFamilyProfilePrepared { Source = source, Analysis = analysis, WorkPlaneToken = token, WorkPlane = plane };
    }

    private static bool IsLevel(JToken token) =>
        token.Type == JTokenType.String && string.Equals(((string?)token)?.Trim(), "level", StringComparison.OrdinalIgnoreCase);

    private static double[] Direction(XYZ v) => new[] { v.X, v.Y, v.Z };

    /// <summary>
    /// The work plane for fitted curves: the reference level when they all lie on it, else a reference plane they all
    /// lie on (named planes first), else the fitted plane as {origin, normal}.
    /// </summary>
    private static (JToken? Token, ModelFamilyFormFrame Frame) Coinciding(Document doc, List<double[]> points, double[] origin, double[] normal,
        double tolerance, JObject plane)
    {
        bool AllOn(double[] planeOrigin, double[] planeNormal) =>
            points.All(p => Math.Abs(ModelFamilyFormGeometry.Dot(ModelFamilyFormGeometry.Sub(p, planeOrigin), ModelFamilyFormGeometry.Normalize(planeNormal))) <= tolerance);

        if (ModelFamilyLookup.ReferenceLevel(doc) is { } level && ModelFamilyProfilePlane.Parallel(normal, new[] { 0.0, 0, 1 }))
        {
            var levelOrigin = new[] { 0, 0, level.Elevation * MmPerFoot };
            if (AllOn(levelOrigin, new[] { 0.0, 0, 1 }))
            {
                plane["coincidesWith"] = new JObject { ["kind"] = "level", ["id"] = level.Id.Value, ["name"] = level.Name };
                plane["normal"] = new JArray(0, 0, 1);
                return (null, ModelFamilyProfilePlane.FrameFor(levelOrigin, new[] { 0.0, 0, 1 }));
            }
        }
        var planes = ModelFamilyLookup.ReferencePlanes(doc)
            .OrderBy(rp => string.IsNullOrWhiteSpace(rp.Name) ? 1 : 0).ThenBy(rp => rp.Id.Value);
        foreach (var referencePlane in planes)
        {
            Plane geometry;
            try { geometry = referencePlane.GetPlane(); }
            catch { continue; }
            var planeNormal = Direction(geometry.Normal);
            var planeOrigin = ModelFamilyFormBuilder.Mm(geometry.Origin);
            if (!ModelFamilyProfilePlane.Parallel(normal, planeNormal) || !AllOn(planeOrigin, planeNormal)) continue;
            plane["coincidesWith"] = new JObject { ["kind"] = "referencePlane", ["id"] = referencePlane.Id.Value, ["name"] = referencePlane.Name };
            plane["normal"] = ModelFamilyLookup.Direction(geometry.Normal);
            return (new JValue(referencePlane.Id.Value), ModelFamilyProfilePlane.FrameFor(planeOrigin, planeNormal));
        }
        plane["normal"] = new JArray(normal.Select(v => Math.Round(v, 6)));
        var token = new JObject
        {
            ["origin"] = new JArray(origin.Select(v => Math.Round(v, 6))),
            ["normal"] = new JArray(normal.Select(v => Math.Round(v, 9)))
        };
        return (token, ModelFamilyProfilePlane.FrameFor(origin, normal));
    }

    // ── Create ───────────────────────────────────────────────────────────────

    private sealed class Ready
    {
        public ModelFamilyProfileChoice Choice { get; init; } = null!;
        public ModelFamilyFormExtrusionSpec Spec { get; init; } = null!;
        public ModelFamilyFormPrepared Prepared { get; init; } = null!;
    }

    public static object? Create(ModelFamilyContext context)
    {
        var doc = context.FamilyDoc;
        var problems = context.Problems;
        var plan = ModelFamilyProfilePlan.Parse(context.Args, problems);
        context.Summary = plan.Describe();
        var result = new JObject();
        if (problems.Count > 0) return result;
        if (IsAnnotationOrDetail(doc))
        {
            problems.Add($"'{context.FamilyName}' is a {doc.OwnerFamily.FamilyCategory?.Name} family; solid and void forms need a 3D model family (for example one made from Metric Generic Model.rft).");
            return result;
        }

        // 1. Analyse and check everything before changing the family.
        var prepared = Prepare(context, plan.Request, result);
        if (prepared == null) return result;
        var analysis = prepared.Analysis;
        result["counts"] = analysis.ToJson(0)["counts"];

        var ready = new List<Ready>();
        var previews = new JArray();
        foreach (var form in plan.Forms)
        {
            var choice = ModelFamilyProfileSelection.Choose(analysis, form, problems);
            if (choice == null) continue;
            var spec = ModelFamilyProfileSelection.ToExtrusion(choice, prepared.WorkPlaneToken);
            var formPrepared = ModelFamilyFormBuilder.Prepare(context, spec);
            var row = Describe(choice, analysis);
            if (formPrepared != null)
            {
                foreach (var property in formPrepared.Describe().Properties())
                    if (row[property.Name] == null) row[property.Name] = property.Value;
                ready.Add(new Ready { Choice = choice, Spec = spec, Prepared = formPrepared });
            }
            previews.Add(row);
        }
        foreach (var form in plan.Forms.Where(f => f.CutId != null))
        {
            var element = doc.GetElement(new ElementId(form.CutId!.Value));
            if (element is not GenericForm target)
                problems.Add(element == null
                    ? $"{form.Source}.cut: the family has no element {form.CutId}."
                    : $"{form.Source}.cut: element {form.CutId} is {ModelFamilyLookup.Describe(element)}, not a solid form.");
            else if (ModelFamilyFormBuilder.SafeIsSolid(target) == false)
                problems.Add($"{form.Source}.cut: form {form.CutId} is a void; a void cuts a solid.");
        }
        foreach (var group in ready.Where(r => r.Spec.Solid).GroupBy(r => r.Choice.Outer.Index).Where(g => g.Count() > 1))
            context.Warnings.Add($"Loop {group.Key} is used by {group.Count()} solids ({string.Join(", ", group.Select(r => r.Spec.Source))}); they overlap.");
        result["forms"] = previews;
        if (problems.Count > 0) return result;

        // 2. Create, one transaction per form; a refusal rolls the whole edit back.
        var created = new Dictionary<int, long>();
        var createdRows = new JArray();
        for (var i = 0; i < plan.Forms.Count; i++)
        {
            var item = ready.First(r => r.Spec.Source == plan.Forms[i].Source);
            var form = context.Transact($"Create {item.Spec.Source}", () =>
                ModelFamilyFormBuilder.CreateExtrusion(context, item.Spec, item.Prepared) ?? throw new InvalidOperationException($"Revit did not create {item.Spec.Source}"));
            created[i] = form.Id.Value;
            var row = ModelFamilyFormBuilder.Verify(context, form, item.Prepared);
            foreach (var property in Describe(item.Choice, analysis).Properties())
                if (row[property.Name] == null) row[property.Name] = property.Value;
            row["constrain"] =
                $"An ordinary editable extrusion: lock its faces to reference planes with revit_edit_model_family_references (revit_get_model_family include=[\"forms\"] lists its face labels), and change its offsets with revit_edit_model_family_forms edit [{{\"form\": {form.Id.Value}, \"end\": …}}].";
            createdRows.Add(row);
        }
        result["created"] = createdRows;
        result["createdIds"] = new JArray(created.OrderBy(c => c.Key).Select(c => c.Value));

        // 3. Cuts, through #171's cut code (solid-solid cut, else a geometry combination).
        var cuts = new JArray();
        for (var i = 0; i < plan.Forms.Count; i++)
        {
            var form = plan.Forms[i];
            var solid = form.CutId ?? (form.CutCreated is { } index && created.TryGetValue(index, out var id) ? id : (long?)null);
            if (solid != null) cuts.Add(new JObject { ["void"] = created[i], ["solid"] = solid.Value });
        }
        if (cuts.Count > 0) result["cuts"] = RunCuts(context, cuts);
        for (var i = 0; i < plan.Forms.Count; i++)
            if (!plan.Forms[i].Solid && plan.Forms[i].CutId == null && plan.Forms[i].CutCreated == null)
                context.Warnings.Add($"{plan.Forms[i].Source}: the void {created[i]} cuts nothing yet; give cut (a solid's id, or {{\"created\": \"forms[0]\"}}), or cut it with revit_edit_model_family_forms cuts.");

        // 4. The family curves the forms were made from.
        if (plan.RemoveSourceCurves)
        {
            var ids = ready.SelectMany(r => new[] { r.Choice.Outer }.Concat(r.Choice.Holes)).SelectMany(l => l.SourceIds).Distinct().OrderBy(id => id).ToList();
            var removed = new JArray();
            var alsoDeleted = new List<long>();
            context.Transact("Remove source curves", () =>
            {
                foreach (var id in ids)
                {
                    var element = doc.GetElement(new ElementId(id));
                    if (element is not CurveElement) continue;
                    var deleted = doc.Delete(element.Id).Select(d => d.Value).ToList();
                    removed.Add(id);
                    alsoDeleted.AddRange(deleted.Where(d => d != id && !ids.Contains(d)));
                }
            });
            result["removedCurves"] = removed;
            if (alsoDeleted.Count > 0)
            {
                result["alsoDeleted"] = new JArray(alsoDeleted.Distinct());
                context.Warnings.Add($"Removing the source curves also deleted {alsoDeleted.Distinct().Count()} dependent element(s) (dimensions or constraints on them): {string.Join(", ", alsoDeleted.Distinct().Take(10))}.");
            }
        }
        context.Summary = plan.Describe();
        return result;
    }

    /// <summary>What a form is made from: the loops used and the profile measured from the curves (mm).</summary>
    private static JObject Describe(ModelFamilyProfileChoice choice, ModelFamilyProfileAnalysis analysis)
    {
        var outer = choice.Outer;
        var row = new JObject
        {
            ["source"] = choice.Spec.Source,
            ["loop"] = outer.Index,
            ["holes"] = new JArray(choice.Holes.Select(h => h.Index)),
            ["measured"] = new JObject
            {
                ["width"] = Math.Round(outer.Width, 3),
                ["height"] = Math.Round(outer.Height, 3),
                ["area"] = Math.Round(outer.Area - choice.Holes.Sum(h => h.Area), 3),
                ["min"] = ModelFamilyProfileAnalysis.Point(analysis.Frame.ToWorld(outer.Min)),
                ["max"] = ModelFamilyProfileAnalysis.Point(analysis.Frame.ToWorld(outer.Max)),
                ["centre"] = ModelFamilyProfileAnalysis.Point(analysis.Frame.ToWorld(outer.CentreUv))
            }
        };
        if (choice.Spec.Name.Length > 0) row["name"] = choice.Spec.Name;
        if (outer.Approximated || choice.Holes.Any(h => h.Approximated)) row["approximated"] = true;
        if (choice.Spec.CutId != null) row["cut"] = choice.Spec.CutId;
        else if (choice.Spec.CutCreated != null) row["cut"] = new JObject { ["created"] = $"forms[{choice.Spec.CutCreated}]" };
        return row;
    }

    /// <summary>
    /// Runs #171's cut step (<see cref="ModelFamilyFormExecutor"/>) on the forms just created, in the same family and
    /// transaction group, and merges what it reports.
    /// </summary>
    private static JToken RunCuts(ModelFamilyContext context, JArray cuts)
    {
        var sub = new ModelFamilyContext
        {
            Uiapp = context.Uiapp,
            FamilyDoc = context.FamilyDoc,
            Project = context.Project,
            Request = new McpToolRequest { ToolName = context.Request.ToolName, Arguments = new Dictionary<string, object?> { [ModelFamilyFormPlan.CutsKey] = cuts } },
            Target = context.Target,
            FamilyName = context.FamilyName,
            Apply = context.Apply,
            CancellationToken = context.CancellationToken
        };
        var output = ModelFamilyFormExecutor.Run(sub) as JObject;
        context.Problems.AddRange(sub.Problems);
        context.Warnings.AddRange(sub.Warnings);
        context.Failures.AddRange(sub.Failures);
        return output?[ModelFamilyFormPlan.CutsKey] ?? new JArray();
    }

    private static bool IsAnnotationOrDetail(Document doc)
    {
        try
        {
            var category = doc.OwnerFamily?.FamilyCategory;
            return category != null && (category.CategoryType == CategoryType.Annotation || category.Id.Value == (long)BuiltInCategory.OST_DetailComponents);
        }
        catch
        {
            return false;
        }
    }
}

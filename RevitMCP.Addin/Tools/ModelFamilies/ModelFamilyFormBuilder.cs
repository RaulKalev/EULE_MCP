using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// A form spec checked against the family document, ready to build: the resolved work plane, the profile(s) projected
/// onto it, the path or axis, and the subcategory. Made by <see cref="ModelFamilyFormBuilder.Prepare"/>, which needs
/// no transaction.
/// </summary>
internal sealed class ModelFamilyFormPrepared
{
    public ModelFamilyFormSpec Spec { get; init; } = null!;
    /// <summary>The work plane as resolved from the spec (the sketch plane is created from it inside the transaction).</summary>
    public Plane Plane { get; init; } = null!;
    public ModelFamilyFormFrame Frame { get; init; } = null!;
    /// <summary>The reference plane the form is sketched on (and follows), when the work plane names one.</summary>
    public ReferencePlane? ReferencePlane { get; init; }
    /// <summary>Extrusion and revolve profile on the work plane; sweep profile in its own XY coordinates.</summary>
    public ModelFamilyFormPlanarProfile? Profile { get; set; }
    /// <summary>Blend loops (on the work plane) and swept blend loops (in their own XY coordinates).</summary>
    public ModelFamilyFormPlanarProfile? Bottom { get; set; }
    public ModelFamilyFormPlanarProfile? Top { get; set; }
    public ModelFamilyFormPlanarPath? Path { get; set; }
    /// <summary>A revolve's axis on the work plane, family millimetres.</summary>
    public double[]? AxisStart { get; set; }
    public double[]? AxisEnd { get; set; }
    public Category? Subcategory { get; set; }

    /// <summary>What the preview shows for the form before it exists: plane, measured profile sizes, path.</summary>
    public JObject Describe()
    {
        var row = new JObject
        {
            ["source"] = Spec.Source,
            ["kind"] = ModelFamilyFormBuilder.KindName(Spec.Kind),
            ["solid"] = Spec.Solid,
            ["workPlane"] = new JObject
            {
                ["referencePlane"] = ReferencePlane == null ? null : new JObject { ["id"] = ReferencePlane.Id.Value, ["name"] = ReferencePlane.Name },
                ["origin"] = ModelFamilyLookup.Mm(Plane.Origin),
                ["normal"] = ModelFamilyLookup.Direction(Plane.Normal)
            }
        };
        if (Profile != null) row["profile"] = Profile.Describe();
        if (Bottom != null) row["bottomProfile"] = Bottom.Describe();
        if (Top != null) row["topProfile"] = Top.Describe();
        if (Path != null) row["path"] = Path.Describe();
        switch (Spec)
        {
            case ModelFamilyFormExtrusionSpec e: row["start"] = e.Start; row["end"] = e.End; break;
            case ModelFamilyFormBlendSpec b: row["start"] = b.Start; row["end"] = b.End; break;
            case ModelFamilyFormRevolveSpec r: row["startAngle"] = r.StartAngle; row["endAngle"] = r.EndAngle; break;
        }
        if (Spec.Subcategory.Length > 0) row["subcategory"] = Spec.Subcategory;
        if (Spec.Visible != null) row["visible"] = Spec.Visible;
        return row;
    }
}

/// <summary>
/// Builds solid and void forms in a family document (#171). Reused by #177 (forms from CAD profiles): parse or build
/// a <see cref="ModelFamilyFormSpec"/> (its profile is a <see cref="ModelFamilyFormProfile"/>: loops of line and arc
/// segments in family millimetres), then call <see cref="Prepare"/> to check it (no transaction needed; problems go to
/// <see cref="ModelFamilyContext.Problems"/> with the spec's source path) and one of the <c>Create…</c> methods
/// <b>inside</b> <see cref="ModelFamilyContext.Transact"/>. After the transaction, <see cref="Verify"/> reads back what
/// Revit built (bounding box, offsets, sizes) and reports differences as warnings.
/// </summary>
internal static class ModelFamilyFormBuilder
{
    private const double MmPerFoot = ModelFamilyJson.MmPerFoot;

    public static string KindName(ModelFamilyFormKind kind) => kind switch
    {
        ModelFamilyFormKind.Extrusion => "extrusion",
        ModelFamilyFormKind.Blend => "blend",
        ModelFamilyFormKind.Revolve => "revolve",
        ModelFamilyFormKind.Sweep => "sweep",
        _ => "sweptBlend"
    };

    // ── Conversions ──────────────────────────────────────────────────────────

    /// <summary>A Revit point (feet) as an unrounded millimetre point for the pure maths.</summary>
    public static double[] Mm(XYZ p) => new[] { p.X * MmPerFoot, p.Y * MmPerFoot, p.Z * MmPerFoot };

    public static XYZ Feet(double[] mm) => new(mm[0] / MmPerFoot, mm[1] / MmPerFoot, mm[2] / MmPerFoot);

    /// <summary>
    /// A Revit plane as a millimetre frame, for <see cref="ModelFamilyFormProfile.OnPlane"/>: its origin and normal, and
    /// axes along the family axes when the plane is square to them (Revit's own X/Y axes of a plane made from a normal
    /// are arbitrary), else the plane's own axes.
    /// </summary>
    public static ModelFamilyFormFrame Frame(Plane plane) =>
        ModelFamilyFormFrame.AlignedToFamily(Mm(plane.Origin), new[] { plane.Normal.X, plane.Normal.Y, plane.Normal.Z },
            new[] { plane.XVec.X, plane.XVec.Y, plane.XVec.Z }, new[] { plane.YVec.X, plane.YVec.Y, plane.YVec.Z });

    /// <summary>The checks with this Revit's short-curve tolerance.</summary>
    public static ModelFamilyFormCheck Check(ModelFamilyContext context)
    {
        var check = new ModelFamilyFormCheck();
        try { check.MinSegmentMm = context.Uiapp.Application.ShortCurveTolerance * MmPerFoot; }
        catch { /* the default is Revit's documented value */ }
        return check;
    }

    /// <summary>A segment in family millimetres as a Revit curve.</summary>
    public static Curve ToCurve(ModelFamilyFormSegment segment) => segment.Kind == ModelFamilyFormSegmentKind.Line
        ? Line.CreateBound(Feet(segment.Start), Feet(segment.End))
        : Arc.Create(Feet(segment.Start), Feet(segment.End), Feet(segment.Mid!));

    public static CurveArray ToCurveArray(IEnumerable<ModelFamilyFormSegment> segments)
    {
        var array = new CurveArray();
        foreach (var segment in segments) array.Append(ToCurve(segment));
        return array;
    }

    /// <summary>
    /// A checked profile as Revit's loops. <paramref name="planeCoordinates"/> uses the loops' plane coordinates as
    /// XY (z = 0), which is what sweep and swept-blend profiles need; otherwise the family coordinates on the plane.
    /// </summary>
    public static CurveArrArray ToCurveArrArray(ModelFamilyFormPlanarProfile profile, bool planeCoordinates = false)
    {
        var array = new CurveArrArray();
        foreach (var loop in profile.Loops) array.Append(ToCurveArray(planeCoordinates ? loop.Uv : loop.World));
        return array;
    }

    // ── Prepare (no transaction) ─────────────────────────────────────────────

    /// <summary>
    /// Checks a spec against the family: work plane, profiles on it, path, axis, subcategory. Problems go to
    /// <c>context.Problems</c>; null when there is any. Changes nothing.
    /// </summary>
    public static ModelFamilyFormPrepared? Prepare(ModelFamilyContext context, ModelFamilyFormSpec spec)
    {
        var problems = context.Problems;
        var before = problems.Count;
        var doc = context.FamilyDoc;
        var plane = ModelFamilyLookup.WorkPlane(doc, spec.WorkPlane, $"{spec.Source}.workPlane", problems, out var referencePlane);
        Category? subcategory = null;
        if (spec.Subcategory.Length > 0)
        {
            subcategory = ModelFamilyLookup.Subcategory(doc, spec.Subcategory);
            if (subcategory == null)
            {
                var known = new List<string>();
                try { foreach (Category sub in doc.OwnerFamily.FamilyCategory.SubCategories) known.Add(sub.Name); }
                catch { /* no category */ }
                var close = ModelFamilyJson.Suggest(spec.Subcategory, known);
                problems.Add($"{spec.Source}.subcategory: the family's category has no subcategory '{spec.Subcategory}'" +
                             (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : known.Count > 0 ? $". It has {string.Join(", ", known.Take(12).Select(n => $"'{n}'"))}." : ". Create it first."));
            }
        }
        if (plane == null) return null;
        var frame = Frame(plane);
        var check = Check(context);
        var prepared = new ModelFamilyFormPrepared { Spec = spec, Plane = plane, Frame = frame, ReferencePlane = referencePlane, Subcategory = subcategory };
        switch (spec)
        {
            case ModelFamilyFormExtrusionSpec e:
                prepared.Profile = e.Profile.OnPlane(frame, check, problems);
                break;
            case ModelFamilyFormBlendSpec b:
                prepared.Bottom = b.Bottom.OnPlane(frame, check, problems, singleLoop: true);
                prepared.Top = b.Top.OnPlane(frame, check, problems, singleLoop: true);
                break;
            case ModelFamilyFormRevolveSpec r:
                prepared.Profile = r.Profile.OnPlane(frame, check, problems);
                foreach (var (point, name) in new[] { (r.AxisStart, "start"), (r.AxisEnd, "end") })
                {
                    var offset = frame.OffsetOf(point);
                    if (Math.Abs(offset) > check.OffPlaneMm)
                        problems.Add($"{r.Source}.axis.{name}: {ModelFamilyFormGeometry.Format(point)} is {ModelFamilyFormGeometry.Mm(Math.Abs(offset))} mm off the work plane; the axis must lie on it.");
                }
                var axisStartUv = frame.ToUv(r.AxisStart);
                var axisEndUv = frame.ToUv(r.AxisEnd);
                prepared.AxisStart = frame.ToWorld(axisStartUv);
                prepared.AxisEnd = frame.ToWorld(axisEndUv);
                if (ModelFamilyFormGeometry.Distance2D(axisStartUv, axisEndUv) < check.MinSegmentMm)
                    problems.Add($"{r.Source}.axis: its ends are {ModelFamilyFormGeometry.Mm(ModelFamilyFormGeometry.Distance2D(axisStartUv, axisEndUv))} mm apart on the work plane; is the axis perpendicular to the plane?");
                else if (prepared.Profile != null && prepared.Profile.CrossesLine(axisStartUv, axisEndUv))
                    problems.Add($"{r.Source}.profile: lies on both sides of the axis; a revolve's profile may touch the axis but not cross it.");
                break;
            case ModelFamilyFormSweepSpec s:
                prepared.Path = s.Path.OnPlane(frame, check, problems);
                prepared.Profile = s.Profile.OnPlane(ModelFamilyFormFrame.XY, ProfileCheck(check), problems);
                break;
            case ModelFamilyFormSweptBlendSpec sb:
                prepared.Path = sb.Path.OnPlane(frame, check, problems, singleCurve: true);
                prepared.Bottom = sb.Bottom.OnPlane(ModelFamilyFormFrame.XY, ProfileCheck(check), problems, singleLoop: true);
                prepared.Top = sb.Top.OnPlane(ModelFamilyFormFrame.XY, ProfileCheck(check), problems, singleLoop: true);
                break;
        }
        return problems.Count > before ? null : prepared;
    }

    /// <summary>Profiles in their own [u, v] coordinates have no plane to be off; z is ignored.</summary>
    private static ModelFamilyFormCheck ProfileCheck(ModelFamilyFormCheck check) => new()
    {
        MinSegmentMm = check.MinSegmentMm,
        ClosureMm = check.ClosureMm,
        MinAreaMm2 = check.MinAreaMm2,
        OffPlaneMm = double.MaxValue
    };

    // ── Create (inside a transaction) ────────────────────────────────────────

    /// <summary>Creates any kind of form. Call inside <see cref="ModelFamilyContext.Transact"/>.</summary>
    public static GenericForm? Create(ModelFamilyContext context, ModelFamilyFormSpec spec, out ModelFamilyFormPrepared? prepared)
    {
        prepared = Prepare(context, spec);
        if (prepared == null) return null;
        return spec switch
        {
            ModelFamilyFormExtrusionSpec e => CreateExtrusion(context, e, prepared),
            ModelFamilyFormBlendSpec b => CreateBlend(context, b, prepared),
            ModelFamilyFormRevolveSpec r => CreateRevolve(context, r, prepared),
            ModelFamilyFormSweepSpec s => CreateSweep(context, s, prepared),
            ModelFamilyFormSweptBlendSpec sb => CreateSweptBlend(context, sb, prepared),
            _ => null
        };
    }

    /// <summary>An extrusion from <c>start</c> to <c>end</c> along the sketch plane's normal. Inside a transaction.</summary>
    internal static Extrusion? CreateExtrusion(ModelFamilyContext context, ModelFamilyFormExtrusionSpec spec, ModelFamilyFormPrepared? prepared = null)
    {
        prepared ??= Prepare(context, spec);
        if (prepared?.Profile == null) return null;
        var doc = context.FamilyDoc;
        var sketchPlane = SketchPlaneFor(context, spec);
        // NewExtrusion makes start 0 and end = its length, which must not be 0. With end 0 (start below the plane) it is
        // made to −start first; then start is set (never equal to that end on the way), then end.
        var length = Math.Abs(spec.End) > 1e-9 ? spec.End : -spec.Start;
        var extrusion = doc.FamilyCreate.NewExtrusion(spec.Solid, ToCurveArrArray(prepared.Profile), sketchPlane, length / MmPerFoot)
                        ?? throw new InvalidOperationException("Revit returned no extrusion");
        extrusion.StartOffset = spec.Start / MmPerFoot;
        extrusion.EndOffset = spec.End / MmPerFoot;
        ApplyCommon(extrusion, spec, prepared);
        return extrusion;
    }

    /// <summary>
    /// A blend: the bottom loop on the work plane, the top loop lifted by <c>end − start</c> along the sketch plane's
    /// normal, then the offsets set to start and end. Inside a transaction.
    /// </summary>
    internal static Blend? CreateBlend(ModelFamilyContext context, ModelFamilyFormBlendSpec spec, ModelFamilyFormPrepared? prepared = null)
    {
        prepared ??= Prepare(context, spec);
        if (prepared?.Bottom == null || prepared.Top == null) return null;
        var doc = context.FamilyDoc;
        var sketchPlane = SketchPlaneFor(context, spec);
        var normal = sketchPlane.GetPlane().Normal;
        var lift = normal.Multiply((spec.End - spec.Start) / MmPerFoot);
        var bottom = ToCurveArray(prepared.Bottom.Outer.World);
        var top = new CurveArray();
        foreach (var segment in prepared.Top.Outer.World) top.Append(ToCurve(segment).CreateTransformed(Transform.CreateTranslation(lift)));
        // The first section lies in the sketch plane, the second in a parallel plane (RevitAPI: NewBlend profile1, profile2).
        var blend = doc.FamilyCreate.NewBlend(spec.Solid, bottom, top, sketchPlane) ?? throw new InvalidOperationException("Revit returned no blend");
        if (Math.Abs(spec.End) > 1e-9)
        {
            blend.TopOffset = spec.End / MmPerFoot;
            blend.BottomOffset = spec.Start / MmPerFoot;
        }
        else
        {
            blend.BottomOffset = spec.Start / MmPerFoot;
            blend.TopOffset = spec.End / MmPerFoot;
        }
        ApplyCommon(blend, spec, prepared);
        return blend;
    }

    /// <summary>A revolve about an axis on the work plane, angles in degrees. Inside a transaction.</summary>
    internal static Revolution? CreateRevolve(ModelFamilyContext context, ModelFamilyFormRevolveSpec spec, ModelFamilyFormPrepared? prepared = null)
    {
        prepared ??= Prepare(context, spec);
        if (prepared?.Profile == null || prepared.AxisStart == null || prepared.AxisEnd == null) return null;
        var doc = context.FamilyDoc;
        var sketchPlane = SketchPlaneFor(context, spec);
        var axis = Line.CreateBound(Feet(prepared.AxisStart), Feet(prepared.AxisEnd));
        var revolution = doc.FamilyCreate.NewRevolution(spec.Solid, ToCurveArrArray(prepared.Profile), sketchPlane, axis,
                             spec.StartAngle * Math.PI / 180, spec.EndAngle * Math.PI / 180)
                         ?? throw new InvalidOperationException("Revit returned no revolve");
        ApplyCommon(revolution, spec, prepared);
        return revolution;
    }

    /// <summary>
    /// A sweep along a path on the work plane. The profile is given in XY (its [u, v] coordinates, z = 0); Revit maps
    /// it onto the plane perpendicular to the path at <c>profilePlane</c> of segment <c>profileCurve</c>. Inside a transaction.
    /// </summary>
    internal static Sweep? CreateSweep(ModelFamilyContext context, ModelFamilyFormSweepSpec spec, ModelFamilyFormPrepared? prepared = null)
    {
        prepared ??= Prepare(context, spec);
        if (prepared?.Path == null || prepared.Profile == null) return null;
        var doc = context.FamilyDoc;
        var sketchPlane = SketchPlaneFor(context, spec);
        var profile = context.Uiapp.Application.Create.NewCurveLoopsProfile(ToCurveArrArray(prepared.Profile, planeCoordinates: true));
        var location = spec.ProfilePlane switch
        {
            "middle" => ProfilePlaneLocation.MidPoint,
            "end" => ProfilePlaneLocation.End,
            _ => ProfilePlaneLocation.Start
        };
        var sweep = doc.FamilyCreate.NewSweep(spec.Solid, ToCurveArray(prepared.Path.World), sketchPlane, profile, spec.ProfileCurve, location)
                    ?? throw new InvalidOperationException("Revit returned no sweep");
        ApplyCommon(sweep, spec, prepared);
        return sweep;
    }

    /// <summary>A swept blend along one curve on the work plane, both loops in XY. Inside a transaction.</summary>
    internal static SweptBlend? CreateSweptBlend(ModelFamilyContext context, ModelFamilyFormSweptBlendSpec spec, ModelFamilyFormPrepared? prepared = null)
    {
        prepared ??= Prepare(context, spec);
        if (prepared?.Path == null || prepared.Bottom == null || prepared.Top == null) return null;
        var doc = context.FamilyDoc;
        var sketchPlane = SketchPlaneFor(context, spec);
        var create = context.Uiapp.Application.Create;
        var bottom = create.NewCurveLoopsProfile(ToCurveArrArray(prepared.Bottom, planeCoordinates: true));
        var top = create.NewCurveLoopsProfile(ToCurveArrArray(prepared.Top, planeCoordinates: true));
        var sweptBlend = doc.FamilyCreate.NewSweptBlend(spec.Solid, ToCurve(prepared.Path.World[0]), sketchPlane, bottom, top)
                         ?? throw new InvalidOperationException("Revit returned no swept blend");
        ApplyCommon(sweptBlend, spec, prepared);
        return sweptBlend;
    }

    private static SketchPlane SketchPlaneFor(ModelFamilyContext context, ModelFamilyFormSpec spec)
    {
        var problems = new List<string>();
        return ModelFamilyLookup.SketchPlane(context.FamilyDoc, spec.WorkPlane, $"{spec.Source}.workPlane", problems)
               ?? throw new InvalidOperationException(problems.Count > 0 ? problems[0] : "Revit could not make a sketch plane for the work plane");
    }

    private static void ApplyCommon(GenericForm form, ModelFamilyFormSpec spec, ModelFamilyFormPrepared prepared)
    {
        if (prepared.Subcategory != null)
        {
            try { form.Subcategory = prepared.Subcategory; }
            catch (Exception ex) { throw new InvalidOperationException($"Revit refused subcategory '{prepared.Subcategory.Name}' on the {(spec.Solid ? "solid" : "void")}: {ex.Message.TrimEnd('.')}"); }
        }
        if (spec.Visible != null) SetVisible(form, spec.Visible.Value);
    }

    /// <summary>
    /// Shows or hides a form through its Visible parameter (GenericForm.Visible is read-only in the API). Inside a
    /// transaction; throws when the parameter is read-only or driven by a family parameter.
    /// </summary>
    public static void SetVisible(GenericForm form, bool visible)
    {
        var parameter = form.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM)
                        ?? throw new InvalidOperationException($"form {form.Id.Value} has no Visible parameter");
        if (parameter.IsReadOnly || !parameter.Set(visible ? 1 : 0))
            throw new InvalidOperationException($"Revit did not set Visible on form {form.Id.Value} (it is read-only or driven by a family parameter)");
    }

    // ── Verify (after the transaction) ───────────────────────────────────────

    /// <summary>
    /// Reads back a created form: id, kind, sketch plane, offsets or angles, bounding box. Differences from the spec
    /// (an extrusion's measured size beyond 0.1 mm, offsets Revit did not keep) go to <c>context.Warnings</c>.
    /// </summary>
    public static JObject Verify(ModelFamilyContext context, GenericForm form, ModelFamilyFormPrepared prepared)
    {
        var spec = prepared.Spec;
        var row = new JObject
        {
            ["source"] = spec.Source,
            ["id"] = form.Id.Value,
            ["kind"] = KindName(spec.Kind),
            ["solid"] = SafeIsSolid(form) ?? spec.Solid
        };
        if (ModelFamilyFormRead.SketchPlaneOf(form) is { } sketchPlane) row["sketchPlane"] = ModelFamilyFormRead.DescribeSketchPlane(context.FamilyDoc, sketchPlane);
        if (prepared.Profile != null && spec.Kind != ModelFamilyFormKind.Sweep) row["profile"] = prepared.Profile.Describe();
        if (SafeBox(form) is { } box) row["boundingBox"] = ModelFamilyFormRead.Box(box);

        void Compare(string name, double requested, double measured, string unit, double tolerance)
        {
            row[name] = Math.Round(measured, 3);
            if (Math.Abs(measured - requested) > tolerance)
                context.Warnings.Add($"{spec.Source}: Revit made {name} {ModelFamilyFormGeometry.Mm(measured)} {unit}, requested {ModelFamilyFormGeometry.Mm(requested)} {unit}.");
        }
        switch (form)
        {
            case Extrusion extrusion when spec is ModelFamilyFormExtrusionSpec e:
                Compare("start", e.Start, extrusion.StartOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                Compare("end", e.End, extrusion.EndOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                if (prepared.Profile != null && SafeBox(form) is { } extrusionBox)
                {
                    var mismatches = ModelFamilyFormMeasure.CompareExtrusion(prepared.Frame, prepared.Profile.Width, prepared.Profile.Height,
                        e.End - e.Start, Mm(extrusionBox.Min), Mm(extrusionBox.Max));
                    if (mismatches == null) row["sizeCheck"] = "skipped: the work plane is not aligned with the family axes";
                    else
                    {
                        row["sizeCheck"] = mismatches.Count == 0 ? "ok" : "mismatch";
                        foreach (var mismatch in mismatches) context.Warnings.Add($"{spec.Source}: the extrusion's {mismatch}.");
                    }
                }
                break;
            case Blend blend when spec is ModelFamilyFormBlendSpec b:
                Compare("start", b.Start, blend.BottomOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                Compare("end", b.End, blend.TopOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                break;
            case Revolution revolution when spec is ModelFamilyFormRevolveSpec r:
                Compare("startAngle", r.StartAngle, revolution.StartAngle * 180 / Math.PI, "°", 0.01);
                Compare("endAngle", r.EndAngle, revolution.EndAngle * 180 / Math.PI, "°", 0.01);
                break;
        }
        if (spec.Subcategory.Length > 0) row["subcategory"] = SafeSubcategory(form);
        if (spec.Visible != null) row["visible"] = SafeVisible(form);
        return row;
    }

    internal static bool? SafeIsSolid(GenericForm form)
    {
        try { return form.IsSolid; }
        catch { return null; }
    }

    internal static bool? SafeVisible(GenericForm form)
    {
        try { return form.Visible; }
        catch { return null; }
    }

    internal static string? SafeSubcategory(GenericForm form)
    {
        try { return form.Subcategory?.Name; }
        catch { return null; }
    }

    internal static BoundingBoxXYZ? SafeBox(Element element)
    {
        try { return element.get_BoundingBox(null); }
        catch { return null; }
    }
}

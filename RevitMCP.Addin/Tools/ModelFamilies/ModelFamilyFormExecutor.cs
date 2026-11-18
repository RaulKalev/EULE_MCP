using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The body of revit_preview_edit_model_family_forms and revit_edit_model_family_forms (#171). Reads the operation
/// lists (<c>remove</c>, <c>extrusions</c>, <c>blends</c>, <c>revolves</c>, <c>sweeps</c>, <c>sweptBlends</c>,
/// <c>edit</c>, <c>cuts</c>, <c>joins</c>) with <see cref="ModelFamilyFormPlan"/>, checks all of them against the
/// family before changing anything, then runs them in that order, one transaction per item.
/// </summary>
internal static class ModelFamilyFormExecutor
{
    public const string What = "edit forms";

    private const double MmPerFoot = ModelFamilyJson.MmPerFoot;

    /// <summary>The plan of a request in one line, for the approval summary.</summary>
    public static string Plan(McpToolRequest request) =>
        ModelFamilyFormPlan.Parse(request.Arguments ?? new Dictionary<string, object?>(), new List<string>()).Describe();

    /// <summary>What a form reference stands for once checked: an existing element, or a form this call creates.</summary>
    private sealed class Target
    {
        public Element? Element { get; init; }
        public ModelFamilyFormSpec? Created { get; init; }
        public bool IsSolid => Element is GenericForm form ? ModelFamilyFormBuilder.SafeIsSolid(form) ?? true : Created?.Solid ?? true;
        public string Kind => Element != null ? ModelFamilyFormRead.KindOf(Element) : ModelFamilyFormBuilder.KindName(Created!.Kind);
    }

    public static object? Run(ModelFamilyContext context)
    {
        var doc = context.FamilyDoc;
        var problems = context.Problems;
        var plan = ModelFamilyFormPlan.Parse(context.Args, problems);
        context.Summary = plan.Describe();
        var result = new JObject { ["order"] = new JArray(ModelFamilyFormPlan.Order) };
        if (problems.Count > 0) return result;

        if (doc.OwnerFamily?.FamilyCategory is { } category && IsAnnotationOrTwoD(doc))
        {
            problems.Add($"'{context.FamilyName}' is a {category.Name} family; solid and void forms need a 3D model family (for example one made from Metric Generic Model.rft).");
            return result;
        }

        // 1. Check everything against the family before changing it.
        var removed = new HashSet<long>();
        var removeTargets = new List<GenericForm>();
        foreach (var reference in plan.Remove)
        {
            var element = doc.GetElement(new ElementId(reference.Id!.Value));
            if (element is GenericForm form)
            {
                if (removed.Add(form.Id.Value)) removeTargets.Add(form);
            }
            else problems.Add(element == null
                ? $"{reference.Source}: the family has no element {reference.Id}."
                : $"{reference.Source}: element {reference.Id} is {ModelFamilyLookup.Describe(element)}, not a solid or void form. To dissolve a geometry combination use joins [{{\"combination\": {reference.Id}, \"unjoin\": true}}].");
        }

        var prepared = new List<ModelFamilyFormPrepared>();
        var specs = plan.Forms.ToDictionary(s => s.Source, s => s, StringComparer.Ordinal);
        foreach (var spec in plan.Forms)
            if (ModelFamilyFormBuilder.Prepare(context, spec) is { } ready) prepared.Add(ready);

        Target? Check(ModelFamilyFormRef reference)
        {
            if (reference.Created != null)
            {
                if (specs.TryGetValue(reference.Created, out var spec)) return new Target { Created = spec };
                return null;   // the plan already reported a missing entry, or the entry has its own problem
            }
            var element = doc.GetElement(new ElementId(reference.Id!.Value));
            if (element == null) problems.Add($"{reference.Source}: the family has no element {reference.Id}.");
            else if (removed.Contains(element.Id.Value)) problems.Add($"{reference.Source}: element {reference.Id} is removed by remove, which runs first.");
            else if (element is not GenericForm && element is not GeomCombination)
                problems.Add($"{reference.Source}: element {reference.Id} is {ModelFamilyLookup.Describe(element)}, not a form.");
            else return new Target { Element = element };
            return null;
        }

        foreach (var edit in plan.Edits)
        {
            var target = Check(edit.Form);
            if (target == null) continue;
            if (target.Element is GeomCombination) { problems.Add($"{edit.Form.Source}: element {edit.Form.Resolve(new Dictionary<string, long>())} is a geometry combination; edit its member forms."); continue; }
            var kind = target.Kind;
            if ((edit.Start != null || edit.End != null) && kind is not ("extrusion" or "blend"))
                problems.Add($"{edit.Source}: start and end apply to extrusions and blends; {edit.Form} is a {kind}.");
            if ((edit.StartAngle != null || edit.EndAngle != null) && kind != "revolve")
                problems.Add($"{edit.Source}: startAngle and endAngle apply to revolves; {edit.Form} is a {kind}.");
            if (!string.IsNullOrEmpty(edit.Subcategory) && ModelFamilyLookup.Subcategory(doc, edit.Subcategory!) == null)
                problems.Add($"{edit.Source}.subcategory: the family's category has no subcategory '{edit.Subcategory}'.");
            if (target.Element is GenericForm existing) CheckDriven(context, edit, existing);
        }
        foreach (var cut in plan.Cuts)
        {
            var cutter = Check(cut.Void);
            var solid = Check(cut.Solid);
            if (cutter != null && cutter.IsSolid && cutter.Element is not GeomCombination)
                problems.Add($"{cut.Void.Source}: {cut.Void} is a solid; the cutting form must be a void (solid: false).");
            if (solid != null && !solid.IsSolid)
                problems.Add($"{cut.Solid.Source}: {cut.Solid} is a void; a void cuts a solid.");
        }
        foreach (var join in plan.Joins)
        {
            if (join.Unjoin)
            {
                var combination = join.Combination == null ? null : Check(join.Combination);
                if (combination != null && combination.Element is not GeomCombination)
                    problems.Add($"{join.Combination!.Source}: {join.Combination} is a form, not a geometry combination; revit_get_model_family include=[\"combinations\"] lists them.");
            }
            else
            {
                foreach (var element in join.Elements) Check(element);
                var ids = join.Elements.Select(e => e.Id?.ToString() ?? e.Created).ToList();
                if (ids.Distinct().Count() != ids.Count) problems.Add($"{join.Source}.elements: lists the same form twice.");
            }
        }

        result["forms"] = new JArray(prepared.Select(p => p.Describe()));
        if (problems.Count > 0) return result;

        // 2. Run the operations in order.
        var created = new Dictionary<string, long>(StringComparer.Ordinal);
        var createdRows = new JArray();

        if (removeTargets.Count > 0)
        {
            var removedRows = new JArray();
            context.Transact("Remove forms", () =>
            {
                foreach (var form in removeTargets)
                {
                    var id = form.Id;
                    var deleted = doc.Delete(id).Select(d => d.Value).Where(d => d != id.Value).ToList();
                    removedRows.Add(new JObject { ["id"] = id.Value, ["alsoDeleted"] = new JArray(deleted) });
                    if (deleted.Count > 0)
                        context.Warnings.Add($"Removing form {id.Value} also deleted {deleted.Count} dependent element(s) (dimensions, labels or joins): {string.Join(", ", deleted.Take(10))}.");
                }
            });
            result["removed"] = removedRows;
        }

        foreach (var ready in prepared)
        {
            var spec = ready.Spec;
            var form = context.Transact($"Create {spec.Source}", () => spec switch
            {
                ModelFamilyFormExtrusionSpec e => (GenericForm?)ModelFamilyFormBuilder.CreateExtrusion(context, e, ready),
                ModelFamilyFormBlendSpec b => ModelFamilyFormBuilder.CreateBlend(context, b, ready),
                ModelFamilyFormRevolveSpec r => ModelFamilyFormBuilder.CreateRevolve(context, r, ready),
                ModelFamilyFormSweepSpec s => ModelFamilyFormBuilder.CreateSweep(context, s, ready),
                ModelFamilyFormSweptBlendSpec sb => ModelFamilyFormBuilder.CreateSweptBlend(context, sb, ready),
                _ => null
            } ?? throw new InvalidOperationException($"Revit did not create {spec.Source}"));
            created[spec.Source] = form.Id.Value;
            createdRows.Add(ModelFamilyFormBuilder.Verify(context, form, ready));
        }
        if (createdRows.Count > 0) result["created"] = createdRows;

        if (plan.Edits.Count > 0)
        {
            var editRows = new JArray();
            foreach (var edit in plan.Edits) editRows.Add(Edit(context, edit, created));
            result["edited"] = editRows;
        }
        if (plan.Cuts.Count > 0)
        {
            var cutRows = new JArray();
            foreach (var cut in plan.Cuts) cutRows.Add(Cut(context, cut, created));
            result["cuts"] = cutRows;
        }
        if (plan.Joins.Count > 0)
        {
            var joinRows = new JArray();
            foreach (var join in plan.Joins) joinRows.Add(Join(context, join, created));
            result["joins"] = joinRows;
        }

        // 3. A void that cuts nothing changes nothing in the family's geometry.
        foreach (var ready in prepared.Where(p => !p.Spec.Solid))
        {
            if (doc.GetElement(new ElementId(created[ready.Spec.Source])) is not GenericForm voidForm) continue;
            var (_, cuts) = ModelFamilyFormRead.CutRelations(doc, voidForm);
            if (cuts.Count == 0)
                context.Warnings.Add($"{ready.Spec.Source}: the void {voidForm.Id.Value} cuts nothing yet; add cuts [{{\"void\": {{\"created\": \"{ready.Spec.Source}\"}}, \"solid\": <id>}}].");
        }
        result["createdIds"] = JObject.FromObject(created);
        return result;
    }

    private static bool IsAnnotationOrTwoD(Document doc)
    {
        try
        {
            var category = doc.OwnerFamily.FamilyCategory;
            return category != null && category.CategoryType == CategoryType.Annotation ||
                   category != null && category.Id.Value == (long)BuiltInCategory.OST_DetailComponents;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>An offset or angle driven by a family parameter is set by that parameter; setting it here would be undone.</summary>
    private static void CheckDriven(ModelFamilyContext context, ModelFamilyFormEditSpec edit, GenericForm form)
    {
        void Check(bool given, string key, BuiltInParameter builtIn)
        {
            if (!given) return;
            try
            {
                if (form.get_Parameter(builtIn) is { } parameter && ModelFamilyAssociations.Associated(context.FamilyDoc, parameter) is { } driver)
                    context.Problems.Add($"{edit.Source}.{key}: is driven by the family parameter '{driver.Definition.Name}'; change that parameter's value (or dissociate it) instead.");
            }
            catch { /* no such parameter on this form */ }
        }
        var isBlend = form is Blend;
        Check(edit.Start != null, "start", isBlend ? BuiltInParameter.BLEND_START_PARAM : BuiltInParameter.EXTRUSION_START_PARAM);
        Check(edit.End != null, "end", isBlend ? BuiltInParameter.BLEND_END_PARAM : BuiltInParameter.EXTRUSION_END_PARAM);
        Check(edit.StartAngle != null, "startAngle", BuiltInParameter.REVOLUTION_START_ANGLE);
        Check(edit.EndAngle != null, "endAngle", BuiltInParameter.REVOLUTION_END_ANGLE);
        Check(edit.Visible != null, "visible", BuiltInParameter.IS_VISIBLE_PARAM);
    }

    private static GenericForm FormOf(ModelFamilyContext context, ModelFamilyFormRef reference, IReadOnlyDictionary<string, long> created)
    {
        var id = reference.Resolve(created) ?? throw new InvalidOperationException($"{reference.Source}: {reference} was not created");
        return context.FamilyDoc.GetElement(new ElementId(id)) as GenericForm
               ?? throw new InvalidOperationException($"{reference.Source}: element {id} is not a form");
    }

    private static Element ElementOf(ModelFamilyContext context, ModelFamilyFormRef reference, IReadOnlyDictionary<string, long> created)
    {
        var id = reference.Resolve(created) ?? throw new InvalidOperationException($"{reference.Source}: {reference} was not created");
        return context.FamilyDoc.GetElement(new ElementId(id)) ?? throw new InvalidOperationException($"{reference.Source}: element {id} no longer exists");
    }

    /// <summary>
    /// Sets a start/end pair (Revit's internal units) in an order that never makes the two equal on the way: when the
    /// new start equals the current end, the end moves first.
    /// </summary>
    private static void SetPair(double? start, double? end, double currentEnd, Action<double> setStart, Action<double> setEnd)
    {
        if (start != null && end != null && Math.Abs(start.Value - currentEnd) < 1e-9)
        {
            setEnd(end.Value);
            setStart(start.Value);
            return;
        }
        if (start != null) setStart(start.Value);
        if (end != null) setEnd(end.Value);
    }

    private static JObject Edit(ModelFamilyContext context, ModelFamilyFormEditSpec edit, IReadOnlyDictionary<string, long> created)
    {
        var form = FormOf(context, edit.Form, created);
        var row = new JObject { ["source"] = edit.Source, ["id"] = form.Id.Value, ["kind"] = ModelFamilyFormRead.KindOf(form) };
        context.Transact($"Edit {edit.Source}", () =>
        {
            switch (form)
            {
                case Extrusion extrusion when edit.Start != null || edit.End != null:
                    if (edit.Start == null && Math.Abs(edit.End!.Value / MmPerFoot - extrusion.StartOffset) < 1e-6 ||
                        edit.End == null && Math.Abs(edit.Start!.Value / MmPerFoot - extrusion.EndOffset) < 1e-6)
                        throw new InvalidOperationException($"{edit.Source}: start and end would be equal");
                    SetPair(edit.Start / MmPerFoot, edit.End / MmPerFoot, extrusion.EndOffset, v => extrusion.StartOffset = v, v => extrusion.EndOffset = v);
                    break;
                case Blend blend when edit.Start != null || edit.End != null:
                    if (edit.Start == null && Math.Abs(edit.End!.Value / MmPerFoot - blend.BottomOffset) < 1e-6 ||
                        edit.End == null && Math.Abs(edit.Start!.Value / MmPerFoot - blend.TopOffset) < 1e-6)
                        throw new InvalidOperationException($"{edit.Source}: start and end would be equal");
                    SetPair(edit.Start / MmPerFoot, edit.End / MmPerFoot, blend.TopOffset, v => blend.BottomOffset = v, v => blend.TopOffset = v);
                    break;
            }
            if (form is Revolution revolution && (edit.StartAngle != null || edit.EndAngle != null))
            {
                var start = edit.StartAngle ?? revolution.StartAngle * 180 / Math.PI;
                var end = edit.EndAngle ?? revolution.EndAngle * 180 / Math.PI;
                var angleProblems = new List<string>();
                if (!ModelFamilyFormPlan.CheckAngles(start, end, edit.Source, angleProblems))
                    throw new InvalidOperationException(angleProblems[0]);
                SetPair(edit.StartAngle == null ? null : start * Math.PI / 180, edit.EndAngle == null ? null : end * Math.PI / 180,
                    revolution.EndAngle, v => revolution.StartAngle = v, v => revolution.EndAngle = v);
            }
            if (edit.Visible != null) ModelFamilyFormBuilder.SetVisible(form, edit.Visible.Value);
            if (edit.Subcategory != null)
            {
                var subcategory = edit.Subcategory.Length == 0
                    ? context.FamilyDoc.OwnerFamily.FamilyCategory
                    : ModelFamilyLookup.Subcategory(context.FamilyDoc, edit.Subcategory) ?? throw new InvalidOperationException($"{edit.Source}.subcategory: no subcategory '{edit.Subcategory}'");
                form.Subcategory = subcategory;
            }
        });

        // Read back what Revit kept.
        void Compare(string key, double? requested, double measured, string unit, double tolerance)
        {
            row[key] = Math.Round(measured, 4);
            if (requested != null && Math.Abs(measured - requested.Value) > tolerance)
                context.Warnings.Add($"{edit.Source}: Revit kept {key} {ModelFamilyFormGeometry.Mm(measured)} {unit}, requested {ModelFamilyFormGeometry.Mm(requested.Value)} {unit}.");
        }
        switch (form)
        {
            case Extrusion extrusion:
                Compare("start", edit.Start, extrusion.StartOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                Compare("end", edit.End, extrusion.EndOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                break;
            case Blend blend:
                Compare("start", edit.Start, blend.BottomOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                Compare("end", edit.End, blend.TopOffset * MmPerFoot, "mm", ModelFamilyFormMeasure.ToleranceMm);
                break;
            case Revolution revolution:
                Compare("startAngle", edit.StartAngle, revolution.StartAngle * 180 / Math.PI, "°", 0.01);
                Compare("endAngle", edit.EndAngle, revolution.EndAngle * 180 / Math.PI, "°", 0.01);
                break;
        }
        if (edit.Visible != null) row["visible"] = ModelFamilyFormBuilder.SafeVisible(form);
        if (edit.Subcategory != null) row["subcategory"] = ModelFamilyFormBuilder.SafeSubcategory(form);
        if (ModelFamilyFormBuilder.SafeBox(form) is { } box) row["boundingBox"] = ModelFamilyFormRead.Box(box);
        return row;
    }

    /// <summary>
    /// A void cuts a solid. Revit's SolidSolidCutUtils is tried first; where it does not apply (it is documented for
    /// projects and conceptual families) the two are combined into a geometry combination, which is how a void cuts a
    /// solid in the Family Editor. The solid's volume is measured before and after.
    /// </summary>
    private static JObject Cut(ModelFamilyContext context, ModelFamilyFormCutSpec cut, IReadOnlyDictionary<string, long> created)
    {
        var doc = context.FamilyDoc;
        var cutter = ElementOf(context, cut.Void, created);
        var solid = ElementOf(context, cut.Solid, created);
        var row = new JObject { ["source"] = cut.Source, ["void"] = cutter.Id.Value, ["solid"] = solid.Id.Value };
        double MeasuredVolume() => ModelFamilyFormRead.CombinationIds(solid).FirstOrDefault() is var combinationId && combinationId != 0 &&
                                   doc.GetElement(new ElementId(combinationId)) is GeomCombination combination
            ? ModelFamilyFormRead.VolumeMm3(combination)
            : ModelFamilyFormRead.VolumeMm3(solid);

        if (cut.Remove)
        {
            var how = context.Transact($"Remove cut {cut.Source}", () =>
            {
                var exists = false;
                try { exists = SolidSolidCutUtils.CutExistsBetweenElements(cutter, solid, out _); }
                catch { /* not eligible for solid-solid cuts */ }
                if (exists)
                {
                    SolidSolidCutUtils.RemoveCutBetweenSolids(doc, solid, cutter);
                    return "solidSolidCut";
                }
                var shared = ModelFamilyFormRead.CombinationIds(cutter).Intersect(ModelFamilyFormRead.CombinationIds(solid)).ToList();
                if (shared.Count == 0) return "none";
                var combination = (GeomCombination)doc.GetElement(new ElementId(shared[0]));
                var members = ModelFamilyFormRead.Members(combination);
                if (members.Count > 2)
                    throw new InvalidOperationException($"{cut.Source}: the void cuts through geometry combination {combination.Id.Value} with {members.Count} members; unjoin it (joins [{{\"combination\": {combination.Id.Value}, \"unjoin\": true}}]) and cut the others again");
                DeleteCombination(doc, combination, members, cut.Source);
                return "combination";
            });
            row["removed"] = how != "none";
            row["method"] = how;
            if (how == "none") context.Warnings.Add($"{cut.Source}: element {cutter.Id.Value} did not cut {solid.Id.Value}; nothing to remove.");
            return row;
        }

        var before = MeasuredVolume();
        string? reason = null;
        var method = context.Transact($"Cut {cut.Source}", () =>
        {
            var can = false;
            var failure = CutFailureReason.CutAllowed;
            try { can = SolidSolidCutUtils.CanElementCutElement(cutter, solid, out failure); }
            catch (Exception ex) { reason = ex.Message; }
            if (!can && failure == CutFailureReason.CutAlreadyExists) return "alreadyCuts";
            if (can)
            {
                try
                {
                    SolidSolidCutUtils.AddCutBetweenSolids(doc, solid, cutter);
                    return "solidSolidCut";
                }
                catch (Exception ex)
                {
                    reason = ex.Message;
                }
            }
            else reason ??= Describe(failure);
            if (ModelFamilyFormRead.CombinationIds(cutter).Intersect(ModelFamilyFormRead.CombinationIds(solid)).Any()) return "alreadyCuts";
            if (cutter is not CombinableElement combinableVoid || solid is not CombinableElement combinableSolid)
                throw new InvalidOperationException($"{cut.Source}: Revit cannot cut {solid.Id.Value} with {cutter.Id.Value}: {reason}");
            var array = new CombinableElementArray();
            array.Append(combinableSolid);
            array.Append(combinableVoid);
            try
            {
                var combination = doc.CombineElements(array);
                row["combination"] = combination?.Id.Value;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{cut.Source}: Revit cannot cut {solid.Id.Value} with {cutter.Id.Value}: {ex.Message.TrimEnd('.')} (solid-solid cut: {reason})");
            }
            return "combination";
        });
        var after = MeasuredVolume();
        row["method"] = method;
        row["volumeBefore"] = Math.Round(before, 1);
        row["volumeAfter"] = Math.Round(after, 1);
        if (method != "alreadyCuts" && before > 0 && after >= before - 1e-3)
            context.Warnings.Add($"{cut.Source}: the cut was added, but the solid's volume did not change ({ModelFamilyFormGeometry.Mm(before)} mm³); the void may not overlap the solid.");
        if (method == "alreadyCuts") context.Warnings.Add($"{cut.Source}: element {cutter.Id.Value} already cuts {solid.Id.Value}.");
        return row;
    }

    private static string Describe(CutFailureReason reason) => reason switch
    {
        CutFailureReason.CutNotAppropriateForElements => "a solid-solid cut is not possible for these elements in this family",
        CutFailureReason.OppositeCutExists => "the solid already cuts the void (the opposite cut exists)",
        CutFailureReason.CutAlreadyExists => "the cut already exists",
        _ => reason.ToString()
    };

    /// <summary>Deletes a geometry combination and checks its members survived; a deleted member throws, which rolls the step back.</summary>
    private static void DeleteCombination(Document doc, GeomCombination combination, List<Element> members, string source)
    {
        var memberIds = members.Select(m => m.Id).ToList();
        doc.Delete(combination.Id);
        var lost = memberIds.Where(id => doc.GetElement(id) == null).Select(id => id.Value).ToList();
        if (lost.Count > 0)
            throw new InvalidOperationException($"{source}: deleting geometry combination {combination.Id.Value} would also delete its member(s) {string.Join(", ", lost)}; nothing was changed");
    }

    private static JObject Join(ModelFamilyContext context, ModelFamilyFormJoinSpec join, IReadOnlyDictionary<string, long> created)
    {
        var doc = context.FamilyDoc;
        if (join.Unjoin)
        {
            var combination = (GeomCombination)ElementOf(context, join.Combination!, created);
            var members = ModelFamilyFormRead.Members(combination);
            var id = combination.Id.Value;
            context.Transact($"Unjoin {join.Source}", () => DeleteCombination(doc, combination, members, join.Source));
            return new JObject { ["source"] = join.Source, ["unjoined"] = id, ["members"] = new JArray(members.Select(m => m.Id.Value)) };
        }
        var elements = join.Elements.Select(e => ElementOf(context, e, created)).ToList();
        var row = new JObject { ["source"] = join.Source, ["elements"] = new JArray(elements.Select(e => e.Id.Value)) };
        context.Transact($"Join {join.Source}", () =>
        {
            var array = new CombinableElementArray();
            foreach (var element in elements)
            {
                if (element is not CombinableElement combinable)
                    throw new InvalidOperationException($"{join.Source}: element {element.Id.Value} cannot be joined");
                array.Append(combinable);
            }
            var combination = doc.CombineElements(array) ?? throw new InvalidOperationException($"{join.Source}: Revit did not join the forms");
            row["combination"] = combination.Id.Value;
        });
        if (row["combination"]?.Value<long>() is { } combinationId && doc.GetElement(new ElementId(combinationId)) is GeomCombination made)
        {
            var members = ModelFamilyFormRead.Members(made).Select(m => m.Id.Value).ToHashSet();
            var missing = elements.Where(e => !members.Contains(e.Id.Value)).Select(e => e.Id.Value).ToList();
            row["members"] = new JArray(members.OrderBy(m => m));
            if (missing.Count > 0) context.Warnings.Add($"{join.Source}: geometry combination {combinationId} does not list {string.Join(", ", missing)} as members.");
        }
        return row;
    }
}

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The body of revit_edit_model_family_components and its preview (#175). Runs the lists in
/// <see cref="ModelFamilyComponentPlan.Order"/>: remove, subcategories, nestedFamilies, instances, symbolicLines,
/// modelLines, setSubcategory, visibility. A later list may name what an earlier one created. A list that fails stops
/// the lists after it (they would only repeat the failure), and every problem is reported together.
/// </summary>
internal static class ModelFamilyComponentExecutor
{
    public static object? Run(ModelFamilyContext context)
    {
        var plan = ModelFamilyComponentPlan.Parse(context.Args, context.Problems);
        if (context.Problems.Count == 0 && plan.OperationCount == 0)
            context.Problems.Add("Nothing to do: give at least one of " + string.Join(", ", ModelFamilyComponentPlan.Order) + ".");
        if (context.Problems.Count > 0) return null;

        context.Summary = plan.Describe();
        var created = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var result = new JObject();

        void Step(string section, Func<JArray> run)
        {
            if (context.Problems.Count > 0 || plan.CountOf(section) == 0) return;
            result[section] = run();
        }

        Step("remove", () => Remove(context, plan.Remove));
        Step("subcategories", () => Subcategories(context, plan.Subcategories));
        Step("nestedFamilies", () => ModelFamilyComponentNested.LoadFamilies(context, plan.NestedFamilies));
        Step("instances", () => ModelFamilyComponentNested.PlaceInstances(context, plan.Instances, created));
        Step("symbolicLines", () => ModelFamilyComponentGraphics.SymbolicLines(context, plan.SymbolicLines, created));
        Step("modelLines", () => ModelFamilyComponentGraphics.ModelLines(context, plan.ModelLines, created));
        Step("setSubcategory", () => ModelFamilyComponentGraphics.SetSubcategory(context, plan.SetSubcategory, created));
        Step("visibility", () => Visibility(context, plan.Visibility, created));

        if (context.Problems.Count > 0)
            context.Warnings.Add("The lists after the one that failed were not run; fix the problems and run the whole call again (nothing was changed).");
        result["created"] = new JObject(created.Select(c => new JProperty(c.Key, new JArray(c.Value))));
        return result;
    }

    // ── remove ───────────────────────────────────────────────────────────────

    private static JArray Remove(ModelFamilyContext context, List<long> ids)
    {
        var doc = context.FamilyDoc;
        var rows = new JArray();
        var elements = new List<Element>();
        foreach (var id in ids)
        {
            var element = doc.GetElement(new ElementId(id));
            if (element == null)
            {
                context.Problems.Add($"remove: the family has no element {id}.");
                continue;
            }
            var kind = ModelFamilyComponentSupport.KindOf(element);
            if (kind is "nestedInstance" or "symbolicLine" or "modelLine") elements.Add(element);
            else
                context.Problems.Add($"remove: {ModelFamilyLookup.Describe(element)} is a {kind}, not a nested instance, symbolic line or model line. " +
                                     (kind == "form" ? "Forms belong to the form tool of this family." : kind == "referenceLine" ? "Reference lines belong to revit_edit_model_family_references." : "Its own tool removes it."));
        }
        if (context.Problems.Count > 0) return rows;

        foreach (var element in elements) rows.Add(ModelFamilyComponentSupport.Row(element));
        var removed = 0;
        var ok = context.TryTransact("Remove components", () =>
        {
            var deleted = doc.Delete(elements.Select(e => e.Id).ToList());
            removed = deleted.Count;
            foreach (var element in elements)
                if (doc.GetElement(element.Id) != null)
                    throw new InvalidOperationException($"Revit kept {ModelFamilyLookup.Describe(element)}.");
        }, out var error);
        if (!ok)
        {
            context.Problems.Add($"remove: {error}");
            return new JArray();
        }
        if (removed > elements.Count)
            context.Warnings.Add($"remove: Revit also deleted {removed - elements.Count} dependent element(s), such as alignments that locked a removed instance.");
        return rows;
    }

    // ── subcategories ────────────────────────────────────────────────────────

    private static JArray Subcategories(ModelFamilyContext context, List<ComponentSubcategorySpec> specs)
    {
        var rows = new JArray();
        var doc = context.FamilyDoc;
        var parent = doc.OwnerFamily?.FamilyCategory;
        if (parent == null)
        {
            context.Problems.Add("subcategories: the family has no category; set one before adding subcategories.");
            return rows;
        }
        foreach (var spec in specs)
        {
            JObject? row = null;
            var ok = context.TryTransact($"Subcategory {spec.Name}", () => row = ApplySubcategory(doc, parent, spec), out var error);
            if (ok) rows.Add(row!);
            else context.Problems.Add($"{spec.Source}: {error}");
        }
        return rows;
    }

    private static JObject ApplySubcategory(Document doc, Category parent, ComponentSubcategorySpec spec)
    {
        var existing = ModelFamilyLookup.Subcategory(doc, spec.Name);
        var before = existing == null ? null : ModelFamilyComponentSupport.DescribeSubcategory(doc, existing);
        var category = existing ?? doc.Settings.Categories.NewSubcategory(parent, spec.Name);

        if (spec.LineWeightProjection is { } projection)
        {
            category.SetLineWeight(projection, GraphicsStyleType.Projection);
            if (category.GetLineWeight(GraphicsStyleType.Projection) != projection)
                throw new InvalidOperationException($"Revit kept projection line weight {category.GetLineWeight(GraphicsStyleType.Projection)} on '{spec.Name}', not {projection}.");
        }
        if (spec.LineWeightCut is { } cut)
        {
            try { category.SetLineWeight(cut, GraphicsStyleType.Cut); }
            catch (Exception ex) { throw new InvalidOperationException($"Revit refused cut line weight {cut} for '{spec.Name}' ({ex.Message.TrimEnd('.')}); a cut weight needs a category that is cut in plan."); }
        }
        if (spec.Color is { } rgb) category.LineColor = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);
        if (spec.LinePattern != null)
        {
            var patternName = spec.LinePattern.Trim();
            ElementId patternId;
            if (patternName.Length == 0 || string.Equals(patternName, "Solid", StringComparison.OrdinalIgnoreCase)) patternId = LinePatternElement.GetSolidPatternId();
            else
            {
                var patterns = new FilteredElementCollector(doc).OfClass(typeof(LinePatternElement)).Cast<LinePatternElement>().ToList();
                var match = patterns.FirstOrDefault(p => string.Equals(p.Name, patternName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var close = ModelFamilyJson.Suggest(patternName, patterns.Select(p => p.Name));
                    throw new InvalidOperationException($"the family has no line pattern '{patternName}'" +
                                                        (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : patterns.Count > 0 ? $". It has {string.Join(", ", patterns.Take(10).Select(p => $"'{p.Name}'"))} and Solid." : ". Use Solid, or load the pattern into the family first."));
                }
                patternId = match.Id;
            }
            category.SetLinePatternId(patternId, GraphicsStyleType.Projection);
        }
        if (spec.Material != null)
        {
            var materialName = spec.Material.Trim();
            if (materialName.Length == 0) category.Material = null;
            else
            {
                var materials = new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().ToList();
                var match = materials.FirstOrDefault(m => string.Equals(m.Name, materialName, StringComparison.OrdinalIgnoreCase));
                if (match == null)
                {
                    var close = ModelFamilyJson.Suggest(materialName, materials.Select(m => m.Name));
                    throw new InvalidOperationException($"the family has no material '{materialName}'" +
                                                        (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : ". Create it with the materials tool first."));
                }
                category.Material = match;
            }
        }

        var after = ModelFamilyComponentSupport.DescribeSubcategory(doc, category);
        var row = new JObject
        {
            ["name"] = category.Name,
            ["id"] = category.Id.Value,
            ["action"] = existing == null ? "created" : JToken.DeepEquals(before, after) ? "unchanged" : "updated",
            ["after"] = after
        };
        if (before != null) row["before"] = before;
        return row;
    }

    // ── visibility ───────────────────────────────────────────────────────────

    private static JArray Visibility(ModelFamilyContext context, List<ComponentVisibilitySpec> specs, Dictionary<string, List<long>> created)
    {
        var rows = new JArray();
        var doc = context.FamilyDoc;
        foreach (var spec in specs)
        {
            var ids = ModelFamilyComponentSupport.ResolveIds(spec.Elements, created, context.Problems);
            if (ids.Count == 0) continue;
            var done = new JArray();
            var ok = context.TryTransact("Visibility", () =>
            {
                foreach (var id in ids)
                {
                    var element = doc.GetElement(new ElementId(id))
                                  ?? throw new InvalidOperationException($"{spec.Source}: the family has no element {id}.");
                    done.Add(ModelFamilyComponentVisibilityRevit.Apply(doc, element, spec.Request, spec.Source, context.Warnings));
                }
            }, out var error);
            if (!ok) context.Problems.Add($"{spec.Source}: {error}");
            else foreach (var row in done) rows.Add(row);
        }
        return rows;
    }
}

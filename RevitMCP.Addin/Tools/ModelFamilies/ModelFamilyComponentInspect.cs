using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>The revit_get_model_family sections of the nested-component feature (#175): what a caller needs to verify an edit.</summary>
internal static class ModelFamilyComponentInspect
{
    public static void Register()
    {
        ModelFamilyInspect.AddSection("subcategories",
            "Subcategories of the family's category: id, name, line weights, colour, pattern, material.", Subcategories);
        ModelFamilyInspect.AddSection("nestedFamilies",
            "Families loaded into the family: id, name, category, placement type, shared, types.", NestedFamilies);
        ModelFamilyInspect.AddSection("nestedInstances",
            "Nested instances: id, family, type, position and rotation (mm, degrees), host view, visible, visibleParameter, associations, references.", NestedInstances);
        ModelFamilyInspect.AddSection("symbolicLines",
            "Symbolic lines: id, geometry (mm), view, subcategory, visibility.", (context, limit) => Lines(context, limit, symbolic: true));
        ModelFamilyInspect.AddSection("modelLines",
            "Model lines (not reference lines): id, geometry (mm), work plane, subcategory, visibility.", (context, limit) => Lines(context, limit, symbolic: false));
        ModelFamilyInspect.AddSection("visibility",
            "Visibility of every form, line and nested instance: id, visible, visibleParameter, detail levels, view flags (one row each).", Visibility);
    }

    private static JToken Subcategories(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var parent = doc.OwnerFamily?.FamilyCategory;
        var rows = new JArray();
        if (parent == null) return rows;
        foreach (var category in parent.SubCategories.Cast<Category>().OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Take(limit))
            rows.Add(ModelFamilyComponentSupport.DescribeSubcategory(doc, category));
        return rows;
    }

    private static JToken NestedFamilies(ModelFamilyContext context, int limit) =>
        new JArray(ModelFamilyComponentNested.NestedFamilyList(context.FamilyDoc).Take(limit)
            .Select(f => ModelFamilyComponentNested.DescribeFamily(context.FamilyDoc, f)));

    private static JToken NestedInstances(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var rows = new JArray();
        foreach (var instance in new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>().Take(limit))
        {
            var row = ModelFamilyComponentNested.Describe(doc, instance);
            var associated = new JArray(ModelFamilyAssociations.Associable(doc, instance).OfType<JObject>().Where(r => r["associatedWith"]?.Type == JTokenType.String));
            row["associations"] = associated;
            var flags = ModelFamilyComponentVisibilityRevit.ReadFlags(instance);
            if (flags != null) row["visibilityFlags"] = flags.ToJson();
            rows.Add(row);
        }
        return rows;
    }

    private static JToken Lines(ModelFamilyContext context, int limit, bool symbolic)
    {
        var doc = context.FamilyDoc;
        var rows = new JArray();
        var curves = new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).Cast<CurveElement>()
            .Where(c => symbolic ? c is SymbolicCurve : c is ModelCurve { IsReferenceLine: false });
        foreach (var curve in curves.Take(limit))
        {
            string? plane = null;
            try { plane = curve.SketchPlane?.Name; }
            catch { /* a curve without a sketch plane */ }
            var row = ModelFamilyComponentGraphics.GraphicRow(doc, curve, plane);
            var visibility = ModelFamilyComponentVisibilityRevit.Describe(doc, curve);
            foreach (var key in new[] { "visible", "visibleParameter", "detailLevels", "views" })
                if (visibility[key] != null) row[key] = visibility[key];
            rows.Add(row);
        }
        return rows;
    }

    private static JToken Visibility(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var elements = new List<Element>();
        elements.AddRange(new FilteredElementCollector(doc).OfClass(typeof(GenericForm)).ToElements());
        elements.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CurveElement)).ToElements()
            .Where(e => e is SymbolicCurve || e is ModelCurve { IsReferenceLine: false }));
        elements.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).ToElements());
        return new JArray(elements.Take(limit).Select(e => ModelFamilyComponentVisibilityRevit.Describe(doc, e)));
    }
}

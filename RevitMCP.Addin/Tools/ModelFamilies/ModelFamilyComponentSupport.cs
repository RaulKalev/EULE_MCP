using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Helpers the nested-component tools share (#175): kinds of element, subcategories and their styles, element references.</summary>
internal static class ModelFamilyComponentSupport
{
    /// <summary>What a component element is, in the words of the tool surface.</summary>
    public static string KindOf(Element element) => element switch
    {
        FamilyInstance => "nestedInstance",
        SymbolicCurve => "symbolicLine",
        ModelCurve { IsReferenceLine: true } => "referenceLine",
        ModelCurve => "modelLine",
        GenericForm => "form",
        _ => element.GetType().Name
    };

    /// <summary>The name of the subcategory an element is on (forms hold a category, lines a graphics style).</summary>
    public static string? SubcategoryNameOf(Element element)
    {
        try
        {
            return element switch
            {
                GenericForm form => form.Subcategory?.Name,
                SymbolicCurve symbolic => symbolic.Subcategory?.GraphicsStyleCategory?.Name,
                ModelCurve model => model.Subcategory?.GraphicsStyleCategory?.Name,
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The projection graphics style of a subcategory name (what lines take); null with a reason when there is none.</summary>
    public static GraphicsStyle? ResolveStyle(Document doc, string name, out string? error)
    {
        var category = ResolveCategory(doc, name, out error);
        if (category == null) return null;
        var style = category.GetGraphicsStyle(GraphicsStyleType.Projection);
        if (style == null) error = $"'{name}' has no graphics style to assign.";
        return style;
    }

    /// <summary>
    /// The category a subcategory name stands for: a subcategory of the family's category, the family category itself,
    /// or Revit's invisible lines. Null with a reason when there is none.
    /// </summary>
    public static Category? ResolveCategory(Document doc, string name, out string? error)
    {
        error = null;
        var parent = doc.OwnerFamily?.FamilyCategory;
        Category? category = ModelFamilyLookup.Subcategory(doc, name);
        if (category == null && parent != null && string.Equals(parent.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) category = parent;
        if (category == null)
        {
            try
            {
                var invisible = doc.Settings.Categories.get_Item(BuiltInCategory.OST_InvisibleLines);
                if (invisible != null && string.Equals(invisible.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)) category = invisible;
            }
            catch { /* the document has no such category */ }
        }
        if (category == null)
        {
            var known = parent == null ? new List<string>() : parent.SubCategories.Cast<Category>().Select(c => c.Name).ToList();
            var close = ModelFamilyJson.Suggest(name, known);
            error = $"the family's category has no subcategory '{name}'" +
                    (close.Count > 0 ? $"; did you mean {string.Join(", ", close.Select(n => $"'{n}'"))}?" : known.Count > 0 ? $". It has {string.Join(", ", known.Take(10).Select(n => $"'{n}'"))}." : ".") +
                    " Create it in the subcategories list of the same call.";
        }
        return category;
    }

    /// <summary>A subcategory as a row: id, name, weights, colour, pattern, material.</summary>
    public static JObject DescribeSubcategory(Document doc, Category category)
    {
        var row = new JObject { ["id"] = category.Id.Value, ["name"] = category.Name };
        row["lineWeightProjection"] = Weight(category, GraphicsStyleType.Projection);
        row["lineWeightCut"] = Weight(category, GraphicsStyleType.Cut);
        try
        {
            var color = category.LineColor;
            row["color"] = color != null && color.IsValid ? new JArray((int)color.Red, (int)color.Green, (int)color.Blue) : null;
        }
        catch { row["color"] = null; }
        try
        {
            var patternId = category.GetLinePatternId(GraphicsStyleType.Projection);
            row["linePattern"] = patternId == ElementId.InvalidElementId ? null
                : patternId == LinePatternElement.GetSolidPatternId() ? "Solid"
                : doc.GetElement(patternId)?.Name;
        }
        catch { row["linePattern"] = null; }
        try { row["material"] = category.Material?.Name; }
        catch { row["material"] = null; }
        return row;
    }

    private static JToken? Weight(Category category, GraphicsStyleType type)
    {
        try
        {
            var weight = category.GetLineWeight(type);
            return weight == null ? null : new JValue((int)weight);
        }
        catch { return null; }
    }

    /// <summary>The element ids the references stand for (created ones resolved); a problem for each that cannot be.</summary>
    public static List<long> ResolveIds(List<ComponentElementRef> references, IReadOnlyDictionary<string, List<long>> created, List<string> problems)
    {
        var ids = new List<long>();
        foreach (var reference in references)
        {
            if (ModelFamilyComponentRefs.Resolve(reference, created, out var resolved, out var error))
            {
                foreach (var id in resolved)
                    if (!ids.Contains(id)) ids.Add(id);
            }
            else problems.Add(error!);
        }
        return ids;
    }

    /// <summary>A compact row for an element of the family: id, kind and description.</summary>
    public static JObject Row(Element element) => new()
    {
        ["id"] = element.Id.Value,
        ["kind"] = KindOf(element),
        ["description"] = ModelFamilyLookup.Describe(element)
    };
}

using Autodesk.Revit.DB;
using RevitMCP.Addin.Coordination.Clash.Geometry;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Coordination.Clash.Services;

public class ClashCategoryDiscoveryService
{
    /// <summary>Instances checked per category for a solid before the category is reported.</summary>
    private const int SolidSampleSize = 5;

    public List<object> GetClashableCategories(
        Document doc,
        bool includeLinks,
        bool includeGenericModels,
        bool includeImportedGeometry)
    {
        var result = new List<object>();

        AddDocumentCategories(doc, "Host", includeGenericModels, result);

        if (includeImportedGeometry)
        {
            var importCount = new FilteredElementCollector(doc)
                .OfClass(typeof(ImportInstance))
                .GetElementCount();
            if (importCount > 0)
                result.Add(new { category = ClashCategoryRules.ImportedGeometry, count = importCount, source = "Host" });
        }

        if (!includeLinks) return result;

        foreach (var link in new FilteredElementCollector(doc)
                     .OfClass(typeof(RevitLinkInstance))
                     .Cast<RevitLinkInstance>())
        {
            var linkDoc = link.GetLinkDocument();
            if (linkDoc == null) continue;
            AddDocumentCategories(linkDoc, link.Name, includeGenericModels, result);
        }

        return result;
    }

    /// <summary>
    /// Adds every category of <paramref name="doc"/> that <see cref="ClashCategoryRules"/> accepts, with its
    /// view-independent instance count, in preferred order.
    /// </summary>
    private static void AddDocumentCategories(Document doc, string source, bool includeGenericModels, List<object> result)
    {
        var found = new List<(string BuiltInName, string Name, int Count)>();

        foreach (Category cat in doc.Settings.Categories)
        {
            var facts = FactsFor(cat);
            if (!ClashCategoryRules.Evaluate(facts).Clashable) continue;
            if (!includeGenericModels && cat.BuiltInCategory == BuiltInCategory.OST_GenericModel) continue;

            try
            {
                var collector = InstancesOf(doc, cat.Id);
                facts.ElementCount = collector.GetElementCount();
                if (facts.ElementCount > 0)
                    facts.HasSolidGeometry = InstancesOf(doc, cat.Id)
                        .Take(SolidSampleSize)
                        .Any(e => ClashSolidExtractor.TryExtract(e, out _) != null);
            }
            catch { continue; /* category not valid in this document — skip */ }

            if (ClashCategoryRules.Evaluate(facts).Clashable)
                found.Add((facts.BuiltInName, cat.Name, facts.ElementCount ?? 0));
        }

        foreach (var (_, name, count) in ClashCategoryRules.Order(found, f => f.BuiltInName, f => f.Name))
            result.Add(new { category = name, count, source });
    }

    /// <summary>Instances of a category that clash detection collects (view-independent, not types).</summary>
    internal static FilteredElementCollector InstancesOf(Document doc, ElementId categoryId) =>
        new FilteredElementCollector(doc)
            .OfCategoryId(categoryId)
            .WhereElementIsNotElementType()
            .WhereElementIsViewIndependent();

    /// <summary>Reads the facts the clashable rule needs from a Revit category.</summary>
    internal static ClashCategoryFacts FactsFor(Category cat)
    {
        var facts = new ClashCategoryFacts { Name = cat.Name, IsSubcategory = cat.Parent != null };
        try
        {
            facts.CategoryType = cat.CategoryType.ToString();
            facts.IsTagCategory = cat.IsTagCategory;
            var bic = cat.BuiltInCategory;
            facts.BuiltInName = bic == BuiltInCategory.INVALID ? string.Empty : bic.ToString();
        }
        catch { /* leave the facts incomplete: the rule rejects them */ }
        return facts;
    }

    /// <summary>
    /// Resolves a category name for clash detection: a curated English alias or "OST_" name first, then the
    /// document's own category names (exact, case-insensitive, normalized — like the rest of the connector).
    /// Returns null with a reason when the name is unknown or the category cannot be clash-checked.
    /// </summary>
    public static BuiltInCategory? Resolve(Document doc, string name, out string? error)
    {
        error = null;
        Category? cat = null;

        var alias = ClashCategoryRules.TryResolveAlias(name);
        if (alias != null && Enum.TryParse<BuiltInCategory>(alias, true, out var aliasBic))
        {
            try { cat = Category.GetCategory(doc, aliasBic); } catch { cat = null; }
        }

        if (cat == null)
        {
            var resolved = new CategoryResolver().Resolve(doc, name);
            if (resolved.Category == null)
            {
                error = $"Unknown category '{name}' — skipped." +
                        (resolved.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", resolved.Suggestions)}?" : string.Empty) +
                        " revit_list_clashable_categories lists the categories this model has.";
                return null;
            }
            cat = resolved.Category;
        }

        var verdict = ClashCategoryRules.Evaluate(FactsFor(cat));
        if (!verdict.Clashable)
        {
            error = $"Category '{name}' cannot be clash-checked: {verdict.Reason} — skipped.";
            return null;
        }
        return cat.BuiltInCategory;
    }
}

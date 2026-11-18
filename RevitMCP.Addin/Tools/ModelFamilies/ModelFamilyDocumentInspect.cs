using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// revit_get_model_family (#169): reads the sections every model-family feature registered with
/// <see cref="ModelFamilyInspect"/>, and registers the four this feature owns (family, views, levels, elements).
/// </summary>
internal static class ModelFamilyDocumentInspect
{
    public const string IncludeArg = "include";
    public const string LimitArg = "limit";
    /// <summary>Element ids a section describes in detail. Other features' sections read it with <see cref="ElementIds"/>.</summary>
    public const string ElementIdsArg = "elementIds";

    /// <summary>
    /// The <c>elementIds</c> argument of revit_get_model_family, for any section that describes specific elements.
    /// Pass <paramref name="problems"/> only from the tool itself, so a bad id is reported once.
    /// </summary>
    public static List<long> ElementIds(ModelFamilyContext context, List<string>? problems = null) =>
        ModelFamilyDocumentSpec.ParseIds(ModelFamilyJson.Token(context.Args, ElementIdsArg), ElementIdsArg, problems);

    /// <summary>Adds the sections this feature owns. Called from <see cref="ModelFamilyDocumentTools.Register"/>.</summary>
    public static void RegisterSections()
    {
        ModelFamilyInspect.AddSection("family",
            "Name, category (name and OST_ built-in name), placement and hosting, settings (work plane-based, always vertical, shared, cuts with voids, room calculation point, part type), types, path, Revit version.",
            (context, _) => Family(context));
        ModelFamilyInspect.AddSection("views", "Views of the family: id, name, viewType, for later calls that name a view.",
            (context, limit) => Capped(context, "views", ModelFamilyDocumentRevit.Views(context.FamilyDoc, limit, out var total), total));
        ModelFamilyInspect.AddSection("levels", "Levels of the family: id, name, elevationMm.",
            (context, limit) => Capped(context, "levels", ModelFamilyDocumentRevit.Levels(context.FamilyDoc, limit, out var total), total));
        ModelFamilyInspect.AddSection("elements",
            "Counts of what the family holds by class and by category (forms, reference planes, dimensions, connectors, nested instances, curves, imports); with elementIds, a row for each of those elements.",
            Elements);
    }

    /// <summary>The body of revit_get_model_family.</summary>
    public static object? Read(ModelFamilyContext context)
    {
        var problems = context.Problems;
        var sections = ModelFamilyInspect.Sections;
        var known = sections.Select(s => s.Name).ToList();
        var include = ModelFamilyDocumentSpec.ParseInclude(ModelFamilyJson.Token(context.Args, IncludeArg), known, problems);
        var limit = ModelFamilyDocumentSpec.ParseLimit(ModelFamilyJson.Token(context.Args, LimitArg), problems);
        ElementIds(context, problems);
        if (problems.Count > 0) return null;

        var result = new JObject
        {
            ["sections"] = new JArray(sections.Select(s => new JObject { ["name"] = s.Name, ["summary"] = s.Summary }))
        };
        foreach (var name in include)
        {
            var section = sections.First(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
            try
            {
                result[section.Name] = section.Read(context, limit);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result[section.Name] = new JObject { ["error"] = ex.Message };
                context.Warnings.Add($"Section '{section.Name}' could not be read: {ex.Message.TrimEnd('.')}. The other sections are complete.");
            }
        }
        try
        {
            result["counts"] = new JObject
            {
                ["views"] = ModelFamilyLookup.Views(context.FamilyDoc).Count,
                ["types"] = context.FamilyDoc.FamilyManager.Types.Size,
                ["parameters"] = context.FamilyDoc.FamilyManager.Parameters.Size
            };
        }
        catch
        {
            // Counts are a convenience only.
        }

        var category = ModelFamilyDocumentRevit.DescribeCategory(SafeCategory(context));
        context.Summary = $"{context.FamilyName}: {category}, {ModelFamilyDocumentRevit.Placement(context.FamilyDoc)}; {include.Count} section(s).";
        return result;
    }

    private static Autodesk.Revit.DB.Category? SafeCategory(ModelFamilyContext context)
    {
        try { return context.FamilyDoc.OwnerFamily?.FamilyCategory; }
        catch { return null; }
    }

    private static JToken Family(ModelFamilyContext context)
    {
        var doc = context.FamilyDoc;
        var result = new JObject
        {
            ["name"] = context.FamilyName,
            ["category"] = ModelFamilyDocumentRevit.CategoryJson(SafeCategory(context))
        };
        foreach (var property in ModelFamilyDocumentRevit.PlacementJson(doc).Properties()) result[property.Name] = property.Value;
        foreach (var property in ModelFamilyDocumentRevit.SettingsJson(doc).Properties()) result[property.Name] = property.Value;
        var partTypeText = ModelFamilyDocumentRevit.PartTypeText(doc);
        if (partTypeText.Length > 0) result["partTypeText"] = partTypeText;
        result["changeableSettings"] = new JArray(ModelFamilyDocumentRevit.ChangeableSettings(doc));
        string? currentType = null;
        try { currentType = doc.FamilyManager.CurrentType?.Name; } catch { /* no current type */ }
        result["currentType"] = currentType;
        result["typeNames"] = new JArray(ModelFamilyDocumentRevit.TypeNames(doc));
        result["path"] = string.IsNullOrEmpty(doc.PathName) ? null : doc.PathName;
        try { result["revitVersion"] = context.Uiapp.Application.VersionNumber; } catch { /* diagnostics only */ }
        try { result["isModified"] = doc.IsModified; } catch { /* not available for this document */ }
        return result;
    }

    private static JToken Elements(ModelFamilyContext context, int limit)
    {
        var doc = context.FamilyDoc;
        var result = new JObject
        {
            ["byClass"] = JObject.FromObject(new SortedDictionary<string, int>(ModelFamilyDocumentRevit.CountsByClass(doc), StringComparer.Ordinal)),
            ["byCategory"] = JObject.FromObject(new SortedDictionary<string, int>(ModelFamilyDocumentRevit.CountsByCategory(doc), StringComparer.Ordinal))
        };
        var ids = ElementIds(context);
        if (ids.Count > 0)
        {
            result["requested"] = new JArray(ids.Take(limit).Select(id => ModelFamilyDocumentRevit.ElementRow(doc, id)));
            if (ids.Count > limit) context.Warnings.Add($"elements: {ids.Count} element ids were given; the first {limit} are described (raise limit).");
        }
        return result;
    }

    private static JToken Capped(ModelFamilyContext context, string section, JArray items, int total)
    {
        if (total > items.Count) context.Warnings.Add($"{section}: {total} found, the first {items.Count} are listed (raise limit).");
        return items;
    }
}

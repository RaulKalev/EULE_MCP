using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// Reads the family-level state the document tools (#169) report and change: category, placement, the settings of
/// <see cref="ModelFamilyDocumentSpec.Settings"/>, views, levels and element counts. Shared by the read tool and the
/// create/edit tools so a before/after compares like with like.
/// </summary>
internal static class ModelFamilyDocumentRevit
{
    // ── Category ─────────────────────────────────────────────────────────────

    public static JObject CategoryJson(Category? category) => category == null
        ? new JObject { ["name"] = null, ["builtIn"] = null }
        : new JObject { ["name"] = category.Name, ["builtIn"] = BuiltInName(category), ["id"] = category.Id.Value };

    public static string? BuiltInName(Category category)
    {
        try
        {
            var builtIn = category.BuiltInCategory;
            return builtIn == BuiltInCategory.INVALID ? null : builtIn.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static string DescribeCategory(Category? category) => category == null
        ? "(none)"
        : BuiltInName(category) is { } builtIn ? $"{category.Name} ({builtIn})" : category.Name;

    /// <summary>The top-level categories of the family document, for matching the <c>category</c> argument.</summary>
    public static List<Category> Categories(Document familyDoc)
    {
        var result = new List<Category>();
        foreach (Category category in familyDoc.Settings.Categories)
        {
            try { if (category != null && category.Name.Length > 0) result.Add(category); }
            catch { /* a category Revit cannot describe */ }
        }
        return result;
    }

    // ── Placement ────────────────────────────────────────────────────────────

    public static string Placement(Document familyDoc)
    {
        try { return familyDoc.OwnerFamily.FamilyPlacementType.ToString(); }
        catch { return "Invalid"; }
    }

    public static string? HostingBehavior(Document familyDoc)
    {
        var parameter = OwnerParameter(familyDoc, BuiltInParameter.FAMILY_HOSTING_BEHAVIOR);
        if (parameter == null) return null;
        try { return parameter.AsValueString() ?? parameter.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch { return null; }
    }

    /// <summary>Placement type, hosting text, whether a host is needed, and one sentence for the caller.</summary>
    public static JObject PlacementJson(Document familyDoc)
    {
        var placement = Placement(familyDoc);
        var needsHost = ModelFamilyDocumentSpec.PlacementNeedsHost(placement);
        var workPlaneBased = ReadSetting(familyDoc, ModelFamilyDocumentSpec.Settings[0]) is JValue { Type: JTokenType.Boolean } v ? (bool?)v : null;
        return new JObject
        {
            ["placement"] = placement,
            ["hostingBehavior"] = HostingBehavior(familyDoc),
            ["isHosted"] = needsHost,
            ["placeableWithoutHost"] = needsHost == null ? null : !needsHost.Value,
            ["placementNote"] = ModelFamilyDocumentSpec.PlacementNote(placement, workPlaneBased)
        };
    }

    // ── Settings ─────────────────────────────────────────────────────────────

    public static BuiltInParameter BuiltIn(ModelFamilySettingInfo info) =>
        Enum.TryParse<BuiltInParameter>(info.BuiltInParameter, out var builtIn) ? builtIn : BuiltInParameter.INVALID;

    public static Parameter? OwnerParameter(Document familyDoc, BuiltInParameter builtIn)
    {
        if (builtIn == BuiltInParameter.INVALID) return null;
        try { return familyDoc.OwnerFamily?.get_Parameter(builtIn); }
        catch { return null; }
    }

    public static Parameter? SettingParameter(Document familyDoc, ModelFamilySettingInfo info) => OwnerParameter(familyDoc, BuiltIn(info));

    /// <summary>A setting's current value: true/false, a part type name, or null when the family has no such parameter.</summary>
    public static JToken? ReadSetting(Document familyDoc, ModelFamilySettingInfo info)
    {
        var parameter = SettingParameter(familyDoc, info);
        if (parameter == null || parameter.StorageType != StorageType.Integer) return null;
        try
        {
            var value = parameter.AsInteger();
            if (info.Kind == ModelFamilySettingKind.Bool) return new JValue(value != 0);
            return new JValue(PartTypeName(value));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Every setting of the table with its value (null where the family has none) and whether it can be changed.</summary>
    public static JObject SettingsJson(Document familyDoc)
    {
        var result = new JObject();
        foreach (var info in ModelFamilyDocumentSpec.Settings) result[info.Key] = ReadSetting(familyDoc, info);
        return result;
    }

    /// <summary>The settings this family (its category and template) lets an edit change, for problem messages.</summary>
    public static List<string> ChangeableSettings(Document familyDoc) => ModelFamilyDocumentSpec.Settings
        .Where(info => SettingParameter(familyDoc, info) is { } p && !SafeReadOnly(p))
        .Select(info => info.Key).ToList();

    public static bool SafeReadOnly(Parameter parameter)
    {
        try { return parameter.IsReadOnly; }
        catch { return true; }
    }

    /// <summary>
    /// The PartType name of a stored integer. Compared as Int64 because Revit's enums do not share one underlying
    /// type (BuiltInCategory is Int64), so a cast through int could throw or miss.
    /// </summary>
    public static string PartTypeName(long value)
    {
        foreach (var item in Enum.GetValues(typeof(PartType)))
            if (Convert.ToInt64(item, System.Globalization.CultureInfo.InvariantCulture) == value) return item.ToString()!;
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>The stored integer of a PartType name, or null.</summary>
    public static int? PartTypeValue(string enumName)
    {
        foreach (var item in Enum.GetValues(typeof(PartType)))
            if (string.Equals(item.ToString(), enumName, StringComparison.Ordinal))
                return (int)Convert.ToInt64(item, System.Globalization.CultureInfo.InvariantCulture);
        return null;
    }

    public static string[] PartTypeNames() => Enum.GetNames(typeof(PartType));

    public static string PartTypeText(Document familyDoc)
    {
        var parameter = SettingParameter(familyDoc, ModelFamilyDocumentSpec.Settings.First(s => s.Kind == ModelFamilySettingKind.PartType));
        try { return parameter?.AsValueString() ?? string.Empty; }
        catch { return string.Empty; }
    }

    // ── Views, levels, parameters, types ─────────────────────────────────────

    public static JArray Views(Document familyDoc, int limit, out int total)
    {
        var views = ModelFamilyLookup.Views(familyDoc).OrderBy(v => v.ViewType.ToString(), StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase).ToList();
        total = views.Count;
        return new JArray(views.Take(limit).Select(v => new JObject { ["id"] = v.Id.Value, ["name"] = v.Name, ["viewType"] = v.ViewType.ToString() }));
    }

    public static JArray Levels(Document familyDoc, int limit, out int total)
    {
        var levels = new FilteredElementCollector(familyDoc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation).ToList();
        total = levels.Count;
        return new JArray(levels.Take(limit).Select(l => new JObject
        {
            ["id"] = l.Id.Value,
            ["name"] = l.Name,
            ["elevationMm"] = ModelFamilyJson.ToMm(l.Elevation)
        }));
    }

    public static List<string> ParameterNames(Document familyDoc)
    {
        var names = new List<string>();
        foreach (FamilyParameter parameter in familyDoc.FamilyManager.Parameters)
        {
            try { names.Add(parameter.Definition.Name); }
            catch { /* a parameter without a definition */ }
        }
        return names;
    }

    public static List<string> TypeNames(Document familyDoc)
    {
        var names = new List<string>();
        foreach (FamilyType type in familyDoc.FamilyManager.Types)
        {
            try { if (!string.IsNullOrEmpty(type.Name)) names.Add(type.Name); }
            catch { /* a type being removed */ }
        }
        return names;
    }

    // ── Element counts ───────────────────────────────────────────────────────

    /// <summary>
    /// What the family holds, counted by class: forms (Extrusion, Blend, …), reference planes and points, dimensions,
    /// connectors, nested instances, curves (ModelLine, SymbolicCurve, …), imports and text. Plus the family
    /// parameters and types. These are the keys of the before/after comparison.
    /// </summary>
    public static Dictionary<string, int> CountsByClass(Document familyDoc)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var element in Counted(familyDoc))
        {
            var key = element.GetType().Name;
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        counts["FamilyParameter"] = familyDoc.FamilyManager.Parameters.Size;
        counts["FamilyType"] = familyDoc.FamilyManager.Types.Size;
        return counts;
    }

    /// <summary>The same elements as <see cref="CountsByClass"/>, counted by category name.</summary>
    public static Dictionary<string, int> CountsByCategory(Document familyDoc)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var element in Counted(familyDoc))
        {
            string key;
            try { key = element.Category?.Name ?? "(no category)"; }
            catch { key = "(no category)"; }
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private static IEnumerable<Element> Counted(Document familyDoc) =>
        new FilteredElementCollector(familyDoc).WhereElementIsNotElementType().Where(IsCounted);

    private static bool IsCounted(Element element) => element is GenericForm or ReferencePlane or ReferencePoint or Dimension
        or ConnectorElement or FamilyInstance or CurveElement or ImportInstance or ModelText or TextNote;

    /// <summary>A short row for an element named by id.</summary>
    public static JObject ElementRow(Document familyDoc, long id)
    {
        var element = familyDoc.GetElement(new ElementId(id));
        if (element == null) return new JObject { ["id"] = id, ["error"] = "the family has no element with this id" };
        string? name = null, category = null;
        try { name = element.Name; } catch { /* no name */ }
        try { category = element.Category?.Name; } catch { /* no category */ }
        return new JObject
        {
            ["id"] = id,
            ["class"] = element.GetType().Name,
            ["category"] = category,
            ["name"] = string.IsNullOrEmpty(name) ? null : name
        };
    }
}

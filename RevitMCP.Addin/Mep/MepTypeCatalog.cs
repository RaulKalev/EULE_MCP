using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;

namespace RevitMCP.Addin.Mep;

/// <summary>Which fittings a tray or conduit type can place, and where that was read.</summary>
internal sealed class MepFittingAvailability
{
    /// <summary>Fitting kind (elbow, verticalElbow, tee, cross, transition, union) → "Family: Type", or null when missing.</summary>
    public Dictionary<string, string?> Families { get; } = new(StringComparer.Ordinal);

    /// <summary>Fitting kind → true when routing preferences of the type list rules for it.</summary>
    public Dictionary<string, bool> RoutingRules { get; } = new(StringComparer.Ordinal);

    public bool WithFittings { get; set; } = true;

    public bool Has(string kind) =>
        !WithFittings && (kind == "elbow" || kind == "verticalElbow") ||
        Families.TryGetValue(kind, out var family) && family != null ||
        RoutingRules.TryGetValue(kind, out var rules) && rules;

    /// <summary>{elbow: available|missing, …} as the tools report it.</summary>
    public Dictionary<string, string> Status() =>
        MepTypeCatalog.FittingKinds.ToDictionary(k => k, k => Has(k) ? "available" : "missing", StringComparer.Ordinal);

    public List<string> Missing(IEnumerable<string> kinds) => kinds.Where(k => !Has(k)).Distinct().ToList();
}

/// <summary>
/// The cable tray and conduit types of a document (#106): lookup by name or id, fitting availability and standard sizes.
/// Tray and conduit types do not use pipe/duct routing preferences for their fittings: Revit keeps the default bend, tee,
/// cross, transition and union families as type parameters (RBS_CURVETYPE_DEFAULT_*). Both sources are read: a fitting
/// counts as available when the type parameter names a family or the routing preference manager lists rules for it.
/// </summary>
internal static class MepTypeCatalog
{
    public static readonly string[] FittingKinds = { "elbow", "verticalElbow", "tee", "cross", "transition", "union" };

    private static readonly Dictionary<string, BuiltInParameter[]> FittingParameters = new(StringComparer.Ordinal)
    {
        ["elbow"] = new[]
        {
            BuiltInParameter.RBS_CURVETYPE_DEFAULT_HORIZONTAL_BEND_PARAM, BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM,
            BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOW_PARAM
        },
        // Cable trays have their own vertical inside/outside bends; a conduit's one bend serves every direction.
        ["verticalElbow"] = new[]
        {
            BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOWUP_PARAM, BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOWDOWN_PARAM,
            BuiltInParameter.RBS_CURVETYPE_DEFAULT_BEND_PARAM, BuiltInParameter.RBS_CURVETYPE_DEFAULT_ELBOW_PARAM
        },
        ["tee"] = new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_TEE_PARAM },
        ["cross"] = new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_CROSS_PARAM },
        ["transition"] = new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_TRANSITION_PARAM },
        ["union"] = new[] { BuiltInParameter.RBS_CURVETYPE_DEFAULT_UNION_PARAM }
    };

    private static readonly Dictionary<string, RoutingPreferenceRuleGroupType> RuleGroups = new(StringComparer.Ordinal)
    {
        ["elbow"] = RoutingPreferenceRuleGroupType.Elbows,
        ["verticalElbow"] = RoutingPreferenceRuleGroupType.Elbows,
        ["tee"] = RoutingPreferenceRuleGroupType.Junctions,
        ["cross"] = RoutingPreferenceRuleGroupType.Crosses,
        ["transition"] = RoutingPreferenceRuleGroupType.Transitions,
        ["union"] = RoutingPreferenceRuleGroupType.Unions
    };

    public const double MmPerFoot = 304.8;

    public static double FtToMm(double feet) => feet * MmPerFoot;

    public static string KindOf(MEPCurveType type) => type is ConduitType ? MepKind.Conduit : MepKind.CableTray;

    public static string KindOf(Element element) => element is Conduit ? MepKind.Conduit : MepKind.CableTray;

    public static bool IsKind(Element? element, string kind) =>
        kind == MepKind.CableTray ? element is CableTray : element is Conduit;

    public static List<MEPCurveType> Types(Document doc, string? kind)
    {
        var result = new List<MEPCurveType>();
        if (kind == null || kind == MepKind.CableTray)
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CableTrayType)).Cast<MEPCurveType>());
        if (kind == null || kind == MepKind.Conduit)
            result.AddRange(new FilteredElementCollector(doc).OfClass(typeof(ConduitType)).Cast<MEPCurveType>());
        return result.OrderBy(t => KindOf(t), StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A type of the kind by numeric id or name (exact, then case-insensitive, then a unique substring).</summary>
    public static MEPCurveType? Resolve(Document doc, string kind, string selector, out string? error, out List<string> available)
    {
        error = null;
        var types = Types(doc, kind);
        available = types.Select(t => t.Name).ToList();
        var trimmed = selector.Trim();

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
        {
            var byId = types.FirstOrDefault(t => t.Id.Value == id);
            if (byId != null) return byId;
            error = doc.GetElement(new ElementId(id)) is MEPCurveType other
                ? $"Type {id} ('{other.Name}') is a {MepKind.Label(KindOf(other))} type, not a {MepKind.Label(kind)} type."
                : $"No {MepKind.Label(kind)} type has id {id}.";
            return null;
        }

        var match = types.FirstOrDefault(t => t.Name == trimmed)
                    ?? types.FirstOrDefault(t => string.Equals(t.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        var partial = types.Where(t => t.Name.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        if (partial.Count == 1) return partial[0];

        error = types.Count == 0
            ? $"The document has no {MepKind.Label(kind)} types. Load or create one in Revit first."
            : partial.Count > 1
                ? $"'{trimmed}' matches several {MepKind.Label(kind)} types: {string.Join(", ", partial.Select(t => t.Name))}. Give the exact name or the id."
                : $"No {MepKind.Label(kind)} type is named '{trimmed}'. Types: {string.Join(", ", available)}.";
        return null;
    }

    public static MepFittingAvailability Fittings(Document doc, MEPCurveType type)
    {
        var availability = new MepFittingAvailability
        {
            WithFittings = type switch
            {
                CableTrayType tray => Safe(() => tray.IsWithFitting, true),
                ConduitType conduit => Safe(() => conduit.IsWithFitting, true),
                _ => true
            }
        };

        foreach (var kind in FittingKinds)
            availability.Families[kind] = FittingFamily(doc, type, FittingParameters[kind]);

        RoutingPreferenceManager? manager = null;
        try { manager = type.RoutingPreferenceManager; }
        catch { /* tray and conduit types may have none */ }
        foreach (var kind in FittingKinds)
        {
            var rules = 0;
            if (manager != null)
            {
                try { rules = manager.GetNumberOfRules(RuleGroups[kind]); }
                catch { rules = 0; }
            }
            availability.RoutingRules[kind] = rules > 0;
        }
        return availability;
    }

    /// <summary>"Family: Type" of the first parameter that names a fitting family, or null.</summary>
    private static string? FittingFamily(Document doc, ElementType type, IEnumerable<BuiltInParameter> parameters)
    {
        foreach (var bip in parameters)
        {
            try
            {
                var parameter = type.get_Parameter(bip);
                if (parameter == null || parameter.StorageType != StorageType.ElementId) continue;
                var id = parameter.AsElementId();
                if (id == null || id == ElementId.InvalidElementId) continue;
                if (doc.GetElement(id) is FamilySymbol symbol)
                    return $"{symbol.FamilyName}: {symbol.Name}";
            }
            catch
            {
                // A parameter this type does not carry.
            }
        }
        return null;
    }

    /// <summary>The project's cable tray size table (Electrical Settings), nominal sizes in mm. Used for width and height alike.</summary>
    public static List<double> TraySizesMm(Document doc)
    {
        var result = new List<double>();
        try
        {
            var sizes = CableTraySizes.GetCableTraySizes(doc);
            if (sizes == null) return result;
            foreach (MEPSize size in sizes)
                result.Add(MepMath.Round(FtToMm(size.NominalDiameter)));
        }
        catch
        {
            // No electrical settings in this document.
        }
        return result.Distinct().OrderBy(s => s).ToList();
    }

    /// <summary>Every conduit standard of the project and its nominal sizes in mm (Electrical Settings → Conduit Settings → Size).</summary>
    public static Dictionary<string, List<double>> ConduitStandards(Document doc)
    {
        var result = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var settings = ConduitSizeSettings.GetConduitSizeSettings(doc);
            if (settings == null) return result;
            foreach (KeyValuePair<string, ConduitSizes> standard in settings)
            {
                var sizes = new List<double>();
                foreach (ConduitSize size in standard.Value)
                {
                    if (!Safe(() => size.UsedInSizeLists, true)) continue;
                    sizes.Add(MepMath.Round(FtToMm(size.NominalDiameter)));
                }
                result[standard.Key] = sizes.Distinct().OrderBy(s => s).ToList();
            }
        }
        catch
        {
            // No conduit settings in this document.
        }
        return result;
    }

    /// <summary>The conduit standard a conduit type uses ("Standard" type parameter), or null.</summary>
    public static string? ConduitStandard(ConduitType type)
    {
        try
        {
            var parameter = type.get_Parameter(BuiltInParameter.CONDUIT_STANDARD_TYPE_PARAM);
            if (parameter == null) return null;
            var value = parameter.AsValueString();
            if (string.IsNullOrWhiteSpace(value) && parameter.StorageType == StorageType.String) value = parameter.AsString();
            return string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The standard sizes a conduit type allows: its standard's list, or null when it cannot be told.</summary>
    public static (string? Standard, List<double>? SizesMm) ConduitSizesOf(Document doc, ConduitType type, Dictionary<string, List<double>>? standards = null)
    {
        standards ??= ConduitStandards(doc);
        var standard = ConduitStandard(type);
        if (standard != null && standards.TryGetValue(standard, out var sizes) && sizes.Count > 0)
            return (standard, sizes);
        // One standard in the project: it can only be that one.
        if (standards.Count == 1 && standards.Values.First().Count > 0)
            return (standard ?? standards.Keys.First(), standards.Values.First());
        return (standard, null);
    }

    public static string? FamilyName(ElementType type) => Safe(() => type.FamilyName, (string?)null);

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}

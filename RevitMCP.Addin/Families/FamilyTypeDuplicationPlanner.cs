using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Tools;

namespace RevitMCP.Addin.Families;

/// <summary>
/// Turns the duplicate-family-types arguments into a concrete list of copies to create.
/// The preview tool renders the plan; the write tool executes exactly the same plan, so the two
/// can never drift apart.
/// </summary>
internal static class FamilyTypeDuplicationPlanner
{
    public const int MaxCopies = 50;

    public static FamilyTypeDuplicationRequest ParseRequest(Dictionary<string, object?> arguments)
    {
        return new FamilyTypeDuplicationRequest
        {
            TypeIds = ToolArguments.GetLongArray(arguments, "typeIds"),
            FamilyName = ToolArguments.GetString(arguments, "familyName").Trim(),
            TypeName = ToolArguments.GetString(arguments, "typeName").Trim(),
            NumberOfCopies = ToolArguments.GetInt(arguments, "numberOfCopies", 1),
            NamePrefix = ToolArguments.GetString(arguments, "namePrefix"),
            NameSuffix = ToolArguments.GetString(arguments, "nameSuffix", " - Copy"),
            NewTypeNames = ToolArguments.GetStringArray(arguments, "newTypeNames"),
            ParameterOverrides = ViewManagerToolSupport.GetStringDictionary(arguments, "parameterOverrides"),
            Variants = ParseVariants(arguments)
        };
    }

    /// <summary>
    /// Builds the plan. Returns null with <paramref name="error"/> set when the request itself is
    /// invalid; per-copy problems are reported on the individual plan entries instead.
    /// </summary>
    public static List<PlannedFamilyTypeCopy>? Build(
        Document doc,
        FamilyTypeDuplicationRequest request,
        List<string> warnings,
        out string? error)
    {
        error = null;

        if (request.NumberOfCopies < 1 || request.NumberOfCopies > MaxCopies)
        {
            error = $"numberOfCopies must be between 1 and {MaxCopies}.";
            return null;
        }

        var sources = FamilyTypeSupport.ResolveTypes(
            doc, request.TypeIds, request.FamilyName, request.TypeName, warnings, out error);
        if (sources == null)
            return null;

        if (request.Variants.Count > 0)
        {
            if (sources.Count != 1)
            {
                error = "variants describes one copy per entry and needs exactly one source type. " +
                        $"{sources.Count} source types were resolved — call the tool once per source type.";
                return null;
            }
            if (request.NumberOfCopies != 1)
            {
                error = "variants and numberOfCopies cannot be combined; the variants array already " +
                        "defines how many copies are created.";
                return null;
            }
        }

        if (request.NewTypeNames.Length > 0)
        {
            if (request.Variants.Count > 0)
            {
                error = "Provide either newTypeNames or variants, not both.";
                return null;
            }
            if (request.NumberOfCopies != 1)
            {
                error = "newTypeNames gives one explicit name per source type and cannot be combined " +
                        "with numberOfCopies > 1.";
                return null;
            }
            if (request.NewTypeNames.Length != sources.Count)
            {
                error = $"newTypeNames has {request.NewTypeNames.Length} entries but {sources.Count} " +
                        "source type(s) were resolved. Supply one name per source type.";
                return null;
            }
        }

        var takenNamesByFamily = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var plan = new List<PlannedFamilyTypeCopy>();

        for (var sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
        {
            var source = sources[sourceIndex];
            var takenNames = GetTakenNames(doc, source, takenNamesByFamily);

            if (request.Variants.Count > 0)
            {
                foreach (var variant in request.Variants)
                {
                    plan.Add(BuildEntry(
                        source, variant.Name, variant.Parameters, takenNames, plan.Count + 1, request.Variants.Count));
                }
                continue;
            }

            for (var copyIndex = 1; copyIndex <= request.NumberOfCopies; copyIndex++)
            {
                var requestedName = request.NewTypeNames.Length > 0
                    ? request.NewTypeNames[sourceIndex]
                    : FamilyTypeNamePlanner.ComposeCopyName(
                        FamilyTypeSupport.SafeName(source),
                        request.NamePrefix,
                        request.NameSuffix,
                        copyIndex,
                        request.NumberOfCopies);

                plan.Add(BuildEntry(
                    source, requestedName, request.ParameterOverrides, takenNames, copyIndex, request.NumberOfCopies));
            }
        }

        return plan;
    }

    private static PlannedFamilyTypeCopy BuildEntry(
        ElementType source,
        string requestedName,
        Dictionary<string, string> parameters,
        HashSet<string> takenNames,
        int copyIndex,
        int totalCopies)
    {
        var entry = new PlannedFamilyTypeCopy
        {
            Source = source,
            CopyIndex = copyIndex,
            TotalCopies = totalCopies,
            RequestedName = requestedName,
            Parameters = parameters
        };

        if (!FamilyTypeNamePlanner.IsValidTypeName(requestedName, out var nameError))
        {
            entry.BlockedReason = nameError;
            entry.ResolvedName = requestedName;
            return entry;
        }

        var resolved = FamilyTypeNamePlanner.ResolveUniqueName(requestedName, takenNames);
        entry.ResolvedName = resolved;
        entry.WasRenamedForUniqueness = !string.Equals(resolved, requestedName, StringComparison.Ordinal);
        takenNames.Add(resolved);
        return entry;
    }

    private static HashSet<string> GetTakenNames(
        Document doc,
        ElementType type,
        Dictionary<string, HashSet<string>> cache)
    {
        var key = FamilyKey(type);
        if (cache.TryGetValue(key, out var cached))
            return cached;

        var names = FamilyTypeSupport.CollectSiblingNames(doc, type);
        cache[key] = names;
        return names;
    }

    /// <summary>
    /// Identifies the scope a type name has to be unique in: the owning family for loadable types,
    /// the type class plus category for system types.
    /// </summary>
    private static string FamilyKey(ElementType type)
    {
        try
        {
            if (type is FamilySymbol symbol && symbol.Family != null)
                return "family:" + symbol.Family.Id.Value;
        }
        catch { }

        return "class:" + type.GetType().FullName + "|category:" + (type.Category?.Id.Value.ToString() ?? "none");
    }

    private static List<FamilyTypeVariant> ParseVariants(Dictionary<string, object?> arguments)
    {
        var variants = new List<FamilyTypeVariant>();
        if (!arguments.TryGetValue("variants", out var raw) || raw == null)
            return variants;

        JArray? array = raw switch
        {
            JArray ja => ja,
            string text => ToolArguments.TryParseJArray(text),
            _ => TryFromObject(raw)
        };

        if (array == null)
            return variants;

        foreach (var token in array)
        {
            if (token is not JObject obj)
                continue;

            var name = obj["name"]?.Value<string>()
                       ?? obj["newTypeName"]?.Value<string>()
                       ?? string.Empty;

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj["parameters"] is JObject parameterObject)
            {
                foreach (var property in parameterObject.Properties())
                {
                    parameters[property.Name] = property.Value.Type == JTokenType.Null
                        ? string.Empty
                        : property.Value.ToString();
                }
            }

            variants.Add(new FamilyTypeVariant { Name = name, Parameters = parameters });
        }

        return variants;
    }

    private static JArray? TryFromObject(object value)
    {
        try { return JArray.FromObject(value); }
        catch { return null; }
    }
}

internal sealed class FamilyTypeDuplicationRequest
{
    public long[] TypeIds { get; set; } = [];
    public string FamilyName { get; set; } = string.Empty;
    public string TypeName { get; set; } = string.Empty;
    public int NumberOfCopies { get; set; } = 1;
    public string NamePrefix { get; set; } = string.Empty;
    public string NameSuffix { get; set; } = " - Copy";
    public string[] NewTypeNames { get; set; } = [];
    public Dictionary<string, string> ParameterOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FamilyTypeVariant> Variants { get; set; } = new();
}

internal sealed class FamilyTypeVariant
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class PlannedFamilyTypeCopy
{
    public ElementType Source { get; set; } = null!;
    public int CopyIndex { get; set; }
    public int TotalCopies { get; set; }
    public string RequestedName { get; set; } = string.Empty;
    public string ResolvedName { get; set; } = string.Empty;
    public bool WasRenamedForUniqueness { get; set; }
    public string? BlockedReason { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool CanDuplicate => BlockedReason == null;
}

namespace RevitMCP.Addin.Query;

/// <summary>
/// The family and type names of an element, resolved from its <c>ElementType</c>.
/// Used to answer the identity pseudo-parameters (Type, Type Name, Family, Family Name,
/// Family and Type) that Revit does not expose as readable string parameters.
/// </summary>
public sealed class ElementIdentity
{
    public string FamilyName { get; init; } = string.Empty;
    public string TypeName { get; init; } = string.Empty;

    /// <summary>
    /// Phase, design option and workset names (#109), set by callers that read them for the model-state
    /// pseudo-parameters. Null: not read, so those filters fall back to the element's own parameters.
    /// </summary>
    public ElementModelState? ModelState { get; init; }

    public static readonly ElementIdentity Empty = new();
}

/// <summary>
/// An element's model state as names (#109): the phase it was created and demolished in, its design option and its
/// workset. Empty string = none or not applicable (no phase parameters, main model, not workshared).
/// </summary>
public sealed class ElementModelState
{
    public string PhaseCreated { get; init; } = string.Empty;
    public string PhaseDemolished { get; init; } = string.Empty;
    public string DesignOption { get; init; } = string.Empty;
    public string Workset { get; init; } = string.Empty;
    /// <summary>Who has the element checked out ("" = nobody); null when ownership was not read.</summary>
    public string? OwnedBy { get; init; }
}

/// <summary>
/// Pseudo-parameters for the model state (#109): Phase Created, Phase Demolished, Design Option, Workset. Revit's own
/// parameters of these names are element ids or integers whose display text varies ("Main Model", "None"); the
/// pseudo-parameters compare the plain names, with an empty value for none (Design Option notEquals "" = in an option).
/// </summary>
public static class ModelStateParameters
{
    private enum Kind { PhaseCreated, PhaseDemolished, DesignOption, Workset }

    private static readonly Dictionary<string, Kind> _names = new(StringComparer.Ordinal)
    {
        ["phasecreated"] = Kind.PhaseCreated,
        ["phasedemolished"] = Kind.PhaseDemolished,
        ["designoption"] = Kind.DesignOption,
        ["workset"] = Kind.Workset,
    };

    /// <summary>Display names accepted as model-state pseudo-parameters.</summary>
    public static readonly IReadOnlyList<string> Names = new[] { "Phase Created", "Phase Demolished", "Design Option", "Workset" };

    /// <summary>True when <paramref name="parameterName"/> names a model-state pseudo-parameter (whole name, case/whitespace-insensitive).</summary>
    public static bool IsModelStateName(string? parameterName) =>
        !string.IsNullOrWhiteSpace(parameterName) && _names.ContainsKey(ParameterMatcher.Normalize(parameterName!));

    /// <summary>The value from <paramref name="state"/>; false when the name is not one or the state was not read.</summary>
    public static bool TryGetValue(string? parameterName, ElementModelState? state, out string value)
    {
        value = string.Empty;
        if (state == null || string.IsNullOrWhiteSpace(parameterName) ||
            !_names.TryGetValue(ParameterMatcher.Normalize(parameterName!), out var kind))
            return false;
        value = (kind switch
        {
            Kind.PhaseCreated => state.PhaseCreated,
            Kind.PhaseDemolished => state.PhaseDemolished,
            Kind.DesignOption => state.DesignOption,
            _ => state.Workset
        }) ?? string.Empty;
        return true;
    }
}

/// <summary>
/// Pseudo-parameters that filter on an element's family / type names.
/// <para>
/// Revit's own "Type", "Family" and "Family and Type" instance parameters are ElementId
/// parameters, so the parameter reader surfaces them as numeric ids ("Type" = "123456"),
/// and the type element's "Type Name" / "Family Name" are not part of its iterable
/// parameter set. A filter such as <c>Type contains WiFi</c> therefore never saw the
/// type name. These names are resolved from the element's type instead.
/// </para>
/// </summary>
public static class ElementIdentityParameters
{
    private enum Kind { Type, Family, FamilyAndType }

    // Keyed by ParameterMatcher.Normalize(name): lower-case, whitespace/_/- removed.
    private static readonly Dictionary<string, Kind> _names = new(StringComparer.Ordinal)
    {
        ["type"] = Kind.Type,
        ["typename"] = Kind.Type,
        ["family"] = Kind.Family,
        ["familyname"] = Kind.Family,
        ["familyandtype"] = Kind.FamilyAndType,
    };

    /// <summary>Display names accepted as identity pseudo-parameters.</summary>
    public static readonly IReadOnlyList<string> Names =
        new[] { "Type", "Type Name", "Family", "Family Name", "Family and Type" };

    /// <summary>True when <paramref name="parameterName"/> names an identity pseudo-parameter
    /// (whole-name match, case/whitespace-insensitive; "Type Mark" or "Type Id" are not).</summary>
    public static bool IsIdentityName(string? parameterName) =>
        !string.IsNullOrWhiteSpace(parameterName) &&
        _names.ContainsKey(ParameterMatcher.Normalize(parameterName!));

    /// <summary>Resolves the pseudo-parameter value, or returns false when the name is not one.</summary>
    public static bool TryGetValue(string? parameterName, ElementIdentity? identity, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(parameterName) ||
            !_names.TryGetValue(ParameterMatcher.Normalize(parameterName!), out var kind))
            return false;

        identity ??= ElementIdentity.Empty;
        value = kind switch
        {
            Kind.Type => identity.TypeName ?? string.Empty,
            Kind.Family => identity.FamilyName ?? string.Empty,
            _ => FormatFamilyAndType(identity.FamilyName, identity.TypeName)
        };
        return true;
    }

    /// <summary>Revit's "Family: Type" display form.</summary>
    public static string FormatFamilyAndType(string? familyName, string? typeName)
    {
        var fam = familyName?.Trim() ?? string.Empty;
        var type = typeName?.Trim() ?? string.Empty;
        if (fam.Length == 0) return type;
        if (type.Length == 0) return fam;
        return $"{fam}: {type}";
    }
}

/// <summary>
/// Shared parameter-filter evaluation used by every query-style tool
/// (find_elements_by_parameter, select_elements_by_query, get_elements_info,
/// count/group/export tools and the uncircuited-element tools). Pure logic.
/// </summary>
public static class ParameterFilterEvaluator
{
    /// <summary>True when any filter targets an identity pseudo-parameter, so the caller
    /// must resolve the element's <see cref="ElementIdentity"/>.</summary>
    public static bool NeedsIdentity(IEnumerable<ParameterFilterDto> filters) =>
        filters.Any(f => ElementIdentityParameters.IsIdentityName(f.ParameterName) ||
                         ModelStateParameters.IsModelStateName(f.ParameterName));

    /// <summary>True when any filter targets a model-state pseudo-parameter (Phase Created, Design Option, ...), so
    /// the caller should fill <see cref="ElementIdentity.ModelState"/> (#109).</summary>
    public static bool NeedsModelState(IEnumerable<ParameterFilterDto> filters) =>
        filters.Any(f => ModelStateParameters.IsModelStateName(f.ParameterName));

    /// <summary>
    /// Filters that must be evaluated against real parameter values. Model-state filters stay in the list unless
    /// <paramref name="modelStateResolved"/>: a caller that does not fill <see cref="ElementIdentity.ModelState"/>
    /// keeps matching them against Revit's own Phase Created / Workset parameters, as before #109.
    /// </summary>
    public static List<ParameterFilterDto> ParameterBackedFilters(IEnumerable<ParameterFilterDto> filters, bool modelStateResolved = false) =>
        filters.Where(f => !ElementIdentityParameters.IsIdentityName(f.ParameterName) &&
                           !(modelStateResolved && ModelStateParameters.IsModelStateName(f.ParameterName))).ToList();

    /// <summary>
    /// Evaluates all filters (AND). Identity pseudo-parameters are answered from
    /// <paramref name="identity"/>; every other filter passes when any parameter whose name
    /// and scope match satisfies the operator. A missing parameter only passes isEmpty.
    /// </summary>
    public static bool Passes(
        IReadOnlyList<ParameterValueDto> parameters,
        IReadOnlyList<ParameterFilterDto> filters,
        ElementIdentity? identity = null)
    {
        foreach (var filter in filters)
        {
            if (ElementIdentityParameters.TryGetValue(filter.ParameterName, identity, out var identityValue) ||
                ModelStateParameters.TryGetValue(filter.ParameterName, identity?.ModelState, out identityValue))
            {
                if (!EvaluateOperator(identityValue, filter.Operator, filter.Value))
                    return false;
                continue;
            }

            var matched = false;
            var anyCandidate = false;
            foreach (var p in parameters)
            {
                if (!ScopeMatches(p.Scope, filter.Scope) ||
                    !ParameterMatcher.Matches(p.Name, filter.ParameterName, filter.MatchMode))
                    continue;

                anyCandidate = true;
                // An element id parameter shows a name (X3); a filter on the id still matches.
                if (EvaluateOperator(p.Value, filter.Operator, filter.Value) ||
                    (p.RawValue is long id && p.StorageType == "ElementId" &&
                     EvaluateOperator(id.ToString(System.Globalization.CultureInfo.InvariantCulture), filter.Operator, filter.Value)))
                {
                    matched = true;
                    break;
                }
            }

            if (!anyCandidate)
            {
                if (filter.Operator == "isEmpty") continue;
                return false;
            }

            if (!matched) return false;
        }
        return true;
    }

    public static bool ScopeMatches(string paramScope, string filterScope) =>
        filterScope switch
        {
            "Instance" => paramScope == "Instance",
            "Type" => paramScope == "Type",
            _ => true
        };

    public static bool EvaluateOperator(string? value, string op, string? filterValue)
    {
        value ??= string.Empty;
        filterValue ??= string.Empty;
        return op switch
        {
            "equals" => string.Equals(value, filterValue, StringComparison.OrdinalIgnoreCase),
            "notEquals" => !string.Equals(value, filterValue, StringComparison.OrdinalIgnoreCase),
            "contains" => value.Contains(filterValue, StringComparison.OrdinalIgnoreCase),
            "notContains" => !value.Contains(filterValue, StringComparison.OrdinalIgnoreCase),
            "startsWith" => value.StartsWith(filterValue, StringComparison.OrdinalIgnoreCase),
            "endsWith" => value.EndsWith(filterValue, StringComparison.OrdinalIgnoreCase),
            "isEmpty" => string.IsNullOrEmpty(value),
            "isNotEmpty" => !string.IsNullOrEmpty(value),
            "greaterThan" => double.TryParse(value, out var v1) && double.TryParse(filterValue, out var f1) && v1 > f1,
            "lessThan" => double.TryParse(value, out var v2) && double.TryParse(filterValue, out var f2) && v2 < f2,
            _ => false
        };
    }
}

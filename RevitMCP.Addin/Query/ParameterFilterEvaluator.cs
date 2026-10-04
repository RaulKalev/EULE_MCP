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

    public static readonly ElementIdentity Empty = new();
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
        filters.Any(f => ElementIdentityParameters.IsIdentityName(f.ParameterName));

    /// <summary>Filters that must be evaluated against real parameter values.</summary>
    public static List<ParameterFilterDto> ParameterBackedFilters(IEnumerable<ParameterFilterDto> filters) =>
        filters.Where(f => !ElementIdentityParameters.IsIdentityName(f.ParameterName)).ToList();

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
            if (ElementIdentityParameters.TryGetValue(filter.ParameterName, identity, out var identityValue))
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
                if (EvaluateOperator(p.Value, filter.Operator, filter.Value))
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

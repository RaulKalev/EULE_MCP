namespace RevitMCP.Addin.Query;

/// <summary>
/// One parameter an element exposes, reduced to what name resolution needs (no Revit types,
/// so the rules are unit-testable).
/// </summary>
public sealed class ParameterCandidate
{
    public string Name { get; set; } = string.Empty;

    /// <summary>BuiltInParameter enum name (e.g. <c>INSTANCE_FREE_HOST_OFFSET_PARAM</c>); null for project/shared/family parameters.</summary>
    public string? BuiltInParameter { get; set; }

    /// <summary>Shared parameter GUID; null when the parameter is not shared.</summary>
    public string? Guid { get; set; }

    public string StorageType { get; set; } = string.Empty;
    public bool IsReadOnly { get; set; }

    /// <summary>True when the parameter lives on the element's type rather than on the element itself.</summary>
    public bool IsTypeParameter { get; set; }

    /// <summary>Caller-owned handle (the Revit <c>Parameter</c>), carried through untouched.</summary>
    public object? Tag { get; set; }

    public bool IsBuiltIn => !string.IsNullOrEmpty(BuiltInParameter);
}

/// <summary>How a caller identifies the parameter to write.</summary>
public sealed class ParameterTarget
{
    public string Name { get; set; } = string.Empty;

    /// <summary>When true only an exact (case-insensitive) name match is accepted.</summary>
    public bool ExactMatch { get; set; }

    /// <summary>Optional BuiltInParameter enum name; when set it alone decides the match.</summary>
    public string? BuiltInParameter { get; set; }

    /// <summary>Optional shared parameter GUID; when set it alone decides the match.</summary>
    public string? Guid { get; set; }

    public string Describe() =>
        !string.IsNullOrWhiteSpace(Guid) ? $"GUID {Guid}"
        : !string.IsNullOrWhiteSpace(BuiltInParameter) ? $"built-in {BuiltInParameter}"
        : $"'{Name}'";
}

public sealed class ParameterResolution
{
    public ParameterCandidate? Selected { get; private set; }

    /// <summary>exact | partial | builtIn | guid; empty when nothing was selected.</summary>
    public string MatchedBy { get; private set; } = string.Empty;

    /// <summary>All parameters that matched equally well when the match was ambiguous.</summary>
    public IReadOnlyList<ParameterCandidate> Candidates { get; private set; } = Array.Empty<ParameterCandidate>();

    public string? Problem { get; private set; }

    public bool IsAmbiguous => Selected == null && Candidates.Count > 1;

    /// <summary>
    /// Picks the parameter a selector means. A GUID or BuiltInParameter decides on its own. Otherwise an
    /// exact name wins over a partial one, a user/shared/family parameter wins over a built-in parameter
    /// with the same name, and an instance parameter wins over a type parameter. A partial match is only
    /// used when it is the single candidate; anything still tied is reported, never guessed — writing the
    /// wrong parameter silently is worse than failing (issue #86: "Offset" landed in "Offset from Host").
    /// </summary>
    public static ParameterResolution Resolve(IReadOnlyList<ParameterCandidate> parameters, ParameterTarget selector)
    {
        if (!string.IsNullOrWhiteSpace(selector.Guid))
        {
            var target = NormalizeGuid(selector.Guid!);
            return Pick(parameters.Where(p => p.Guid != null && NormalizeGuid(p.Guid) == target).ToList(), "guid", selector);
        }

        if (!string.IsNullOrWhiteSpace(selector.BuiltInParameter))
        {
            var target = selector.BuiltInParameter!.Trim();
            return Pick(parameters.Where(p => string.Equals(p.BuiltInParameter, target, StringComparison.OrdinalIgnoreCase)).ToList(), "builtIn", selector);
        }

        var name = selector.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            return Fail("parameterName, builtInParameter or parameterGuid is required.");

        var exact = parameters.Where(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0)
            return Pick(Narrow(exact), "exact", selector);

        if (selector.ExactMatch)
            return Fail($"Parameter {selector.Describe()} not found (exactMatch=true).");

        var partial = parameters.Where(p => ParameterMatcher.Matches(p.Name, name, "ContainsNormalized")).ToList();
        if (partial.Count > 1)
            return Ambiguous(partial, selector);
        return Pick(partial, "partial", selector);
    }

    /// <summary>Tie-breaks among equally named parameters: non-built-in, then instance.</summary>
    private static List<ParameterCandidate> Narrow(List<ParameterCandidate> matches)
    {
        if (matches.Count < 2) return matches;
        var userDefined = matches.Where(p => !p.IsBuiltIn).ToList();
        if (userDefined.Count > 0) matches = userDefined;
        if (matches.Count < 2) return matches;
        var instance = matches.Where(p => !p.IsTypeParameter).ToList();
        return instance.Count > 0 ? instance : matches;
    }

    private static ParameterResolution Pick(List<ParameterCandidate> matches, string matchedBy, ParameterTarget selector)
    {
        if (matches.Count == 0)
            return Fail($"Parameter {selector.Describe()} not found.");
        if (matches.Count > 1)
            return Ambiguous(matches, selector);
        return new ParameterResolution { Selected = matches[0], MatchedBy = matchedBy, Candidates = matches };
    }

    private static ParameterResolution Ambiguous(List<ParameterCandidate> matches, ParameterTarget selector) => new()
    {
        Candidates = matches,
        Problem = $"Parameter {selector.Describe()} is ambiguous: it matches {matches.Count} parameters " +
                  $"({string.Join(", ", matches.Take(6).Select(DescribeCandidate))}). Nothing was written. " +
                  "Pass the exact name with exactMatch=true, or builtInParameter / parameterGuid."
    };

    private static ParameterResolution Fail(string problem) => new() { Problem = problem };

    public static string DescribeCandidate(ParameterCandidate p)
    {
        var origin = p.IsBuiltIn ? p.BuiltInParameter : p.Guid != null ? "shared" : "user";
        return $"'{p.Name}' [{origin}, {p.StorageType}{(p.IsTypeParameter ? ", type" : "")}]";
    }

    private static string NormalizeGuid(string guid) => guid.Trim().Trim('{', '}').ToLowerInvariant();
}

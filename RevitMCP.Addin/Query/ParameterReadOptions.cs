namespace RevitMCP.Addin.Query;

public class ParameterReadOptions
{
    public bool IncludeInstanceParameters { get; set; } = true;
    public bool IncludeTypeParameters { get; set; } = true;
    public IReadOnlyList<string> ParameterNames { get; set; } = Array.Empty<string>();
    public string ParameterNameMatchMode { get; set; } = "Contains";

    /// <summary>
    /// Optional per-parameter selectors used by filtered queries. Unlike
    /// <see cref="ParameterNames"/>, each selector retains its own match mode and scope.
    /// This lets the query engine avoid materializing unrelated parameter values while
    /// preserving the exact matching behavior of every filter.
    /// </summary>
    public IReadOnlyList<ParameterSelector> ParameterSelectors { get; set; } =
        Array.Empty<ParameterSelector>();
}

public sealed class ParameterSelector
{
    public string Name { get; set; } = string.Empty;
    public string MatchMode { get; set; } = "Contains";
    public string Scope { get; set; } = "InstanceAndType";

    public bool Matches(string parameterName, string parameterScope)
    {
        if (!ScopeMatches(parameterScope))
            return false;

        return ParameterMatcher.Matches(parameterName, Name, MatchMode);
    }

    private bool ScopeMatches(string parameterScope)
    {
        if (Scope == "Instance")
            return parameterScope == "Instance";
        if (Scope == "Type")
            return parameterScope == "Type";
        return true;
    }
}

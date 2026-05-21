using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Query;

public static class ParameterMatcher
{
    // Removes spaces, underscores, hyphens, and digits — preserves letters including Estonian.
    private static readonly Regex _stripPattern = new(@"[\s_\-]+", RegexOptions.Compiled);

    public static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return _stripPattern.Replace(name.ToLowerInvariant(), "").Trim();
    }

    /// <summary>
    /// Returns true if <paramref name="parameterName"/> matches <paramref name="searchTerm"/>
    /// under the given <paramref name="matchMode"/>.
    /// </summary>
    public static bool Matches(string parameterName, string searchTerm, string matchMode)
    {
        if (string.IsNullOrEmpty(searchTerm)) return true;
        if (string.IsNullOrEmpty(parameterName)) return false;

        return matchMode switch
        {
            "Exact" => string.Equals(parameterName, searchTerm, StringComparison.OrdinalIgnoreCase),
            "ExactNormalized" => string.Equals(Normalize(parameterName), Normalize(searchTerm), StringComparison.Ordinal),
            "ContainsNormalized" => Normalize(parameterName).Contains(Normalize(searchTerm), StringComparison.Ordinal),
            _ => parameterName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) // "Contains" is default
        };
    }
}

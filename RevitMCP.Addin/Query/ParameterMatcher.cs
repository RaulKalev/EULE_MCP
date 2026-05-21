using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Query;

public static class ParameterMatcher
{
    private static readonly Regex _stripPattern = new(@"[\s_\-]+", RegexOptions.Compiled);
    private static readonly char[] _tokenSeparators = ['_', ' ', '-', '.', ':'];

    public static string Normalize(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return _stripPattern.Replace(name.ToLowerInvariant(), "").Trim();
    }

    public static bool Matches(string parameterName, string searchTerm, string matchMode)
    {
        if (string.IsNullOrEmpty(searchTerm)) return true;
        if (string.IsNullOrEmpty(parameterName)) return false;

        return matchMode switch
        {
            "Exact" => string.Equals(parameterName, searchTerm, StringComparison.OrdinalIgnoreCase),
            "ExactNormalized" => string.Equals(Normalize(parameterName), Normalize(searchTerm), StringComparison.Ordinal),
            "ContainsNormalized" => ContainsNormalized(parameterName, searchTerm),
            _ => parameterName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool ContainsNormalized(string candidate, string searchTerm)
    {
        var candidateNorm = Normalize(candidate);
        var searchNorm = Normalize(searchTerm);

        if (candidateNorm.Contains(searchNorm, StringComparison.Ordinal))
            return true;

        // Token-based ordered matching for ELENEA-style names:
        // "ELENEA_Nimetus" -> tokens ["ELENEA","Nimetus"] -> both found in order inside "eleneaüld001nimetus"
        var tokens = searchTerm.Split(_tokenSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
            return false;

        var currentIndex = 0;
        foreach (var token in tokens)
        {
            var tokenNorm = Normalize(token);
            if (string.IsNullOrEmpty(tokenNorm))
                continue;

            var foundIndex = candidateNorm.IndexOf(tokenNorm, currentIndex, StringComparison.Ordinal);
            if (foundIndex < 0)
                return false;

            currentIndex = foundIndex + tokenNorm.Length;
        }

        return true;
    }
}

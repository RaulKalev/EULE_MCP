namespace RevitMCP.Addin.Documentation.Placement;

/// <summary>
/// Pure-logic fuzzy name matcher. No Revit API dependencies — fully unit-testable.
/// Used by ViewSheetMatchingService to find best-match sheets for views.
/// </summary>
public static class FuzzyNameMatcher
{
    /// <summary>Computes a normalized similarity score in [0,1] between two strings.</summary>
    public static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;

        var aa = a.ToLowerInvariant();
        var bb = b.ToLowerInvariant();
        if (aa == bb) return 1.0;

        int maxLen = Math.Max(aa.Length, bb.Length);
        int dist   = LevenshteinDistance(aa, bb);
        return 1.0 - (double)dist / maxLen;
    }

    /// <summary>Returns true if similarity(a, b) &gt;= threshold.</summary>
    public static bool IsMatch(string a, string b, double threshold = 0.6) =>
        Similarity(a, b) >= threshold;

    // ── private ──────────────────────────────────────────────────────────────

    private static int LevenshteinDistance(string a, string b)
    {
        int m = a.Length, n = b.Length;
        int[] prev = new int[n + 1];
        int[] curr = new int[n + 1];

        for (int j = 0; j <= n; j++) prev[j] = j;

        for (int i = 1; i <= m; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= n; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[n];
    }
}

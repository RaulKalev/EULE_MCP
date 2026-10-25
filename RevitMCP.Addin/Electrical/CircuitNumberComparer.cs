namespace RevitMCP.Addin.Electrical;

/// <summary>
/// Natural ordering for circuit numbers: digit runs compare as numbers, so "2" sorts before "10"
/// and multi-pole numbers such as "1,3" sort after "1" and before "2". Numbers sort before names
/// ("Spare"), text compares case-insensitively, and empty or null numbers sort last.
/// No Revit API dependency — safe to unit-test directly.
/// </summary>
public sealed class CircuitNumberComparer : IComparer<string?>
{
    /// <summary>Shared instance.</summary>
    public static readonly CircuitNumberComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        bool xEmpty = string.IsNullOrWhiteSpace(x), yEmpty = string.IsNullOrWhiteSpace(y);
        if (xEmpty || yEmpty) return xEmpty == yEmpty ? 0 : xEmpty ? 1 : -1;

        string a = x!.Trim(), b = y!.Trim();
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            bool aDigit = char.IsDigit(a[i]), bDigit = char.IsDigit(b[j]);
            if (aDigit != bDigit) return aDigit ? -1 : 1;

            int iEnd = i, jEnd = j;
            while (iEnd < a.Length && char.IsDigit(a[iEnd]) == aDigit) iEnd++;
            while (jEnd < b.Length && char.IsDigit(b[jEnd]) == bDigit) jEnd++;

            int cmp = aDigit
                ? CompareDigits(a.Substring(i, iEnd - i), b.Substring(j, jEnd - j))
                : string.Compare(a.Substring(i, iEnd - i), b.Substring(j, jEnd - j), StringComparison.OrdinalIgnoreCase);
            if (cmp != 0) return cmp;

            i = iEnd; j = jEnd;
        }

        // A prefix sorts first ("1" before "1,3"); identical-looking numbers fall back to ordinal for a stable order.
        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }

    /// <summary>Compares two digit runs by value (any length), then fewer leading zeros first.</summary>
    private static int CompareDigits(string a, string b)
    {
        string ta = a.TrimStart('0'), tb = b.TrimStart('0');
        if (ta.Length != tb.Length) return ta.Length.CompareTo(tb.Length);
        int cmp = string.CompareOrdinal(ta, tb);
        return cmp != 0 ? cmp : a.Length.CompareTo(b.Length);
    }
}

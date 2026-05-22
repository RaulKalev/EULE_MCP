using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documentation.Placement;

/// <summary>
/// Matches views to target sheets based on configurable match modes.
/// All methods are static and take a Document so they run on the Revit thread.
/// </summary>
public static class ViewSheetMatchingService
{
    public const string ModeExactName          = "ExactName";
    public const string ModeContains           = "Contains";
    public const string ModeFuzzy              = "Fuzzy";
    public const string ModeSheetNumberPrefix  = "SheetNumberPrefix";
    public const string ModeSheetNumberSuffix  = "SheetNumberSuffix";
    public const string ModeCustomParameter    = "CustomParameter";

    public record MatchProposal(
        long   ViewId,
        string ViewName,
        long?  SheetId,
        string? SheetNumber,
        string? SheetName,
        double  Score,
        string  Reason);

    /// <summary>
    /// For each view, find the best matching sheet from the candidate list.
    /// </summary>
    public static List<MatchProposal> Match(
        Document                   doc,
        IEnumerable<long>          viewIds,
        IEnumerable<ViewSheet>     candidateSheets,
        string                     matchMode,
        double                     fuzzyThreshold  = 0.6,
        string                     customParamName = "")
    {
        var sheets      = candidateSheets.ToList();
        var proposals   = new List<MatchProposal>();

        foreach (var viewId in viewIds)
        {
            if (doc.GetElement(new ElementId(viewId)) is not View view) continue;

            (ViewSheet? best, double bestScore, string reason) = matchMode switch
            {
                ModeExactName         => BestExact(view, sheets),
                ModeContains          => BestContains(view, sheets),
                ModeFuzzy             => BestFuzzy(view, sheets, fuzzyThreshold),
                ModeSheetNumberPrefix => BestPrefix(view, sheets),
                ModeSheetNumberSuffix => BestSuffix(view, sheets),
                ModeCustomParameter   => BestCustomParam(doc, view, sheets, customParamName),
                _                     => (null, 0, $"Unknown mode '{matchMode}'")
            };

            proposals.Add(best != null
                ? new MatchProposal(viewId, view.Name, best.Id.Value, best.SheetNumber, best.Name, bestScore, reason)
                : new MatchProposal(viewId, view.Name, null, null, null, 0, reason));
        }

        return proposals;
    }

    // ── match strategies ─────────────────────────────────────────────────────

    private static (ViewSheet?, double, string) BestExact(View view, List<ViewSheet> sheets)
    {
        // Match view name against sheet name (title) OR sheet number
        var match = sheets.FirstOrDefault(s =>
            string.Equals(s.Name, view.Name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(s.SheetNumber, view.Name, StringComparison.OrdinalIgnoreCase));
        return match != null
            ? (match, 1.0, "ExactName match")
            : (null, 0, "No exact name/number match found");
    }

    private static (ViewSheet?, double, string) BestContains(View view, List<ViewSheet> sheets)
    {
        // Match view name against sheet name (title) OR sheet number (contains)
        var match = sheets.FirstOrDefault(s =>
            s.Name.Contains(view.Name, StringComparison.OrdinalIgnoreCase) ||
            view.Name.Contains(s.Name, StringComparison.OrdinalIgnoreCase) ||
            s.SheetNumber.Contains(view.Name, StringComparison.OrdinalIgnoreCase) ||
            view.Name.Contains(s.SheetNumber, StringComparison.OrdinalIgnoreCase));
        return match != null
            ? (match, 0.8, "Contains match")
            : (null, 0, "No contains match found");
    }

    private static (ViewSheet?, double, string) BestFuzzy(View view, List<ViewSheet> sheets, double threshold)
    {
        ViewSheet? best = null;
        double bestScore = threshold;
        foreach (var s in sheets)
        {
            // Score against both sheet name and sheet number, take the higher
            var scoreByName   = FuzzyNameMatcher.Similarity(view.Name, s.Name);
            var scoreByNumber = FuzzyNameMatcher.Similarity(view.Name, s.SheetNumber);
            var score = Math.Max(scoreByName, scoreByNumber);
            if (score > bestScore) { best = s; bestScore = score; }
        }
        return best != null
            ? (best, bestScore, $"Fuzzy match (score={bestScore:F2})")
            : (null, 0, $"No fuzzy match above threshold {threshold:F2}");
    }

    private static (ViewSheet?, double, string) BestPrefix(View view, List<ViewSheet> sheets)
    {
        // Try to derive prefix from view name (e.g. "E-01 Floor Plan" → prefix "E-01")
        var parts  = view.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var prefix = parts.Length > 0 ? parts[0] : view.Name;
        var match  = sheets.FirstOrDefault(s =>
            s.SheetNumber.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return match != null
            ? (match, 0.9, $"SheetNumberPrefix match (prefix='{prefix}')")
            : (null, 0, $"No sheet number starting with '{prefix}'");
    }

    private static (ViewSheet?, double, string) BestSuffix(View view, List<ViewSheet> sheets)
    {
        var parts  = view.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var suffix = parts.Length > 0 ? parts[^1] : view.Name;
        var match  = sheets.FirstOrDefault(s =>
            s.SheetNumber.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        return match != null
            ? (match, 0.9, $"SheetNumberSuffix match (suffix='{suffix}')")
            : (null, 0, $"No sheet number ending with '{suffix}'");
    }

    private static (ViewSheet?, double, string) BestCustomParam(
        Document doc, View view, List<ViewSheet> sheets, string paramName)
    {
        if (string.IsNullOrWhiteSpace(paramName))
            return (null, 0, "customParamName is required for CustomParameter mode");

        var viewParamVal = GetParamValue(view, paramName);
        if (viewParamVal == null)
            return (null, 0, $"View has no parameter '{paramName}'");

        foreach (var s in sheets)
        {
            var sheetParamVal = GetParamValue(s, paramName);
            if (sheetParamVal != null &&
                string.Equals(sheetParamVal, viewParamVal, StringComparison.OrdinalIgnoreCase))
                return (s, 1.0, $"CustomParameter '{paramName}' match");
        }
        return (null, 0, $"No sheet with '{paramName}' = '{viewParamVal}'");
    }

    private static string? GetParamValue(Element element, string paramName)
    {
        try
        {
            var p = element.LookupParameter(paramName);
            if (p != null) return p.AsValueString() ?? p.AsString();
        }
        catch { }
        return null;
    }
}

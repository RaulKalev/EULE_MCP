using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Query;

public class CategoryResolver
{
    public CategoryResolveResult Resolve(Document doc, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CategoryResolveResult { Message = "No category specified." };

        var normSearch = ParameterMatcher.Normalize(name);
        Category? exact = null;
        Category? caseInsensitive = null;
        Category? normalized = null;
        var all = new List<string>();

        foreach (Category cat in doc.Settings.Categories)
        {
            all.Add(cat.Name);

            if (exact == null && cat.Name == name)
                exact = cat;
            if (caseInsensitive == null && string.Equals(cat.Name, name, StringComparison.OrdinalIgnoreCase))
                caseInsensitive = cat;
            if (normalized == null && ParameterMatcher.Normalize(cat.Name) == normSearch)
                normalized = cat;
        }

        var resolved = exact ?? caseInsensitive ?? normalized;
        if (resolved != null)
            return new CategoryResolveResult { Category = resolved, Message = $"Resolved to '{resolved.Name}'." };

        // Partial suggestions: normalized name contains search
        var suggestions = all
            .Where(n => ParameterMatcher.Normalize(n).Contains(normSearch, StringComparison.Ordinal)
                     || normSearch.Contains(ParameterMatcher.Normalize(n), StringComparison.Ordinal))
            .OrderBy(n => n)
            .Take(10)
            .ToList();

        return new CategoryResolveResult
        {
            Message = $"Category '{name}' not found.",
            Suggestions = suggestions
        };
    }
}

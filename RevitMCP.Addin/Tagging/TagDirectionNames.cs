using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Tagging;

/// <summary>
/// Which tag type name is the Left / Right / Up / Down variant of a family keyword. The 2026-10-25 Revit check found
/// "Paneel, Paremal" and "Paneel, Vasakul" unmatched: only English words were known, and the keyword itself was
/// never required, so any type containing "left" matched. Direction words match whole name tokens (an Estonian
/// "all" must not match inside another word). Pure.
/// </summary>
public static class TagDirectionNames
{
    private static readonly Dictionary<string, (string[] Exact, string[] Prefixes)> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Left"] = (["left"], ["vasak"]),
        ["Right"] = (["right"], ["parem"]),
        ["Up"] = (["up", "top", "ülal", "üles"], ["ülev"]),
        ["Down"] = (["down", "bottom", "all", "alla", "alt"], [])
    };

    /// <param name="keyword">Family/type keyword; when given, the name must contain it (spaces, - and _ ignored).</param>
    public static bool Matches(string? typeName, string? keyword, string direction)
    {
        if (string.IsNullOrWhiteSpace(typeName) || !Words.TryGetValue(direction, out var words)) return false;
        if (!string.IsNullOrWhiteSpace(keyword) && !Squash(typeName!).Contains(Squash(keyword!))) return false;
        var tokens = Regex.Split(typeName!.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(t => t.Length > 0);
        return tokens.Any(t => words.Exact.Contains(t) || words.Prefixes.Any(p => t.StartsWith(p, StringComparison.Ordinal)));
    }

    private static string Squash(string value) =>
        value.Replace("-", string.Empty).Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
}

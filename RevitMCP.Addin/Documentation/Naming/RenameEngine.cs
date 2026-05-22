using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Documentation.Naming;

/// <summary>
/// Pure-logic rename engine. No Revit API dependencies — fully unit-testable.
/// Modes: FindReplace, PrefixSuffix, Template, RegexFindReplace.
/// </summary>
public static class RenameEngine
{
    public const string ModeFindReplace      = "FindReplace";
    public const string ModePrefixSuffix     = "PrefixSuffix";
    public const string ModeTemplate         = "Template";
    public const string ModeRegexFindReplace = "RegexFindReplace";

    /// <param name="currentName">The current element name.</param>
    /// <param name="mode">One of the mode constants above.</param>
    /// <param name="find">Text to find (FindReplace / RegexFindReplace modes).</param>
    /// <param name="replace">Replacement text (FindReplace / RegexFindReplace modes).</param>
    /// <param name="prefix">Prefix string (PrefixSuffix mode).</param>
    /// <param name="suffix">Suffix string (PrefixSuffix mode).</param>
    /// <param name="template">Template pattern — use {Name} as placeholder (Template mode).</param>
    /// <param name="tokens">Extra token replacements for Template mode (key=token, value=replacement).</param>
    /// <returns>The proposed new name, or <c>null</c> if the name would not change.</returns>
    public static string? Apply(
        string currentName,
        string mode,
        string find    = "",
        string replace = "",
        string prefix  = "",
        string suffix  = "",
        string template = "",
        IReadOnlyDictionary<string, string>? tokens = null)
    {
        string result = mode switch
        {
            ModeFindReplace => currentName.Replace(find, replace, StringComparison.Ordinal),
            ModePrefixSuffix => prefix + currentName + suffix,
            ModeTemplate => ApplyTemplate(currentName, template, tokens),
            ModeRegexFindReplace => ApplyRegex(currentName, find, replace),
            _ => currentName
        };

        return result == currentName ? null : result;
    }

    private static string ApplyTemplate(string currentName, string template, IReadOnlyDictionary<string, string>? tokens)
    {
        if (string.IsNullOrEmpty(template)) return currentName;
        var result = template.Replace("{Name}", currentName, StringComparison.OrdinalIgnoreCase);
        if (tokens != null)
            foreach (var (k, v) in tokens)
                result = result.Replace("{" + k + "}", v, StringComparison.OrdinalIgnoreCase);
        return result;
    }

    private static string ApplyRegex(string currentName, string pattern, string replacement)
    {
        if (string.IsNullOrEmpty(pattern)) return currentName;
        try
        {
            return Regex.Replace(currentName, pattern, replacement ?? string.Empty,
                RegexOptions.None, TimeSpan.FromSeconds(2));
        }
        catch (RegexMatchTimeoutException) { return currentName; }
        catch (ArgumentException)          { return currentName; }
    }

    /// <summary>Validates the mode string and returns an error message, or null if valid.</summary>
    public static string? ValidateMode(string mode) => mode switch
    {
        ModeFindReplace or ModePrefixSuffix or ModeTemplate or ModeRegexFindReplace => null,
        _ => $"Unknown rename mode '{mode}'. Valid: {ModeFindReplace}, {ModePrefixSuffix}, {ModeTemplate}, {ModeRegexFindReplace}."
    };
}

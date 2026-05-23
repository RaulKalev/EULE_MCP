using System.Text;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Converts pattern strings like "{ProjectNumber}_{Stage}_{Discipline}-{Group}-{Sequence}"
/// into named-capture-group regexes and tries to parse a sheet number against them.
/// </summary>
internal static class SheetNumberParser
{
    // Maps placeholder names to their regex capture groups.
    // The character class restricts matching to what makes sense for each segment.
    private static readonly Dictionary<string, string> PlaceholderCaptures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ProjectNumber"] = @"(?<ProjectNumber>[^_]+)",
        ["Stage"]         = @"(?<Stage>[^_]+)",
        ["Discipline"]    = @"(?<Discipline>[^-_]+)",
        ["Group"]         = @"(?<Group>[^-_]+)",
        ["Sequence"]      = @"(?<Sequence>[^_]+)",
        ["Revision"]      = @"(?<Revision>.+)",
        ["Date"]          = @"(?<Date>.+)",
    };

    public static ParsedSheetNumber? Parse(string sheetNumber, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var result = TryParse(sheetNumber, pattern);
            if (result is not null) return result;
        }
        return null;
    }

    private static ParsedSheetNumber? TryParse(string sheetNumber, string pattern)
    {
        var regexStr = BuildRegex(pattern);
        var match = Regex.Match(sheetNumber, regexStr, RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        return new ParsedSheetNumber
        {
            ProjectNumber = match.Groups["ProjectNumber"].Value,
            Stage         = match.Groups["Stage"].Value,
            Discipline    = match.Groups["Discipline"].Value,
            Group         = match.Groups["Group"].Value,
            Sequence      = match.Groups["Sequence"].Value,
            Revision      = match.Groups["Revision"].Success ? match.Groups["Revision"].Value : string.Empty,
        };
    }

    private static string BuildRegex(string pattern)
    {
        var sb = new StringBuilder("^");
        var remaining = pattern;

        while (remaining.Length > 0)
        {
            var braceStart = remaining.IndexOf('{');
            if (braceStart < 0)
            {
                sb.Append(Regex.Escape(remaining));
                break;
            }

            if (braceStart > 0)
                sb.Append(Regex.Escape(remaining[..braceStart]));

            var braceEnd = remaining.IndexOf('}', braceStart);
            if (braceEnd < 0)
            {
                sb.Append(Regex.Escape(remaining[braceStart..]));
                break;
            }

            var placeholder = remaining[(braceStart + 1)..braceEnd];
            sb.Append(PlaceholderCaptures.TryGetValue(placeholder, out var capture)
                ? capture
                : $"(?<{placeholder}>[^_-]+)");

            remaining = remaining[(braceEnd + 1)..];
        }

        sb.Append('$');
        return sb.ToString();
    }
}

internal class ParsedSheetNumber
{
    public string ProjectNumber { get; init; } = string.Empty;
    public string Stage         { get; init; } = string.Empty;
    public string Discipline    { get; init; } = string.Empty;
    public string Group         { get; init; } = string.Empty;
    public string Sequence      { get; init; } = string.Empty;
    public string Revision      { get; init; } = string.Empty;
}

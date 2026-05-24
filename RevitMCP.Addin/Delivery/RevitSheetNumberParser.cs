using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Delivery;

/// <summary>
/// Parses Revit sheet numbers into structured components.
/// Handles both the full EULE format (with project/stage prefix) and the short discipline-only format.
///
/// Supported formats:
///   Full:  {projectNumber}_{stage}_{discipline}-{group}-{sequence}   e.g. 1626_TP_EL-5-01
///   Short: {discipline}-{group}-{sequence}                           e.g. EL-5-01
/// </summary>
public static class RevitSheetNumberParser
{
    // Full format: 1626_TP_EL-5-01
    private static readonly Regex _fullPattern = new(
        @"^(\d{3,8})_([A-Z]{2,4})_(([A-Z]{1,3})-(\d{1,3})-(\d{2,4}))$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Short format: EL-5-01
    private static readonly Regex _shortPattern = new(
        @"^(([A-Z]{1,3})-(\d{1,3})-(\d{2,4}))$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to parse a Revit sheet number. Returns null for unrecognised formats.
    /// </summary>
    public static ParsedRevitSheetNumber? Parse(string sheetNumber)
    {
        if (string.IsNullOrWhiteSpace(sheetNumber)) return null;

        var s = sheetNumber.Trim();

        var full = _fullPattern.Match(s);
        if (full.Success)
        {
            return new ParsedRevitSheetNumber
            {
                ProjectNumber = full.Groups[1].Value,
                Stage         = full.Groups[2].Value,
                SheetNumberCore = full.Groups[3].Value,
                Discipline    = full.Groups[4].Value,
                Group         = full.Groups[5].Value,
                Sequence      = full.Groups[6].Value
            };
        }

        var shortM = _shortPattern.Match(s);
        if (shortM.Success)
        {
            return new ParsedRevitSheetNumber
            {
                SheetNumberCore = shortM.Groups[1].Value,
                Discipline      = shortM.Groups[2].Value,
                Group           = shortM.Groups[3].Value,
                Sequence        = shortM.Groups[4].Value
            };
        }

        return null;
    }
}

public sealed class ParsedRevitSheetNumber
{
    /// <summary>e.g. "1626" — null for short-format sheet numbers.</summary>
    public string? ProjectNumber { get; init; }

    /// <summary>e.g. "TP", "PP" — null for short-format sheet numbers.</summary>
    public string? Stage { get; init; }

    /// <summary>e.g. "EL", "EN", "EA"</summary>
    public string? Discipline { get; init; }

    /// <summary>e.g. "5"</summary>
    public string? Group { get; init; }

    /// <summary>e.g. "01", "001"</summary>
    public string? Sequence { get; init; }

    /// <summary>The discipline-group-sequence core, e.g. "EL-5-01". This is what file names reference.</summary>
    public string SheetNumberCore { get; init; } = string.Empty;
}

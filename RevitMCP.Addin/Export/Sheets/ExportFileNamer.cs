using System.Text.RegularExpressions;
using RevitMCP.Addin.Delivery;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>How exported sheet files are named.</summary>
public enum ExportNamingMode
{
    /// <summary>The sheet number, as EliteSheets names its files: the company sheet number already holds project, stage and title.</summary>
    SheetNumber,
    /// <summary>"{SheetNumber}_{SheetName}".</summary>
    SheetNumberAndName,
    /// <summary>A token pattern: {SheetNumber}, {SheetName}, {Revision}.</summary>
    Pattern
}

/// <summary>Parsed <c>naming</c> argument.</summary>
public sealed class ExportNaming
{
    public ExportNamingMode Mode { get; init; } = ExportNamingMode.SheetNumber;
    public string Pattern { get; init; } = string.Empty;
    /// <summary>Append "_r{revision}" from the sheet's current revision (default off: many deliveries keep it only in the title block).</summary>
    public bool IncludeRevision { get; init; }

    public string Describe() => Mode switch
    {
        ExportNamingMode.SheetNumberAndName => "sheetNumberAndName",
        ExportNamingMode.Pattern => "pattern:" + Pattern,
        _ => "sheetNumber"
    } + (IncludeRevision ? " + revision" : string.Empty);

    /// <summary>
    /// "sheetNumber" (default, also "company"), "sheetNumberAndName", or "pattern:&lt;tokens&gt;". Null with an error
    /// for anything else.
    /// </summary>
    public static ExportNaming? Parse(string? raw, bool includeRevision, out string? error)
    {
        error = null;
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0 || value.Equals("sheetNumber", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("company", StringComparison.OrdinalIgnoreCase))
            return new ExportNaming { Mode = ExportNamingMode.SheetNumber, IncludeRevision = includeRevision };
        if (value.Equals("sheetNumberAndName", StringComparison.OrdinalIgnoreCase))
            return new ExportNaming { Mode = ExportNamingMode.SheetNumberAndName, IncludeRevision = includeRevision };
        if (value.StartsWith("pattern:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = value.Substring("pattern:".Length).Trim();
            var unknown = Regex.Matches(pattern, @"\{([^}]*)\}").Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(t => !ExportFileNamer.Tokens.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (pattern.Length == 0 || unknown.Count > 0 || !pattern.Contains("{"))
            {
                error = "naming pattern must use the tokens {SheetNumber}, {SheetName} and {Revision}" +
                        (unknown.Count > 0 ? $"; unknown: {string.Join(", ", unknown.Select(u => "{" + u + "}"))}" : string.Empty) + ".";
                return null;
            }
            return new ExportNaming { Mode = ExportNamingMode.Pattern, Pattern = pattern, IncludeRevision = includeRevision };
        }
        error = $"Unknown naming '{value}'. Use sheetNumber (default), sheetNumberAndName or pattern:<tokens>.";
        return null;
    }
}

/// <summary>
/// Builds export file names and checks them against the delivery file name grammar (<see cref="DeliveryFileNameParser"/>),
/// so the delivery check can be run on the exported folder. Pure.
/// </summary>
public static class ExportFileNamer
{
    public static readonly string[] Tokens = { "SheetNumber", "SheetName", "Revision" };

    /// <summary>File name length limit (stem and extension), well under Windows' MAX_PATH with a deep delivery folder.</summary>
    public const int MaxFileNameLength = 120;

    private static readonly Regex Invalid = new(@"[\\/:*?""<>|\x00-\x1F]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The stem (no extension) for a sheet, or for a merged group named <paramref name="sheetNumber"/>.</summary>
    public static string BuildStem(ExportNaming naming, string sheetNumber, string sheetName, string revision)
    {
        var revisionToken = FormatRevision(revision);
        string stem = naming.Mode switch
        {
            ExportNamingMode.SheetNumberAndName => string.IsNullOrWhiteSpace(sheetName) ? sheetNumber : $"{sheetNumber}_{sheetName}",
            ExportNamingMode.Pattern => ApplyPattern(naming.Pattern, sheetNumber, sheetName, revisionToken),
            _ => sheetNumber
        };

        var patternHasRevision = naming.Mode == ExportNamingMode.Pattern &&
                                 naming.Pattern.IndexOf("{Revision}", StringComparison.OrdinalIgnoreCase) >= 0;
        if (naming.IncludeRevision && !patternHasRevision && revisionToken.Length > 0)
            stem = $"{stem}_{revisionToken}";

        return Sanitize(stem);
    }

    /// <summary>"02" → "r02", "2" → "r02", "r03"/"R03" → "r03", "A" → "rA"; empty stays empty.</summary>
    public static string FormatRevision(string? revision)
    {
        var r = (revision ?? string.Empty).Trim();
        if (r.Length == 0 || r == "-") return string.Empty;
        var m = Regex.Match(r, @"^(?:[Rr](?:[Ee][Vv])?)?\s*(\d{1,3})$");
        if (m.Success) return "r" + m.Groups[1].Value.PadLeft(2, '0');
        return "r" + Sanitize(r);
    }

    /// <summary>
    /// Windows-invalid characters become "_", runs of whitespace one space, trailing dots and spaces are dropped.
    /// Estonian letters stay (NTFS names are Unicode). Truncated to fit <see cref="MaxFileNameLength"/> with a 5-character extension.
    /// </summary>
    public static string Sanitize(string? name)
    {
        var s = Invalid.Replace(name ?? string.Empty, "_");
        s = Spaces.Replace(s, " ").Trim().TrimEnd('.', ' ');
        var max = MaxFileNameLength - 5;
        if (s.Length > max) s = s.Substring(0, max).TrimEnd('.', ' ', '_');
        return s.Length == 0 ? "Export" : s;
    }

    /// <summary>
    /// Null when <paramref name="fileName"/> parses as a delivery file name whose sheet core matches the sheet's, else
    /// the warning to report (status sheet_number_not_company_pattern).
    /// </summary>
    public static string? CheckDeliveryRoundTrip(string fileName, string sheetNumber)
    {
        var parsed = DeliveryFileNameParser.Parse(fileName);
        if (parsed == null)
            return $"'{fileName}' does not follow the delivery file name pattern {{project}}_{{stage}}_{{discipline}}-{{group}}-{{sequence}}[_{{description}}]; " +
                   "the delivery check will not recognise it (sheet number not in the company pattern).";
        var core = RevitSheetNumberParser.Parse(sheetNumber)?.SheetNumberCore;
        if (core != null && !string.Equals(core, parsed.SheetNumber, StringComparison.OrdinalIgnoreCase))
            return $"'{fileName}' reads as sheet {parsed.SheetNumber} in the delivery check, but the sheet is {core}.";
        return null;
    }

    private static string ApplyPattern(string pattern, string sheetNumber, string sheetName, string revisionToken)
    {
        // Literal replacement (sheet names may contain '$'), case-insensitive token names.
        var s = Regex.Replace(pattern, @"\{SheetNumber\}", _ => sheetNumber, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\{SheetName\}", _ => sheetName, RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\{Revision\}", _ => revisionToken, RegexOptions.IgnoreCase);
        // An empty token (no revision) leaves "A__B" or a trailing "_": collapse it. Only underscores, so a "--" in a
        // sheet number is never touched.
        if (revisionToken.Length == 0 || sheetName.Length == 0)
            s = Regex.Replace(s, "__+", "_").Trim('_', ' ');
        return s;
    }
}

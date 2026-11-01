using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Documentation.Sheets;

/// <summary>What one sheet's title block says, as the checks need it.</summary>
public sealed class TitleBlockSheetValues
{
    public string SheetNumber { get; set; } = string.Empty;
    public double? WidthMm { get; set; }
    public double? HeightMm { get; set; }
    public string? Format { get; set; }
    public string? Date { get; set; }
    public string? Version { get; set; }
}

/// <summary>One finding of the title block checks.</summary>
public sealed class TitleBlockWarning
{
    /// <summary>format_mismatch, format_unreadable, dates_differ or versions_differ.</summary>
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public IReadOnlyList<string> SheetNumbers { get; set; } = Array.Empty<string>();
}

/// <summary>
/// ISO sheet size matching and the checks of revit_get_titleblock_values (#142): written format against real size, and
/// dates and versions across a set of sheets. Pure, unit tested in RevitMCP.Tests.
/// </summary>
public static class TitleBlockChecks
{
    private const double SizeToleranceMm = 3;

    private static readonly (string Name, double Short, double Long)[] IsoSizes =
    {
        ("A4", 210, 297), ("A3", 297, 420), ("A2", 420, 594), ("A1", 594, 841), ("A0", 841, 1189)
    };

    private static readonly string[] FormatNames = { "format", "formaat", "sheet size", "paper size", "lehe formaat", "suurus" };
    private static readonly string[] DateNames = { "date", "kuupäev", "kuupaev", "sheet issue date", "väljastamise kuupäev" };
    private static readonly string[] VersionNames = { "version", "versioon", "revision", "current revision", "redaktsioon", "muudatus" };

    /// <summary>"A3" for a matching size, "A3x3" for an ISO extended sheet (the long sides repeat the short side of A3); null otherwise.</summary>
    public static string? MatchIsoSize(double widthMm, double heightMm)
    {
        var shortSide = Math.Min(widthMm, heightMm);
        var longSide = Math.Max(widthMm, heightMm);
        foreach (var (name, s, l) in IsoSizes)
        {
            if (Math.Abs(shortSide - s) <= SizeToleranceMm && Math.Abs(longSide - l) <= SizeToleranceMm)
                return name;
        }
        foreach (var (name, s, l) in IsoSizes)
        {
            // Extended formats of ISO 5457: the width of the base format is repeated along its long side.
            if (Math.Abs(shortSide - l) > SizeToleranceMm) continue;
            var repeats = longSide / s;
            var whole = Math.Round(repeats);
            if (whole >= 3 && Math.Abs(longSide - whole * s) <= SizeToleranceMm)
                return $"{name}x{whole:0}";
        }
        return null;
    }

    /// <summary>
    /// The format a title block value states: "A3", "A1x3", or "WxH" for written dimensions. Null when it states none.
    /// </summary>
    public static string? ParseWrittenFormat(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var iso = Regex.Match(value, @"(?<![\p{L}\p{N}])A\s*([0-4])(?:\s*[xX×]\s*(\d))?(?![\p{L}\p{N}])", RegexOptions.IgnoreCase);
        if (iso.Success)
            return iso.Groups[2].Success ? $"A{iso.Groups[1].Value}x{iso.Groups[2].Value}" : $"A{iso.Groups[1].Value}";

        var size = Regex.Match(value, @"(\d{3,4})\s*[xX×*]\s*(\d{3,4})");
        return size.Success ? $"{size.Groups[1].Value}x{size.Groups[2].Value}" : null;
    }

    /// <summary>Whether a written format matches the real size. Dimensions compare in either order.</summary>
    public static bool FormatMatches(string written, double widthMm, double heightMm)
    {
        var size = Regex.Match(written, @"^(\d{3,4})x(\d{3,4})$");
        if (size.Success)
        {
            var a = double.Parse(size.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var b = double.Parse(size.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            return Math.Abs(Math.Min(a, b) - Math.Min(widthMm, heightMm)) <= SizeToleranceMm &&
                   Math.Abs(Math.Max(a, b) - Math.Max(widthMm, heightMm)) <= SizeToleranceMm;
        }
        return string.Equals(MatchIsoSize(widthMm, heightMm), written, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The parameter that holds a role: the caller's name, else the first known name that has a value.</summary>
    public static string? FindParameter(IEnumerable<string> names, string role, string? overrideName)
    {
        var list = names.ToList();
        if (!string.IsNullOrWhiteSpace(overrideName))
            return list.FirstOrDefault(n => string.Equals(n, overrideName!.Trim(), StringComparison.OrdinalIgnoreCase));

        var known = role switch
        {
            "format" => FormatNames,
            "date" => DateNames,
            "version" => VersionNames,
            _ => Array.Empty<string>()
        };
        foreach (var candidate in known)
        {
            var match = list.FirstOrDefault(n => string.Equals(n, candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    public static List<TitleBlockWarning> Check(IReadOnlyList<TitleBlockSheetValues> sheets)
    {
        var warnings = new List<TitleBlockWarning>();

        foreach (var sheet in sheets)
        {
            if (string.IsNullOrWhiteSpace(sheet.Format) || sheet.WidthMm == null || sheet.HeightMm == null) continue;
            var written = ParseWrittenFormat(sheet.Format);
            var real = MatchIsoSize(sheet.WidthMm.Value, sheet.HeightMm.Value) ?? $"{sheet.WidthMm:0}x{sheet.HeightMm:0} mm";
            if (written == null)
            {
                warnings.Add(new TitleBlockWarning
                {
                    Code = "format_unreadable",
                    Message = $"Sheet {sheet.SheetNumber}: the written format '{sheet.Format}' does not state a sheet size; the real size is {real}.",
                    SheetNumbers = new[] { sheet.SheetNumber }
                });
            }
            else if (!FormatMatches(written, sheet.WidthMm.Value, sheet.HeightMm.Value))
            {
                warnings.Add(new TitleBlockWarning
                {
                    Code = "format_mismatch",
                    Message = $"Sheet {sheet.SheetNumber}: the title block says '{sheet.Format}' but the sheet is {real}.",
                    SheetNumbers = new[] { sheet.SheetNumber }
                });
            }
        }

        AddDifference(warnings, sheets, s => s.Date, "dates_differ", "dates");
        AddDifference(warnings, sheets, s => s.Version, "versions_differ", "versions");
        return warnings;
    }

    private static void AddDifference(
        List<TitleBlockWarning> warnings, IReadOnlyList<TitleBlockSheetValues> sheets,
        Func<TitleBlockSheetValues, string?> value, string code, string what)
    {
        var groups = sheets
            .Where(s => !string.IsNullOrWhiteSpace(value(s)))
            .GroupBy(s => value(s)!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count < 2) return;

        warnings.Add(new TitleBlockWarning
        {
            Code = code,
            Message = $"The {what} differ between the sheets: " +
                      string.Join("; ", groups.Select(g => $"'{g.Key}' on {string.Join(", ", g.Select(s => s.SheetNumber))}")) + ".",
            SheetNumbers = groups.SelectMany(g => g.Select(s => s.SheetNumber)).ToList()
        });
    }
}

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Schedules;

/// <summary>Row paging and column-heading detection for schedule table data (#103). Pure.</summary>
public static class ScheduleTableMath
{
    public const int DefaultPageSize = 100;
    public const int MaxPageSize = 500;

    /// <summary>
    /// The 0-based first row, row count and page count of a 1-based page. A page past the end returns no rows and keeps
    /// the page count, so the caller can say how many pages there are.
    /// </summary>
    public static (int Start, int Count, int TotalPages) Page(int totalRows, int page, int pageSize)
    {
        var size = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        var pages = totalRows <= 0 ? 0 : (totalRows + size - 1) / size;
        var current = Math.Max(1, page);
        var start = (current - 1) * size;
        if (start >= totalRows) return (Math.Max(0, totalRows), 0, pages);
        return (start, Math.Min(size, totalRows - start), pages);
    }

    /// <summary>
    /// Where the data rows of a schedule's body begin. Revit puts the column headings (and grouped headings above them)
    /// at the top of the body section, followed in the schedule view by one empty row; data starts after both. Looks
    /// for the row whose cells are the visible fields' headings among the first rows; 0 when none is found (headers
    /// switched off).
    /// </summary>
    public static int DataStartRow(IReadOnlyList<IReadOnlyList<string>> leadingRows, IReadOnlyList<string> headings)
    {
        if (headings.Count == 0 || headings.All(string.IsNullOrWhiteSpace)) return 0;
        var limit = Math.Min(leadingRows.Count, 6);
        for (var r = 0; r < limit; r++)
        {
            if (!IsHeadingRow(leadingRows[r], headings)) continue;
            var start = r + 1;
            if (start < leadingRows.Count && IsBlank(leadingRows[start])) start++;
            return start;
        }
        return 0;
    }

    public static bool IsBlank(IEnumerable<string> cells) => cells.All(string.IsNullOrWhiteSpace);

    private static bool IsHeadingRow(IReadOnlyList<string> row, IReadOnlyList<string> headings)
    {
        var compared = 0;
        for (var c = 0; c < headings.Count && c < row.Count; c++)
        {
            if (string.IsNullOrWhiteSpace(headings[c])) continue;
            if (!string.Equals(Clean(row[c]), Clean(headings[c]), StringComparison.OrdinalIgnoreCase)) return false;
            compared++;
        }
        return compared > 0;
    }

    private static string Clean(string? text) => Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
}

/// <summary>File names for schedule exports: sanitized, with the format's extension, never overwriting a file. Pure.</summary>
public static class ScheduleExportNaming
{
    private static readonly Regex Invalid = new(@"[\\/:*?""<>|\x00-\x1f]", RegexOptions.Compiled);

    public static readonly string[] Formats = { "csv", "txt", "xlsx" };

    public static bool TryNormalizeFormat(string? raw, out string format)
    {
        format = (raw ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
        if (format.Length == 0) format = "csv";
        if (format is "excel" or "xls") format = "xlsx";
        if (format is "tsv" or "text" or "tab") format = "txt";
        return Formats.Contains(format);
    }

    public static string Sanitize(string? name)
    {
        var cleaned = Invalid.Replace((name ?? string.Empty).Trim().Trim('"'), "_").Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Schedule" : cleaned;
    }

    /// <summary>
    /// The file path in <paramref name="folder"/>: the requested file name (or the schedule name), sanitized, with the
    /// format's extension; an existing file gets a timestamp, then a counter.
    /// </summary>
    public static string Resolve(string folder, string? fileName, string scheduleName, string format, Func<string, bool> exists, DateTime now)
    {
        var extension = "." + format;
        var name = Sanitize(string.IsNullOrWhiteSpace(fileName) ? scheduleName : Path.GetFileName(fileName!.Trim()));
        var stem = name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - extension.Length) : name;
        foreach (var other in Formats)
            if (stem.EndsWith("." + other, StringComparison.OrdinalIgnoreCase))
                stem = stem.Substring(0, stem.Length - other.Length - 1);
        if (stem.Length == 0) stem = "Schedule";

        var path = Path.Combine(folder, stem + extension);
        if (!exists(path)) return path;
        var stamp = now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
        var stamped = Path.Combine(folder, $"{stem}_{stamp}{extension}");
        for (var n = 2; exists(stamped); n++)
            stamped = Path.Combine(folder, $"{stem}_{stamp}_{n}{extension}");
        return stamped;
    }
}

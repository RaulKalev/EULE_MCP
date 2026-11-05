using System.IO;
using System.Text.RegularExpressions;

namespace RevitMCP.Addin.Export.Sheets;

/// <summary>A sheet as the export planner sees it: no Revit types, so the grouping and naming rules are testable.</summary>
public sealed class ExportSheetInfo
{
    public long Id { get; init; }
    public string SheetNumber { get; init; } = string.Empty;
    public string SheetName { get; init; } = string.Empty;
    /// <summary>The sheet's current revision number (e.g. "02", "A"), or empty when it has none.</summary>
    public string Revision { get; init; } = string.Empty;
    public bool AppearsInSheetList { get; init; } = true;
    public bool HasTitleBlock { get; init; } = true;
    /// <summary>Printed size in millimetres, 0 when unknown.</summary>
    public double WidthMm { get; init; }
    public double HeightMm { get; init; }
}

/// <summary>One merged drawing: sheets that are exported into one file, in merge order.</summary>
public sealed class SheetMergeGroup
{
    /// <summary>The group id from the sheet number, e.g. "05" for "…-7-05_Title--1".</summary>
    public string GroupKey { get; init; } = string.Empty;
    /// <summary>File name stem shared by the group, e.g. "1626_TP_EL-7-05_Title".</summary>
    public string BaseName { get; init; } = string.Empty;
    public List<ExportSheetInfo> Sheets { get; init; } = new();
}

/// <summary>Result of <see cref="SheetMergeGrouping.Partition"/>.</summary>
public sealed class SheetMergePartition
{
    public List<ExportSheetInfo> Singles { get; } = new();
    public List<SheetMergeGroup> Groups { get; } = new();
}

/// <summary>
/// The EliteSheets merge rule (RaulKalev/EliteSheets, SheetGroupingService), kept as it is so a delivery made with the
/// connector matches one made with the plugin:
/// <list type="bullet">
/// <item>A sheet is part of a merged drawing when its number ends with a merge order <c>--N</c> and contains the group
/// marker <c>-7-{group}_</c>, e.g. <c>1626_TP_EL-7-05_Kilbi skeem--1</c> and <c>…--2</c>.</item>
/// <item>All sheets with the same group id become one file: one combined PDF (pages in merge order), and one DXF
/// (DWG with the ODA converter) where the sheets sit side by side in the DXF template.</item>
/// <item>The merged file is named <c>{prefix}-7-{group}_{title}</c>, the sheet number without the <c>--N</c>.</item>
/// <item>Every other sheet is exported on its own.</item>
/// </list>
/// En and em dashes count as hyphens. Pure.
/// </summary>
public static class SheetMergeGrouping
{
    private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars()
        .Concat(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }).Distinct().ToArray();

    private static readonly Regex MergeOrderRegex = new(@"--\s*(\d+)\s*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroupNumberRegex = new(@"-7-\s*([0-9]+)\s*_", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PartsRegex = new(
        @"^(?<prefix>.+?)-7-\s*(?<group>\d+)\s*_(?<title>.+?)(?:--\s*\d+\s*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Splits sheets into singles and merged groups. Groups keep the order of their first sheet in the input; the sheets
    /// in a group are sorted by merge order, then sheet number. With <paramref name="merge"/> false every sheet is single.
    /// </summary>
    public static SheetMergePartition Partition(IEnumerable<ExportSheetInfo> sheets, bool merge = true)
    {
        var result = new SheetMergePartition();
        var byKey = new Dictionary<string, List<(ExportSheetInfo Sheet, int Order)>>(StringComparer.OrdinalIgnoreCase);
        var keyOrder = new List<string>();

        foreach (var sheet in sheets)
        {
            if (!merge || !TryParseMergeOrder(sheet.SheetNumber, out var order) || !TryParseGroupNumber(sheet.SheetNumber, out var key))
            {
                result.Singles.Add(sheet);
                continue;
            }
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = new List<(ExportSheetInfo, int)>();
                keyOrder.Add(key);
            }
            list.Add((sheet, order));
        }

        foreach (var key in keyOrder)
        {
            var ordered = byKey[key]
                .OrderBy(t => t.Order)
                .ThenBy(t => t.Sheet.SheetNumber, StringComparer.OrdinalIgnoreCase)
                .Select(t => t.Sheet)
                .ToList();
            result.Groups.Add(new SheetMergeGroup
            {
                GroupKey = key,
                BaseName = BuildCombinedFileName(ordered[0].SheetNumber, key),
                Sheets = ordered
            });
        }
        return result;
    }

    /// <summary>"…--1" or "…-- 1" at the end of the sheet number.</summary>
    public static bool TryParseMergeOrder(string? sheetNumber, out int order)
    {
        order = int.MaxValue;
        if (string.IsNullOrWhiteSpace(sheetNumber)) return false;
        var m = MergeOrderRegex.Match(sheetNumber);
        return m.Success && int.TryParse(m.Groups[1].Value, out order);
    }

    /// <summary>"…-7-05_…" gives group "05".</summary>
    public static bool TryParseGroupNumber(string? sheetNumber, out string group)
    {
        group = string.Empty;
        if (string.IsNullOrWhiteSpace(sheetNumber)) return false;
        var m = GroupNumberRegex.Match(NormalizeDashes(sheetNumber!));
        if (!m.Success) return false;
        group = m.Groups[1].Value.Trim();
        return group.Length > 0;
    }

    /// <summary>True when the sheet number marks the sheet as part of a merged drawing.</summary>
    public static bool IsMergePart(string? sheetNumber) =>
        TryParseMergeOrder(sheetNumber, out _) && TryParseGroupNumber(sheetNumber, out _);

    /// <summary>"{prefix}-7-{group}_{title}" from a group member's number; "Group-{group}" when it cannot be parsed.</summary>
    public static string BuildCombinedFileName(string? sheetNumber, string fallbackGroupNumber)
    {
        if (string.IsNullOrWhiteSpace(sheetNumber))
            return $"Group-{fallbackGroupNumber}";

        var normalized = NormalizeDashes(sheetNumber!);
        var m = PartsRegex.Match(normalized);
        if (m.Success)
        {
            var prefix = m.Groups["prefix"].Value.TrimEnd('_', '-', ' ');
            var group = m.Groups["group"].Value.Trim();
            var title = m.Groups["title"].Value.Trim();
            foreach (var c in InvalidFileNameChars)
                title = title.Replace(c.ToString(), string.Empty);
            if (prefix.Length > 0 && group.Length > 0 && title.Length > 0)
                return $"{prefix}-7-{group}_{title}";
        }

        var p7 = normalized.IndexOf("-7-", StringComparison.Ordinal);
        if (p7 > 0)
        {
            var cleanPrefix = normalized.Substring(0, p7).TrimEnd('_', '-', ' ');
            return $"{cleanPrefix}-7-{fallbackGroupNumber}";
        }
        return $"Group-{fallbackGroupNumber}";
    }

    private static string NormalizeDashes(string s) => s.Replace('–', '-').Replace('—', '-');
}

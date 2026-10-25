using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Delivery;

/// <summary>
/// Compares a <see cref="DeliveryScanResult"/> against a list of Revit sheet descriptors.
/// No Revit API dependency — takes pre-collected sheet data so it is testable.
/// </summary>
public static class DeliveryRevitSheetComparer
{
    /// <param name="scan">Scanned delivery folder result.</param>
    /// <param name="revitSheets">Sheet list collected from the live Revit document.</param>
    /// <param name="requiredExtensions">Lower-case extensions to require per sheet (e.g. "pdf", "dwg").</param>
    /// <param name="stageFilter">If non-empty, only check files whose stage matches one of these.</param>
    /// <param name="disciplineFilter">If non-empty, only check sheets whose number prefix matches one of these disciplines.</param>
    public static List<IssueDto> Compare(
        DeliveryScanResult scan,
        IReadOnlyList<RevitSheetDescriptor> revitSheets,
        IReadOnlyList<string> requiredExtensions,
        IReadOnlyList<string> stageFilter,
        IReadOnlyList<string> disciplineFilter,
        string runId)
        => CompareSheets(scan, revitSheets, requiredExtensions, stageFilter, disciplineFilter, runId).Issues;

    /// <summary>
    /// Same as <see cref="Compare"/>, but also reports how many sheets were actually compared and which
    /// were skipped because <paramref name="disciplineFilter"/> was given and their number could not be parsed
    /// (their discipline is unknown). Those skipped sheets are reported as one warning issue, never dropped silently.
    /// </summary>
    public static DeliveryRevitSheetComparison CompareSheets(
        DeliveryScanResult scan,
        IReadOnlyList<RevitSheetDescriptor> revitSheets,
        IReadOnlyList<string> requiredExtensions,
        IReadOnlyList<string> stageFilter,
        IReadOnlyList<string> disciplineFilter,
        string runId)
    {
        var issues = new List<IssueDto>();
        var unparsed = new List<string>();
        int compared = 0;
        int seq = 0;

        // Build lookup: sheet number → sheet descriptor
        var sheetByNumber = revitSheets
            .GroupBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // Build lookup: sheetNumber + ext → list of matching files
        var filesBySheetAndExt = new Dictionary<string, List<DeliveryFileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in scan.Files)
        {
            if (file.Parsed == null) continue;
            var key = $"{file.Parsed.SheetNumber}|{file.Parsed.Extension}";
            if (!filesBySheetAndExt.TryGetValue(key, out var list))
                filesBySheetAndExt[key] = list = new List<DeliveryFileEntry>();
            list.Add(file);
        }

        // ── Check each Revit sheet has required files ─────────────────────────
        foreach (var sheet in revitSheets)
        {
            if (disciplineFilter.Count > 0)
            {
                if (sheet.Discipline == null)
                {
                    unparsed.Add(sheet.OriginalSheetNumber ?? sheet.SheetNumber);
                    continue;
                }
                if (!disciplineFilter.Contains(sheet.Discipline, StringComparer.OrdinalIgnoreCase))
                    continue;
            }

            compared++;
            foreach (var ext in requiredExtensions)
            {
                var key = $"{sheet.SheetNumber}|{ext}";
                if (!filesBySheetAndExt.TryGetValue(key, out var matches) || matches.Count == 0)
                {
                    seq++;
                    issues.Add(ext == "pdf"
                        ? DeliveryIssueBuilder.MissingPdf(runId, seq, sheet.SheetNumber, sheet.SheetName)
                        : DeliveryIssueBuilder.MissingDwg(runId, seq, sheet.SheetNumber, sheet.SheetName));
                }
                else if (matches.Count > 1)
                {
                    seq++;
                    issues.Add(DeliveryIssueBuilder.DuplicateFile(runId, seq, sheet.SheetNumber, ext,
                        matches.Select(m => m.FileName)));
                }
                else
                {
                    // Stage mismatch
                    var file = matches[0];
                    if (stageFilter.Count > 0 && file.Parsed != null &&
                        !stageFilter.Contains(file.Parsed.Stage, StringComparer.OrdinalIgnoreCase))
                    {
                        seq++;
                        issues.Add(DeliveryIssueBuilder.StageMismatch(runId, seq, file.FileName,
                            file.Parsed.Stage, stageFilter.FirstOrDefault()));
                    }
                }
            }
        }

        // ── Check each parsed file has a matching Revit sheet ─────────────────
        foreach (var file in scan.Files)
        {
            if (file.Parsed == null) continue;
            if (!requiredExtensions.Contains(file.Parsed.Extension, StringComparer.OrdinalIgnoreCase)) continue;

            if (disciplineFilter.Count > 0 && !disciplineFilter.Contains(file.Parsed.Discipline, StringComparer.OrdinalIgnoreCase))
                continue;
            if (stageFilter.Count > 0 && !stageFilter.Contains(file.Parsed.Stage, StringComparer.OrdinalIgnoreCase))
                continue;

            if (!sheetByNumber.ContainsKey(file.Parsed.SheetNumber))
            {
                seq++;
                issues.Add(DeliveryIssueBuilder.OrphanFile(runId, seq, file.FileName, file.Parsed.Extension));
            }

            if (file.SizeBytes == 0)
            {
                seq++;
                issues.Add(DeliveryIssueBuilder.EmptyFile(runId, seq, file.FileName));
            }
            else if (IsSuspiciouslySmall(file))
            {
                seq++;
                issues.Add(DeliveryIssueBuilder.SmallFile(runId, seq, file.FileName, file.SizeBytes));
            }
        }

        if (unparsed.Count > 0)
        {
            seq++;
            issues.Add(DeliveryIssueBuilder.UnparsedSheetNumbers(runId, seq, unparsed.Count,
                unparsed.Take(MaxUnparsedExamples), disciplineFilter));
        }

        return new DeliveryRevitSheetComparison
        {
            Issues = issues,
            ComparedSheetCount = compared,
            UnparsedSheetNumbers = unparsed
        };
    }

    /// <summary>How many unparsed sheet numbers the warning issue names as examples.</summary>
    public const int MaxUnparsedExamples = 5;

    /// <summary>
    /// The one-line summary the delivery tools return, e.g.
    /// "Checked 3 file(s) against 2 of 22 Revit sheet(s); 20 skipped because their number could not be parsed …".
    /// </summary>
    public static string DescribeSheetCounts(int fileCount, int totalSheets, DeliveryRevitSheetComparison comparison)
    {
        var text = comparison.ComparedSheetCount == totalSheets
            ? $"Checked {fileCount} file(s) against {totalSheets} Revit sheet(s)."
            : $"Checked {fileCount} file(s) against {comparison.ComparedSheetCount} of {totalSheets} Revit sheet(s).";
        if (comparison.UnparsedSheetNumbers.Count > 0)
            text += $" {comparison.UnparsedSheetNumbers.Count} sheet(s) skipped because their number could not be parsed for disciplineFilter, e.g. " +
                    string.Join(", ", comparison.UnparsedSheetNumbers.Take(MaxUnparsedExamples)) + ".";
        return text;
    }

    private static bool IsSuspiciouslySmall(DeliveryFileEntry file)
    {
        return file.Parsed?.Extension switch
        {
            "pdf" => file.SizeBytes > 0 && file.SizeBytes < 10_000,
            "dwg" => file.SizeBytes > 0 && file.SizeBytes < 5_000,
            _ => false
        };
    }
}

public sealed class RevitSheetDescriptor
{
    /// <summary>The parsed core (e.g. "EN-5-10"), or the raw sheet number when it could not be parsed.</summary>
    public string SheetNumber { get; init; } = string.Empty;
    public string SheetName { get; init; } = string.Empty;
    /// <summary>Null when the sheet number could not be parsed.</summary>
    public string? Discipline { get; init; }
    public string? Stage { get; init; }
    public string? ProjectNumber { get; init; }
    /// <summary>The sheet number as it is in Revit, e.g. "73226_PP_EN-5-10_1k-ATS".</summary>
    public string? OriginalSheetNumber { get; init; }

    /// <summary>Builds a descriptor from a Revit sheet's number and name via <see cref="RevitSheetNumberParser"/>.</summary>
    public static RevitSheetDescriptor FromSheet(string sheetNumber, string sheetName)
    {
        var parsed = RevitSheetNumberParser.Parse(sheetNumber);
        return new RevitSheetDescriptor
        {
            SheetNumber         = parsed?.SheetNumberCore ?? sheetNumber,
            SheetName           = sheetName,
            Discipline          = parsed?.Discipline,
            Stage               = parsed?.Stage,
            ProjectNumber       = parsed?.ProjectNumber,
            OriginalSheetNumber = sheetNumber
        };
    }
}

/// <summary>Result of <see cref="DeliveryRevitSheetComparer.CompareSheets"/>.</summary>
public sealed class DeliveryRevitSheetComparison
{
    public List<IssueDto> Issues { get; init; } = new();

    /// <summary>Sheets that passed the discipline filter and were checked against the delivery folder.</summary>
    public int ComparedSheetCount { get; init; }

    /// <summary>Raw numbers of sheets skipped because a discipline filter was given and the number did not parse.</summary>
    public List<string> UnparsedSheetNumbers { get; init; } = new();
}

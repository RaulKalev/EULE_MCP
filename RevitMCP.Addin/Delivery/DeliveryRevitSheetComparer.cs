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
    {
        var issues = new List<IssueDto>();
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
            if (disciplineFilter.Count > 0 && !disciplineFilter.Contains(sheet.Discipline ?? "", StringComparer.OrdinalIgnoreCase))
                continue;

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

        return issues;
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
    public string SheetNumber { get; init; } = string.Empty;
    public string SheetName { get; init; } = string.Empty;
    public string? Discipline { get; init; }
    public string? Stage { get; init; }
    public string? ProjectNumber { get; init; }
}

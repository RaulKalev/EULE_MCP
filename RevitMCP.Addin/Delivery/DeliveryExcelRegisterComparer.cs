using System.IO;
using ClosedXML.Excel;
using RevitMCP.Core.Models.Issues;

namespace RevitMCP.Addin.Delivery;

/// <summary>
/// Compares a <see cref="DeliveryScanResult"/> against an Excel document register.
/// No Revit API dependency — pure ClosedXML logic, testable.
/// </summary>
public static class DeliveryExcelRegisterComparer
{
    private static readonly string[] _sheetNumberHeaders =
        ["nr", "number", "dokumendi nr", "sheet number", "drawing number", "doc nr", "doc number"];

    /// <param name="scan">Scanned delivery folder result.</param>
    /// <param name="excelFilePath">Path to the Excel document register.</param>
    /// <param name="worksheetName">Worksheet name; empty = use first visible worksheet.</param>
    /// <param name="requiredExtensions">Extensions to check (e.g. "pdf", "dwg").</param>
    /// <param name="runId">Run ID for issue IDs.</param>
    public static (List<IssueDto> issues, string? error) Compare(
        DeliveryScanResult scan,
        string excelFilePath,
        string worksheetName,
        IReadOnlyList<string> requiredExtensions,
        string runId)
    {
        if (!File.Exists(excelFilePath))
            return (new List<IssueDto>(), $"Excel register not found: {excelFilePath}");

        var ext = Path.GetExtension(excelFilePath).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xlsm")
            return (new List<IssueDto>(), $"Unsupported file format '{ext}'. Only .xlsx and .xlsm are supported.");

        IXLWorksheet ws;
        try
        {
            using var wb = new XLWorkbook(excelFilePath);
            ws = string.IsNullOrWhiteSpace(worksheetName)
                ? wb.Worksheets.FirstOrDefault(w => !w.Visibility.HasFlag(XLWorksheetVisibility.Hidden))
                    ?? wb.Worksheets.First()
                : wb.Worksheet(worksheetName);

            return CompareWithWorksheet(scan, ws, requiredExtensions, runId);
        }
        catch (Exception ex)
        {
            return (new List<IssueDto>(), $"Failed to read Excel register: {ex.Message}");
        }
    }

    private static (List<IssueDto>, string?) CompareWithWorksheet(
        DeliveryScanResult scan,
        IXLWorksheet ws,
        IReadOnlyList<string> requiredExtensions,
        string runId)
    {
        var issues = new List<IssueDto>();
        int seq = 0;

        // Detect header row and sheet-number column
        var used = ws.RangeUsed();
        if (used == null)
            return (issues, "Worksheet has no data.");

        int headerRow = 0;
        int sheetNumberCol = 0;
        for (int r = used.FirstRow().RowNumber(); r <= Math.Min(used.FirstRow().RowNumber() + 5, used.LastRow().RowNumber()); r++)
        {
            for (int c = used.FirstColumn().ColumnNumber(); c <= used.LastColumn().ColumnNumber(); c++)
            {
                var val = ws.Cell(r, c).GetString().Trim().ToLowerInvariant();
                if (_sheetNumberHeaders.Any(h => val.Contains(h)))
                {
                    headerRow = r;
                    sheetNumberCol = c;
                    break;
                }
            }
            if (headerRow > 0) break;
        }

        if (headerRow == 0 || sheetNumberCol == 0)
            return (issues, "Could not detect a sheet-number column in the register. Expected a header like 'Dokumendi nr', 'Nr', or 'Sheet Number'.");

        // Collect register rows
        var registerRows = new List<RegisterRow>();
        var docNumberCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int r = headerRow + 1; r <= used.LastRow().RowNumber(); r++)
        {
            var docNum = ws.Cell(r, sheetNumberCol).GetString().Trim();
            if (string.IsNullOrWhiteSpace(docNum)) continue;

            registerRows.Add(new RegisterRow { SheetNumber = docNum, RowIndex = r });
            docNumberCounts[docNum] = docNumberCounts.TryGetValue(docNum, out var cnt) ? cnt + 1 : 1;
        }

        // Flag duplicate document numbers in register
        foreach (var (docNum, count) in docNumberCounts.Where(kv => kv.Value > 1))
        {
            seq++;
            issues.Add(DeliveryIssueBuilder.RegisterDuplicateDocNumber(runId, seq, docNum, count));
        }

        // Build set of sheet numbers from scanned files
        var filesBySheetAndExt = new Dictionary<string, List<DeliveryFileEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in scan.Files)
        {
            if (file.Parsed == null) continue;
            if (!requiredExtensions.Contains(file.Parsed.Extension, StringComparer.OrdinalIgnoreCase)) continue;
            var key = $"{file.Parsed.SheetNumber}|{file.Parsed.Extension}";
            if (!filesBySheetAndExt.TryGetValue(key, out var list))
                filesBySheetAndExt[key] = list = new List<DeliveryFileEntry>();
            list.Add(file);
        }

        var registerSheetNumbers = new HashSet<string>(
            registerRows.Select(r => r.SheetNumber), StringComparer.OrdinalIgnoreCase);

        // Check each register row has files
        foreach (var row in registerRows)
        {
            foreach (var ext in requiredExtensions)
            {
                var key = $"{row.SheetNumber}|{ext}";
                if (!filesBySheetAndExt.ContainsKey(key))
                {
                    seq++;
                    issues.Add(DeliveryIssueBuilder.RegisterRowMissingFile(runId, seq, row.SheetNumber, ext));
                }
            }
        }

        // Check each file has a register row
        foreach (var file in scan.Files)
        {
            if (file.Parsed == null) continue;
            if (!requiredExtensions.Contains(file.Parsed.Extension, StringComparer.OrdinalIgnoreCase)) continue;

            if (!registerSheetNumbers.Contains(file.Parsed.SheetNumber))
            {
                seq++;
                issues.Add(DeliveryIssueBuilder.FileMissingFromRegister(runId, seq, file.FileName));
            }
        }

        return (issues, null);
    }

    private sealed class RegisterRow
    {
        public string SheetNumber { get; init; } = string.Empty;
        public int RowIndex { get; init; }
    }
}

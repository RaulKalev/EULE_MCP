using System.IO;
using ClosedXML.Excel;

namespace RevitMCP.Addin.Excel;

/// <summary>
/// Modifies existing Excel workbooks while preserving formatting.
/// All methods create a backup when requested and return structured result objects.
/// </summary>
public static class ExcelWorkbookModifier
{
    /// <summary>
    /// Updates specific cells in a worksheet. Preserves all other formatting.
    /// When dryRun=true, previews changes without saving or creating a backup.
    /// </summary>
    public static (object? Result, string? Error) UpdateCells(
        string filePath,
        string worksheetName,
        List<(string Cell, string? Value)> updates,
        bool backupBeforeSave,
        bool dryRun = false)
    {
        if (!File.Exists(filePath))
            return (null, $"File not found: {filePath}");

        string? backupPath = null;
        if (!dryRun && backupBeforeSave)
        {
            try { backupPath = ExcelBackupService.CreateBackup(filePath); }
            catch (Exception ex) { return (null, $"Backup failed: {ex.Message}"); }
        }

        try
        {
            using var wb = new XLWorkbook(filePath);

            if (!wb.TryGetWorksheet(worksheetName, out var ws))
                return (null, $"Worksheet '{worksheetName}' not found.");

            var applied = new List<object>();
            var errors = new List<string>();

            foreach (var (cellAddr, value) in updates)
            {
                try
                {
                    var cell = ws.Cell(cellAddr);
                    var oldValue = cell.GetString();
                    if (!dryRun) cell.SetValue(value ?? string.Empty);
                    applied.Add(new { cell = cellAddr, oldValue, newValue = value ?? string.Empty });
                }
                catch (Exception ex)
                {
                    errors.Add($"Cell {cellAddr}: {ex.Message}");
                }
            }

            if (!dryRun) wb.Save();

            return (new
            {
                dryRun,
                filePath,
                worksheetName,
                updatedCount = dryRun ? 0 : applied.Count,
                plannedUpdateCount = dryRun ? applied.Count : (int?)null,
                errorCount = errors.Count,
                updates = applied,
                errors,
                backupPath
            }, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to update workbook: {ex.Message}");
        }
    }

    /// <summary>
    /// Inserts rows at a given position, copying style from a template row.
    /// Row values are keyed by column letter (e.g. "A", "B").
    /// When dryRun=true, previews the operation without saving or creating a backup.
    /// </summary>
    public static (object? Result, string? Error) InsertRows(
        string filePath,
        string worksheetName,
        int insertAtRow,
        int copyStyleFromRow,
        List<Dictionary<string, string>> rows,
        bool backupBeforeSave,
        bool dryRun = false)
    {
        if (!File.Exists(filePath))
            return (null, $"File not found: {filePath}");

        if (rows.Count == 0)
            return (null, "No rows to insert.");

        string? backupPath = null;
        if (!dryRun && backupBeforeSave)
        {
            try { backupPath = ExcelBackupService.CreateBackup(filePath); }
            catch (Exception ex) { return (null, $"Backup failed: {ex.Message}"); }
        }

        try
        {
            using var wb = new XLWorkbook(filePath);

            if (!wb.TryGetWorksheet(worksheetName, out var ws))
                return (null, $"Worksheet '{worksheetName}' not found.");

            if (dryRun)
            {
                // Build preview without mutating
                var usedRange = ws.RangeUsed();
                var previewRange = usedRange != null
                    ? $"{ws.Cell(insertAtRow, usedRange.FirstColumn().ColumnNumber()).Address}:" +
                      $"{ws.Cell(insertAtRow + rows.Count - 1, usedRange.LastColumn().ColumnNumber()).Address}"
                    : $"Row {insertAtRow}–{insertAtRow + rows.Count - 1}";

                return (new
                {
                    dryRun = true,
                    worksheetName,
                    insertAtRow,
                    copyStyleFromRow,
                    plannedRowCount = rows.Count,
                    affectedRange = previewRange,
                    rows
                }, null);
            }

            // Insert blank rows above the target row
            ws.Row(insertAtRow).InsertRowsAbove(rows.Count);

            // The style source row shifts down if it was at or below the insert point
            int adjustedStyleRow = copyStyleFromRow >= insertAtRow
                ? copyStyleFromRow + rows.Count
                : copyStyleFromRow;

            for (int i = 0; i < rows.Count; i++)
            {
                int targetRow = insertAtRow + i;
                ExcelStyleCopyService.CopyRowStyle(ws, adjustedStyleRow, targetRow);

                foreach (var (colKey, value) in rows[i])
                {
                    try
                    {
                        // Accept column letters like "A" or address-style like "A5"
                        // Build full address: colLetter + rowNumber
                        var addr = colKey.Length == 1 || (colKey.Length <= 3 && colKey.All(char.IsLetter))
                            ? $"{colKey.ToUpperInvariant()}{targetRow}"
                            : colKey; // already a full address
                        ws.Cell(addr).SetValue(value);
                    }
                    catch { /* skip invalid column reference */ }
                }
            }

            wb.Save();

            var used = ws.RangeUsed();
            var insertedRange = used != null
                ? $"{ws.Cell(insertAtRow, used.FirstColumn().ColumnNumber()).Address}:" +
                  $"{ws.Cell(insertAtRow + rows.Count - 1, used.LastColumn().ColumnNumber()).Address}"
                : $"Row {insertAtRow}–{insertAtRow + rows.Count - 1}";

            return (new
            {
                filePath,
                worksheetName,
                insertedRowCount = rows.Count,
                insertedAtRow = insertAtRow,
                affectedRange = insertedRange,
                backupPath
            }, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to insert rows: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends rows after the last data row, copying style from the last data row.
    /// When matchHeaders=true, row dictionaries are keyed by header name.
    /// When a named table is provided, the table range is extended to include the new rows.
    /// When dryRun=true, previews the operation without saving or creating a backup.
    /// </summary>
    public static (object? Result, string? Error) AppendTableRows(
        string filePath,
        string worksheetName,
        string tableName,
        bool matchHeaders,
        List<Dictionary<string, string>> rows,
        bool backupBeforeSave,
        bool dryRun = false)
    {
        if (!File.Exists(filePath))
            return (null, $"File not found: {filePath}");

        if (rows.Count == 0)
            return (null, "No rows to append.");

        string? backupPath = null;
        if (!dryRun && backupBeforeSave)
        {
            try { backupPath = ExcelBackupService.CreateBackup(filePath); }
            catch (Exception ex) { return (null, $"Backup failed: {ex.Message}"); }
        }

        try
        {
            using var wb = new XLWorkbook(filePath);

            if (!wb.TryGetWorksheet(worksheetName, out var ws))
                return (null, $"Worksheet '{worksheetName}' not found.");

            var (headerRowNum, detectedHeaders) = ExcelHeaderDetector.DetectHeaders(ws);
            if (headerRowNum == 0)
                return (null, "Could not detect a header row in the worksheet.");

            var used = ws.RangeUsed();
            int lastDataRow = used?.LastRow().RowNumber() ?? headerRowNum;
            int styleTemplateRow = lastDataRow > headerRowNum ? lastDataRow : headerRowNum;

            if (dryRun)
            {
                var unmatchedDry = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (matchHeaders)
                {
                    foreach (var rowData in rows)
                        foreach (var key in rowData.Keys)
                        {
                            int col = ExcelHeaderDetector.FindColumnByHeader(ws, headerRowNum, key);
                            if (col == 0) unmatchedDry.Add(key);
                        }
                }

                return (new
                {
                    dryRun = true,
                    worksheetName,
                    headerRow = headerRowNum,
                    lastDataRow,
                    plannedStartRow = lastDataRow + 1,
                    plannedRowCount = rows.Count,
                    unmatchedHeaders = unmatchedDry.ToList()
                }, null);
            }

            var unmatchedHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int appendedCount = 0;

            foreach (var rowData in rows)
            {
                int newRow = lastDataRow + 1;
                ExcelStyleCopyService.CopyRowStyle(ws, styleTemplateRow, newRow);

                foreach (var (key, value) in rowData)
                {
                    if (!matchHeaders)
                    {
                        // Direct column letter addressing
                        try
                        {
                            var addr = key.Length <= 3 && key.All(char.IsLetter)
                                ? $"{key.ToUpperInvariant()}{newRow}"
                                : key;
                            ws.Cell(addr).SetValue(value);
                        }
                        catch { /* skip invalid reference */ }
                        continue;
                    }

                    int colNum = ExcelHeaderDetector.FindColumnByHeader(ws, headerRowNum, key);
                    if (colNum == 0)
                    {
                        unmatchedHeaders.Add(key);
                        continue;
                    }
                    ws.Cell(newRow, colNum).SetValue(value);
                }

                lastDataRow = newRow;
                appendedCount++;
            }

            // Extend named table if present
            if (!string.IsNullOrWhiteSpace(tableName))
            {
                var table = ws.Tables.FirstOrDefault(t =>
                    string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase));

                if (table != null)
                {
                    var tblFirst = table.RangeAddress.FirstAddress;
                    var newLastRow = lastDataRow;
                    var newLastCol = table.RangeAddress.LastAddress.ColumnNumber;
                    var newRange = ws.Range(tblFirst.RowNumber, tblFirst.ColumnNumber, newLastRow, newLastCol);
                    table.Resize(newRange);
                }
            }

            wb.Save();

            var warnings = unmatchedHeaders
                .Select(h => $"Header '{h}' not found — column skipped.")
                .ToList();

            return (new
            {
                filePath,
                worksheetName,
                appendedRowCount = appendedCount,
                unmatchedHeaderCount = unmatchedHeaders.Count,
                unmatchedHeaders = unmatchedHeaders.ToList(),
                backupPath
            }, null);
        }
        catch (Exception ex)
        {
            return (null, $"Failed to append rows: {ex.Message}");
        }
    }
}

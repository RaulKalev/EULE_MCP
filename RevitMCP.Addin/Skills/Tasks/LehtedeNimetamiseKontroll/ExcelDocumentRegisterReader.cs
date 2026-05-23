using System.IO;
using ClosedXML.Excel;
using RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll.Models;

namespace RevitMCP.Addin.Skills.Tasks.LehtedeNimetamiseKontroll;

/// <summary>
/// Reads a document register from an Excel file using ClosedXML.
/// Detects the header row by matching known column aliases and returns a list of DocumentRegisterItems.
/// </summary>
internal static class ExcelDocumentRegisterReader
{
    public static List<DocumentRegisterItem> Read(
        string filePath,
        string[] numberAliases,
        string[] nameAliases,
        string[] disciplineAliases,
        string[] stageAliases,
        out List<string> warnings)
    {
        warnings = new List<string>();
        var items = new List<DocumentRegisterItem>();

        if (!File.Exists(filePath))
        {
            warnings.Add($"Exceli fail ei leita: {filePath}");
            return items;
        }

        using var workbook = new XLWorkbook(filePath);

        // Find first non-empty worksheet
        IXLWorksheet? ws = null;
        foreach (var sheet in workbook.Worksheets)
        {
            if (!sheet.IsEmpty()) { ws = sheet; break; }
        }

        if (ws is null)
        {
            warnings.Add("Exceli failis ei leitud andmeid.");
            return items;
        }

        var usedRange = ws.RangeUsed();
        if (usedRange is null) return items;

        var firstRow = usedRange.RangeAddress.FirstAddress.RowNumber;
        var lastRow  = usedRange.RangeAddress.LastAddress.RowNumber;
        var firstCol = usedRange.RangeAddress.FirstAddress.ColumnNumber;
        var lastCol  = usedRange.RangeAddress.LastAddress.ColumnNumber;

        // Scan first 5 rows for the header
        int headerRow = -1;
        int numberCol = -1, nameCol = -1, disciplineCol = -1, stageCol = -1;

        for (int r = firstRow; r <= Math.Min(firstRow + 4, lastRow) && headerRow < 0; r++)
        {
            bool foundAny = false;
            for (int c = firstCol; c <= lastCol; c++)
            {
                var val = ws.Cell(r, c).GetString().Trim();
                if (MatchesAny(val, numberAliases))    { numberCol     = c; foundAny = true; }
                else if (MatchesAny(val, nameAliases)) { nameCol        = c; foundAny = true; }
                else if (MatchesAny(val, disciplineAliases)) { disciplineCol = c; foundAny = true; }
                else if (MatchesAny(val, stageAliases))      { stageCol       = c; foundAny = true; }
            }
            if (foundAny) headerRow = r;
        }

        if (headerRow < 0 || numberCol < 0)
        {
            warnings.Add("Ei leitud veeru päist dokumendi numbri jaoks. Kontrolli Exceli faili veerude nimetusi.");
            return items;
        }

        // Read data rows
        for (int r = headerRow + 1; r <= lastRow; r++)
        {
            var num = numberCol > 0 ? ws.Cell(r, numberCol).GetString().Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(num)) continue;

            items.Add(new DocumentRegisterItem
            {
                DocumentNumber = num,
                DocumentName   = nameCol       > 0 ? ws.Cell(r, nameCol).GetString().Trim()       : string.Empty,
                Discipline     = disciplineCol > 0 ? ws.Cell(r, disciplineCol).GetString().Trim() : string.Empty,
                Stage          = stageCol      > 0 ? ws.Cell(r, stageCol).GetString().Trim()      : string.Empty,
                RowNumber      = r,
            });
        }

        return items;
    }

    private static bool MatchesAny(string value, string[] aliases) =>
        aliases.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
}

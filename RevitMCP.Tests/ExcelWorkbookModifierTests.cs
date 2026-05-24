using ClosedXML.Excel;
using RevitMCP.Addin.Excel;
using RevitMCP.Core.Models.Excel;
using Xunit;

namespace RevitMCP.Tests;

public class ExcelWorkbookModifierTests
{
    // ── Helper ────────────────────────────────────────────────────────────────

    /// <summary>Creates a temp .xlsx with the given data, returns the path.</summary>
    private static string CreateTempWorkbook(string worksheetName, string[,] data, string[]? tableRange = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rkmcp_test_{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(worksheetName);

        for (int r = 0; r < data.GetLength(0); r++)
            for (int c = 0; c < data.GetLength(1); c++)
                ws.Cell(r + 1, c + 1).SetValue(data[r, c]);

        wb.SaveAs(path);
        return path;
    }

    // ── Backup ────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateBackup_CreatesNewFile()
    {
        var original = CreateTempWorkbook("Sheet1", new string[,] { { "A", "B" } });
        try
        {
            var backup = ExcelBackupService.CreateBackup(original);
            try
            {
                Assert.True(File.Exists(backup));
                Assert.NotEqual(original, backup);
                Assert.Contains("backup", backup, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (File.Exists(backup)) File.Delete(backup);
            }
        }
        finally
        {
            if (File.Exists(original)) File.Delete(original);
        }
    }

    [Fact]
    public void CreateBackup_NonExistentFile_Throws()
    {
        Assert.ThrowsAny<IOException>(() =>
            ExcelBackupService.CreateBackup(@"C:\does\not\exist\file.xlsx"));
    }

    // ── UpdateCells ───────────────────────────────────────────────────────────

    [Fact]
    public void UpdateCells_ValidCells_UpdatesValues()
    {
        var path = CreateTempWorkbook("Data", new string[,]
        {
            { "Name",   "Value" },
            { "Item A", "OldVal" }
        });
        try
        {
            var updates = new List<(string, string?)> { ("B2", "NewVal") };
            var (result, error) = ExcelWorkbookModifier.UpdateCells(path, "Data", updates, backupBeforeSave: false);

            Assert.Null(error);
            Assert.NotNull(result);

            // Verify by re-reading
            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("Data");
            Assert.Equal("NewVal", ws.Cell("B2").GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void UpdateCells_WithBackup_CreatesBackupFile()
    {
        var path = CreateTempWorkbook("Sheet1", new string[,] { { "A", "B" }, { "1", "2" } });
        string? backupPath = null;
        try
        {
            var updates = new List<(string, string?)> { ("A1", "Updated") };
            var (result, error) = ExcelWorkbookModifier.UpdateCells(path, "Sheet1", updates, backupBeforeSave: true);

            Assert.Null(error);
            var resultDict = result as dynamic;
            // The backup path is embedded in the anonymous result object
            // Verify the original file still has a backup sibling
            var dir = Path.GetDirectoryName(path)!;
            var stem = Path.GetFileNameWithoutExtension(path);
            var backups = Directory.GetFiles(dir, $"{stem}_backup_*.xlsx");
            Assert.NotEmpty(backups);
            backupPath = backups[0];
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (backupPath != null && File.Exists(backupPath)) File.Delete(backupPath);
        }
    }

    [Fact]
    public void UpdateCells_FileNotFound_ReturnsError()
    {
        var updates = new List<(string, string?)> { ("A1", "X") };
        var (result, error) = ExcelWorkbookModifier.UpdateCells(
            @"C:\nonexistent\missing.xlsx", "Sheet1", updates, backupBeforeSave: false);

        Assert.NotNull(error);
        Assert.Null(result);
        Assert.Contains("not found", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UpdateCells_InvalidWorksheet_ReturnsError()
    {
        var path = CreateTempWorkbook("Sheet1", new string[,] { { "A" } });
        try
        {
            var updates = new List<(string, string?)> { ("A1", "X") };
            var (result, error) = ExcelWorkbookModifier.UpdateCells(path, "NoSuchSheet", updates, backupBeforeSave: false);

            Assert.NotNull(error);
            Assert.Contains("not found", error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── InsertRows ────────────────────────────────────────────────────────────

    [Fact]
    public void InsertRows_InsertsAtCorrectPosition()
    {
        var path = CreateTempWorkbook("Sheet1", new string[,]
        {
            { "Header" },
            { "Row2" },
            { "Row3" }
        });
        try
        {
            var rows = new List<Dictionary<string, string>>
            {
                new() { ["A"] = "InsertedRow" }
            };
            var (result, error) = ExcelWorkbookModifier.InsertRows(
                path, "Sheet1", insertAtRow: 2, copyStyleFromRow: 2, rows, backupBeforeSave: false);

            Assert.Null(error);

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("Sheet1");
            Assert.Equal("Header",      ws.Cell(1, 1).GetString());
            Assert.Equal("InsertedRow", ws.Cell(2, 1).GetString());
            Assert.Equal("Row2",        ws.Cell(3, 1).GetString());
            Assert.Equal("Row3",        ws.Cell(4, 1).GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InsertRows_EmptyRows_ReturnsError()
    {
        var path = CreateTempWorkbook("Sheet1", new string[,] { { "A" } });
        try
        {
            var (result, error) = ExcelWorkbookModifier.InsertRows(
                path, "Sheet1", insertAtRow: 1, copyStyleFromRow: 1,
                new List<Dictionary<string, string>>(), backupBeforeSave: false);

            Assert.NotNull(error);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── AppendTableRows ───────────────────────────────────────────────────────

    [Fact]
    public void AppendTableRows_MatchHeaders_AppendsCorrectly()
    {
        var path = CreateTempWorkbook("Data", new string[,]
        {
            { "Name",   "Code", "Group" },
            { "Item A", "A001", "G1" }
        });
        try
        {
            var rows = new List<Dictionary<string, string>>
            {
                new() { ["Name"] = "Item B", ["Code"] = "B002", ["Group"] = "G2" }
            };
            var (result, error) = ExcelWorkbookModifier.AppendTableRows(
                path, "Data", tableName: "", matchHeaders: true, rows, backupBeforeSave: false);

            Assert.Null(error);

            using var wb = new XLWorkbook(path);
            var ws = wb.Worksheet("Data");
            Assert.Equal("Item B", ws.Cell(3, 1).GetString());
            Assert.Equal("B002",   ws.Cell(3, 2).GetString());
            Assert.Equal("G2",     ws.Cell(3, 3).GetString());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AppendTableRows_UnmatchedHeader_IsWarned()
    {
        var path = CreateTempWorkbook("Data", new string[,]
        {
            { "Name", "Code" },
            { "A",    "001"  }
        });
        try
        {
            var rows = new List<Dictionary<string, string>>
            {
                new() { ["Name"] = "B", ["Code"] = "002", ["Nonexistent"] = "X" }
            };
            var (result, error) = ExcelWorkbookModifier.AppendTableRows(
                path, "Data", tableName: "", matchHeaders: true, rows, backupBeforeSave: false);

            Assert.Null(error);

            // The result carries unmatchedHeaders info
            Assert.NotNull(result);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ── ExcelWorkbookInspector ────────────────────────────────────────────────

    [Fact]
    public void InspectWorkbook_ValidFile_ReturnsMetadata()
    {
        var path = CreateTempWorkbook("MySheet", new string[,]
        {
            { "Col1", "Col2" },
            { "A",    "B"    },
            { "C",    "D"    }
        });
        try
        {
            var (dto, error) = ExcelWorkbookInspector.Inspect(path, includePreviewRows: true, previewRowCount: 2);

            Assert.Null(error);
            Assert.NotNull(dto);
            Assert.Single(dto!.Worksheets);
            var sheet = dto.Worksheets[0];
            Assert.Equal("MySheet", sheet.Name);
            Assert.Equal(2, sheet.Headers.Count);
            Assert.Equal("Col1", sheet.Headers[0]);
            Assert.Equal(2, sheet.PreviewRows.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void InspectWorkbook_FileNotFound_ReturnsError()
    {
        var (dto, error) = ExcelWorkbookInspector.Inspect(@"C:\missing.xlsx", false, 0);
        Assert.NotNull(error);
        Assert.Null(dto);
    }

    [Fact]
    public void InspectWorkbook_UnsupportedExtension_ReturnsError()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "a,b,c");
            var (dto, error) = ExcelWorkbookInspector.Inspect(path, false, 0);
            Assert.NotNull(error);
            Assert.Contains("Unsupported", error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

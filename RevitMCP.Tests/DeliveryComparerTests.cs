using ClosedXML.Excel;
using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using Xunit;

namespace RevitMCP.Tests;

public class DeliveryComparerTests : IDisposable
{
    private readonly string _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public DeliveryComparerTests()
    {
        System.IO.Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteFile(string relativePath, string? content = null)
    {
        var full = System.IO.Path.Combine(_tempDir, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        // Write >10KB to avoid SuspiciouslySmallFile warnings in comparers
        System.IO.File.WriteAllText(full, content ?? new string('X', 20_000));
    }

    private DeliveryScanResult ScanTempDir(IEnumerable<string>? exts = null)
    {
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        return scanner.Scan(_tempDir, false, exts, 5000);
    }

    // ── RevitSheetComparer ───────────────────────────────────────────────────

    [Fact]
    public void RevitSheetComparer_AllFilesPresent_NoIssues()
    {
        WriteFile("1626_TP_EL-5-01_Plaan.pdf");
        WriteFile("1626_TP_EL-5-01_Plaan.dwg");
        var scan = ScanTempDir();

        var sheets = new[]
        {
            new RevitSheetDescriptor { SheetNumber = "EL-5-01", SheetName = "Plaan", Discipline = "EL" }
        };

        var issues = DeliveryRevitSheetComparer.Compare(scan, sheets, new[] { "pdf", "dwg" }, Array.Empty<string>(), Array.Empty<string>(), "TESTRUN");
        Assert.Empty(issues);
    }

    [Fact]
    public void RevitSheetComparer_MissingPdf_ReturnsIssue()
    {
        WriteFile("1626_TP_EL-5-01_Plaan.dwg");
        var scan = ScanTempDir();

        var sheets = new[]
        {
            new RevitSheetDescriptor { SheetNumber = "EL-5-01", SheetName = "Plaan", Discipline = "EL" }
        };

        var issues = DeliveryRevitSheetComparer.Compare(scan, sheets, new[] { "pdf", "dwg" }, Array.Empty<string>(), Array.Empty<string>(), "TESTRUN");
        Assert.Single(issues);
        Assert.Contains("pdf", issues[0].Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RevitSheetComparer_OrphanFile_ReturnsIssue()
    {
        WriteFile("1626_TP_EL-5-99_Unknown.pdf"); // no matching sheet
        var scan = ScanTempDir();

        var sheets = new[]
        {
            new RevitSheetDescriptor { SheetNumber = "EL-5-01", SheetName = "Known", Discipline = "EL" }
        };

        var issues = DeliveryRevitSheetComparer.Compare(scan, sheets, new[] { "pdf" }, Array.Empty<string>(), Array.Empty<string>(), "TESTRUN");
        // EL-5-01 missing pdf AND EL-5-99 orphan
        Assert.Contains(issues, i => i.Category.Contains("Orphan", StringComparison.OrdinalIgnoreCase));
    }

    // ── ExcelRegisterComparer ────────────────────────────────────────────────

    private string CreateExcelRegister(IEnumerable<string> sheetNumbers, string worksheetName = "Register")
    {
        var path = System.IO.Path.Combine(_tempDir, "register.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(worksheetName);
        ws.Cell(1, 1).Value = "Dokumendi nr";
        ws.Cell(1, 2).Value = "Nimetus";
        int row = 2;
        foreach (var num in sheetNumbers)
        {
            ws.Cell(row, 1).Value = num;
            ws.Cell(row, 2).Value = "Test sheet";
            row++;
        }
        wb.SaveAs(path);
        return path;
    }

    [Fact]
    public void ExcelComparer_AllFilesPresent_NoIssues()
    {
        WriteFile("1626_TP_EL-5-01_Plaan.pdf");
        WriteFile("1626_TP_EL-5-01_Plaan.dwg");
        var scan = ScanTempDir();
        var xlPath = CreateExcelRegister(new[] { "EL-5-01" });

        var (issues, error) = DeliveryExcelRegisterComparer.Compare(scan, xlPath, string.Empty, new[] { "pdf", "dwg" }, "TESTRUN");
        Assert.Null(error);
        Assert.Empty(issues);
    }

    [Fact]
    public void ExcelComparer_MissingFile_ReturnsIssue()
    {
        // Register has EL-5-01 but no file in folder
        var scan = ScanTempDir();
        var xlPath = CreateExcelRegister(new[] { "EL-5-01" });

        var (issues, error) = DeliveryExcelRegisterComparer.Compare(scan, xlPath, string.Empty, new[] { "pdf" }, "TESTRUN");
        Assert.Null(error);
        Assert.NotEmpty(issues);
    }

    [Fact]
    public void ExcelComparer_FileNotInRegister_ReturnsIssue()
    {
        WriteFile("1626_TP_EL-5-99_Orphan.pdf");
        var scan = ScanTempDir();
        var xlPath = CreateExcelRegister(new[] { "EL-5-01" }); // 5-99 not in register

        var (issues, error) = DeliveryExcelRegisterComparer.Compare(scan, xlPath, string.Empty, new[] { "pdf" }, "TESTRUN");
        Assert.Null(error);
        Assert.Contains(issues, i => i.Category.Contains("MissingFromRegister", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExcelComparer_DuplicateDocNumber_ReturnsIssue()
    {
        var xlPath = CreateExcelRegister(new[] { "EL-5-01", "EL-5-01" }); // duplicate
        var scan = ScanTempDir();

        var (issues, error) = DeliveryExcelRegisterComparer.Compare(scan, xlPath, string.Empty, new[] { "pdf" }, "TESTRUN");
        Assert.Null(error);
        Assert.Contains(issues, i => i.Category.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExcelComparer_NonExistentFile_ReturnsError()
    {
        var scan = ScanTempDir();
        var (issues, error) = DeliveryExcelRegisterComparer.Compare(scan, @"C:\nonexistent.xlsx", string.Empty, new[] { "pdf" }, "TESTRUN");
        Assert.NotNull(error);
    }
}

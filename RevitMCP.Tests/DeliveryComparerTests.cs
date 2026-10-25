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

    [Fact]
    public void RevitSheetComparer_RealSheetNumbersWithFilters_AreCompared()
    {
        // H13: these sheet numbers used to fail to parse, so disciplineFilter dropped every sheet.
        WriteFile("73226_PP_EN-5-10_1k-ATS.pdf");
        var scan = ScanTempDir();

        var sheets = new[]
        {
            RevitSheetDescriptor.FromSheet("73226_PP_EN-5-10_1k-ATS", "1k ATS"),
            RevitSheetDescriptor.FromSheet("73226_PP_EN-5-20_1k-Side", "1k Side")
        };

        var result = DeliveryRevitSheetComparer.CompareSheets(scan, sheets, new[] { "pdf" },
            new[] { "PP" }, new[] { "EN" }, "TESTRUN");

        Assert.Equal(2, result.ComparedSheetCount);
        Assert.Empty(result.UnparsedSheetNumbers);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("Delivery.MissingPDF", issue.Category);
        Assert.Equal("EN-5-20", issue.SheetNumber);
    }

    [Fact]
    public void RevitSheetComparer_UnparsedSheetWithDisciplineFilter_IsReported()
    {
        var scan = ScanTempDir();
        var sheets = new[]
        {
            RevitSheetDescriptor.FromSheet("EN-5-01", "Parsed"),
            RevitSheetDescriptor.FromSheet("A101", "Unparsed 1"),
            RevitSheetDescriptor.FromSheet("Kaanetiitel", "Unparsed 2")
        };

        var result = DeliveryRevitSheetComparer.CompareSheets(scan, sheets, new[] { "pdf" },
            Array.Empty<string>(), new[] { "EN" }, "TESTRUN");

        Assert.Equal(1, result.ComparedSheetCount);
        Assert.Equal(new[] { "A101", "Kaanetiitel" }, result.UnparsedSheetNumbers);
        var warning = Assert.Single(result.Issues, i => i.Category == "Delivery.UnparsedSheetNumber");
        Assert.Contains("2 Revit sheet(s)", warning.Title);
        Assert.Contains("A101", warning.Description);
        Assert.Contains("Kaanetiitel", warning.Description);

        var message = DeliveryRevitSheetComparer.DescribeSheetCounts(scan.Files.Count, sheets.Length, result);
        Assert.Contains("against 1 of 3 Revit sheet(s)", message);
        Assert.Contains("2 sheet(s) skipped", message);
    }

    [Fact]
    public void RevitSheetComparer_UnparsedSheetWithoutFilter_IsComparedByRawNumber()
    {
        WriteFile("1626_TP_EL-5-01_Plaan.pdf");
        var scan = ScanTempDir();
        var sheets = new[] { RevitSheetDescriptor.FromSheet("A101", "Unparsed") };

        var result = DeliveryRevitSheetComparer.CompareSheets(scan, sheets, new[] { "pdf" },
            Array.Empty<string>(), Array.Empty<string>(), "TESTRUN");

        Assert.Equal(1, result.ComparedSheetCount);
        Assert.Empty(result.UnparsedSheetNumbers);
        Assert.DoesNotContain(result.Issues, i => i.Category == "Delivery.UnparsedSheetNumber");
        Assert.Contains(result.Issues, i => i.Category == "Delivery.MissingPDF" && i.SheetNumber == "A101");
        Assert.Equal("Checked 1 file(s) against 1 Revit sheet(s).",
            DeliveryRevitSheetComparer.DescribeSheetCounts(scan.Files.Count, sheets.Length, result));
    }

    [Fact]
    public void RevitSheetComparer_DisciplineFilterExcludesOthers_CountsOnlyCompared()
    {
        var scan = ScanTempDir();
        var sheets = new[]
        {
            RevitSheetDescriptor.FromSheet("1626_TP_EL-5-01", "EL"),
            RevitSheetDescriptor.FromSheet("1626_TP_EN-5-01", "EN")
        };

        var result = DeliveryRevitSheetComparer.CompareSheets(scan, sheets, new[] { "pdf" },
            Array.Empty<string>(), new[] { "EN" }, "TESTRUN");

        Assert.Equal(1, result.ComparedSheetCount);
        Assert.Empty(result.UnparsedSheetNumbers);
        Assert.Contains("against 1 of 2 Revit sheet(s)",
            DeliveryRevitSheetComparer.DescribeSheetCounts(0, sheets.Length, result));
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

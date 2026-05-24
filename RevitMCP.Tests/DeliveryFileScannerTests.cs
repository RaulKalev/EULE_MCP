using RevitMCP.Addin.Delivery;
using RevitMCP.Addin.FileSystem;
using Xunit;

namespace RevitMCP.Tests;

public class DeliveryFileScannerTests : IDisposable
{
    private readonly string _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public DeliveryFileScannerTests()
    {
        System.IO.Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void WriteFile(string relativePath, string content = "test")
    {
        var full = System.IO.Path.Combine(_tempDir, relativePath);
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
    }

    [Fact]
    public void Scan_FolderNotExist_ReturnsFailure()
    {
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(@"C:\NonExistentFolder_XYZ_123", true, null, 5000);
        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void Scan_EmptyFolder_ReturnsSuccessWithZeroFiles()
    {
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(_tempDir, false, null, 5000);
        Assert.True(result.Success);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void Scan_ParseableEuleFile_ParsedIsNotNull()
    {
        WriteFile("1626_TP_EL-5-01_Valgustuse_plaan.pdf");
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(_tempDir, false, new[] { ".pdf" }, 5000);
        Assert.True(result.Success);
        Assert.Single(result.Files);
        Assert.NotNull(result.Files[0].Parsed);
        Assert.Equal("EL-5-01", result.Files[0].Parsed!.SheetNumber);
    }

    [Fact]
    public void Scan_NonEuleFile_ParsedIsNull()
    {
        WriteFile("random_document.pdf");
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(_tempDir, false, new[] { ".pdf" }, 5000);
        Assert.True(result.Success);
        Assert.Single(result.Files);
        Assert.Null(result.Files[0].Parsed);
    }

    [Fact]
    public void Scan_ExtensionFilter_OnlyReturnsMatchingFiles()
    {
        WriteFile("1626_TP_EL-5-01.pdf");
        WriteFile("1626_TP_EL-5-01.dwg");
        WriteFile("1626_TP_EL-5-02.ifc");
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(_tempDir, false, new[] { "pdf" }, 5000);
        Assert.True(result.Success);
        Assert.Single(result.Files);
        Assert.Equal("pdf", result.Files[0].Extension);
    }

    [Fact]
    public void Scan_MaxResults_LimitsOutput()
    {
        for (int i = 1; i <= 10; i++)
            WriteFile($"1626_TP_EL-5-{i:D2}_Sheet.pdf");
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(_tempDir, false, new[] { ".pdf" }, 3);
        Assert.True(result.Success);
        Assert.Equal(3, result.Files.Count);
    }

    [Fact]
    public void Scan_Recursive_FindsFilesInSubFolders()
    {
        WriteFile(@"sub\1626_TP_EL-5-01.pdf");
        WriteFile("1626_TP_EL-5-02.pdf");
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(_tempDir, recursive: true, null, 5000);
        Assert.True(result.Success);
        Assert.Equal(2, result.Files.Count);
    }

    [Fact]
    public void Scan_EmptyFile_ProducesWarning()
    {
        var path = System.IO.Path.Combine(_tempDir, "1626_TP_EL-5-01.pdf");
        System.IO.File.WriteAllBytes(path, []);
        var scanner = new DeliveryFileScanner(new FilePathPolicy());
        var result = scanner.Scan(_tempDir, false, new[] { ".pdf" }, 5000);
        Assert.True(result.Success);
        Assert.NotEmpty(result.Warnings);
    }
}

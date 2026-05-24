using RevitMCP.Addin.Delivery;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Tests for DeliveryFolderPolicyChecker — Fix 2 validation.
/// </summary>
public class DeliveryFolderPolicyCheckerTests
{
    // ── Helper ────────────────────────────────────────────────────────────────

    private static DeliveryScanResult MakeScan(IEnumerable<string> fileNames, IEnumerable<string>? subFolders = null)
    {
        var files = fileNames.Select(n =>
        {
            var parsed = DeliveryFileNameParser.Parse(n);
            return new DeliveryFileEntry
            {
                FileName = n,
                Extension = System.IO.Path.GetExtension(n).TrimStart('.').ToLowerInvariant(),
                FullPath = @"C:\Delivery\" + n,
                SizeBytes = 1024,
                Parsed = parsed
            };
        }).ToList();

        return new DeliveryScanResult
        {
            Success = true,
            FolderPath = @"C:\Delivery",
            Files = files,
            SubFolders = subFolders?.ToList() ?? []
        };
    }

    // ── Temp file detection ────────────────────────────────────────────────────

    [Fact]
    public void CheckTempFiles_OfficeLockFile_IsFlagged()
    {
        var scan = MakeScan(["~$1626_TP_EL-5-01_Valgustuse_plaan.xlsx"]);
        var policy = new DeliveryFolderPolicy { CheckTempFiles = true };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Single(issues);
        Assert.Contains("TempFiles", issues[0].Category);
    }

    [Fact]
    public void CheckTempFiles_BakFile_IsFlagged()
    {
        var scan = MakeScan(["drawing.bak"]);
        var policy = new DeliveryFolderPolicy { CheckTempFiles = true };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Single(issues);
        Assert.Contains("TempFiles", issues[0].Category);
    }

    [Fact]
    public void CheckTempFiles_NormalPdf_IsNotFlagged()
    {
        var scan = MakeScan(["1626_TP_EL-5-01_Valgustuse_plaan.pdf"]);
        var policy = new DeliveryFolderPolicy { CheckTempFiles = true };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Empty(issues);
    }

    [Fact]
    public void CheckTempFiles_Disabled_DoesNotFlag()
    {
        var scan = MakeScan(["~$locked.xlsx", "drawing.bak"]);
        var policy = new DeliveryFolderPolicy { CheckTempFiles = false };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Empty(issues);
    }

    // ── Required folder detection ──────────────────────────────────────────────

    [Fact]
    public void CheckRequiredFolders_MissingFolder_IsFlagged()
    {
        var scan = MakeScan([], subFolders: ["DWG"]);
        var policy = new DeliveryFolderPolicy
        {
            CheckRequiredFolders = true,
            RequiredFolders = ["PDF", "DWG"]
        };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Single(issues);
        Assert.Contains("FolderStructure", issues[0].Category);
        Assert.Contains("PDF", issues[0].Title);
    }

    [Fact]
    public void CheckRequiredFolders_AllPresent_NoIssues()
    {
        var scan = MakeScan([], subFolders: ["PDF", "DWG"]);
        var policy = new DeliveryFolderPolicy
        {
            CheckRequiredFolders = true,
            RequiredFolders = ["PDF", "DWG"]
        };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Empty(issues);
    }

    // ── Old revision detection ─────────────────────────────────────────────────

    [Fact]
    public void CheckOldRevisions_MultipleRevisionsForSameSheet_FlagsOlderOnes()
    {
        var scan = MakeScan([
            "1626_TP_EL-5-01_Valgustuse_plaan_Rev01.pdf",
            "1626_TP_EL-5-01_Valgustuse_plaan_Rev02.pdf"
        ]);
        var policy = new DeliveryFolderPolicy { CheckOldRevisions = true, CheckTempFiles = false };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Single(issues); // Only the older Rev01 is flagged
        Assert.Contains("Revision", issues[0].Category);
        Assert.Contains("Rev01", issues[0].Title);
    }

    [Fact]
    public void CheckOldRevisions_SingleRevision_NoIssues()
    {
        var scan = MakeScan(["1626_TP_EL-5-01_Valgustuse_plaan_Rev01.pdf"]);
        var policy = new DeliveryFolderPolicy { CheckOldRevisions = true, CheckTempFiles = false };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Empty(issues);
    }

    // ── Required project files ─────────────────────────────────────────────────

    [Fact]
    public void CheckRequiredProjectFiles_MissingExtension_IsFlagged()
    {
        var scan = MakeScan(["1626_TP_EL-5-01_Valgustuse_plaan.pdf"]);
        var policy = new DeliveryFolderPolicy
        {
            CheckTempFiles = false,
            RequiredProjectFileExtensions = [".rvt"]
        };
        int seq = 1;

        var issues = DeliveryFolderPolicyChecker.Check(scan, policy, "TEST", ref seq);

        Assert.Single(issues);
        Assert.Contains("ProjectFiles", issues[0].Category);
    }

    // ── Glob pattern matching ─────────────────────────────────────────────────

    [Theory]
    [InlineData("~$locked.xlsx", "~$*", true)]
    [InlineData("drawing.bak", "*.bak", true)]
    [InlineData("drawing.pdf", "*.bak", false)]
    [InlineData("thumbs.db", "thumbs.db", true)]
    [InlineData("THUMBS.DB", "thumbs.db", true)]  // case-insensitive
    [InlineData("file.tmp", "*.tmp", true)]
    public void GlobPattern_MatchesExpected(string fileName, string pattern, bool expected)
    {
        var result = DeliveryFolderPolicyChecker.MatchesGlobPattern(fileName, pattern);
        Assert.Equal(expected, result);
    }
}

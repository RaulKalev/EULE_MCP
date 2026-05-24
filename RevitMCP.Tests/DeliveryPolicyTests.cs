using RevitMCP.Addin.Delivery;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>
/// Tests for DeliveryFolderPolicyChecker — old revision sorting and policy checks.
/// </summary>
public class DeliveryPolicyTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DeliveryFileEntry MakeEntry(string fileName, string sheetNumber, string? revision, string ext)
        => new()
        {
            FileName = fileName,
            Extension = ext,
            FullPath = $"C:\\Delivery\\{fileName}",
            Parsed = new ParsedDeliveryFileName
            {
                ProjectNumber = "1626",
                Stage = "TP",
                SheetNumber = sheetNumber,
                Discipline = "EL",
                Group = "5",
                Sequence = "01",
                Revision = revision,
                Extension = ext
            }
        };

    private static DeliveryScanResult MakeScan(params DeliveryFileEntry[] files)
        => new()
        {
            Success = true,
            FolderPath = "C:\\Delivery",
            Files = [.. files]
        };

    private static DeliveryFolderPolicy OldRevisionPolicy()
        => new() { CheckOldRevisions = true };

    // ── Old revision detection — numeric sorting ───────────────────────────────

    [Fact]
    public void CheckOldRevisions_NumericRevisions_R9_IsOlderThan_R10()
    {
        var scan = MakeScan(
            MakeEntry("1626_TP_EL-5-01_R9.pdf", "EL-5-01", "R9", "pdf"),
            MakeEntry("1626_TP_EL-5-01_R10.pdf", "EL-5-01", "R10", "pdf"));

        int seq = 1;
        var runId = "TEST";
        var issues = DeliveryFolderPolicyChecker.Check(scan, OldRevisionPolicy(), runId, ref seq);

        // Only R9 should be flagged, not R10
        Assert.Single(issues);
        Assert.Contains("R9", issues[0].Description ?? issues[0].Title);
        Assert.DoesNotContain("R10", issues[0].Description ?? issues[0].Title);
    }

    [Fact]
    public void CheckOldRevisions_NumericRevisions_R01_R02_R10_FlagsOlderTwo()
    {
        var scan = MakeScan(
            MakeEntry("1626_TP_EL-5-01_R01.pdf", "EL-5-01", "R01", "pdf"),
            MakeEntry("1626_TP_EL-5-01_R02.pdf", "EL-5-01", "R02", "pdf"),
            MakeEntry("1626_TP_EL-5-01_R10.pdf", "EL-5-01", "R10", "pdf"));

        int seq = 1;
        var issues = DeliveryFolderPolicyChecker.Check(scan, OldRevisionPolicy(), "TEST", ref seq);

        // R01 and R02 flagged, R10 is latest
        Assert.Equal(2, issues.Count);
        var titles = issues.Select(i => i.Description ?? i.Title ?? "").ToList();
        Assert.Contains(titles, t => t.Contains("R01"));
        Assert.Contains(titles, t => t.Contains("R02"));
        Assert.DoesNotContain(titles, t => t.Contains("R10"));
    }

    [Fact]
    public void CheckOldRevisions_AlphaRevisions_A_B_C_FlagsOlderTwo()
    {
        var scan = MakeScan(
            MakeEntry("1626_TP_EL-5-01_A.pdf", "EL-5-01", "A", "pdf"),
            MakeEntry("1626_TP_EL-5-01_B.pdf", "EL-5-01", "B", "pdf"),
            MakeEntry("1626_TP_EL-5-01_C.pdf", "EL-5-01", "C", "pdf"));

        int seq = 1;
        var issues = DeliveryFolderPolicyChecker.Check(scan, OldRevisionPolicy(), "TEST", ref seq);

        // A and B flagged, C is latest
        Assert.Equal(2, issues.Count);
        var titles = issues.Select(i => i.Description ?? i.Title ?? "").ToList();
        Assert.Contains(titles, t => t.Contains("EL-5-01"));
    }

    [Fact]
    public void CheckOldRevisions_OneRevisionOnly_NoIssue()
    {
        var scan = MakeScan(
            MakeEntry("1626_TP_EL-5-01_R01.pdf", "EL-5-01", "R01", "pdf"));

        int seq = 1;
        var issues = DeliveryFolderPolicyChecker.Check(scan, OldRevisionPolicy(), "TEST", ref seq);

        Assert.Empty(issues);
    }

    [Fact]
    public void CheckOldRevisions_MixedExtensions_TreatedSeparately()
    {
        // Same sheet, R01 and R02 in both PDF and DWG — 2 old revisions total
        var scan = MakeScan(
            MakeEntry("1626_TP_EL-5-01_R01.pdf", "EL-5-01", "R01", "pdf"),
            MakeEntry("1626_TP_EL-5-01_R02.pdf", "EL-5-01", "R02", "pdf"),
            MakeEntry("1626_TP_EL-5-01_R01.dwg", "EL-5-01", "R01", "dwg"),
            MakeEntry("1626_TP_EL-5-01_R02.dwg", "EL-5-01", "R02", "dwg"));

        int seq = 1;
        var issues = DeliveryFolderPolicyChecker.Check(scan, OldRevisionPolicy(), "TEST", ref seq);

        // R01 pdf and R01 dwg both flagged
        Assert.Equal(2, issues.Count);
    }

    [Fact]
    public void CheckOldRevisions_REV_Prefix_NumericOrder()
    {
        var scan = MakeScan(
            MakeEntry("1626_TP_EL-5-01_REV02.pdf", "EL-5-01", "REV02", "pdf"),
            MakeEntry("1626_TP_EL-5-01_REV10.pdf", "EL-5-01", "REV10", "pdf"));

        int seq = 1;
        var issues = DeliveryFolderPolicyChecker.Check(scan, OldRevisionPolicy(), "TEST", ref seq);

        // REV02 is older than REV10
        Assert.Single(issues);
        Assert.Contains("REV02", issues[0].Description ?? issues[0].Title ?? "");
    }

    // ── Delivery scan default extensions ─────────────────────────────────────

    [Fact]
    public void DeliveryFolderPolicy_DefaultsAreOptIn()
    {
        // Verifying default policy has CheckTempFiles=true but old revisions=true
        var policy = new DeliveryFolderPolicy();
        Assert.True(policy.CheckTempFiles);
        Assert.True(policy.CheckOldRevisions);
        Assert.False(policy.CheckRequiredFolders);
        Assert.False(policy.CheckSuspiciousExtensions);
    }
}

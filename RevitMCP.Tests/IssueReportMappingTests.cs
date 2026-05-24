using RevitMCP.Addin.Delivery;
using RevitMCP.Core.Models.Issues;
using Xunit;

namespace RevitMCP.Tests;

public class IssueReportMappingTests
{
    private const string RunId = "ABCD1234";

    [Fact]
    public void BuildReport_SetsRunIdAndTitle()
    {
        var report = DeliveryIssueBuilder.BuildReport(RunId, "Test Report", new List<IssueDto>());
        Assert.Equal(RunId, report.RunId);
        Assert.Equal("Test Report", report.Title);
        Assert.Equal(0, report.TotalCount);
    }

    [Fact]
    public void MissingPdf_ReturnsErrorSeverity_DeliveryCategory()
    {
        var issue = DeliveryIssueBuilder.MissingPdf(RunId, 1, "EL-5-01", null);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.StartsWith("Delivery", issue.Category, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("EL-5-01", issue.Title + issue.Description);
        Assert.Equal($"{RunId}-0001", issue.IssueId);
    }

    [Fact]
    public void MissingDwg_ReturnsErrorSeverity()
    {
        var issue = DeliveryIssueBuilder.MissingDwg(RunId, 1, "EN-2-03", null);
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void OrphanFile_ReturnsWarningSeverity()
    {
        var issue = DeliveryIssueBuilder.OrphanFile(RunId, 1, "unknown.pdf", "pdf");
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void EmptyFile_ReturnsErrorSeverity()
    {
        var issue = DeliveryIssueBuilder.EmptyFile(RunId, 1, "empty.pdf");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void DuplicateFile_ReturnsWarningSeverity()
    {
        var issue = DeliveryIssueBuilder.DuplicateFile(RunId, 1, "EL-5-01", "pdf", new[] { "file1.pdf", "file2.pdf" });
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void RegisterRowMissingFile_ReturnsErrorSeverity()
    {
        var issue = DeliveryIssueBuilder.RegisterRowMissingFile(RunId, 1, "EL-5-01", "pdf");
        Assert.Equal(IssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void FileMissingFromRegister_ReturnsWarningSeverity()
    {
        var issue = DeliveryIssueBuilder.FileMissingFromRegister(RunId, 1, "extra.pdf");
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void RegisterDuplicateDocNumber_ReturnsWarningSeverity()
    {
        var issue = DeliveryIssueBuilder.RegisterDuplicateDocNumber(RunId, 1, "EL-5-01", 2);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void BuildReport_CountsMatchSeverities()
    {
        var issues = new List<IssueDto>
        {
            DeliveryIssueBuilder.MissingPdf(RunId, 1, "EL-5-01", null),    // Error
            DeliveryIssueBuilder.MissingDwg(RunId, 2, "EL-5-01", null),    // Warning (MissingDwg is Warning)
            DeliveryIssueBuilder.OrphanFile(RunId, 3, "x.pdf", "pdf"),     // Warning
            DeliveryIssueBuilder.EmptyFile(RunId, 4, "y.pdf")              // Error
        };
        var report = DeliveryIssueBuilder.BuildReport(RunId, "Test", issues);
        Assert.Equal(4, report.TotalCount);
        Assert.Equal(2, report.ErrorCount);
        Assert.Equal(2, report.WarningCount);
        Assert.Equal(0, report.CriticalCount);
    }
}

using RevitMCP.Addin.Export;
using RevitMCP.Addin.Reports;
using RevitMCP.Core.Models.Issues;
using System.Text.Json;
using Xunit;

namespace RevitMCP.Tests;

public class IssueReportDtoTests
{
    // ── Serialize / Deserialize ───────────────────────────────────────────────

    [Fact]
    public void Serialize_Deserialize_RoundTrip_PreservesAllFields()
    {
        var original = new IssueReportDto
        {
            RunId = "TEST-001",
            Title = "QA Report",
            ModelTitle = "Project X",
            Issues = new List<IssueDto>
            {
                new()
                {
                    IssueId = "TEST-001-0001",
                    RunId = "TEST-001",
                    Severity = IssueSeverity.Error,
                    Category = "Electrical",
                    Title = "Missing cable type",
                    Description = "Circuit has no cable type"
                }
            }
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<IssueReportDto>(json)!;

        Assert.Equal("TEST-001", restored.RunId);
        Assert.Equal("QA Report", restored.Title);
        Assert.Equal("Project X", restored.ModelTitle);
        Assert.Single(restored.Issues);
        Assert.Equal("TEST-001-0001", restored.Issues[0].IssueId);
        Assert.Equal(IssueSeverity.Error, restored.Issues[0].Severity);
    }

    [Fact]
    public void ConvenienceCounts_AreCorrect_AfterRoundTrip()
    {
        var report = new IssueReportDto
        {
            RunId = "R1",
            Issues = new List<IssueDto>
            {
                new() { Severity = IssueSeverity.Critical },
                new() { Severity = IssueSeverity.Error },
                new() { Severity = IssueSeverity.Error },
                new() { Severity = IssueSeverity.Warning },
                new() { Severity = IssueSeverity.Info }
            }
        };

        var json = JsonSerializer.Serialize(report);
        var restored = JsonSerializer.Deserialize<IssueReportDto>(json)!;

        Assert.Equal(5, restored.TotalCount);
        Assert.Equal(1, restored.CriticalCount);
        Assert.Equal(2, restored.ErrorCount);
        Assert.Equal(1, restored.WarningCount);
        Assert.Equal(1, restored.InfoCount);
    }

    // ── Merge reports ─────────────────────────────────────────────────────────

    [Fact]
    public void MergeReports_CombinesAllIssues_StableCounts()
    {
        var svc = new IssueReportService();

        var r1 = new IssueReportDto
        {
            RunId = "R1",
            Title = "Report 1",
            SourceTools = new List<string> { "tool_a" },
            Issues = new List<IssueDto>
            {
                new() { Severity = IssueSeverity.Error, Category = "Electrical" },
                new() { Severity = IssueSeverity.Warning, Category = "Electrical" }
            }
        };
        var r2 = new IssueReportDto
        {
            RunId = "R2",
            Title = "Report 2",
            SourceTools = new List<string> { "tool_b" },
            Issues = new List<IssueDto>
            {
                new() { Severity = IssueSeverity.Critical, Category = "Coordination" }
            }
        };

        var merged = svc.MergeReports("Merged", new[] { r1, r2 });

        Assert.Equal(3, merged.TotalCount);
        Assert.Equal(1, merged.CriticalCount);
        Assert.Equal(1, merged.ErrorCount);
        Assert.Equal(1, merged.WarningCount);
        Assert.NotNull(merged.RunId);
        Assert.Contains("tool_a", merged.SourceTools);
        Assert.Contains("tool_b", merged.SourceTools);
    }

    [Fact]
    public void MergeReports_EmptyList_ReturnsZeroCounts()
    {
        var svc = new IssueReportService();
        var merged = svc.MergeReports("Empty", Array.Empty<IssueReportDto>());

        Assert.Equal(0, merged.TotalCount);
        Assert.Empty(merged.Issues);
    }

    [Fact]
    public void MergeReports_IssuesFromSourceRetainOriginalIds()
    {
        var svc = new IssueReportService();
        var r = new IssueReportDto
        {
            RunId = "SRC",
            Issues = new List<IssueDto>
            {
                new() { IssueId = "SRC-0001", Severity = IssueSeverity.Info }
            }
        };

        var merged = svc.MergeReports("Test", new[] { r });

        Assert.Equal("SRC-0001", merged.Issues[0].IssueId);
    }

    // ── Export path sanitization ──────────────────────────────────────────────

    [Fact]
    public void ResolveFilePath_InvalidCharsInName_AreReplaced()
    {
        var svc = new ExportPathService();
        var path = svc.ResolveFilePath("Report: A/B\\C?*.xlsx");

        var fileName = Path.GetFileName(path);
        // None of the invalid chars should be in the file name
        foreach (var c in new[] { ':', '/', '\\', '?', '*' })
            Assert.DoesNotContain(c, fileName);
    }

    [Fact]
    public void ResolveFilePath_NoExtension_AddsXlsx()
    {
        var svc = new ExportPathService();
        var path = svc.ResolveFilePath("MyReport");

        Assert.EndsWith(".xlsx", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveFilePath_AlreadyHasXlsx_DoesNotDoubleExtension()
    {
        var svc = new ExportPathService();
        var path = svc.ResolveFilePath("MyReport.xlsx");

        // Should not become MyReport.xlsx.xlsx
        Assert.Equal(1, Path.GetFileName(path).Split('.').Length - 1); // exactly 1 dot from extension
    }
}

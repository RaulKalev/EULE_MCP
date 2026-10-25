using RevitMCP.Addin.ModelState;
using RevitMCP.Core.Models.Issues;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#109: audit thresholds, config overrides, section status and the issues the skill task writes.</summary>
public class AuditRulesTests
{
    [Theory]
    [InlineData("warnings.total", 100, "ok")]
    [InlineData("warnings.total", 101, "warn")]
    [InlineData("warnings.total", 501, "bad")]
    [InlineData("families.inPlace", 10, "ok")]
    [InlineData("families.inPlace", 11, "warn")]
    [InlineData("imports.cadImports", 0, "ok")]
    [InlineData("imports.cadImports", 1, "warn")]
    [InlineData("views.unplacedPercent", 20.5, "warn")]
    [InlineData("views.unplacedPercent", 20, "ok")]
    [InlineData("links.notLoaded", 6, "bad")]
    public void CompanyDefaults_AreStrictlyAbove(string key, double value, string expected) =>
        Assert.Equal(expected, AuditRules.Default().Evaluate(key, value));

    [Fact]
    public void FiguresWithoutARule_HaveNoBadge() =>
        Assert.Null(AuditRules.Default().Evaluate("views.total", 10_000));

    [Fact]
    public void Overrides_SetWarnAndBad_PerKey()
    {
        var warnings = new List<string>();
        var rules = AuditRules.Default().WithOverrides(
            "{\"warnings.total\": {\"warn\": 200, \"bad\": 1000}, \"families.inPlace\": 5, \"imports.cadImports\": false}",
            "project config", warnings);

        Assert.Empty(warnings);
        Assert.Equal("ok", rules.Evaluate("warnings.total", 150));
        Assert.Equal("warn", rules.Evaluate("warnings.total", 600));
        Assert.Equal("bad", rules.Evaluate("warnings.total", 1001));
        Assert.Equal("warn", rules.Evaluate("families.inPlace", 6));
        Assert.Equal(50, rules.Get("families.inPlace")!.Bad);       // a number sets warn only
        Assert.Null(rules.Evaluate("imports.cadImports", 99));     // false turns the badge off
        // The defaults themselves are untouched.
        Assert.Equal("warn", AuditRules.Default().Evaluate("imports.cadImports", 1));
    }

    [Fact]
    public void Overrides_LayerInOrder_LaterScopesWin()
    {
        var warnings = new List<string>();
        var rules = AuditRules.Default()
            .WithOverrides("{\"rooms.unplaced\": {\"warn\": 5, \"bad\": 10}}", "company config", warnings)
            .WithOverrides("{\"rooms.unplaced\": {\"bad\": null}}", "project config", warnings);
        Assert.Equal(5, rules.Get("rooms.unplaced")!.Warn);
        Assert.Null(rules.Get("rooms.unplaced")!.Bad);
        Assert.Equal("warn", rules.Evaluate("rooms.unplaced", 500));
    }

    [Fact]
    public void BadOverrides_AreReported_AndIgnored()
    {
        var warnings = new List<string>();
        var rules = AuditRules.Default().WithOverrides("{\"warnings.total\": \"lots\", \"my.figure\": 3, \"links.unpinned\": {\"warn\": \"x\"}}", "user config", warnings);
        Assert.Contains(warnings, w => w.Contains("'warnings.total' must be a number"));
        Assert.Contains(warnings, w => w.Contains("'my.figure' is not an audit figure"));
        Assert.Contains(warnings, w => w.Contains("'links.unpinned.warn' is not a number"));
        Assert.Equal("warn", rules.Evaluate("warnings.total", 101));
        Assert.Equal("warn", rules.Evaluate("my.figure", 4));

        warnings.Clear();
        AuditRules.Default().WithOverrides("[1, 2]", "user config", warnings);
        Assert.Single(warnings);
        warnings.Clear();
        AuditRules.Default().WithOverrides("{not json", "user config", warnings);
        Assert.Contains("not valid JSON", warnings.Single());
    }

    [Fact]
    public void SectionStatus_IsTheWorstFigure_ErrorsWin()
    {
        var rules = AuditRules.Default();
        var section = new AuditSection { Name = "rooms", Title = "Ruumid" };
        section.Count("rooms.total", "Ruume", 40);
        section.Count("rooms.unplaced", "Paigutamata", 3);
        section.Count("rooms.notEnclosed", "Piirideta", 0);
        section.Finish(rules);
        Assert.Equal(AuditStatus.Warn, section.Status);
        Assert.Null(section.Counts[0].Status);
        Assert.Equal(0, section.Counts[1].Threshold);

        var failed = new AuditSection { Name = "links", Error = "InvalidOperationException: boom" };
        failed.Finish(rules);
        Assert.Equal(AuditStatus.Error, failed.Status);

        var skipped = new AuditSection { Name = "worksets", Status = AuditStatus.Skipped };
        skipped.Finish(rules);
        Assert.Equal(AuditStatus.Skipped, skipped.Status);
    }

    [Fact]
    public void Sections_ResolveInPageOrder_AndReportUnknownNames()
    {
        var sections = AuditSections.Resolve(new[] { "rooms", "WARNINGS", "design_options", "nope" }, out var unknown);
        Assert.Equal(new[] { "warnings", "rooms", "designOptions" }, sections);
        Assert.Equal(new[] { "nope" }, unknown);
        Assert.Equal(AuditSections.All.Count, AuditSections.Resolve(null, out _).Count);
    }

    [Fact]
    public void Issues_OnePerFlaggedFigure_AndPerFailedSection()
    {
        var rules = AuditRules.Default();
        var warnings = new AuditSection { Name = "warnings", Title = "Hoiatused" };
        warnings.Count("warnings.total", "Hoiatusi kokku", 812);
        warnings.Count("warnings.kinds", "Erinevaid", 40);
        warnings.Finish(rules);
        var families = new AuditSection { Name = "families", Title = "Kohapealsed perekonnad" };
        families.Count("families.inPlace", "Kohapealseid perekondi", 12);
        families.Finish(rules);
        var links = new AuditSection { Name = "links", Title = "Lingid", Error = "boom" };
        links.Finish(rules);
        var report = new AuditReport { DocumentTitle = "M", Sections = { warnings, families, links } };

        var issues = AuditIssues.Build(report, "run1", "audit.model", DateTimeOffset.UnixEpoch);
        Assert.Equal(3, issues.Count);
        Assert.Equal(IssueSeverity.Error, issues[0].Severity);
        Assert.Equal("ModelAudit.warnings", issues[0].Category);
        Assert.Contains("812", issues[0].Title);
        Assert.Contains("lävi 500", issues[0].Description);
        Assert.Equal(IssueSeverity.Warning, issues[1].Severity);
        Assert.Equal("ModelAudit.links", issues[2].Category);
        Assert.Equal(3, issues.Select(i => i.IssueId).Distinct().Count());
        Assert.All(issues, i => Assert.Equal("audit.model", i.SourceTool));
        Assert.Equal(AuditStatus.Bad, report.Status);
    }
}

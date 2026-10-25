using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelPages;
using RevitMCP.Addin.ModelState;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#109: the audit page (plain tables, element rows, no external URLs, size cap) and its JSON mirror.</summary>
public class AuditReportHtmlTests
{
    private static AuditReport Sample(int unplacedViews = 3)
    {
        var rules = AuditRules.Default();
        var views = new AuditSection { Name = AuditSections.Views, Title = AuditSections.TitleOf(AuditSections.Views) };
        views.Count("views.total", "Vaateid", 10);
        views.Count("views.unplacedPercent", "Paigutamata osakaal", 30, "%");
        var table = views.Table("unplacedViews", "Lehtedel paigutamata vaated", ("id", "Id"), ("name", "Nimi"), ("viewType", "Tüüp"));
        for (var i = 0; i < unplacedViews; i++) table.Add(1000 + i, 1000 + i, $"Plaan <{i}> & \"lõige\"", "FloorPlan");
        table.Total = unplacedViews;
        views.Finish(rules);

        var warnings = new AuditSection { Name = AuditSections.Warnings, Title = AuditSections.TitleOf(AuditSections.Warnings) };
        warnings.Count("warnings.total", "Hoiatusi kokku", 4);
        var groups = warnings.Table("warningGroups", "Hoiatused kirjelduse kaupa", ("description", "Kirjeldus"), ("count", "Arv"));
        groups.Add(null, "Elements have duplicate 'Mark' values", 4);
        groups.Total = 1;
        warnings.Finish(rules);

        var worksets = new AuditSection { Name = AuditSections.Worksets, Title = AuditSections.TitleOf(AuditSections.Worksets), Sampled = true };
        worksets.Notes.Add("Elementide omandus on valimist.");
        worksets.Finish(rules);

        var links = new AuditSection { Name = AuditSections.Links, Title = "Lingid", Error = "InvalidOperationException: link table" };
        links.Finish(rules);

        return new AuditReport
        {
            DocumentTitle = "Kool_EN",
            RevitVersion = "2026",
            GeneratedAt = new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero),
            DurationMs = 4200,
            Sections = { warnings, views, worksets, links },
            ThresholdSources = { "ettevõtte vaikeväärtused" }
        };
    }

    [Fact]
    public void Page_IsEstonianPlainHtml_WithOneTablePerList()
    {
        var html = AuditReportHtml.Build(Sample());
        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("<html lang=\"et\">", html);
        Assert.Contains("<title>Mudeli tervisekontroll: Kool_EN</title>", html);
        // overview + unplaced views + warning groups
        Assert.Equal(3, Regex.Matches(html, "<table>").Count);
        Assert.Contains("<section id=\"views\">", html);
        Assert.Contains("<span class=\"warn\">tähelepanu</span>", html);
        Assert.Contains("Kontroll ebaõnnestus:", html);       // the failed section is shown, not dropped
        Assert.Contains("valimist", html);                     // sampled sections say so
        Assert.DoesNotContain("<script", html);
        Assert.DoesNotContain("<style", html);
    }

    [Fact]
    public void ElementRows_CarryDataRevitId_OtherRowsDoNot()
    {
        var html = AuditReportHtml.Build(Sample());
        Assert.Equal(3, Regex.Matches(html, "<tr data-revit-id=\"100[0-2]\">").Count);
        Assert.Contains("<tr><td>Elements have duplicate &#39;Mark&#39; values</td>", html);
    }

    [Fact]
    public void Text_IsEscaped_AndKeepsEstonianLetters()
    {
        var html = AuditReportHtml.Build(Sample());
        Assert.Contains("Plaan &lt;0&gt; &amp; &quot;lõige&quot;", html);
        Assert.DoesNotContain("&#245;", html);
    }

    [Fact]
    public void Page_HasNoExternalUrls()
    {
        var html = AuditReportHtml.Build(Sample());
        Assert.DoesNotMatch(@"(?i)(href|src)\s*=\s*""(https?:)?//", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void LargeLists_AreShortenedToFitTheSizeCap()
    {
        var report = Sample(unplacedViews: 20_000);
        var full = AuditReportHtml.Build(report, AuditReportHtml.DefaultMaxRows, shortened: false);
        Assert.Contains("Näidatud 500 rida 20000-st.", full);

        const int cap = 40_000;
        var html = AuditReportHtml.Build(report, cap);
        Assert.True(Encoding.UTF8.GetByteCount(html) <= cap, $"{Encoding.UTF8.GetByteCount(html)} bytes");
        Assert.Contains("Loetelud on lühendatud", html);
        Assert.True(Encoding.UTF8.GetByteCount(AuditReportHtml.Build(report)) <= ModelPageStore.MaxHtmlBytes);
    }

    [Fact]
    public void PageId_IsAValidModelPageId_PerModel()
    {
        var id = AuditReportHtml.PageId("3F2A-9C:Kool");
        Assert.True(ModelPageStore.IsValidId(id), id);
        Assert.StartsWith(AuditReportHtml.PageIdPrefix, id);
        Assert.Equal(id, AuditReportHtml.PageId("3F2A-9C:Kool"));
        Assert.True(ModelPageStore.IsValidId(AuditReportHtml.PageId(new string('a', 200))));
    }

    [Fact]
    public void Json_ListsOnlyTheRequestedItemsPerSection_ThePageKeepsEveryRow()
    {
        var report = Sample(unplacedViews: 250);
        var json = JObject.FromObject(AuditJson.Describe(report, null, null, AuditJson.DefaultItemsPerSection));
        Assert.Equal(AuditJson.DefaultItemsPerSection, (int)json["itemsPerSection"]!);
        var views = ((JArray)json["sections"]!).Single(s => (string?)s["name"] == "views");
        Assert.Equal(AuditJson.DefaultItemsPerSection, ((JArray)views["items"]!).Count);
        Assert.True((bool?)views["itemsTruncated"]);
        Assert.Equal(250, (int)views["itemsTotal"]!);

        Assert.Equal(0, AuditJson.ClampItems(-5));
        Assert.Equal(AuditJson.MaxItemsPerSection, AuditJson.ClampItems(5000));
    }

    [Fact]
    public void Json_MirrorsThePage()
    {
        var report = Sample(unplacedViews: 250);
        var json = JObject.FromObject(AuditJson.Describe(report, "http://127.0.0.1:1/p/audit-x", "audit-x"));
        Assert.Equal("audit-x", (string?)json["pageId"]);
        var sections = (JArray)json["sections"]!;
        Assert.Equal(report.Sections.Select(s => s.Name), sections.Select(s => (string?)s["name"]));

        var views = sections.Single(s => (string?)s["name"] == "views");
        Assert.Equal("warn", (string?)views["status"]);
        Assert.Equal(30, (double)views["counts"]!["views.unplacedPercent"]!);
        Assert.Equal(AuditJson.MaxItemsPerSection, ((JArray)views["items"]!).Count);
        Assert.True((bool?)views["itemsTruncated"]);
        Assert.Equal(1000, (long)views["items"]![0]!["id"]!);
        Assert.Equal("unplacedViews", (string?)views["items"]![0]!["list"]);
        Assert.Equal("views.unplacedPercent", (string?)views["flagged"]![0]!["key"]);

        var worksets = sections.Single(s => (string?)s["name"] == "worksets");
        Assert.True((bool?)worksets["sampled"]);
        var links = sections.Single(s => (string?)s["name"] == "links");
        Assert.Equal("error", (string?)links["status"]);
        Assert.NotNull((string?)links["error"]);
    }
}

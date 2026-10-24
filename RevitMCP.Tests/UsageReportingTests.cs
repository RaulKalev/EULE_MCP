using System.IO;
using ClosedXML.Excel;
using Newtonsoft.Json;
using RevitMCP.Addin.Usage;
using RevitMCP.Core.Logging;
using Xunit;

namespace RevitMCP.Tests;

public sealed class UsageReportingTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "usage-" + Guid.NewGuid().ToString("N"));

    public UsageReportingTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static string Line(string user, string tool, DateTimeOffset at, bool success = true, string permission = "ReadOnly",
        string approval = "NotRequired", long? affected = null, string? project = "1626", long durationMs = 1000, string client = "Claude Code",
        string message = "") =>
        JsonConvert.SerializeObject(new LogEntry
        {
            Timestamp = at, User = user, Client = client, Tool = tool, Permission = permission, ApprovalStatus = approval,
            Status = success ? "Success" : "Failed", DurationMs = durationMs, ResponseSizeBytes = 4000, AffectedCount = affected,
            ProjectId = project, Model = "1626_PP_EN", Message = message
        }, Formatting.None);

    [Theory]
    [InlineData("P-77", "1626", "1626_PP_EN", "P-77")]
    [InlineData(null, "1626", "73226_PP_EN", "1626")]
    [InlineData(null, " ", "73226_PP_EN.rvt", "73226")]
    [InlineData(null, null, "Kooli hoone", "Kooli hoone")]
    [InlineData(null, null, "", null)]
    public void ProjectId_ConfigThenNumberThenModelName(string? config, string? number, string model, string? expected) =>
        Assert.Equal(expected, UsageProjectId.Resolve(config, number, model));

    [Fact]
    public void ProjectId_ReadsTheProjectConfigAboveTheModel()
    {
        var models = Path.Combine(_root, "P", "EN");
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(Path.Combine(_root, "P", ".rktools"));
        File.WriteAllText(Path.Combine(_root, "P", ".rktools", "mcp.project.config.json"), """{ "projectId": "EULE-1626" }""");
        Assert.Equal("EULE-1626", UsageProjectId.FromProjectConfig(Path.Combine(models, "m.rvt")));
        Assert.Null(UsageProjectId.FromProjectConfig("Autodesk Docs://x/m.rvt"));
    }

    [Fact]
    public void Settings_CompanyBaselinesWithUserOverrides()
    {
        var company = System.Text.Json.Nodes.JsonNode.Parse("""
            { "usage": { "sharedFolder": "S:\\usage", "timeSavedBaselines": {
                "revit_place_tags": { "secondsPerElement": 20 }, "revit_run_clash_preset": { "secondsPerCall": 900 } } } }
            """)!.AsObject();
        var user = System.Text.Json.Nodes.JsonNode.Parse("""{ "usage": { "timeSavedBaselines": { "revit_place_tags": { "secondsPerElement": 25 } } } }""")!.AsObject();

        var s = UsageSettings.From(user, company);

        Assert.Equal("S:\\usage", s.SharedFolder);
        Assert.Equal(25, s.Baselines["revit_place_tags"].SecondsPerElement);
        Assert.Equal(900, s.Baselines["revit_run_clash_preset"].SecondsPerCall);
        Assert.EndsWith("reports", s.ReportFolder);
        Assert.EndsWith("costs", s.CostExportFolder);
        Assert.Null(UsageSettings.From(null, null).SharedFolder);
    }

    [Fact]
    public void Share_AppendsPerUserDayMachine_AndCatchesUpADayThatFailed()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        var share = new UsageShare(logs);
        var day = new DateTime(2026, 10, 23);
        File.WriteAllText(Path.Combine(logs, "2026-10-23.jsonl"), "a\nb\n");

        // The share is a file, not a folder: the append fails and the day is remembered.
        var blocked = Path.Combine(_root, "blocked");
        File.WriteAllText(blocked, "not a folder");
        Assert.False(share.Append(blocked, "raul", "WS-1", day, "b\n"));
        Assert.Equal(new[] { "2026-10-23" }, new UsageShare(logs).PendingDays());

        // The next successful append copies the whole local day over the shared file.
        var shared = Path.Combine(_root, "shared");
        Assert.True(share.Append(shared, "raul", "WS-1", day, "c\n"));
        var path = UsageShare.SharedPath(shared, "raul", "WS-1", day);
        Assert.EndsWith(Path.Combine("raul", "2026-10-23.WS-1.jsonl"), path);
        Assert.Equal("a\nb\n", File.ReadAllText(path));
        Assert.Empty(share.PendingDays());
        Assert.Equal("domain_user", UsageShare.Segment("domain\\user", "x"));
    }

    [Fact]
    public void Aggregate_CountsOutcomesSessionsAffectedAndTimeSaved()
    {
        var t0 = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        var lines = new[]
        {
            Line("raul", "revit_graph_route", t0),
            Line("raul", "revit_place_tags", t0.AddMinutes(1), success: false, permission: "RequiresApproval", approval: "Pending"),
            Line("raul", "revit_place_tags", t0.AddMinutes(2), permission: "RequiresApproval", approval: "Approved", affected: 120, durationMs: 4000),
            Line("raul", "revit_set_elevation", t0.AddMinutes(3), success: false, permission: "RequiresApproval", approval: "Rejected"),
            Line("raul", "revit_set_elevation", t0.AddMinutes(4), success: false, permission: "RequiresApproval", approval: "Expired"),
            Line("raul", "revit_get_elements_info", t0.AddHours(2), success: false),
            Line("mari", "revit_place_tags", t0.AddDays(1), permission: "RequiresApproval", approval: "Approved", affected: 10, project: "1700", client: "Codex"),
            // An older line without approvalStatus detail: the message gives it away.
            Line("mari", "revit_place_tags", t0.AddDays(1).AddMinutes(1), success: false, message: "'Place 3 tags' is pending approval in Revit. Open …"),
            "{ not json",
            Line("raul", "system", t0)
        };
        var records = UsageAggregator.Parse(lines, out var unreadable);
        Assert.Equal(1, unreadable);
        Assert.Equal(8, records.Count);

        var baselines = new Dictionary<string, TimeSavedBaseline>(StringComparer.OrdinalIgnoreCase)
        {
            ["revit_place_tags"] = new() { SecondsPerElement = 20 },
            ["revit_graph_route"] = new() { SecondsPerCall = 0.5 }
        };
        var data = UsageAggregator.Aggregate("2026-09", records, baselines, new UsageCosts(), t0.AddDays(40));

        var t = data.Total;
        Assert.Equal(6, t.Calls);                 // placeholders are not calls
        Assert.Equal(2, t.ApprovalsRequested);
        Assert.Equal(3, t.Successes);
        Assert.Equal(1, t.Failures);
        Assert.Equal(1, t.Rejected);
        Assert.Equal(1, t.Expired);
        Assert.Equal(0.75, t.SuccessRate);
        Assert.Equal(4, t.Writes);
        Assert.Equal(2, t.Approved);
        Assert.Equal(130, t.Affected);
        // 120 × 20 − 4 s and 10 × 20 − 1 s; the route's 0.5 s baseline is less than its own second, floored at zero.
        Assert.Equal(2396 + 199, t.SavedSeconds, 3);

        var raul = data.ByUser.Single(g => g.Key == "raul");
        Assert.Equal(2, raul.Sessions);           // the 2-hour gap starts a new session
        Assert.Equal(1, raul.ActiveDays);
        Assert.Equal(new[] { "1626", "1700" }, data.ByProject.Select(g => g.Key).OrderBy(k => k));
        Assert.Equal(2, data.ByClient.Count);
        Assert.Contains(data.ByOutcome, g => g.Key == UsageOutcomes.Rejected);
        Assert.Contains(data.ByPermission, g => g.Key == "write" && g.Writes == 4);
    }

    [Fact]
    public void Costs_ParseSemicolonsAndJoinByUserOrEmail()
    {
        var problems = new List<string>();
        var rows = UsageCosts.Parse(new[] { "User;Client;Cost;InputTokens;OutputTokens", "raul@eule.ee;Claude Code;12,50;100;20", "mari;Codex;x;1;1" }, problems);
        Assert.Single(rows);
        Assert.Single(problems);
        var costs = new UsageCosts { Rows = rows };
        Assert.Equal(12.50m, costs.For("Raul").Single().Cost);
        Assert.Empty(costs.For("mari"));
    }

    [Fact]
    public void Runner_WritesHtmlAndExcel_ForTheMonth()
    {
        var shared = Path.Combine(_root, "shared");
        var t0 = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero);
        Directory.CreateDirectory(Path.Combine(shared, "raul"));
        File.WriteAllLines(Path.Combine(shared, "raul", "2026-09-03.WS-1.jsonl"), new[]
        {
            Line("raul", "revit_place_tags", t0, permission: "RequiresApproval", approval: "Approved", affected: 5),
            Line("raul", "revit_get_elements_info", t0.AddMinutes(1))
        });
        File.WriteAllLines(Path.Combine(shared, "raul", "2026-10-01.WS-1.jsonl"), new[] { Line("raul", "revit_count_elements", t0.AddMonths(1)) });
        Directory.CreateDirectory(Path.Combine(shared, "costs"));
        File.WriteAllText(Path.Combine(shared, "costs", "2026-09.csv"), "user,client,cost,inputTokens,outputTokens\nraul,Claude Code,3.20,1000,200\n");
        var settings = new UsageSettings { SharedFolder = shared };
        settings.Baselines["revit_place_tags"] = new TimeSavedBaseline { SecondsPerElement = 20 };

        var result = UsageReportRunner.Run(settings, "2026-09", null, "both", t0.AddMonths(1), "body{}");

        Assert.True(result.Success, result.Message);
        Assert.Equal(2, result.Data!.Total.Calls);
        var html = File.ReadAllText(result.HtmlPath!);
        Assert.Contains("<style>body{}</style>", html);
        Assert.Contains("data-revit-tools=\"rows\"", html);
        Assert.Contains("Time saved (estimate)", html);
        Assert.Contains("revit_place_tags", html);
        Assert.Contains("3.20", html);
        using (var wb = new XLWorkbook(result.ExcelPath!))
        {
            Assert.True(wb.Worksheets.Contains("Users"));
            Assert.Equal("raul", wb.Worksheet("Users").Cell(2, 1).GetString());
        }

        Assert.Equal("2026-09", UsageReportRunner.Month(null, new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.False(UsageReportRunner.Run(settings, "Sept", null, "both", t0, null).Success);
        Assert.False(UsageReportRunner.Run(new UsageSettings(), "2026-09", null, "both", t0, null).Success);
    }

    [Fact]
    public void Html_EscapesNames()
    {
        var data = UsageAggregator.Aggregate("2026-09",
            UsageAggregator.Parse(new[] { Line("<script>", "revit_count_elements", DateTimeOffset.UtcNow) }, out _),
            new Dictionary<string, TimeSavedBaseline>(), new UsageCosts(), DateTimeOffset.UtcNow);
        var html = UsageReportWriter.Html(data);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}

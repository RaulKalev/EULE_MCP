using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Jobs;
using RevitMCP.Addin.Plans;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

internal static class JobFixtures
{
    public static readonly Dictionary<string, ToolPermission> Permissions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["revit_graph_query"] = ToolPermission.ReadOnly,
        ["revit_get_elements_info"] = ToolPermission.ReadOnly,
        ["revit_set_parameter"] = ToolPermission.RequiresApproval,
        ["revit_move_elements"] = ToolPermission.RequiresApproval,
        ["revit_place_tags"] = ToolPermission.RequiresApproval,
        ["revit_delete"] = ToolPermission.DestructiveRequiresManualApproval,
        ["revit_job_submit"] = ToolPermission.RequiresApproval
    };

    public static JobDefinition Parse(object args)
    {
        var dict = JObject.FromObject(args).Properties().ToDictionary(p => p.Name, p => (object?)p.Value);
        return JobDefinitionParser.Parse(dict, n => Permissions.TryGetValue(n, out var p) ? p : null, _ => false);
    }

    public static object Steps3 => new object[]
    {
        new { tool = "revit_graph_query", arguments = new { op = "find", category = "Electrical Fixtures" }, bind = new { ids = "$.data.items[*].id" } },
        new { tool = "revit_set_parameter", title = "Set Mark", arguments = new { elementIds = "{{ids}}", parameterName = "Mark", value = "S-{{first}}" } },
        new { tool = "revit_get_elements_info", arguments = new { elementIds = "{{ids}}" } }
    };
}

public class JobJsonPathTests
{
    private static readonly JObject Result = JObject.Parse(@"{
        ""success"": true,
        ""data"": {
            ""items"": [ { ""id"": 1, ""name"": ""A"" }, { ""id"": 2, ""name"": ""B"" }, { ""id"": 3, ""name"": ""C"" } ],
            ""moves"": [ { ""elementId"": 10, ""status"": ""Moved"" }, { ""elementId"": 11, ""status"": ""Skipped"" }, { ""elementId"": 12, ""status"": ""Moved"", ""count"": 2 } ],
            ""ids"": [""7"", ""8""],
            ""count"": 3
        }
    }");

    [Theory]
    [InlineData("$.data.items[*].id", "[1,2,3]")]
    [InlineData("$.data.items[:2].id", "[1,2]")]
    [InlineData("$.data.items[1].name", "\"B\"")]
    [InlineData("$.DATA.Count", "3")]
    [InlineData("$.data.moves[?(@.status=='Moved')].elementId", "[10,12]")]
    [InlineData("$.data.moves[?(@.count==2)].elementId", "[12]")]
    [InlineData("$.data.missing", null)]
    [InlineData("$.data.moves[?(@.status=='Gone')].elementId", null)]
    public void SelectsTheSupportedForms(string path, string? expected)
    {
        var value = JobJsonPath.Select(Result, path);
        Assert.Equal(expected, value?.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Theory]
    [InlineData("$.data.items[*].id", true)]
    [InlineData("$..id", false)]
    [InlineData("data.items", false)]
    [InlineData("$.data.items[?(@.id>1)]", false)]
    public void ParsesOnlyTheSubset(string path, bool ok) => Assert.Equal(ok, JobJsonPath.TryParse(path, out _));

    [Fact]
    public void WholePlaceholderKeepsTheArrayAndInlineBecomesText()
    {
        var args = JObject.Parse(@"{ ""elementIds"": ""{{ids}}"", ""value"": ""S-{{n}}"", ""nested"": { ""moves"": [ { ""elementId"": ""{{first}}"" } ] } }");
        var vars = new Dictionary<string, JToken> { ["ids"] = new JArray(1, 2), ["n"] = new JValue(5), ["first"] = new JValue(1L) };

        var filled = JobJsonPath.Substitute(args, vars, out var missing);

        Assert.Null(missing);
        Assert.Equal(JTokenType.Array, filled!["elementIds"]!.Type);
        Assert.Equal("S-5", (string?)filled["value"]);
        Assert.Equal(1L, (long)filled["nested"]!["moves"]![0]!["elementId"]!);
        Assert.Equal("{{ids}}", (string?)args["elementIds"]);   // the template is not changed
    }

    [Fact]
    public void MissingVariableIsReported()
    {
        Assert.Null(JobJsonPath.Substitute(JObject.Parse(@"{ ""a"": ""{{nope}}"" }"), new Dictionary<string, JToken>(), out var missing));
        Assert.Equal("nope", missing);
    }

    [Fact]
    public void StringIdsBecomeNumbers()
        => Assert.Equal("[7,8]", JobJsonPath.AsIds(JobJsonPath.Select(Result, "$.data.ids")!).ToString(Newtonsoft.Json.Formatting.None));
}

public class JobDefinitionParserTests
{
    [Fact]
    public void ValidJobGetsDefaultsAndAnEnvelopeFromItsWriteSteps()
    {
        var steps = (object[])JobFixtures.Steps3;
        steps[0] = new { tool = "revit_graph_query", arguments = new { op = "find" }, bind = new { ids = "$.data.items[*].id", first = "$.data.items[0].id" } };
        var def = JobFixtures.Parse(new { title = "Mark sockets", steps });

        Assert.True(def.IsValid, string.Join("; ", def.Errors));
        Assert.Equal(3, def.Job.Steps.Count);
        Assert.True(def.Job.Steps[0].IsReadOnly);
        Assert.False(def.Job.Steps[1].IsReadOnly);
        Assert.Equal(new[] { "revit_set_parameter" }, def.Plan.Envelope.Tools);
        Assert.Equal(120, def.Plan.Envelope.ExpiresMinutes);
        Assert.Equal("error", def.Job.Stop.StopOnNewWarnings);
        Assert.True(JobIds.IsValid(def.Job.Id));
        Assert.Equal(new[] { "revit_graph_query", "Set Mark", "revit_get_elements_info" }, def.Job.PlanSteps);
    }

    [Fact]
    public void UnknownToolDestructiveStepAndNestedJobsAreRefused()
    {
        var def = JobFixtures.Parse(new
        {
            title = "x",
            steps = new object[]
            {
                new { tool = "revit_nope" },
                new { tool = "revit_delete", arguments = new { elementIds = new[] { 1 } } },
                new { tool = "revit_job_submit" }
            }
        });

        Assert.Contains(def.Errors, e => e.Contains("unknown tool 'revit_nope'"));
        Assert.Contains(def.Errors, e => e.Contains("revit_delete is destructive"));
        Assert.Contains(def.Errors, e => e.Contains("cannot run inside a job"));
    }

    [Fact]
    public void DestructiveStepIsAllowedWithTheFlagButNotInThePlan()
    {
        var def = JobFixtures.Parse(new
        {
            title = "x",
            allowDestructiveSteps = true,
            steps = new object[] { new { tool = "revit_set_parameter", arguments = new { elementIds = new[] { 1 } } }, new { tool = "revit_delete", arguments = new { elementIds = new[] { 1 } } } }
        });

        Assert.True(def.IsValid, string.Join("; ", def.Errors));
        Assert.DoesNotContain("revit_delete", def.Plan.Envelope.Tools);
    }

    [Fact]
    public void PlaceholderMustBeBoundByAnEarlierStepAndPathsMustParse()
    {
        var def = JobFixtures.Parse(new
        {
            title = "x",
            steps = new object[]
            {
                new { tool = "revit_set_parameter", arguments = new { elementIds = "{{ids}}" } },
                new { tool = "revit_graph_query", bind = new { ids = "$..id" } }
            }
        });

        Assert.Contains(def.Errors, e => e.Contains("{{ids}} is not bound by an earlier step"));
        Assert.Contains(def.Errors, e => e.Contains("bind 'ids'"));
    }

    [Fact]
    public void TooManyStepsAndMissingTitle()
    {
        var many = Enumerable.Range(0, JobDefinitionParser.MaxSteps + 1).Select(_ => new { tool = "revit_graph_query" }).ToArray();
        Assert.Contains(JobFixtures.Parse(new { title = "x", steps = many }).Errors, e => e.Contains("at most"));
        Assert.Contains(JobFixtures.Parse(new { title = "", steps = new[] { new { tool = "revit_graph_query" } } }).Errors, e => e.Contains("title"));
    }

    [Fact]
    public void StopConditionsAreParsedAndClamped()
    {
        var def = JobFixtures.Parse(new
        {
            title = "x",
            steps = new[] { new { tool = "revit_set_parameter" } },
            stop = new { stopOnFailure = false, stopOnNewWarnings = "any", maxNewWarnings = 3, maxDurationMinutes = 9999, maxConsecutiveBusy = 0 }
        });

        Assert.False(def.Job.Stop.StopOnFailure);
        Assert.Equal("any", def.Job.Stop.StopOnNewWarnings);
        Assert.Equal(3, def.Job.Stop.MaxNewWarnings);
        Assert.Equal(PlanEnvelope.MaxExpiresMinutes, def.Job.Stop.MaxDurationMinutes);
        Assert.Equal(1, def.Job.Stop.MaxConsecutiveBusy);
    }

    [Fact]
    public void StepOutsideAnExplicitEnvelopeIsWarned()
    {
        var def = JobFixtures.Parse(new
        {
            title = "x",
            envelope = new { tools = new[] { "revit_set_parameter" } },
            steps = new[] { new { tool = "revit_set_parameter" }, new { tool = "revit_move_elements" } }
        });

        Assert.True(def.IsValid);
        Assert.Contains(def.Warnings, w => w.Contains("revit_move_elements is not in envelope.tools"));
    }
}

public class JobStateMachineTests
{
    private static Job JobWith(int steps, Action<JobStopConditions>? stop = null)
    {
        var job = new Job { Id = JobIds.NewId(), Title = "t" };
        for (var i = 0; i < steps; i++) job.Steps.Add(new JobStep { Index = i, Tool = "revit_set_parameter" });
        stop?.Invoke(job.Stop);
        return job;
    }

    [Fact]
    public void FailureStopsUnlessTolerated()
    {
        var job = JobWith(2);
        job.Steps[0].Message = "boom";
        var stop = JobStateMachine.AfterStep(job, job.Steps[0], StepOutcome.Failed);
        Assert.Equal(JobState.Failed, stop.Stop);
        Assert.Contains("boom", stop.Reason);

        job = JobWith(2);
        job.Steps[0].ContinueOnError = true;
        var next = JobStateMachine.AfterStep(job, job.Steps[0], StepOutcome.Failed);
        Assert.True(next.Continue);
        Assert.True(job.Steps[0].IsFinished);
        Assert.Same(job.Steps[1], job.NextStep);
    }

    [Fact]
    public void OptionalRefusalIsSkippedAndLastStepCompletes()
    {
        var job = JobWith(1);
        job.Steps[0].Optional = true;
        Assert.Equal(StepOutcome.Skipped, JobStateMachine.Classify(job.Steps[0], false, "validation_failed"));
        Assert.Equal(StepOutcome.Failed, JobStateMachine.Classify(new JobStep(), false, "validation_failed"));
        Assert.Equal(StepOutcome.WaitingApproval, JobStateMachine.Classify(new JobStep(), false, "approval_required"));
        Assert.Equal(StepOutcome.Busy, JobStateMachine.Classify(new JobStep(), false, "revit_busy"));

        Assert.Equal(JobState.Completed, JobStateMachine.AfterStep(job, job.Steps[0], StepOutcome.Skipped).Stop);
    }

    [Fact]
    public void CancelAndPauseAreHonouredAfterTheRunningStep()
    {
        var job = JobWith(3);
        job.PendingControl = "cancel";
        Assert.Equal(JobState.Cancelled, JobStateMachine.AfterStep(job, job.Steps[0], StepOutcome.Succeeded).Stop);
        job.PendingControl = "pause";
        Assert.Equal(JobState.Paused, JobStateMachine.AfterStep(job, job.Steps[1], StepOutcome.Succeeded).Stop);
    }

    [Fact]
    public void NewErrorWarningPausesAndResumeAcceptsIt()
    {
        var job = JobWith(3);
        Assert.Null(JobStateMachine.WarningStop(job, newErrors: 0, newTotal: 5, null));
        Assert.Contains("Room is not enclosed", JobStateMachine.WarningStop(job, 1, 6, "Room is not enclosed"));

        job.NewErrors = 1;
        job.NewWarnings = 6;
        JobStateMachine.AcceptWarnings(job);
        Assert.Null(JobStateMachine.WarningStop(job, 1, 6, null));
        Assert.NotNull(JobStateMachine.WarningStop(job, 2, 7, null));
    }

    [Fact]
    public void AnyModeWithTolerance()
    {
        var job = JobWith(1, s => { s.StopOnNewWarnings = "any"; s.MaxNewWarnings = 2; });
        Assert.Null(JobStateMachine.WarningStop(job, 0, 2, null));
        Assert.Contains("warning", JobStateMachine.WarningStop(job, 0, 3, null));
        Assert.Null(JobStateMachine.WarningStop(JobWith(1, s => s.StopOnNewWarnings = "none"), 9, 9, null));
    }

    [Fact]
    public void BusyBackoffAndDuration()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), JobStateMachine.BusyDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(20), JobStateMachine.BusyDelay(3));
        Assert.Equal(TimeSpan.FromSeconds(60), JobStateMachine.BusyDelay(10));
        var job = JobWith(1, s => { s.MaxConsecutiveBusy = 3; s.MaxDurationMinutes = 1; });
        Assert.False(JobStateMachine.BusyExhausted(job, 2));
        Assert.True(JobStateMachine.BusyExhausted(job, 3));
        Assert.True(JobStateMachine.DurationExceeded(job, 60_000));
        Assert.False(JobStateMachine.DurationExceeded(job, 59_999));
    }
}

public class JobJournalStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rmcp-jobs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    private static Job New(JobState state, string model = "abc123", DateTimeOffset? at = null)
    {
        var job = new Job { Id = JobIds.NewId(), Title = "t", ModelId = model, State = state, CreatedAt = at ?? DateTimeOffset.Now, PlanId = "plan-000001" };
        job.Steps.Add(new JobStep { Index = 0, Tool = "revit_graph_query", State = JobStepState.Succeeded, Bind = new() { ["ids"] = "$.data.items[*].id" } });
        job.Steps.Add(new JobStep { Index = 1, Tool = "revit_set_parameter", State = JobStepState.Running, Arguments = JObject.Parse(@"{""elementIds"":""{{ids}}""}") });
        job.Variables["ids"] = new JArray(1, 2, 3);
        return job;
    }

    [Fact]
    public void RoundTripMarksUnfinishedJobsSuspended()
    {
        var store = new JobJournalStore(_folder);
        var running = New(JobState.Running);
        var done = New(JobState.Completed);
        store.Save(running);
        store.Save(done);

        var loaded = new JobJournalStore(_folder).LoadAll();

        var r = loaded.Single(j => j.Id == running.Id);
        Assert.Equal(JobState.Suspended, r.State);
        Assert.Equal(JobJournalStore.RestartReason, r.StopReason);
        Assert.Null(r.PlanId);
        Assert.Equal(JobStepState.Pending, r.Steps[1].State);
        Assert.Same(r.Steps[1], r.NextStep);
        Assert.Equal("[1,2,3]", r.Variables["ids"].ToString(Newtonsoft.Json.Formatting.None));
        Assert.Equal("{{ids}}", (string?)r.Steps[1].Arguments["elementIds"]);
        Assert.Equal(JobState.Completed, loaded.Single(j => j.Id == done.Id).State);
    }

    [Fact]
    public void TrimReplacesLargeData()
    {
        var big = new JObject { ["success"] = true, ["message"] = "m", ["data"] = new JObject { ["text"] = new string('x', JobJournalStore.MaxResultBytes + 10) } };
        var trimmed = JobJournalStore.Trim(big);
        Assert.True((bool)trimmed["data"]!["truncated"]!);
        Assert.Equal("m", (string?)trimmed["message"]);
        var small = new JObject { ["data"] = new JObject { ["n"] = 1 } };
        Assert.Same(small, JobJournalStore.Trim(small));
    }

    [Fact]
    public void EvictsTheOldestFinishedJournals()
    {
        var store = new JobJournalStore(_folder);
        var start = DateTimeOffset.Now.AddDays(-1);
        var suspended = New(JobState.Suspended, at: start);
        store.Save(suspended);
        for (var i = 0; i < JobJournalStore.MaxPerModel + 5; i++)
        {
            var job = New(JobState.Completed, at: start.AddMinutes(i + 1));
            job.FinishedAt = job.CreatedAt;
            store.Save(job);
        }

        var all = store.LoadAll();

        Assert.Equal(JobJournalStore.MaxPerModel, all.Count);
        Assert.Contains(all, j => j.Id == suspended.Id);   // unfinished journals are never evicted
    }
}

public class JobReportHtmlTests
{
    [Fact]
    public void ReportIsASelfContainedModelPage()
    {
        var job = new Job { Id = "job-0a0b0c", Title = "Mark <sockets>", DocumentTitle = "P125", State = JobState.Completed, ElapsedMs = 90_000 };
        job.Steps.Add(new JobStep { Index = 0, Tool = "revit_set_parameter", State = JobStepState.Succeeded, Message = "ok" });
        job.Steps.Add(new JobStep { Index = 1, Tool = "revit_place_tags", State = JobStepState.Failed, Tolerated = true, Message = "no view" });

        var cp = new Checkpoint { Id = "cp-0a0b0c", ModelId = "m", Title = "Plan" };
        cp.Snapshot[1] = new ElementSnapshot { Category = "Electrical Fixtures", Name = "Socket", Parameters = { ["Mark"] = "A" }, Raw = { ["Mark"] = "S:A" } };
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_set_parameter", Operation = "TransactionCommitted", TransactionNames = { "Revit MCP - Set Parameter" }, Modified = new[] { 1L }, Added = new[] { 50L } });
        cp.Identities[50] = new ElementIdentity { Category = "Electrical Fixture Tags" };
        var diff = CheckpointDiffBuilder.Build(cp, SnapshotDiff.CompareAll(cp.Snapshot, _ => new ElementSnapshot { Parameters = { ["Mark"] = "S-1" } }));
        var warnings = WarningDiff.Compare(Array.Empty<WarningKey>(), new[] { new WarningKey { DefinitionId = "d", Description = "Overlap", Severity = "Error", ElementIds = new[] { 1L, 2L } } });

        var html = JobReportHtml.Build(job, cp, diff, warnings, null, DateTimeOffset.Now);

        Assert.StartsWith("<!doctype html>\n<html lang=\"et\">", html);
        Assert.Contains("Mark &lt;sockets&gt;", html);
        Assert.Contains("data-revit-id=\"1\"", html);
        Assert.Contains("data-revit-id=\"50\"", html);
        Assert.Contains("data-revit-param=\"Mark\">S-1</td>", html);
        Assert.Contains("data-revit-id=\"1,2\"", html);
        Assert.Contains("Revit MCP - Set Parameter", html);
        Assert.Contains("ebaõnnestus (jätkati)", html);
        Assert.DoesNotContain("http", html);
        // Every table has exactly one body, so the page's table tools apply.
        Assert.Equal(Regex.Matches(html, "<table").Count, Regex.Matches(html, "<tbody>").Count);
    }

    [Fact]
    public void LargeReportsStayUnderThePageCap()
    {
        var job = new Job { Id = "job-0a0b0d", Title = "big", State = JobState.Completed };
        var cp = new Checkpoint { Id = "cp-0a0b0d", ModelId = "m", Title = "Plan" };
        var ids = Enumerable.Range(1, 5000).Select(i => (long)i).ToArray();
        cp.Batches.Add(new ChangeBatch { Attribution = "agent:revit_place_tags", Added = ids, Operation = "TransactionCommitted" });
        foreach (var id in ids) cp.Identities[id] = new ElementIdentity { Category = "Electrical Fixture Tags", Name = "Tag " + id, TypeName = "Standard", Level = "L2" };
        var diff = CheckpointDiffBuilder.Build(cp, new Dictionary<long, ElementDelta>());

        var html = JobReportHtml.Build(job, cp, diff, null, null, DateTimeOffset.Now);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(html) < RevitMCP.Addin.ModelPages.ModelPageStore.MaxHtmlBytes);
        Assert.Equal(JobReportHtml.MaxRows, Regex.Matches(html, "data-revit-id=").Count);
    }
}

public class JobSummaryTests
{
    [Fact]
    public void SummaryNamesStepsStopConditionsAndThePlan()
    {
        var steps = (object[])JobFixtures.Steps3;
        steps[0] = new { tool = "revit_graph_query", bind = new { ids = "$.data.items[*].id", first = "$.data.items[0].id" } };
        var def = JobFixtures.Parse(new { title = "Mark sockets", steps });

        var text = JobSummary.Build(def, "P125");

        Assert.StartsWith("JOB: Mark sockets — 3 steps, 1 of them write. Stops on the first failure, the first new Revit error or after 120 min.", text);
        Assert.Contains("2. Set Mark (revit_set_parameter)", text);
        Assert.Contains("PLAN: Job: Mark sockets", text);
        Assert.Contains("may not delete", text);
    }
}

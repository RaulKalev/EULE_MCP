using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Jobs;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>H14: the job report covers the job's own batches, not everything recorded on its checkpoint since.</summary>
public class JobChangeScopeTests
{
    private const string JobId = "job-0a0b0c";

    private static ElementSnapshot Mark(string value)
        => new() { Category = "Electrical Fixtures", Name = "Socket", Parameters = { ["Mark"] = value }, Raw = { ["Mark"] = "S:" + value } };

    /// <summary>
    /// The job sets Mark on 1 and 2; meanwhile the user edits 3; afterwards export_view_image touches view 9 and a
    /// rollback from another session puts 2 back.
    /// </summary>
    private static Checkpoint Recorded()
    {
        var cp = new Checkpoint { Id = "cp-0a0b0c", ModelId = "m", Title = "Plan" };
        cp.Snapshot[1] = Mark("A");
        cp.Snapshot[2] = Mark("A");
        cp.Snapshot[3] = Mark("A");
        cp.Snapshot[9] = new ElementSnapshot { Category = "Views", Parameters = { ["Title on Sheet"] = "" } };
        var job = ChangeAttribution.Agent("revit_set_parameter", JobIds.ClientFor(JobId));
        cp.Batches.Add(new ChangeBatch { Attribution = job, Operation = "TransactionCommitted", TransactionNames = { "Revit MCP - Set Parameter" }, Modified = new[] { 1L, 2L } });
        cp.Batches.Add(new ChangeBatch { Attribution = ChangeAttribution.User, Operation = "TransactionCommitted", TransactionNames = { "Modify" }, Modified = new[] { 3L } });
        cp.Batches.Add(new ChangeBatch { Attribution = ChangeAttribution.Agent("revit_export_view_image", "Claude"), Operation = "TransactionCommitted", TransactionNames = { "Export" }, Modified = new[] { 9L } });
        cp.Batches.Add(new ChangeBatch { Attribution = ChangeAttribution.Agent("revit_checkpoint_rollback", "Claude"), Operation = "TransactionCommitted", TransactionNames = { "Rollback" }, Modified = new[] { 2L } });
        return cp;
    }

    private static Dictionary<long, ElementDelta> LiveDeltas(Checkpoint cp)
    {
        var now = new Dictionary<long, ElementSnapshot>
        {
            [1] = Mark("B"),
            [2] = Mark("A"),   // rolled back after the job
            [3] = Mark("U"),
            [9] = new ElementSnapshot { Parameters = { ["Title on Sheet"] = "Export" } }
        };
        return SnapshotDiff.CompareAll(cp.Snapshot, id => now[id]);
    }

    [Fact]
    public void OnlyTheJobsBatchesAndTheElementsTheyTouchedCount()
    {
        var cp = Recorded();
        var view = JobChangeScope.Of(cp, JobId);
        var diff = JobChangeScope.Diff(view, LiveDeltas(cp));

        Assert.Single(view.Batches);
        Assert.Equal(4, cp.Batches.Count);   // the stored checkpoint is not changed
        Assert.Equal(new long[] { 1, 2 }, diff.Entries.Keys.OrderBy(k => k));
        Assert.Equal("B", diff.Entries[1].Delta!.Parameters.Single().Now);
        Assert.True(CheckpointDiffReport.IsTouchedUnchanged(diff.Entries[2]));
        Assert.All(diff.Entries.Values, e => Assert.Equal(JobId, JobIds.JobIdOfClient(ChangeAttribution.AgentClient(e.By))));
    }

    [Fact]
    public void ReportListsTheJobsWritesOnlyIncludingOnesNowBackToBefore()
    {
        var cp = Recorded();
        var job = new Job { Id = JobId, Title = "Mark sockets", State = JobState.Completed };
        job.Steps.Add(new JobStep { Index = 0, Tool = "revit_set_parameter", State = JobStepState.Succeeded });
        var view = JobChangeScope.Of(cp, JobId);

        var html = JobReportHtml.Build(job, view, JobChangeScope.Diff(view, LiveDeltas(cp)), null, null, DateTimeOffset.Now);

        Assert.Contains("data-revit-id=\"1\"", html);
        Assert.Contains("data-revit-id=\"2\"", html);
        Assert.Contains("(muudetud, praegu nagu enne)", html);
        Assert.Contains("2 muudetud (neist 1 praegu nagu enne)", html);
        Assert.DoesNotContain("data-revit-id=\"3\"", html);
        Assert.DoesNotContain("data-revit-id=\"9\"", html);
        Assert.Contains("Revit MCP - Set Parameter", html);
        Assert.DoesNotContain("<li>Rollback</li>", html);
        Assert.DoesNotContain("<li>Export</li>", html);
    }

    [Fact]
    public void IncompleteTrackingKeepsEverySnapshotDelta()
    {
        var cp = Recorded();
        cp.TrackingComplete = false;
        var diff = JobChangeScope.Diff(JobChangeScope.Of(cp, JobId), LiveDeltas(cp));
        Assert.Contains(3L, diff.Entries.Keys);
        Assert.Contains(9L, diff.Entries.Keys);
    }

    [Theory]
    [InlineData("agent:revit_set_parameter (Job job-0a0b0c)", "Job job-0a0b0c")]
    [InlineData("agent:revit_set_parameter (Claude Code (VS))", "Claude Code (VS)")]
    [InlineData("agent:revit_set_parameter", null)]
    [InlineData("user", null)]
    public void AgentClient(string attribution, string? client) => Assert.Equal(client, ChangeAttribution.AgentClient(attribution));
}

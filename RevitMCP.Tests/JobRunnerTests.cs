using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Jobs;
using RevitMCP.Addin.Plans;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

/// <summary>#100: the job runner against a fake dispatcher — sequencing, bindings, stops, approvals, busy, cancel.</summary>
public class JobRunnerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rmcp-runner-" + Guid.NewGuid().ToString("N"));
    private readonly List<McpToolRequest> _sent = new();
    private readonly List<(string Id, PlanState State)> _finished = new();
    private Func<McpToolRequest, McpToolResult> _respond = r => Ok(r, null);

    public void Dispose()
    {
        try { Directory.Delete(_folder, true); } catch { }
    }

    private static McpToolResult Ok(McpToolRequest r, object? data) => new() { RequestId = r.RequestId, Success = true, Message = "ok", Data = data };

    private static ToolPermission? PermissionOf(string name)
        => JobFixtures.Permissions.TryGetValue(name, out var p) ? p
            : name is JobService.WarningsTool or JobService.ReportTool ? ToolPermission.ReadOnly : null;

    private JobService Service(ApprovalService? approvals = null)
        => new(
            (request, _) =>
            {
                lock (_sent) _sent.Add(request);
                return Task.FromResult(_respond(request));
            },
            approvals,
            new JobJournalStore(_folder),
            PermissionOf,
            _ => false,
            id => id == null ? null : new Plan { Id = id, State = PlanState.Active },
            (plan, state, _) => { lock (_finished) _finished.Add((plan.Id, state)); },
            null);

    private static Job JobFrom(object steps, object? stop = null, string report = "never")
    {
        var def = JobFixtures.Parse(new { title = "t", steps, stop, report });
        Assert.True(def.IsValid, string.Join("; ", def.Errors));
        var job = def.Job;
        job.ModelId = "m1";
        job.PlanId = "plan-0000aa";
        job.CheckpointId = "cp-0000aa";
        return job;
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not reached");
            await Task.Delay(10);
        }
    }

    private List<McpToolRequest> Sent(string tool)
    {
        lock (_sent) return _sent.Where(r => r.ToolName == tool).ToList();
    }

    private static string Arg(McpToolRequest request, string key) => ((JToken)request.Arguments[key]!).ToString(Newtonsoft.Json.Formatting.None);

    [Fact]
    public async Task RunsStepsWithBindingsAndThePlanId()
    {
        _respond = r => r.ToolName switch
        {
            "revit_graph_query" => Ok(r, new { items = new[] { new { id = 11 }, new { id = 12 } } }),
            JobService.WarningsTool => Ok(r, new { total = 0, bySeverity = new { warning = 0, error = 0 }, rows = Array.Empty<object>() }),
            _ => Ok(r, new { modifiedCount = 2 })
        };
        var job = JobFrom(new object[]
        {
            new { tool = "revit_graph_query", arguments = new { op = "find" }, bind = new { ids = "$.data.items[*].id", first = "$.data.items[0].id" } },
            new { tool = "revit_set_parameter", arguments = new { elementIds = "{{ids}}", parameterName = "Mark", value = "S-{{first}}" } },
            new { tool = "revit_get_elements_info", arguments = new { elementIds = "{{ids}}" } }
        }, report: "auto");
        var service = Service();

        service.Start(job);
        await Until(() => job.IsTerminal && Sent(JobService.ReportTool).Count > 0);

        Assert.Equal(JobState.Completed, job.State);
        var set = Sent("revit_set_parameter").Single();
        Assert.Equal("[11,12]", Arg(set, "elementIds"));
        Assert.Equal("\"S-11\"", Arg(set, "value"));
        Assert.Equal("\"plan-0000aa\"", Arg(set, "planId"));
        Assert.Equal("Job " + job.Id, set.ClientName);
        Assert.False(Sent("revit_graph_query").Single().Arguments.ContainsKey("planId"));   // reads carry no plan
        Assert.Single(Sent(JobService.WarningsTool));                                        // checked after the one write step
        Assert.Contains(_finished, f => f.Id == "plan-0000aa" && f.State == PlanState.Closed);
    }

    [Fact]
    public async Task FailureStopsTheJob()
    {
        _respond = r => r.ToolName == "revit_move_elements"
            ? new McpToolResult { RequestId = r.RequestId, Success = false, Status = "validation_failed", Message = "no such element" }
            : Ok(r, new { total = 0, bySeverity = new { error = 0 } });
        var job = JobFrom(new object[] { new { tool = "revit_move_elements" }, new { tool = "revit_set_parameter" } });
        var service = Service();

        service.Start(job);
        await Until(() => job.IsTerminal);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("no such element", job.StopReason);
        Assert.Equal(JobStepState.Pending, job.Steps[1].State);
        Assert.Empty(Sent("revit_set_parameter"));
    }

    [Fact]
    public async Task NewErrorWarningPausesAndResumeContinues()
    {
        _respond = r => r.ToolName == JobService.WarningsTool
            ? Ok(r, new { total = 1, bySeverity = new { warning = 0, error = 1 }, rows = new[] { new { description = "Room is not enclosed" } } })
            : Ok(r, null);
        var job = JobFrom(new object[] { new { tool = "revit_set_parameter" }, new { tool = "revit_move_elements" } });
        var service = Service();

        service.Start(job);
        await Until(() => job.State == JobState.Paused);
        Assert.Contains("Room is not enclosed", job.StopReason);
        Assert.Equal(JobStepState.Succeeded, job.Steps[0].State);

        service.Resume(job);
        await Until(() => job.IsTerminal);
        Assert.Equal(JobState.Completed, job.State);   // the accepted error does not stop it again
    }

    [Fact]
    public async Task BusyRevitSuspendsAfterTheRetryLimit()
    {
        _respond = r => new McpToolResult { RequestId = r.RequestId, Success = false, Status = "revit_busy", Message = "busy" };
        var job = JobFrom(new object[] { new { tool = "revit_set_parameter" } }, stop: new { maxConsecutiveBusy = 1 });
        var service = Service();

        service.Start(job);
        await Until(() => job.State == JobState.Suspended);

        Assert.Contains("busy", job.StopReason);
        Assert.Equal(JobStepState.Pending, job.Steps[0].State);
    }

    [Fact]
    public async Task WaitsForApprovalAndContinuesWithTheDecision()
    {
        var approvals = new ApprovalService();
        PendingApprovalRequest? parked = null;
        _respond = r =>
        {
            if (r.ToolName == "revit_move_elements")
            {
                parked = new PendingApprovalRequest { OriginalRequest = r, Completion = new TaskCompletionSource<McpToolResult>(), ToolName = r.ToolName };
                approvals.Add(parked);
                return new McpToolResult { RequestId = r.RequestId, Success = false, Status = "approval_required", Message = "pending" };
            }
            return Ok(r, new { total = 0, bySeverity = new { error = 0 } });
        };
        var job = JobFrom(new object[] { new { tool = "revit_move_elements" }, new { tool = "revit_set_parameter" } });
        var service = Service(approvals);

        service.Start(job);
        await Until(() => job.State == JobState.WaitingApproval);
        Assert.Equal(JobStepState.WaitingApproval, job.Steps[0].State);

        parked!.Completion.TrySetResult(new McpToolResult { RequestId = parked.OriginalRequest.RequestId, Success = true, Message = "moved" });
        await Until(() => job.IsTerminal);

        Assert.Equal(JobState.Completed, job.State);
        Assert.Equal("moved", job.Steps[0].Message);
    }

    [Fact]
    public async Task CancelWhileWaitingRejectsTheApproval()
    {
        var approvals = new ApprovalService();
        _respond = r =>
        {
            approvals.Add(new PendingApprovalRequest { OriginalRequest = r, Completion = new TaskCompletionSource<McpToolResult>(), ToolName = r.ToolName });
            return new McpToolResult { RequestId = r.RequestId, Success = false, Status = "approval_required", Message = "pending" };
        };
        var job = JobFrom(new object[] { new { tool = "revit_move_elements" }, new { tool = "revit_set_parameter" } });
        var service = Service(approvals);

        service.Start(job);
        await Until(() => job.State == JobState.WaitingApproval);
        Assert.True(service.Cancel(job.Id));
        await Until(() => job.IsTerminal);

        Assert.Equal(JobState.Cancelled, job.State);
        Assert.Equal(0, approvals.Count);
        Assert.Equal(JobStepState.Cancelled, job.Steps[1].State);
        Assert.Contains(_finished, f => f.State == PlanState.Revoked);
    }

    [Fact]
    public async Task UnboundVariableFailsTheStepBeforeDispatch()
    {
        _respond = r => Ok(r, new { items = Array.Empty<object>() });
        var job = JobFrom(new object[]
        {
            new { tool = "revit_graph_query", bind = new { ids = "$.data.items[*].id" } },
            new { tool = "revit_set_parameter", arguments = new { elementIds = "{{ids}}" } }
        });
        var service = Service();

        service.Start(job);
        await Until(() => job.IsTerminal);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("job_step_invalid", job.Steps[1].Status);
        Assert.Empty(Sent("revit_set_parameter"));
    }

    [Fact]
    public async Task StatusLongPollReturnsOnChange()
    {
        var gate = new TaskCompletionSource<bool>();
        var service = new JobService(
            async (request, _) => { await gate.Task; return Ok(request, new { total = 0, bySeverity = new { error = 0 } }); },
            null, new JobJournalStore(_folder), PermissionOf, _ => false, _ => null, (_, _, _) => { }, null);
        var job = JobFrom(new object[] { new { tool = "revit_set_parameter" } });
        service.Start(job);
        await Until(() => job.Steps[0].State == JobStepState.Running);

        var wait = service.WaitForChangeAsync(job.Id, TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.False(wait.IsCompleted);
        gate.SetResult(true);
        Assert.Same(wait, await Task.WhenAny(wait, Task.Delay(5000)));
    }
}

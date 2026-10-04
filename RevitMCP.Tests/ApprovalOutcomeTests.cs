using RevitMCP.Addin.Approval;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

public class ApprovalOutcomeTests
{
    private static PendingApprovalRequest Pending(string tool = "revit_delete_elements") => new()
    {
        OriginalRequest = new McpToolRequest { ToolName = tool },
        Completion = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously),
        ToolName = tool,
        Summary = "Delete 123 elements"
    };

    [Fact]
    public void NewRequestIsPendingAndFoundByEitherId()
    {
        var service = new ApprovalService();
        var p = Pending();
        service.Add(p);

        Assert.Equal(ApprovalOutcome.Pending, service.GetOutcome(p.ApprovalId)!.State);
        Assert.Same(service.GetOutcome(p.ApprovalId), service.GetOutcome(p.OriginalRequest.RequestId));
        Assert.Null(service.GetOutcome("nope"));
    }

    [Fact]
    public void ApprovedRequestRunsThenRecordsItsResult()
    {
        var service = new ApprovalService();
        service.SetRedispatch(_ => { });
        var p = Pending();
        service.Add(p);

        service.Approve(p.ApprovalId);
        var outcome = service.GetOutcome(p.ApprovalId)!;
        Assert.Equal(ApprovalOutcome.Running, outcome.State);
        Assert.NotNull(outcome.DecidedAt);

        p.Completion.SetResult(new McpToolResult { Success = true, Message = "Deleted 123 elements.", Data = new { deleted = 123 } });

        outcome = service.GetOutcome(p.ApprovalId)!;
        Assert.Equal(ApprovalOutcome.Succeeded, outcome.State);
        Assert.Equal("Deleted 123 elements.", outcome.Result!.Message);
        Assert.NotNull(outcome.CompletedAt);
    }

    [Fact]
    public void ApprovedRequestThatNeverRanIsReportedAsFailedWithTheReason()
    {
        // The silent case behind #85's observation: approved, but the model changed meanwhile.
        var service = new ApprovalService();
        service.SetRedispatch(_ => { });
        var p = Pending();
        service.Add(p);
        service.Approve(p.ApprovalId);

        p.Completion.SetResult(new McpToolResult
        {
            Success = false,
            Status = "approval_context_changed",
            Message = "The Revit model changed after approval was requested."
        });

        var outcome = service.GetOutcome(p.OriginalRequest.RequestId)!;
        Assert.Equal(ApprovalOutcome.Failed, outcome.State);
        Assert.Equal("approval_context_changed", outcome.Result!.Status);
    }

    [Fact]
    public void RejectAndExpiryAreRecorded()
    {
        var service = new ApprovalService(4, TimeSpan.FromMinutes(1));
        var rejected = Pending();
        var expired = Pending();
        expired.CreatedAt = DateTimeOffset.Now.AddMinutes(-5);
        service.Add(rejected);
        service.Add(expired);

        service.Reject(rejected.ApprovalId);
        service.Approve(expired.ApprovalId);

        Assert.Equal(ApprovalOutcome.Rejected, service.GetOutcome(rejected.ApprovalId)!.State);
        var e = service.GetOutcome(expired.ApprovalId)!;
        Assert.Equal(ApprovalOutcome.Failed, e.State);
        Assert.Equal("approval_expired", e.Result!.Status);
    }

    [Fact]
    public void RecentOutcomesAreNewestFirstAndTheHistoryIsBounded()
    {
        var service = new ApprovalService(500, TimeSpan.FromMinutes(10));
        var all = new List<PendingApprovalRequest>();
        for (var i = 0; i < 150; i++)
        {
            var p = Pending($"tool_{i}");
            all.Add(p);
            service.Add(p);
            service.Reject(p.ApprovalId);
        }

        var recent = service.GetRecentOutcomes(5);
        Assert.Equal("tool_149", recent[0].ToolName);
        Assert.Equal(5, recent.Count);
        Assert.Null(service.GetOutcome(all[0].ApprovalId)); // evicted
        Assert.NotNull(service.GetOutcome(all[149].ApprovalId));
    }

    [Fact]
    public void PendingOutcomesAreNeverEvicted()
    {
        var service = new ApprovalService(500, TimeSpan.FromMinutes(10));
        var stillPending = Pending("keep_me");
        service.Add(stillPending);
        for (var i = 0; i < 150; i++)
        {
            var p = Pending();
            service.Add(p);
            service.Reject(p.ApprovalId);
        }

        Assert.Equal(ApprovalOutcome.Pending, service.GetOutcome(stillPending.ApprovalId)!.State);
    }
}

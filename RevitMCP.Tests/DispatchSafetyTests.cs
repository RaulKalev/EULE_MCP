using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;
using Xunit;

namespace RevitMCP.Tests;

public class DispatchSafetyTests
{
    [Fact]
    public void ApprovalContextGuard_RequiresSameDocumentInstance()
    {
        var expected = new object();

        Assert.True(ApprovalContextGuard.IsValid(true, expected, expected));
        Assert.False(ApprovalContextGuard.IsValid(true, expected, new object()));
        Assert.False(ApprovalContextGuard.IsValid(true, expected, null));
        Assert.True(ApprovalContextGuard.IsValid(false, expected, new object()));
    }

    [Fact]
    public void ApprovalContextGuard_RequiresSameSelectionWhenBound()
    {
        Assert.True(ApprovalContextGuard.IsSelectionValid(true, new long[] { 3, 1 }, new long[] { 1, 3 }));
        Assert.False(ApprovalContextGuard.IsSelectionValid(true, new long[] { 1, 3 }, new long[] { 1, 4 }));
        Assert.True(ApprovalContextGuard.IsSelectionValid(false, new long[] { 1 }, Array.Empty<long>()));
    }

    [Fact]
    public void DocumentChangeTracker_ChangesOnlyTheAffectedDocumentStamp()
    {
        var first = new object();
        var second = new object();
        var originalFirst = DocumentChangeTracker.Capture(first);
        var originalSecond = DocumentChangeTracker.Capture(second);

        DocumentChangeTracker.MarkChanged(first);

        Assert.Equal(originalFirst + 1, DocumentChangeTracker.Capture(first));
        Assert.Equal(originalSecond, DocumentChangeTracker.Capture(second));
    }

    [Fact]
    public void ApprovalService_ApprovePreservesBoundContextForRedispatch()
    {
        var service = new ApprovalService();
        var documentToken = new object();
        var pending = CreatePending(documentToken);
        PendingApprovalRequest? redispatched = null;
        service.SetRedispatch(request => redispatched = request);
        service.Add(pending);

        service.Approve(pending.ApprovalId);

        Assert.Same(pending, redispatched);
        Assert.True(redispatched!.OriginalRequest.IsApproved);
        Assert.Same(documentToken, redispatched.OriginDocumentToken);
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public async Task ApprovalService_RejectReturnsStructuredResult()
    {
        var service = new ApprovalService();
        var pending = CreatePending(new object());
        service.Add(pending);

        service.Reject(pending.ApprovalId);
        var result = await pending.Completion.Task;

        Assert.False(result.Success);
        Assert.Equal("approval_rejected", result.Status);
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public void ApprovalService_EnforcesCapacity()
    {
        var service = new ApprovalService(1, TimeSpan.FromMinutes(10));

        Assert.True(service.Add(CreatePending(new object())));
        Assert.False(service.Add(CreatePending(new object())));
        Assert.Equal(1, service.Count);
    }

    [Fact]
    public async Task ApprovalService_RejectsExpiredApproval()
    {
        var service = new ApprovalService(1, TimeSpan.FromMinutes(1));
        var pending = CreatePending(new object());
        pending.CreatedAt = DateTimeOffset.Now.AddMinutes(-2);
        var redispatched = false;
        service.SetRedispatch(_ => redispatched = true);
        Assert.True(service.Add(pending));

        service.Approve(pending.ApprovalId);
        var result = await pending.Completion.Task;

        Assert.False(redispatched);
        Assert.Equal("approval_expired", result.Status);
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public void WorkQueue_IsBoundedAndFifo()
    {
        var queue = new ExternalEventWorkQueue(2);
        using var first = CreateWorkItem("1");
        using var second = CreateWorkItem("2");
        using var rejected = CreateWorkItem("3");

        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(second));
        Assert.False(queue.TryEnqueue(rejected));
        Assert.Equal(2, queue.Count);

        Assert.True(queue.TryDequeue(out var dequeuedFirst));
        Assert.True(queue.TryDequeue(out var dequeuedSecond));
        Assert.Same(first, dequeuedFirst);
        Assert.Same(second, dequeuedSecond);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public async Task WorkQueue_CancelPreventsLaterExecution()
    {
        var queue = new ExternalEventWorkQueue(1);
        using var item = CreateWorkItem("cancel-me");
        Assert.True(queue.TryEnqueue(item));

        Assert.True(queue.TryCancel("cancel-me", "cancelled", "request_cancelled"));
        var result = await item.Completion.Task;

        Assert.True(item.CancellationToken.IsCancellationRequested);
        Assert.Equal("request_cancelled", result.Status);
    }

    [Fact]
    public async Task WorkQueue_DrainCompletesEveryPendingRequest()
    {
        var queue = new ExternalEventWorkQueue(2);
        using var first = CreateWorkItem("1");
        using var second = CreateWorkItem("2");
        Assert.True(queue.TryEnqueue(first));
        Assert.True(queue.TryEnqueue(second));

        queue.Drain("panic stop", "request_cancelled");

        Assert.Equal("request_cancelled", (await first.Completion.Task).Status);
        Assert.Equal("request_cancelled", (await second.Completion.Task).Status);
        Assert.Equal(0, queue.Count);
    }

    private static PendingApprovalRequest CreatePending(object documentToken)
    {
        return new PendingApprovalRequest
        {
            OriginalRequest = new McpToolRequest { ToolName = "test_tool" },
            Completion = new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously),
            IsDocumentBound = true,
            OriginDocumentToken = documentToken,
            OriginDocumentTitle = "Test model"
        };
    }

    private static ExternalEventWorkItem CreateWorkItem(string requestId)
    {
        return new ExternalEventWorkItem(
            new McpToolRequest { RequestId = requestId, ToolName = "test_tool" },
            new TaskCompletionSource<McpToolResult>(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}

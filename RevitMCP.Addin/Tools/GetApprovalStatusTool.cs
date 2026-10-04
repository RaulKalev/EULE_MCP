using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Reports what happened to approval-gated requests after <c>approval_required</c> was returned:
/// still pending, running, succeeded (with the tool's result), failed (with the reason, e.g. the model
/// changed after the request) or rejected. Without it an approved request that failed was invisible
/// to the caller.
/// </summary>
public class GetApprovalStatusTool : IRevitMcpTool, IBackgroundMcpTool
{
    private readonly ApprovalService _approvals;

    public GetApprovalStatusTool(ApprovalService approvals) => _approvals = approvals;

    public string Name => "revit_get_approval_status";
    public string Description => "Reports the outcome of approval-gated requests: pending, running, succeeded with the tool's result, failed with the reason, or rejected. Pass the requestId (or approvalId) from an approval_required response, or nothing to list recent approvals.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Connection;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var id = ToolArguments.GetString(request.Arguments, "requestId");
        if (string.IsNullOrWhiteSpace(id))
            id = ToolArguments.GetString(request.Arguments, "approvalId");

        if (!string.IsNullOrWhiteSpace(id))
        {
            var outcome = _approvals.GetOutcome(id.Trim());
            sw.Stop();
            if (outcome == null)
            {
                return Task.FromResult(new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "not_found",
                    Message = $"No approval request '{id}' is known. It may pre-date the last connector start or have been evicted from the history.",
                    DurationMs = sw.ElapsedMilliseconds
                });
            }

            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = Describe(outcome),
                Data = Detail(outcome),
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var limit = Math.Max(1, Math.Min(100, ToolArguments.GetInt(request.Arguments, "limit", 20)));
        var recent = _approvals.GetRecentOutcomes(limit);
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{recent.Count} recent approval request(s); {recent.Count(o => o.State == ApprovalOutcome.Pending)} pending.",
            Data = new { approvals = recent.Select(Brief).ToList() },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string Describe(ApprovalOutcome o) => o.State switch
    {
        ApprovalOutcome.Pending => $"'{o.Summary}' is still waiting for approval in Revit.",
        ApprovalOutcome.Running => $"'{o.Summary}' was approved and is running.",
        ApprovalOutcome.Succeeded => $"'{o.Summary}' was approved and succeeded: {o.Result?.Message}",
        ApprovalOutcome.Rejected => $"'{o.Summary}' was rejected by the user.",
        _ => $"'{o.Summary}' did not complete ({o.Result?.Status ?? "failed"}): {o.Result?.Message}"
    };

    private static object Brief(ApprovalOutcome o) => new
    {
        approvalId = o.ApprovalId,
        requestId = o.RequestId,
        tool = o.ToolName,
        summary = o.Summary,
        state = o.State,
        status = o.Result?.Status,
        message = o.Result?.Message,
        createdAt = o.CreatedAt,
        completedAt = o.CompletedAt
    };

    private static object Detail(ApprovalOutcome o) => new
    {
        approvalId = o.ApprovalId,
        requestId = o.RequestId,
        tool = o.ToolName,
        summary = o.Summary,
        client = o.ClientName,
        state = o.State,
        createdAt = o.CreatedAt,
        decidedAt = o.DecidedAt,
        completedAt = o.CompletedAt,
        result = o.Result == null ? null : new
        {
            success = o.Result.Success,
            status = o.Result.Status,
            message = o.Result.Message,
            data = o.Result.Data,
            warnings = o.Result.Warnings is { Count: > 0 } ? o.Result.Warnings : null,
            errors = o.Result.Errors is { Count: > 0 } ? o.Result.Errors : null
        }
    };
}

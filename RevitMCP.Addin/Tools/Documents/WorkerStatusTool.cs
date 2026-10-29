using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Dialogs;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>
/// revit_get_worker_status: what worker mode pre-approves, which documents the connector opened, the dialog
/// auto-answer rules, and the dialogs Revit raised during connector work (with their ids, for new rules).
/// </summary>
public sealed class WorkerStatusTool : IRevitMcpTool
{
    public string Name => "revit_get_worker_status";
    public string Description =>
        "Worker mode status: whether open/sync/close/relinquish/reloadLatest/checkout are pre-approved (approval.worker in " +
        "the user config) and for which folders, the documents the connector opened this session, the dialog auto-answer " +
        "rules (dialogs in the user config) and the last dialogs Revit raised while the connector worked — with their " +
        "dialog ids and what was answered. dialogs: how many recent dialogs to list (default 20, max 200).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Worksharing;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var max = Math.Min(200, Math.Max(1, ToolArguments.GetInt(request.Arguments, "dialogs", 20)));
        var policy = WorkerPreapproval.Current;
        var rules = DialogAutoAnswer.Current;
        var dialogs = DialogJournal.Recent(max);

        var warnings = new List<string>();
        warnings.AddRange(policy.Problems);
        warnings.AddRange(rules.Problems);

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = (policy.IsActive
                          ? $"Worker mode pre-approves {string.Join(", ", policy.Operations)} in {policy.Folders.Count} folder(s)."
                          : "Worker mode is off: open, sync, close and the other document operations wait for approval.") +
                      (rules.AutoAnswer ? $" Dialog auto-answer is on ({rules.Rules.Count} id and {rules.MessageRules.Count} message rule(s), unknown={(rules.CancelUnknown ? "cancel" : "show")}, scope={(rules.AlwaysScope ? "always" : "connector")})." : " Dialog auto-answer is off.") +
                      $" {dialogs.Count} dialog(s) recorded.",
            Data = new
            {
                worker = new
                {
                    enabled = policy.Enabled,
                    active = policy.IsActive,
                    operations = policy.Operations,
                    folders = policy.Folders,
                    openedByConnector = WorkerDocumentRegistry.Snapshot()
                },
                dialogRules = new
                {
                    autoAnswer = rules.AutoAnswer,
                    unknown = rules.CancelUnknown ? "cancel" : "show",
                    scope = rules.AlwaysScope ? "always" : "connector",
                    rules = rules.Rules.ToDictionary(r => r.Key, r => r.Value < 0 ? "show" : $"{r.Value} ({DialogRules.Describe(r.Value)})"),
                    messageRules = rules.MessageRules.ToDictionary(r => r.Key, r => r.Value < 0 ? "show" : $"{r.Value} ({DialogRules.Describe(r.Value)})"),
                    logFile = DialogAutoAnswer.LogPath
                },
                dialogs = dialogs.Select(d => new
                {
                    time = d.Time,
                    dialogId = d.DialogId,
                    kind = d.Kind,
                    message = d.Message,
                    action = d.Action,
                    result = d.Result,
                    accepted = d.Accepted,
                    reason = d.Reason,
                    tool = d.Tool,
                    requestId = d.RequestId,
                    client = d.Client,
                    duringConnectorWork = d.DuringConnectorWork
                }).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

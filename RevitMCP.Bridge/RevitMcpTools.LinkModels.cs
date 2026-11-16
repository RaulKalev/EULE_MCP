using System.ComponentModel;
using ModelContextProtocol.Server;
using RevitMCP.Addin.LinkOperations;
using RevitMCP.Core.Models;

namespace RevitMCP.Bridge;

/// <summary>
/// IFC-first model linking with durable, non-retrying operations (docs/link-operations.md, #163). Preview, link and
/// reconcile go to Revit. Status, list and cancel run HERE, on the operation files the add-in keeps, so they answer
/// while Revit's API thread is blocked converting an IFC, without an ExternalEvent or a pipe round trip.
/// </summary>
internal sealed partial class RevitMcpTools
{
    private const string LinkFilesDescription =
        "Files to link: [{path, kind? (ifc|rvt, from the extension), positioning? (origin|shared), pin?, ifExists? (skip|refresh), regenerateIfcCache?}] or plain paths. Up to 25. IFC first; .rvt works too.";

    private static readonly TimeSpan LinkHeartbeatStale = TimeSpan.FromSeconds(90);

    [McpServerTool(Name = "revit_preview_link_models", ReadOnly = true),
     Description("Previews linking IFC (first choice) and RVT files into a document WITHOUT changing anything. Per file: link | refresh | skip (already linked) | invalid (reason: not absolute, unsupported format or positioning, path not allowed, file not found) | already_in_progress (the existing operation id), the IFC cache state (a fresh .ifc.RVT next to the IFC means no conversion) and the operations already open for the document. Same arguments as revit_link_models.")]
    public Task<string> PreviewLinkModels(
        [Description(LinkFilesDescription)] object[]? files = null,
        [Description("Files to link (instead of files).")] string[]? paths = null,
        [Description("origin (default, origin to origin) | shared (shared coordinates). Other modes are not available through the Revit API.")] string positioning = "origin",
        [Description("Pin the instance once it is fully inserted (default false).")] bool pin = false,
        [Description("The file is already linked: skip (default) | refresh (reloads that link through the same path as revit_reload_links_from).")] string ifExists = "skip",
        [Description("Convert the IFC again even when its .ifc.RVT cache is newer (default false).")] bool regenerateIfcCache = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLinkModels("revit_preview_link_models", files, paths, positioning, pin, ifExists, regenerateIfcCache, document, cancellationToken);

    [McpServerTool(Name = "revit_link_models"),
     Description("Links IFC (first choice) and RVT files into a document as DURABLE operations. Requires approval. Returns one operation id per file and does NOT wait for the conversion: a large IFC converts for minutes while Revit does not answer. Follow each id with revit_link_operation_status (it answers without Revit). NEVER repeat this call for a file: a repeat returns the existing operation (status already_in_progress). NEVER retry after a timeout or disconnect: a lost answer is outcome_unknown, not a failure, and is settled by revit_reconcile_link_operation. Operations run one at a time, in order. ifExists=refresh reloads an existing link through the revit_reload_links_from path. Run revit_preview_link_models first; for DWG use revit_link_dwg.")]
    public Task<string> LinkModels(
        [Description(LinkFilesDescription)] object[]? files = null,
        [Description("Files to link (instead of files).")] string[]? paths = null,
        [Description("origin (default, origin to origin) | shared (shared coordinates).")] string positioning = "origin",
        [Description("Pin the instance once it is fully inserted (default false).")] bool pin = false,
        [Description("The file is already linked: skip (default) | refresh.")] string ifExists = "skip",
        [Description("Convert the IFC again even when its .ifc.RVT cache is newer (default false).")] bool regenerateIfcCache = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLinkModels("revit_link_models", files, paths, positioning, pin, ifExists, regenerateIfcCache, document, cancellationToken);

    private async Task<string> SendLinkModels(string toolName, object[]? files, string[]? paths, string positioning, bool pin,
        string ifExists, bool regenerateIfcCache, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["files"] = ToJToken(files),
            ["paths"] = paths ?? [],
            ["positioning"] = positioning,
            ["pin"] = pin,
            ["ifExists"] = ifExists,
            ["regenerateIfcCache"] = regenerateIfcCache,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    [McpServerTool(Name = "revit_reconcile_link_operation"),
     Description("Settles a link operation whose outcome is unknown (Revit stopped answering or restarted while it ran) by looking in the open document: link type exists, points at the file, is loaded, has an instance and content. Records completed or failed. Links, retries and deletes nothing; cache files are kept. Needs Revit to answer; if the model is not open or the result is unclear it stays outcome_unknown for a person to review. operationId from revit_list_link_operations.")]
    public async Task<string> ReconcileLinkOperation(
        [Description("The operation id (lop-…).")] string operationId,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["operationId"] = operationId };
        return FormatResult(await pipeClient.SendAsync("revit_reconcile_link_operation", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_link_operation_status", ReadOnly = true),
     Description("Status of one link operation, read from the connector's files WITHOUT asking Revit: it answers while Revit converts an IFC and its API thread is blocked, and even when Revit is closed. Give operationId, or requestId (the id of the revit_link_models request, as revit_get_approval_status shows it). Returns state (queued | starting | converting | verifying | completed | failed | outcome_unknown | cancelled), effectiveState (outcome_unknown when the owning Revit process is gone), phase, measured elapsed time (never a percentage), ready (true only when completed AND verified), heartbeat age and what to do next. waitSeconds (max 25) holds the call until the state changes; do not poll faster than every 15-30 s.")]
    public async Task<string> LinkOperationStatus(
        [Description("The operation id (lop-…).")] string? operationId = null,
        [Description("The requestId of the revit_link_models call, when the operation id is not known.")] string? requestId = null,
        [Description("Hold the call up to this many seconds (max 25) until the operation changes.")] int waitSeconds = 0,
        CancellationToken cancellationToken = default)
    {
        var store = new LinkOperationStore(LinkOperationStore.DefaultFolder());
        var op = FindLinkOperation(store, operationId, requestId, out var problem);
        if (op == null)
            return FormatResult(new McpToolResult { Success = false, Status = "not_found", Message = problem });

        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 25));
        var seen = (op.State, op.StateChangedAt, op.Phase);
        while (waitSeconds > 0 && LinkOperationStates.IsActive(op.State) && DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { break; }
            op = store.Get(op.Id) ?? op;
            if ((op.State, op.StateChangedAt, op.Phase) != seen) break;
        }

        var now = DateTimeOffset.Now;
        var all = store.LoadAll();
        var liveness = LinkOperationRules.Assess(op, now, IsRevitProcessAlive, LinkHeartbeatStale);
        var positions = LinkOperationStatusView.QueuePositions(all);
        return FormatResult(new McpToolResult
        {
            Success = true,
            Message = $"Operation {op.Id} ({System.IO.Path.GetFileName(op.SourcePath)}): {liveness.EffectiveState}" +
                      (op.Phase.Length > 0 && LinkOperationStates.IsActive(op.State) ? $", {op.Phase}" : string.Empty) +
                      $", {LinkOperationRules.FormatElapsed(op.Elapsed(now))} elapsed.",
            Data = LinkOperationStatusView.Describe(op, now, liveness, positions.TryGetValue(op.Id, out var position) ? position : null),
            Warnings = liveness.Reason == null ? new List<string>() : new List<string> { liveness.Reason }
        });
    }

    [McpServerTool(Name = "revit_list_link_operations", ReadOnly = true),
     Description("Lists link operations from the connector's files WITHOUT asking Revit: newest first, with state, effective state, elapsed time and queue position. Use it to find an operation id, to see what holds Revit (the running one) and what waits, and to find operations that need review (outcome_unknown).")]
    public Task<string> ListLinkOperations(
        [Description("Only these states (queued, starting, converting, verifying, completed, failed, outcome_unknown, cancelled). Default: all.")] string[]? states = null,
        [Description("Only open operations: queued, running or outcome_unknown (default false).")] bool openOnly = false,
        [Description("Maximum rows (default 25).")] int limit = 25)
    {
        var store = new LinkOperationStore(LinkOperationStore.DefaultFolder());
        var now = DateTimeOffset.Now;
        var all = store.LoadAll();
        var positions = LinkOperationStatusView.QueuePositions(all);
        var rows = all
            .Select(o => (Op: o, Liveness: LinkOperationRules.Assess(o, now, IsRevitProcessAlive, LinkHeartbeatStale)))
            .Where(x => states == null || states.Length == 0 || states.Contains(x.Op.State, StringComparer.OrdinalIgnoreCase) ||
                        states.Contains(x.Liveness.EffectiveState, StringComparer.OrdinalIgnoreCase))
            .Where(x => !openOnly || LinkOperationStates.BlocksNewRequests(x.Liveness.EffectiveState))
            .Take(Math.Clamp(limit, 1, 200))
            .Select(x => LinkOperationStatusView.Row(x.Op, now, x.Liveness, positions.TryGetValue(x.Op.Id, out var position) ? position : null))
            .ToList();
        var running = all.FirstOrDefault(o => LinkOperationStates.IsRunning(o.State));
        return Task.FromResult(FormatResult(new McpToolResult
        {
            Success = true,
            Message = $"{rows.Count} link operation(s)." + (running == null ? string.Empty : $" {running.Id} is running ({running.State}): Revit may not answer other calls until it finishes."),
            Data = new { operations = rows, runningOperationId = running?.Id }
        }));
    }

    [McpServerTool(Name = "revit_request_link_cancel"),
     Description("Conservative cancel of a link operation, done on the connector's files without asking Revit. An operation still queued (or just handed over) is cancelled and nothing happens in Revit. One that is already converting is only marked cancel-requested: the Revit call is never interrupted, no thread is stopped, Revit is not killed or restarted; it finishes and is verified like any other. Nothing already done is undone (use revit_remove_links for a link that was created).")]
    public Task<string> RequestLinkCancel(
        [Description("The operation id (lop-…).")] string operationId)
    {
        var store = new LinkOperationStore(LinkOperationStore.DefaultFolder());
        var op = store.Get(operationId);
        if (op == null)
            return Task.FromResult(FormatResult(new McpToolResult { Success = false, Status = "not_found", Message = $"No link operation '{operationId}'." }));
        if (LinkOperationStates.IsTerminal(op.State))
            return Task.FromResult(FormatResult(new McpToolResult { Success = true, Status = "already_finished", Message = $"Operation {op.Id} already ended ({op.State}); nothing to cancel." }));

        store.RequestCancel(op.Id);
        var running = LinkOperationStates.IsRunning(op.State);
        return Task.FromResult(FormatResult(new McpToolResult
        {
            Success = true,
            Status = "cancel_requested",
            Message = running
                ? $"Cancel requested for {op.Id}, which is {op.State}. The running Revit call is not interrupted (that is not safe); it finishes and is verified. Only work that has not started is dropped."
                : $"Cancel requested for {op.Id} ({op.State}). The add-in cancels it before it starts; nothing runs in Revit.",
            Data = new { operationId = op.Id, state = op.State, interrupts = false }
        }));
    }

    private static LinkOperation? FindLinkOperation(LinkOperationStore store, string? operationId, string? requestId, out string problem)
    {
        problem = string.Empty;
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            var op = store.Get(operationId.Trim());
            if (op == null) problem = $"No link operation '{operationId}'. revit_list_link_operations lists them.";
            return op;
        }
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            var found = store.FindByRequest(requestId.Trim());
            if (found.Count == 1) return found[0];
            problem = found.Count == 0
                ? $"No link operation was created for request '{requestId}' (it may still wait for approval in Revit, or every file was skipped). revit_list_link_operations lists them."
                : $"Request '{requestId}' created {found.Count} operations: {string.Join(", ", found.Select(o => o.Id))}. Pass operationId.";
            return null;
        }
        problem = "Pass operationId (or requestId). revit_list_link_operations lists the operations.";
        return null;
    }

    private static bool IsRevitProcessAlive(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

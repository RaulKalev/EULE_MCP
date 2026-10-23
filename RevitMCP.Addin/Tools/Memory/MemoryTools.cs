using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Graph;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Memory;
using RevitMCP.Addin.Memory.Hosting;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Memory;

/// <summary>Builds the memory context of the open model and shapes entries for responses.</summary>
internal static class MemoryToolSupport
{
    /// <summary>Reads the open document (API thread): project key, model, version, project root, user.</summary>
    public static MemoryContext? Context(UIApplication uiapp, McpToolRequest request, MemoryService service, out string? error)
    {
        error = null;
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
        {
            error = "No active document: memory is filed under the open model's project.";
            return null;
        }

        var signal = RevitModelVersionReader.Read(uiapp, doc);
        var (root, problem) = MemoryProjectRoot.Find(signal.ModelPath, ToolArguments.GetString(request.Arguments, "projectRoot"));
        return new MemoryContext
        {
            Project = string.IsNullOrWhiteSpace(signal.ProjectKey) ? null : signal.ProjectKey,
            ProjectRoot = root,
            ProjectFolder = root == null ? null : service.ProjectFolderFor(root),
            ProjectProblem = problem,
            Model = string.IsNullOrWhiteSpace(signal.ModelName) ? null : signal.ModelName,
            ModelVersion = string.IsNullOrWhiteSpace(signal.Value) ? null : signal.Value,
            User = string.IsNullOrWhiteSpace(signal.Username) ? Environment.UserName : signal.Username,
            Client = string.IsNullOrWhiteSpace(request.ClientName) ? null : request.ClientName,
            Now = DateTimeOffset.UtcNow
        };
    }

    public static object Dto(MemoryHit hit, bool full) => full
        ? new
        {
            id = hit.Entry.Id,
            key = hit.Entry.Key,
            value = hit.Entry.Value,
            reason = hit.Entry.Reason,
            scope = hit.Entry.Scope,
            state = hit.Entry.State,
            stale = hit.Stale,
            staleReason = hit.StaleReason,
            source = hit.Entry.Source,
            author = hit.Entry.Author,
            stage = hit.Entry.Stage,
            model = hit.Entry.Model,
            tags = hit.Entry.Tags,
            flags = hit.Entry.Flags.Count == 0 ? null : hit.Entry.Flags,
            lifecycle = hit.Entry.Lifecycle,
            supersedes = hit.Entry.Supersedes,
            requires = hit.Entry.Requires.Count == 0 ? null : hit.Entry.Requires,
            counters = new
            {
                shown = hit.Entry.Counters.Shown,
                used = hit.Entry.Counters.Used,
                worked = hit.Entry.Counters.Worked,
                contradicted = hit.Entry.Counters.Contradicted,
                remembered = hit.Entry.Counters.Remembered,
                lastUsed = hit.Entry.Counters.LastUsed?.ToString("yyyy-MM-dd")
            },
            updated = hit.Entry.Updated.ToString("yyyy-MM-dd")
        }
        : new
        {
            id = hit.Entry.Id,
            key = hit.Entry.Key,
            value = hit.Entry.Value,
            reason = hit.Entry.Reason,
            scope = hit.Entry.Scope,
            state = hit.Entry.State,
            stale = hit.Stale,
            staleReason = hit.StaleReason,
            source = hit.Entry.Source,
            flags = hit.Entry.Flags.Count == 0 ? null : hit.Entry.Flags,
            updated = hit.Entry.Updated.ToString("yyyy-MM-dd")
        };

    public static Task<McpToolResult> Fail(McpToolRequest request, string status, string message) =>
        Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Status = status, Message = message });

    public static string Scope(McpToolRequest request, string fallback)
    {
        var scope = ToolArguments.GetString(request.Arguments, "scope").Trim().ToLowerInvariant();
        return scope.Length == 0 ? fallback : scope;
    }
}

/// <summary>
/// Stores a durable note — a decision and its reason, a user correction, an exception to a rule — in the
/// project memory (approval-gated, shared with everyone on the project) or the user's personal memory, or proposes
/// it for the company tier (a proposal file an administrator applies).
/// Tool name: revit_memory_remember
/// </summary>
public class MemoryRememberTool : IRevitMcpTool, IConditionalApprovalTool
{
    public const string ToolName = "revit_memory_remember";

    public string Name => ToolName;
    public string Description =>
        "Stores a durable note (decision + reason, user correction, exception) in project memory (approval), personal memory, " +
        "or as a company proposal. Args: key, value, reason, scope (project|personal|company), tags[], stage, source (agent|human), " +
        "anyProject, basedOn[] (ids of recalled entries the note relied on), projectRoot.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Configuration;

    /// <summary>
    /// Only a project note is shared as written: it needs approval. A personal note is the user's own, and a company
    /// note is only a proposal file in the user's folder that an administrator reviews.
    /// </summary>
    public bool NeedsApproval(McpToolRequest request) => MemoryToolSupport.Scope(request, MemoryScopes.Project) == MemoryScopes.Project;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = MemoryService.Current;
        if (service == null) return MemoryToolSupport.Fail(request, "not_available", "Shared memory is not available in this Revit session.");

        var context = MemoryToolSupport.Context(uiapp, request, service, out var error);
        if (context == null) return MemoryToolSupport.Fail(request, "no_document", error!);

        var args = request.Arguments;
        var result = service.Remember(context, new MemoryRememberRequest
        {
            Key = ToolArguments.GetString(args, "key"),
            Value = ToolArguments.GetString(args, "value"),
            Reason = ToolArguments.GetString(args, "reason"),
            Scope = MemoryToolSupport.Scope(request, MemoryScopes.Project),
            Tags = ToolArguments.GetStringArray(args, "tags").ToList(),
            Stage = ToolArguments.GetString(args, "stage"),
            Source = ToolArguments.GetString(args, "source", MemorySources.Agent),
            AnyProject = ToolArguments.GetBool(args, "anyProject"),
            BasedOn = ToolArguments.GetStringArray(args, "basedOn").ToList()
        });
        sw.Stop();

        if (!result.Success)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = result.Status,
                Message = result.Message,
                Errors = result.Problems,
                DurationMs = sw.ElapsedMilliseconds
            });

        var entry = result.Entry!;
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = result.Message,
            Data = new
            {
                id = entry.Id,
                key = entry.Key,
                scope = entry.Scope,
                state = entry.State,
                action = result.Kind.ToString().ToLowerInvariant(),
                superseded = result.Superseded?.Id,
                retiredForCap = result.RetiredForCap?.Id,
                flags = entry.Flags.Count == 0 ? null : entry.Flags,
                pending = result.Pending,
                path = result.Path,
                proposal = result.ProposalPath,
                credited = result.Credited == 0 ? (int?)null : result.Credited,
                note = MemoryRecall.Note
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>
/// Searches project and personal memory for notes relevant to a task. Read-only towards the model; counts each
/// returned entry as shown in the user's own tally file.
/// Tool name: revit_memory_recall
/// </summary>
public class MemoryRecallTool : IRevitMcpTool
{
    public string Name => "revit_memory_recall";
    public string Description =>
        "Searches project and personal memory for decisions, preferences and corrections relevant to a task. Hints only — verify live before writing. " +
        "Args: query, scope (all|project|company|personal), tags[], limit (default 5, max 20), includeRetired, projectRoot.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Configuration;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = MemoryService.Current;
        if (service == null) return MemoryToolSupport.Fail(request, "not_available", "Shared memory is not available in this Revit session.");

        var context = MemoryToolSupport.Context(uiapp, request, service, out var error);
        if (context == null) return MemoryToolSupport.Fail(request, "no_document", error!);

        var args = request.Arguments;
        var limit = ToolArguments.GetInt(args, "limit", 5);
        var read = service.Recall(context, new MemoryRecallOptions
        {
            Query = ToolArguments.GetString(args, "query"),
            Tags = ToolArguments.GetStringArray(args, "tags").ToList(),
            Limit = limit < 1 ? 1 : limit > 20 ? 20 : limit,
            IncludeRetired = ToolArguments.GetBool(args, "includeRetired")
        }, MemoryToolSupport.Scope(request, "all"));
        sw.Stop();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = read.Hits.Count == 0
                ? "No memory entry matches."
                : $"{read.Hits.Count} memory entr{(read.Hits.Count == 1 ? "y" : "ies")} matched.",
            Data = new
            {
                note = MemoryRecall.Note,
                project = context.Project,
                entries = read.Hits.Select(h => MemoryToolSupport.Dto(h, false)).ToList()
            },
            Warnings = read.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>
/// Lists the memory entries of the open project (and the user's personal ones), newest first, with state,
/// source, counters and staleness. Does not count as shown.
/// Tool name: revit_memory_list
/// </summary>
public class MemoryListTool : IRevitMcpTool
{
    public string Name => "revit_memory_list";
    public string Description =>
        "Lists memory entries newest first with state, source, counters and staleness. " +
        "Args: scope (all|project|company|personal), state (candidate|confirmed|dormant|retired|all; default: all but retired), tags[], offset, limit (default 25, max 100), projectRoot.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Configuration;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = MemoryService.Current;
        if (service == null) return MemoryToolSupport.Fail(request, "not_available", "Shared memory is not available in this Revit session.");

        var context = MemoryToolSupport.Context(uiapp, request, service, out var error);
        if (context == null) return MemoryToolSupport.Fail(request, "no_document", error!);

        var args = request.Arguments;
        var limit = ToolArguments.GetInt(args, "limit", 25);
        var offset = Math.Max(0, ToolArguments.GetInt(args, "offset"));
        limit = limit < 1 ? 1 : limit > 100 ? 100 : limit;
        var read = service.List(context, MemoryToolSupport.Scope(request, "all"), ToolArguments.GetString(args, "state"),
            ToolArguments.GetStringArray(args, "tags"), offset, limit);
        sw.Stop();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{read.Hits.Count} of {read.Total} memory entr{(read.Total == 1 ? "y" : "ies")}.",
            Data = new
            {
                note = MemoryRecall.Note,
                project = context.Project,
                total = read.Total,
                offset,
                hasMore = offset + read.Hits.Count < read.Total,
                tiers = read.Tiers.Select(t => new { scope = t.Scope, folder = t.Folder, entries = t.Entries, fromMirror = t.FromMirror }).ToList(),
                entries = read.Hits.Select(h => MemoryToolSupport.Dto(h, true)).ToList()
            },
            Warnings = read.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

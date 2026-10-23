using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using ModelContextProtocol.Server;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Memory;
using RevitMCP.Core.Models;

namespace RevitMCP.Bridge;

/// <summary>
/// Shared memory review and consolidation (docs/shared-memory.md, phases 3 and 4). The page and forget go to Revit;
/// <c>revit_memory_consolidate</c> runs here in the bridge on the memory files, so it needs no Revit open.
/// </summary>
internal sealed partial class RevitMcpTools
{
    [McpServerTool(Name = "revit_memory_page", ReadOnly = true),
     Description("Publishes the open project's shared memory as a local review page and opens it: consolidation proposals and disagreements to decide (accept/reject, confirm), the digest (unused, contradicted, waiting for confirmation), and every entry with state, source, counters and staleness, with confirm / retire / promote buttons. Each button needs the user's approval in Revit. Use it when the user wants to review or clean up memory.")]
    public async Task<string> MemoryPage(
        [Description("Open the page in the user's browser. Default true.")] bool open = true,
        [Description("Project root. Optional: found above the model file.")] string? projectRoot = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?> { ["open"] = open, ["projectRoot"] = projectRoot ?? string.Empty };
        return FormatResult(await pipeClient.SendAsync("revit_memory_page", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_memory_forget"),
     Description("Retires shared-memory entries by id: they stop appearing in recall and hints but stay as history; nothing is deleted. Needs the user's approval in Revit. Company entries are changed by an administrator, not here.")]
    public async Task<string> MemoryForget(
        [Description("Entry ids (from revit_memory_recall or revit_memory_list).")] string[] ids,
        [Description("Why the entries are wrong or obsolete; shown in the approval.")] string? reason = null,
        [Description("Project root. Optional: found above the model file.")] string? projectRoot = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["ids"] = ids ?? [],
            ["reason"] = reason ?? string.Empty,
            ["projectRoot"] = projectRoot ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_memory_forget", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_memory_consolidate"),
     Description("The weekly shared-memory consolidation pass a lead runs (no Revit needed: it works on the memory files). " +
                 "action=read returns, per project root, what changed since the last run: duplicate groups, flip-flops, raw observed entries, dormant entries and entries shared across projects, plus howTo. " +
                 "action=propose stores your proposals — merge, rewrite, retire or promote — which a person accepts or rejects on the review page (revit_memory_page); it never changes an entry. " +
                 "action=done records the run (pass readAt from the read).")]
    public string MemoryConsolidate(
        [Description("read (default), propose or done.")] string action = "read",
        [Description("Project roots (folders holding .rktools\\memory). Default: memory.consolidation.projectRoots in the config.")] string[]? projectRoots = null,
        [Description("read: report everything, not only what changed since the last run.")] bool full = false,
        [Description("read: ISO date to report changes since (overrides the last run).")] string? since = null,
        [Description("propose: array of {kind: merge|rewrite|retire|promote, entryIds[], key?, value?, reason?, target? (company|skill|standard), note}.")] object? proposals = null,
        [Description("done: the readAt the read returned.")] string? readAt = null)
    {
        var sw = Stopwatch.StartNew();
        var settings = MemorySettings.Load();
        var result = new McpToolResult { Success = true };
        if (!settings.Enabled)
            return FormatResult(new McpToolResult { Success = false, Status = "not_available", Message = "Shared memory is switched off (memory.enabled=false)." });

        var roots = (projectRoots is { Length: > 0 } ? projectRoots : settings.ConsolidationRoots.ToArray()).ToList();
        if (roots.Count == 0)
            return FormatResult(new McpToolResult
            {
                Success = false,
                Status = "validation_failed",
                Message = "Name the projects: projectRoots, or memory.consolidation.projectRoots in the user or company config."
            });

        var runner = new MemoryConsolidationRunner(settings);
        var now = DateTimeOffset.UtcNow;
        var projects = runner.Load(roots, now, result.Warnings);
        if (projects.Count == 0)
            return FormatResult(new McpToolResult { Success = false, Status = "not_found", Message = "No project memory found under the given roots.", Warnings = result.Warnings });

        switch ((action ?? "read").Trim().ToLowerInvariant())
        {
            case "read":
            {
                DateTimeOffset? from = null;
                if (!string.IsNullOrWhiteSpace(since))
                {
                    if (!DateTimeOffset.TryParse(since, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                        return FormatResult(new McpToolResult { Success = false, Status = "validation_failed", Message = "since is not a date." });
                    from = parsed;
                }
                var report = runner.Read(projects, from, full, now);
                result.Message = $"{report.Changed.Count} changed entries, {report.Duplicates.Count} duplicate groups, {report.FlipFlops.Count} flip-flops, " +
                                 $"{report.Observed.Count} observed, {report.CrossProject.Count} shared across projects, {report.PendingProposals} proposals already waiting.";
                result.Data = new
                {
                    readAt = report.ReadAt.ToString("o", CultureInfo.InvariantCulture),
                    since = report.Since?.ToString("o", CultureInfo.InvariantCulture),
                    projects = projects.Select(p => new
                    {
                        root = p.Root,
                        project = p.Project,
                        entries = p.Entries.Count(e => !e.IsRetired),
                        lastRun = p.LastRun?.ToString("o", CultureInfo.InvariantCulture),
                        pendingProposals = p.Proposals.Count(x => x.Status == MemoryProposalStatus.Pending)
                    }).ToList(),
                    duplicates = report.Duplicates.Select(g => new { project = g.Project, similarity = g.Similarity, entries = g.Entries.Select(Compact).ToList() }).ToList(),
                    flipFlops = report.FlipFlops.Select(Compact).ToList(),
                    observed = report.Observed.Select(Compact).ToList(),
                    crossProject = report.CrossProject.Select(g => new { topic = g.Topic, value = g.Value, projects = g.Projects, entryIds = g.Entries.Select(e => e.Id).ToList() }).ToList(),
                    dormant = report.Dormant.Select(Compact).ToList(),
                    changed = report.Changed.Select(Compact).ToList(),
                    howTo = MemoryConsolidation.HowTo,
                    note = RevitMCP.Addin.Memory.MemoryRecall.Note
                };
                break;
            }
            case "propose":
            {
                List<MemoryProposal>? list;
                try { list = (ToJToken(proposals) as JToken)?.ToObject<List<MemoryProposal>>(); }
                catch (Exception ex) when (ex is Newtonsoft.Json.JsonException or ArgumentException or FormatException)
                {
                    return FormatResult(new McpToolResult { Success = false, Status = "validation_failed", Message = "proposals must be an array of proposal objects: " + ex.Message });
                }
                if (list == null || list.Count == 0)
                    return FormatResult(new McpToolResult { Success = false, Status = "validation_failed", Message = "Pass proposals: an array of {kind, entryIds, …}." });

                var outcomes = runner.Propose(projects, list, Environment.UserName, pipeClient.ClientName, now);
                var stored = outcomes.Count(o => o.Id != null);
                result.Success = stored > 0;
                result.Status = stored == outcomes.Count ? null : stored == 0 ? "validation_failed" : "partial";
                result.Message = $"Stored {stored} of {outcomes.Count} proposals; a lead accepts or rejects them on the review page (revit_memory_page).";
                result.Data = new { proposals = outcomes.Select(o => new { index = o.Index, id = o.Id, project = o.Project, problems = o.Problems.Count == 0 ? null : o.Problems }).ToList() };
                break;
            }
            case "done":
            {
                var when = now;
                if (!string.IsNullOrWhiteSpace(readAt) &&
                    DateTimeOffset.TryParse(readAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                    when = parsed;
                var problems = runner.Done(projects, when, Environment.UserName);
                result.Success = problems.Count == 0;
                result.Errors = problems;
                result.Message = $"Recorded the run for {projects.Count - problems.Count} project(s); the next read starts from {when:o}.";
                break;
            }
            default:
                return FormatResult(new McpToolResult { Success = false, Status = "validation_failed", Message = "action must be read, propose or done." });
        }

        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        return FormatResult(result);
    }

    private static object Compact(MemoryEntry e) => new
    {
        id = e.Id,
        key = e.Key,
        value = e.Value,
        reason = e.Reason,
        state = e.State,
        source = e.Source,
        authors = e.Authors,
        flags = e.Flags.Count == 0 ? null : e.Flags,
        counters = new { shown = e.Counters.Shown, used = e.Counters.Used, worked = e.Counters.Worked, contradicted = e.Counters.Contradicted },
        updated = e.Updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
    };
}

/// <summary>
/// The consolidation pass as an MCP prompt, so a lead runs it the same way in any client (in Claude Code it shows as
/// a slash command). The steps themselves come back in the tool's howTo, so the prompt stays short.
/// </summary>
[McpServerPromptType]
internal sealed class MemoryPrompts
{
    public const string ConsolidateText =
        "Run the weekly shared-memory consolidation pass. 1) Call revit_memory_consolidate action=read (with projectRoots if given). " +
        "2) Follow its howTo: write merge, rewrite, retire and promote proposals with action=propose — only where you are confident; never " +
        "change entries yourself. 3) Call action=done with the readAt from step 1. 4) Report what you proposed, per project, in a few lines, " +
        "and remind the reviewer to accept or reject them on the review page (revit_memory_page).";

    [McpServerPrompt(Name = "memory-consolidate"),
     Description("Weekly shared-memory consolidation: dedupe, explain flip-flops, rewrite observations and propose promotions as proposals for review.")]
    public static string Consolidate(
        [Description("Optional project roots, separated by ';'. Default: memory.consolidation.projectRoots.")] string? projectRoots = null) =>
        string.IsNullOrWhiteSpace(projectRoots)
            ? ConsolidateText
            : ConsolidateText + " Project roots: " + projectRoots;
}

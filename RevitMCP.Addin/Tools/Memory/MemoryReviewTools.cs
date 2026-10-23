using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Memory;
using RevitMCP.Addin.Memory.Hosting;
using RevitMCP.Addin.ModelPages;
using RevitMCP.Addin.ModelPages.Hosting;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Memory;

/// <summary>Builds and stores the memory review page for the active model (a model page with a fixed id per project).</summary>
internal static class MemoryPagePublisher
{
    public static (ModelPageInfo? Info, MemoryReviewData? Data, string? Problem) Publish(UIApplication uiapp, MemoryService service, MemoryContext context, string? client)
    {
        var pages = ModelPageService.Current;
        if (pages == null) return (null, null, "Model pages are not available in this Revit session, so the review page cannot be served.");
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return (null, null, "No active document.");

        var data = service.ReviewData(context);
        var html = MemoryPageHtml.Build(data);
        try
        {
            var info = pages.Store.Save(MemoryPageHtml.PageId(context.Project), MemoryPageHtml.Title(context.Project), html,
                ModelPageRevitHandler.ModelIdOf(uiapp), doc.Title, client, DateTimeOffset.Now);
            return (info, data, null);
        }
        catch (ArgumentException ex)
        {
            return (null, data, ex.Message);
        }
    }
}

/// <summary>
/// Publishes the project's memory as a review page: proposals and disagreements to decide, the digest, and every entry
/// with its state, counters and staleness, with confirm / retire / promote. Stores a local page; changes nothing.
/// Tool name: revit_memory_page
/// </summary>
public class MemoryPageTool : IRevitMcpTool
{
    public string Name => "revit_memory_page";
    public string Description => "Publishes the open project's shared memory as a review page (proposals, disagreements, digest, all entries with confirm/retire/promote). Args: open, projectRoot.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = MemoryService.Current;
        if (service == null) return MemoryToolSupport.Fail(request, "not_available", "Shared memory is not available in this Revit session.");
        var context = MemoryToolSupport.Context(uiapp, request, service, out var error);
        if (context == null) return MemoryToolSupport.Fail(request, "no_document", error!);

        var client = string.IsNullOrWhiteSpace(request.ClientName) ? null : request.ClientName;
        var (info, data, problem) = MemoryPagePublisher.Publish(uiapp, service, context, client);
        if (info == null) return MemoryToolSupport.Fail(request, "not_available", problem ?? "The page could not be published.");

        var url = ModelPageService.Current!.UrlFor(info.Id, out var urlProblem);
        if (url == null) return MemoryToolSupport.Fail(request, "not_available", urlProblem ?? "The page could not be served.");

        var warnings = new List<string>(data!.Warnings);
        var opened = false;
        if (ToolArguments.GetBool(request.Arguments, "open", true))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); opened = true; }
            catch (Exception ex) { warnings.Add("The browser could not be opened: " + ex.Message); }
        }

        sw.Stop();
        var pending = data.Proposals.Count(p => p.Status == MemoryProposalStatus.Pending);
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Published the memory review page at {url}" + (opened ? " and opened it." : "."),
            Data = new
            {
                pageId = info.Id,
                url,
                project = context.Project,
                entries = data.Entries.Count(h => !h.Entry.IsRetired),
                proposalsPending = pending,
                disagreements = data.Flagged.Count,
                digest = new { unused = data.Unused.Count, contradicted = data.Contradicted.Count, waiting = data.Waiting.Count },
                opened
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>
/// Retires memory entries: they stop appearing in recall and hints but stay as history. Never deletes. Approval-gated.
/// Tool name: revit_memory_forget
/// </summary>
public class MemoryForgetTool : IRevitMcpTool
{
    public const string ToolName = "revit_memory_forget";

    public string Name => ToolName;
    public string Description => "Retires memory entries by id (they stay as history; nothing is deleted). Requires approval. Args: ids[], reason, projectRoot.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Configuration;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = MemoryService.Current;
        if (service == null) return MemoryToolSupport.Fail(request, "not_available", "Shared memory is not available in this Revit session.");
        var context = MemoryToolSupport.Context(uiapp, request, service, out var error);
        if (context == null) return MemoryToolSupport.Fail(request, "no_document", error!);

        var result = service.Forget(context, ToolArguments.GetStringArray(request.Arguments, "ids"));
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = result.Success,
            Status = result.Status,
            Message = result.Message,
            Data = result.Success ? new { retired = result.Changed } : null,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>
/// The memory review page's actions: confirm, retire or promote entries, accept or reject a consolidation proposal.
/// Not advertised by the bridge: only the page listener sends it, through the dispatcher. It always needs manual
/// approval, even with Direct Edit on: every page served by the listener shares one origin, so a script in a page an
/// agent wrote could read the review page and send the same request; the person at the Revit screen is the guard.
/// Republishes the page afterwards so a reload shows the result.
/// Tool name: revit_memory_review
/// </summary>
public class MemoryReviewTool : IRevitMcpTool
{
    public const string ToolName = "revit_memory_review";

    public string Name => ToolName;
    public string Description => "Applies a review action from the memory page. Sent by the page listener only. Always requires manual approval.";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Configuration;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = MemoryService.Current;
        if (service == null) return MemoryToolSupport.Fail(request, "not_available", "Shared memory is not available in this Revit session.");
        if (uiapp.ActiveUIDocument?.Document == null) return MemoryToolSupport.Fail(request, "no_document", "No document is open in Revit.");

        // Memory is filed per project: acting from another model's page would change another project's memory.
        if (!string.Equals(ModelPageRevitHandler.ModelIdOf(uiapp), ToolArguments.GetString(request.Arguments, "modelId"), StringComparison.Ordinal))
            return MemoryToolSupport.Fail(request, "different_model", "The page belongs to another model. Switch to it in Revit; nothing was changed.");

        var context = MemoryToolSupport.Context(uiapp, request, service, out var error);
        if (context == null) return MemoryToolSupport.Fail(request, "no_document", error!);

        var result = service.Review(context, ToolArguments.GetString(request.Arguments, "action"),
            ToolArguments.GetStringArray(request.Arguments, "ids"), ToolArguments.GetString(request.Arguments, "proposal"));
        if (result.Success) MemoryPagePublisher.Publish(uiapp, service, context, "Model page");
        sw.Stop();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = result.Success,
            Status = result.Status,
            Message = result.Message,
            Data = new { changed = result.Changed, produced = result.Produced },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

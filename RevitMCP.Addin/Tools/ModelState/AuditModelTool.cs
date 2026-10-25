using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelPages;
using RevitMCP.Addin.ModelPages.Hosting;
using RevitMCP.Addin.ModelState;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelState;

/// <summary>
/// revit_audit_model (#109): a deterministic model health report — warnings, unused types, views, sheets, rooms,
/// imports, in-place families, groups, phases, design options, worksets and ownership, links, graph and agent state —
/// published as a model page. Read-only: the page is additive connector output.
/// </summary>
public sealed class AuditModelTool : IRevitMcpTool
{
    public const string ToolName = "revit_audit_model";

    public string Name => ToolName;
    public string Description =>
        "Model health audit: summary (file size, elements per category), Revit warnings grouped, unused family types, views " +
        "not on sheets / without template, sheets without views and revisions, unplaced / not enclosed / redundant rooms, " +
        "CAD imports and links, in-place families, groups, phases, design options, worksets and elements owned by others " +
        "(sampled), RVT/IFC/DWG links with load status, path type and pinning, and the model graph / checkpoints / plans. " +
        "Each figure gets ok / warn / bad from the thresholds (audit.thresholds in config overrides the company defaults). " +
        "sections limits the report; publish (default true) publishes it as a model page whose rows select the elements " +
        "in Revit; open opens it. The JSON result mirrors the page (sections with status, counts and the first 200 items).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var sections = AuditSections.Resolve(ToolArguments.GetStringArray(request.Arguments, "sections"), out var unknown);
        var warnings = new List<string>();
        if (unknown.Count > 0)
            warnings.Add($"Unknown section(s) ignored: {string.Join(", ", unknown)}. Sections: {string.Join(", ", AuditSections.All)}.");

        var report = AuditModelRunner.Run(uiapp, doc, sections, cancellationToken);
        warnings.AddRange(report.Warnings);

        string? url = null;
        string? pageId = null;
        var opened = false;
        if (ToolArguments.GetBool(request.Arguments, "publish", true))
        {
            var client = string.IsNullOrWhiteSpace(request.ClientName) ? null : request.ClientName;
            (pageId, url) = AuditModelRunner.Publish(doc, report, client, warnings);
            if (url != null && ToolArguments.GetBool(request.Arguments, "open", false))
            {
                try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); opened = true; }
                catch (Exception ex) { warnings.Add("The browser could not be opened: " + ex.Message); }
            }
        }

        var flagged = report.Sections.Count(s => s.Status is AuditStatus.Warn or AuditStatus.Bad);
        var failedSections = report.Sections.Count(s => s.Status == AuditStatus.Error);
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Audit of '{doc.Title}': {AuditReportHtml.StatusText(report.Status)} — {report.Sections.Count} section(s), " +
                      $"{flagged} need attention" + (failedSections > 0 ? $", {failedSections} failed" : string.Empty) +
                      (url != null ? $". Page: {url}" + (opened ? " (opened)." : ".") : "."),
            Data = AuditJson.Describe(report, url, pageId,
                ToolArguments.GetInt(request.Arguments, "itemsPerSection", AuditJson.DefaultItemsPerSection)),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

/// <summary>Runs the audit and publishes its page; shared by the tool and the audit.model skill task.</summary>
internal static class AuditModelRunner
{
    public static AuditReport Run(UIApplication uiapp, Document doc, IReadOnlyList<string> sections, CancellationToken cancellationToken)
    {
        var sources = new List<string>();
        var configWarnings = new List<string>();
        var rules = AuditCollectors.LoadRules(doc, sources, configWarnings);
        var report = new AuditCollectors(uiapp, doc, rules, cancellationToken).Run(sections);
        report.ThresholdSources.AddRange(sources);
        report.Warnings.AddRange(configWarnings);
        return report;
    }

    /// <summary>Publishes (or replaces, same link) the audit page of this model. Returns the page id and url, or nulls with a warning.</summary>
    public static (string? PageId, string? Url) Publish(Document doc, AuditReport report, string? client, List<string> warnings)
    {
        var pages = ModelPageService.Current;
        if (pages == null)
        {
            warnings.Add("Model pages are not available in this Revit session: the report is in the JSON result only.");
            return (null, null);
        }
        var modelId = CheckpointChangeTracker.ModelIdOf(doc);
        var html = AuditReportHtml.Build(report);
        var title = AuditReportHtml.Title(doc.Title);
        if (title.Length > ModelPageStore.MaxTitleLength) title = title.Substring(0, ModelPageStore.MaxTitleLength);
        try
        {
            var info = pages.Store.Save(AuditReportHtml.PageId(modelId), title, html, modelId, doc.Title, client, DateTimeOffset.Now);
            var url = pages.UrlFor(info.Id, out var problem);
            if (url == null) warnings.Add(problem ?? "The page was stored but cannot be served.");
            return (info.Id, url);
        }
        catch (ArgumentException ex)
        {
            warnings.Add("The page could not be published: " + ex.Message);
            return (null, null);
        }
    }
}

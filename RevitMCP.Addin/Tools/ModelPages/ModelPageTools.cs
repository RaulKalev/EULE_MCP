using System.Diagnostics;
using System.IO;
using System.Text;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelPages;
using RevitMCP.Addin.ModelPages.Hosting;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelPages;

/// <summary>
/// Publishes HTML the agent generated from the active model as a local page the user can work from:
/// rows select and zoom to their elements in Revit and show current parameter values, without the agent.
/// Stores a file locally and opens the browser; it does not touch the model.
/// </summary>
public class PublishPageTool : IRevitMcpTool
{
    public string Name => "revit_publish_page";
    public string Description => "Publishes an HTML page generated from the active model as a local interactive page (select/zoom elements, live parameter values).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var service = ModelPageService.Current;
        if (service == null) return Done(request, false, "Model pages are not available in this Revit session.");

        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Done(request, false, "No active document: a page is bound to the model it was generated from.");

        var html = ToolArguments.GetString(request.Arguments, "html");
        var htmlPath = ToolArguments.GetString(request.Arguments, "htmlPath");
        if (string.IsNullOrWhiteSpace(html) && !string.IsNullOrWhiteSpace(htmlPath))
        {
            var problem = ReadFile(htmlPath, out html);
            if (problem != null) return Done(request, false, problem);
        }
        if (string.IsNullOrWhiteSpace(html)) return Done(request, false, "Pass the page as html, or as htmlPath to an .html file.");

        var pageId = ToolArguments.GetString(request.Arguments, "pageId");
        if (RevitMCP.Addin.Memory.MemoryPageHtml.IsMemoryPage(pageId.Trim()))
            return Done(request, false, "Page ids starting with 'memory-' are reserved for the memory review page (revit_memory_page).");
        ModelPageInfo info;
        try
        {
            info = service.Store.Save(
                string.IsNullOrWhiteSpace(pageId) ? null : pageId.Trim(),
                ToolArguments.GetString(request.Arguments, "title"),
                html,
                ModelPageRevitHandler.ModelIdOf(uiapp),
                doc.Title,
                string.IsNullOrWhiteSpace(request.ClientName) ? null : request.ClientName,
                DateTimeOffset.Now);
        }
        catch (ArgumentException ex)
        {
            return Done(request, false, ex.Message);
        }

        var url = service.UrlFor(info.Id, out var urlProblem);
        if (url == null) return Done(request, false, urlProblem ?? "The page could not be served.");

        var warnings = ModelPageHtml.Review(html);
        var opened = false;
        if (ToolArguments.GetBool(request.Arguments, "open", true))
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); opened = true; }
            catch (Exception ex) { warnings.Add("The browser could not be opened: " + ex.Message); }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Published '{info.Title}' at {url}" + (opened ? " and opened it in the browser." : "."),
            Data = new
            {
                pageId = info.Id,
                url,
                index = service.Url,
                title = info.Title,
                model = info.ProjectName,
                bytes = info.Bytes,
                opened,
                note = "The link works on this computer while Revit is running with this model active. Pass pageId to replace the page and keep its link."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    /// <summary>Reads an .html file the agent wrote; returns the problem, or null when <paramref name="html"/> is set.</summary>
    private static string? ReadFile(string path, out string html)
    {
        html = string.Empty;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            var extension = Path.GetExtension(full);
            if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                return "htmlPath must point to an .html file.";
            var file = new FileInfo(full);
            if (!file.Exists) return $"File not found: {full}";
            if (file.Length > ModelPageStore.MaxHtmlBytes)
                return $"The file is {file.Length / 1024:N0} KB; the limit is {ModelPageStore.MaxHtmlBytes / 1024:N0} KB.";
            html = File.ReadAllText(full, Encoding.UTF8);
            return null;
        }
        catch (Exception ex)
        {
            return "The file could not be read: " + ex.Message;
        }
    }

    internal static Task<McpToolResult> Done(McpToolRequest request, bool success, string message) =>
        Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = success, Message = message });
}

/// <summary>Lists the published model pages. Read-only: revit_remove_page deletes one.</summary>
public class ListPagesTool : IRevitMcpTool
{
    public string Name => "revit_list_pages";
    public string Description => "Lists the published model pages with their links; revit_remove_page deletes one.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = ModelPageService.Current;
        if (service == null) return PublishPageTool.Done(request, false, "Model pages are not available in this Revit session.");

        var pages = service.Store.List();
        string? activeModel = null;
        if (uiapp.ActiveUIDocument?.Document != null) activeModel = ModelPageRevitHandler.ModelIdOf(uiapp);

        // Links are only given once something is served; listing alone does not open a port for nothing.
        string? problem = null;
        var rows = pages.Select(p => new
        {
            pageId = p.Id,
            title = p.Title,
            model = p.ProjectName,
            forActiveModel = activeModel != null && p.ModelId == activeModel,
            updated = p.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
            bytes = p.Bytes,
            url = service.UrlFor(p.Id, out problem)
        }).ToList();

        var warnings = new List<string>();
        if (rows.Count > 0 && problem != null) warnings.Add(problem);
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{rows.Count} published page{(rows.Count == 1 ? "" : "s")}.",
            Data = new { index = service.Url, pages = rows },
            Warnings = warnings
        });
    }
}

/// <summary>
/// revit_remove_page (H1): deletes one published page, split off revit_list_pages so the list stays read-only.
/// The model is not changed, so it needs no approval.
/// </summary>
public class RemovePageTool : IRevitMcpTool
{
    public string Name => "revit_remove_page";
    public string Description => "Deletes one published model page (pageId, from revit_list_pages); its link stops working. The model is not changed.";
    public ToolPermission Permission => ToolPermission.DirectEdit;
    public ToolCategory Category => ToolCategory.Reports;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var service = ModelPageService.Current;
        if (service == null) return PublishPageTool.Done(request, false, "Model pages are not available in this Revit session.");

        var pageId = ToolArguments.GetString(request.Arguments, "pageId").Trim();
        if (pageId.Length == 0) return PublishPageTool.Done(request, false, "pageId is required.");
        return service.Store.Remove(pageId)
            ? PublishPageTool.Done(request, true, $"Removed page '{pageId}'. The model was not changed.")
            : PublishPageTool.Done(request, false, $"No page with id '{pageId}'.");
    }
}

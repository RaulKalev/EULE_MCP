using System.Diagnostics;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_preview_remove_links (#90): what removing links would delete, no changes.</summary>
public sealed class PreviewRemoveLinksTool : IRevitMcpTool
{
    public string Name => "revit_preview_remove_links";
    public string Description =>
        "Previews removing DWG/IFC/RVT links from a document WITHOUT changing anything: link type, path, instances and " +
        "the views the instances are visible in. links: link names or type ids (see revit_list_open_documents).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(RemoveLinksExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_remove_links (#90): deletes link types and their instances; approval unless Direct Edit is on.</summary>
public sealed class RemoveLinksTool : IRevitMcpTool
{
    public string Name => "revit_remove_links";
    public string Description =>
        "DESTRUCTIVE: removes DWG/IFC/RVT links (link type and all its instances) from a document, active or not. " +
        "Requires approval unless Direct Edit is on (unattended batches); run revit_preview_remove_links first. deleteIfcCacheFile=true also deletes " +
        "an IFC link's intermediate .ifc.RVT file (off by default).";
    // Direct Edit may skip the approval so link clean-up can run unattended (#90); selectors that match several
    // links are refused, and the removal is a single transaction that rolls back on any error.
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(RemoveLinksExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class RemoveLinksExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = DocumentToolSupport.ResolveTarget(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var selectors = ToolArguments.GetStringArray(request.Arguments, "links");
        if (selectors.Length == 0)
            return DocumentToolSupport.Fail(request, "links is required: link names or type ids.");

        var errors = new List<string>();
        var links = DocumentToolSupport.ResolveLinks(LinkInventory.Collect(doc), selectors, errors);
        if (errors.Count > 0)
            return DocumentToolSupport.Fail(request, "Nothing was removed. " + string.Join(" ", errors), "validation_failed");

        var deleteCache = ToolArguments.GetBool(request.Arguments, "deleteIfcCacheFile", false);

        if (!apply)
        {
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(v => !v.IsTemplate && v.ViewType != ViewType.Schedule && v.ViewType != ViewType.DrawingSheet)
                .ToList();
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Would remove {links.Count} link(s) with {links.Sum(l => l.Instances.Count)} instance(s) from '{doc.Title}'.",
                Data = new
                {
                    document = DocumentToolSupport.DescribeDocument(uiapp, doc),
                    links = links.Select(l => new
                    {
                        typeId = l.TypeId,
                        name = l.Name,
                        kind = l.Kind.ToString().ToUpperInvariant(),
                        path = l.Path,
                        instanceIds = l.Instances.Select(i => i.Id.Value).ToList(),
                        viewsNotHidingIt = NotHiddenIn(l, views),
                        ifcCacheFile = l.Kind == LinkKind.Ifc ? CachePath(l) : null,
                        ifcCacheFileWouldBeDeleted = l.Kind == LinkKind.Ifc && deleteCache && CachePath(l) != null
                    }).ToList()
                },
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var blocked = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (blocked != null) return DocumentToolSupport.Fail(request, blocked);

        var results = new List<object>();
        var cacheFiles = new List<string>();
        var transaction = RevitTransactionRunner.Run(doc, "Revit MCP - Remove links", () =>
        {
            foreach (var link in links)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Read everything before the delete: the link type element is invalid afterwards.
                var typeId = link.TypeId;
                var instanceCount = link.Instances.Count;
                var cache = link.Kind == LinkKind.Ifc ? CachePath(link) : null;
                var deleted = doc.Delete(new ElementId(typeId));
                if (deleteCache && cache != null) cacheFiles.Add(cache);
                results.Add(new
                {
                    typeId,
                    name = link.Name,
                    kind = link.Kind.ToString().ToUpperInvariant(),
                    instancesRemoved = instanceCount,
                    elementsDeleted = deleted?.Count ?? 0
                });
            }
        });

        var warnings = new List<string>();
        if (transaction.Success)
        {
            foreach (var file in cacheFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch (Exception ex)
                {
                    warnings.Add($"IFC cache file {file} was not deleted: {ex.Message}");
                }
            }
        }

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = transaction.Success,
            Status = transaction.Success ? null : "transaction_failed",
            Message = transaction.Success
                ? $"Removed {links.Count} link(s) from '{doc.Title}'. Save the document with revit_save_document."
                : $"Removing links failed and was rolled back: {transaction.Diagnostics.OriginalError ?? "unknown transaction failure"}",
            Data = new
            {
                document = DocumentToolSupport.DescribeDocument(uiapp, doc),
                results = transaction.Success ? results : new List<object>(),
                deletedCacheFiles = transaction.Success ? cacheFiles.Where(f => !File.Exists(f)).ToList() : new List<string>(),
                transaction = transaction.Diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>Views where an instance is not hidden element-wise (category/V/G and view range are not evaluated).</summary>
    private static object NotHiddenIn(LinkEntry link, List<View> views)
    {
        const int MaxListed = 50;
        var result = new List<object>();
        var count = 0;
        foreach (var view in views)
        {
            try
            {
                if (!link.Instances.Any(i => !i.IsHidden(view))) continue;
                count++;
                if (result.Count < MaxListed)
                    result.Add(new { viewId = view.Id.Value, viewName = view.Name });
            }
            catch
            {
                // Views that cannot show the link are skipped.
            }
        }
        return new { count, views = result, truncated = count > result.Count };
    }

    /// <summary>The .ifc.RVT next to the IFC an IFC link was made from, when it exists.</summary>
    private static string? CachePath(LinkEntry link)
    {
        if (link.Path.Length == 0) return null;
        var candidate = link.Path.EndsWith(".ifc.rvt", StringComparison.OrdinalIgnoreCase) ? link.Path : link.Path + ".RVT";
        return File.Exists(candidate) ? candidate : null;
    }
}

using System.Diagnostics;
using System.IO;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Documents;

/// <summary>revit_preview_reload_links_from (#90): pairs chat-provided files with existing links, no changes.</summary>
public sealed class PreviewReloadLinksTool : IRevitMcpTool
{
    public string Name => "revit_preview_reload_links_from";
    public string Description =>
        "Previews reloading DWG/IFC/RVT links of a document from new files WITHOUT changing anything. " +
        "links: [{path, link?}] — link (existing link name or type id) is optional; without it the file is matched to a " +
        "link of the same type by name. Returns the pairing; ambiguous pairs carry a proposal to confirm with the user.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ReloadLinksExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_reload_links_from (#90): reloads links from new files; approval unless Direct Edit is on.</summary>
public sealed class ReloadLinksTool : IRevitMcpTool
{
    public string Name => "revit_reload_links_from";
    public string Description =>
        "Reloads DWG/IFC/RVT links of a document (active or not) from new files. Requires approval unless Direct Edit " +
        "is on (so a batch can run unattended); run revit_preview_reload_links_from first. Refuses to run while any file is ambiguous or unmatched. DWG and RVT links " +
        "reload in place; an IFC link from the same file updates in place, from a new file it is replaced (ifcMethod: auto|update|replace). " +
        "Clears the document's undo history.";
    // Not DestructiveRequiresManualApproval: reloading links is the unattended batch job of #90, so Direct Edit may
    // skip the approval. Ambiguous or unmatched files still stop the run before anything changes.
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ReloadLinksExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class ReloadLinksExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = DocumentToolSupport.ResolveTarget(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var errors = new List<string>();
        var requests = ParseRequests(request.Arguments, errors);
        if (errors.Count > 0) return DocumentToolSupport.Fail(request, string.Join(" ", errors));

        var placement = ToolArguments.GetString(request.Arguments, "placement", LinkReloadService.PlacementMatchExisting);
        if (placement != LinkReloadService.PlacementMatchExisting &&
            placement != LinkReloadService.PlacementOrigin &&
            placement != LinkReloadService.PlacementShared)
            return DocumentToolSupport.Fail(request, "placement must be matchExisting, origin or shared.");
        var regenerateIfcCache = ToolArguments.GetBool(request.Arguments, "regenerateIfcCache", false);
        var ifcMethod = ToolArguments.GetString(request.Arguments, "ifcMethod", LinkReloadService.IfcMethodAuto);
        if (ifcMethod != LinkReloadService.IfcMethodAuto &&
            ifcMethod != LinkReloadService.IfcMethodUpdate &&
            ifcMethod != LinkReloadService.IfcMethodReplace)
            return DocumentToolSupport.Fail(request, "ifcMethod must be auto, update or replace.");

        var links = LinkInventory.Collect(doc);
        var pairs = LinkFileMatcher.Match(links.Select(l => l.ToCandidate()).ToList(), requests);
        var ready = pairs.All(p => p.Status == LinkReloadPair.Matched);
        var plan = pairs.Select(p => DescribePair(p, links, ifcMethod)).ToList();

        if (!apply || !ready)
        {
            sw.Stop();
            var message = ready
                ? $"{pairs.Count} file(s) paired with links in '{doc.Title}'. Ready for revit_reload_links_from."
                : $"Not every file could be paired with a link in '{doc.Title}'; nothing was reloaded. Confirm or fix the pairs " +
                  "by passing link for each file.";
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = !apply,
                Status = apply ? "validation_failed" : null,
                Message = message,
                Data = new
                {
                    document = DocumentToolSupport.DescribeDocument(uiapp, doc),
                    readyToReload = ready,
                    pairs = plan
                },
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        var blocked = DocumentToolSupport.CheckNoOpenTransaction(doc);
        if (blocked != null) return DocumentToolSupport.Fail(request, blocked);

        var outcomes = new List<LinkReloadOutcome>();
        foreach (var pair in pairs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var link = links.First(l => l.TypeId == pair.Link!.TypeId);
            outcomes.Add(LinkReloadService.Reload(uiapp.Application, doc, link, pair.Path, placement, ifcMethod, regenerateIfcCache, cancellationToken));
        }

        sw.Stop();
        var succeeded = outcomes.Count(o => o.Success);
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = succeeded == outcomes.Count,
            Message = $"Reloaded {succeeded}/{outcomes.Count} link(s) in '{doc.Title}'." +
                      (succeeded < outcomes.Count ? " See results for the errors." : " Save the document with revit_save_document."),
            Data = new
            {
                document = DocumentToolSupport.DescribeDocument(uiapp, doc),
                results = outcomes
            },
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static List<LinkReloadRequest> ParseRequests(Dictionary<string, object?> args, List<string> errors)
    {
        var array = DocumentToolSupport.GetObjectArray(args, "links", errors);
        if (array == null || array.Count == 0)
        {
            if (errors.Count == 0) errors.Add("links is required: [{\"path\": \"C:\\\\...\\\\file.ifc\", \"link\": \"optional existing link name\"}].");
            return new List<LinkReloadRequest>();
        }

        var result = new List<LinkReloadRequest>();
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i];
            var path = item is JObject obj ? obj.Value<string>("path") : item.Type == JTokenType.String ? item.Value<string>() : null;
            var link = item is JObject withLink ? withLink.Value<string>("link") : null;
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add($"links[{i}].path is required.");
                continue;
            }
            if (!Path.IsPathRooted(path!.Trim().Trim('"')))
            {
                errors.Add($"links[{i}].path must be a full path: {path}");
                continue;
            }
            result.Add(new LinkReloadRequest { Path = path.Trim().Trim('"'), Link = link });
        }
        return result;
    }

    private static object DescribePair(LinkReloadPair pair, List<LinkEntry> links, string ifcMethod)
    {
        var link = pair.Link == null ? null : links.FirstOrDefault(l => l.TypeId == pair.Link.TypeId);
        return new
        {
            path = pair.Path,
            fileExists = File.Exists(pair.Path),
            kind = pair.Kind?.ToString().ToUpperInvariant(),
            status = pair.Status,
            reason = pair.Reason,
            link = link == null ? null : new
            {
                typeId = link.TypeId,
                name = link.Name,
                currentPath = link.Path,
                instanceCount = link.Instances.Count
            },
            action = link == null || pair.Status != LinkReloadPair.Matched ? null : ActionFor(link, pair.Path, ifcMethod),
            alternatives = pair.Status == LinkReloadPair.Matched
                ? null
                : pair.Alternatives.Select(a => new { typeId = a.TypeId, name = a.Name, path = a.Path }).ToList()
        };
    }

    // Live test (Revit 2026): UpdateFromIFC with a different IFC path converts the new IFC but leaves the link
    // pointing at the old one, so a new file is in practice always a replacement; the same file updates in place.
    private static string ActionFor(LinkEntry link, string newPath, string ifcMethod) => link.Kind switch
    {
        LinkKind.Dwg => "reload in place (instances and overrides kept)",
        LinkKind.Rvt => "reload in place",
        LinkKind.Ifc when ifcMethod != LinkReloadService.IfcMethodReplace && IsSameIfc(link.Path, newPath) =>
            "update in place from the same IFC (ids and overrides kept)",
        LinkKind.Ifc when ifcMethod == LinkReloadService.IfcMethodUpdate =>
            "update in place only - expected to fail for a different IFC file",
        LinkKind.Ifc => "replace: link the new IFC, place it like the old one (position, workset, pin, hidden views, " +
                        "overrides), delete the old link (ids change)",
        _ => "not supported"
    };

    private static bool IsSameIfc(string existing, string wanted)
    {
        if (existing.Length == 0) return false;
        var e = DocumentTargetMatcher.NormalizePath(existing);
        var w = DocumentTargetMatcher.NormalizePath(wanted);
        return e == w || e == w + ".RVT";
    }
}

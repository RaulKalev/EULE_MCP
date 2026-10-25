using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;                           // ToolArguments
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom;

/// <summary>
/// MCP tool: ifc_list_links
/// Returns all Revit link instances in the active document, identifying which are
/// likely derived from IFC models.
/// Phase 1: read-only, no document modifications.
/// </summary>
public class ListIfcLinksTool : IRevitMcpTool
{
    public string Name        => "ifc_list_links";
    public string Description => "Lists the links likely derived from IFC models (includeAllRevitLinks=true: every link, flagged honestly; includeUnloaded=true: unloaded ones too). Use the returned linkInstanceId values with ifc_preview_spaces.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Coordination;

    private readonly IfcLinkDiscoveryService _discovery = new();

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var documentTarget = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (documentTarget == null) return Task.FromResult(documentFailure!);
        var doc = documentTarget.Document;

        var includeAll      = ToolArguments.GetBool(request.Arguments, "includeAllRevitLinks", false);
        var includeUnloaded = ToolArguments.GetBool(request.Arguments, "includeUnloaded",      false);

        var links = _discovery.GetLinks(doc);

        // includeAllRevitLinks lists the .rvt links too (still flagged honestly); includeUnloaded lists unloaded ones (M7).
        var responseLinks = links
            .Where(l => includeAll || l.IsLikelyIfc)
            .Where(l => includeUnloaded || l.IsLoaded)
            .ToList();

        var totalLinks    = links.Count;
        var likelyIfc     = links.Count(l => l.IsLikelyIfc);
        var loadedLinks   = links.Count(l => l.IsLoaded);
        var unloadedLinks = links.Count(l => !l.IsLoaded);

        sw.Stop();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success   = true,
            Message   = $"Found {loadedLinks} loaded link(s); {likelyIfc} likely IFC-derived.",
            Data      = new
            {
                success = true,
                links   = responseLinks,
                summary = new
                {
                    totalLinks,
                    likelyIfcLinks = likelyIfc,
                    loadedLinks,
                    unloadedLinks
                }
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

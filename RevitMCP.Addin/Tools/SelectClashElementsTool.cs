using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SelectClashElementsTool : IRevitMcpTool
{
    public string Name => "revit_select_clash_elements";
    public string Description => "Selects the source and target elements of a clash in the Revit UI. For linked-model targets, selects the RevitLinkInstance instead. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashRunCacheService _cache = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var clashId = ToolArguments.GetString(request.Arguments, "clashId");
        if (string.IsNullOrWhiteSpace(clashId)) return Task.FromResult(Fail(request, "clashId is required."));

        var run = _cache.Load();
        if (run == null) return Task.FromResult(Fail(request, "No clash run in cache. Run a detection first."));

        var clash = run.Clashes.FirstOrDefault(c =>
            c.ClashId.Equals(clashId, StringComparison.OrdinalIgnoreCase));
        if (clash == null) return Task.FromResult(Fail(request, $"ClashId '{clashId}' not found in last run."));

        var elementIds = new List<ElementId>();
        var warnings = new List<string>();

        var srcElem = doc.GetElement(new ElementId(clash.Source.ElementId));
        if (srcElem != null) elementIds.Add(srcElem.Id);

        if (!clash.Target.LinkInstanceId.HasValue)
        {
            var tgtElem = doc.GetElement(new ElementId(clash.Target.ElementId));
            if (tgtElem != null) elementIds.Add(tgtElem.Id);
        }
        else
        {
            var linkInst = doc.GetElement(new ElementId(clash.Target.LinkInstanceId.Value));
            if (linkInst != null)
            {
                elementIds.Add(linkInst.Id);
                warnings.Add($"Target element is in a linked model ('{clash.Target.LinkName ?? "(unknown)"}', element ID {clash.Target.ElementId}). The link instance was selected instead.");
            }
        }

        if (elementIds.Count > 0)
            uidoc.Selection.SetElementIds(elementIds);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {elementIds.Count} element(s) for clash {clashId}.",
            Data = new { clashId, selectedCount = elementIds.Count, selectedIds = elementIds.Select(id => id.Value).ToList() },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

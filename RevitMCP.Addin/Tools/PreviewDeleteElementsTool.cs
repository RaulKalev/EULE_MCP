using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDeleteElementsTool : IRevitMcpTool
{
    public string Name => "revit_preview_delete_elements";
    public string Description =>
        "Previews which model elements would be deleted WITHOUT making any changes. " +
        "Required: elementIds (long array). " +
        "Optional: skipPinned (bool, default true — protect pinned elements). " +
        "Views and sheets are excluded — use revit_preview_delete_views / revit_preview_delete_sheets. " +
        "Returns proposals: elementId, name, category, typeName, level, isPinned, groupName, wouldDelete, reason. " +
        "Deleting an element also deletes its dependents (tags, dimensions, hosted elements).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var skipPinned = ToolArguments.GetBool(request.Arguments, "skipPinned", true);

        if (elementIds.Length == 0)
            return Task.FromResult(Fail(request, "elementIds is required."));

        var proposals   = new List<object>();
        var warnings    = new List<string>();
        int wouldDelete = 0;
        int wouldSkip   = 0;

        foreach (var id in elementIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var el = doc.GetElement(new ElementId(id));
            if (el == null)
            {
                wouldSkip++;
                proposals.Add(new { elementId = id, name = (string?)null, category = (string?)null, typeName = (string?)null, level = (string?)null, isPinned = false, groupName = (string?)null, wouldDelete = false, reason = "Element not found" });
                continue;
            }
            if (el is View)
            {
                wouldSkip++;
                var toolHint = el is ViewSheet ? "revit_preview_delete_sheets" : "revit_preview_delete_views";
                proposals.Add(new { elementId = id, name = el.Name, category = el.Category?.Name, typeName = (string?)null, level = (string?)null, isPinned = el.Pinned, groupName = (string?)null, wouldDelete = false, reason = $"Is a {(el is ViewSheet ? "sheet" : "view")} — use {toolHint}" });
                continue;
            }

            var typeName  = el.GetTypeId() != ElementId.InvalidElementId ? doc.GetElement(el.GetTypeId())?.Name : null;
            var levelName = el.LevelId != ElementId.InvalidElementId ? doc.GetElement(el.LevelId)?.Name : null;
            var groupName = el.GroupId != ElementId.InvalidElementId ? doc.GetElement(el.GroupId)?.Name : null;

            bool del;
            string reason;
            if (skipPinned && el.Pinned)
            {
                del    = false;
                reason = "Protected: pinned (skipPinned=true)";
            }
            else
            {
                del    = true;
                reason = "Eligible for deletion";
            }
            if (del) wouldDelete++; else wouldSkip++;

            if (del && el is Level)
                warnings.Add($"HIGH RISK: '{el.Name}' ({id}) is a Level — deleting it also deletes everything hosted on or associated with it.");
            if (del && (el is RevitLinkInstance || el is RevitLinkType))
                warnings.Add($"HIGH RISK: '{el.Name}' ({id}) is a Revit link — deleting it removes the link from the project.");
            if (del && groupName != null)
                warnings.Add($"'{el.Name}' ({id}) is in group '{groupName}' — Revit may refuse to delete grouped elements outside group edit mode.");

            proposals.Add(new
            {
                elementId = id,
                name      = el.Name,
                category  = el.Category?.Name,
                typeName,
                level     = levelName,
                isPinned  = el.Pinned,
                groupName,
                wouldDelete = del,
                reason
            });
        }

        if (wouldDelete > 0)
            warnings.Add($"WARNING: {wouldDelete} element(s) will be permanently deleted, together with their dependent elements. Use revit_delete_elements to confirm.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {wouldDelete} element(s) would be deleted, {wouldSkip} skipped/protected.",
            Data       = new { wouldDelete, wouldSkip, proposals },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

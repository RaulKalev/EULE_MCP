using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// DESTRUCTIVE — permanently deletes model elements and their dependents.
/// Always requires manual approval (DestructiveRequiresManualApproval).
/// Direct Edit mode does NOT bypass this approval gate.
/// </summary>
public class DeleteElementsTool : IRevitMcpTool
{
    public string Name => "revit_delete_elements";
    public string Description =>
        "DESTRUCTIVE: Permanently deletes model elements and their dependents (tags, dimensions, hosted elements). " +
        "Always requires manual approval — cannot be bypassed. " +
        "Required: elementIds (long array). " +
        "Optional: skipPinned (bool, default true — skips pinned elements). " +
        "Views and sheets are skipped — use revit_delete_views / revit_delete_sheets. " +
        "Always run revit_preview_delete_elements first.";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
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

        var warnings       = new List<string>();
        var toDelete       = new List<(ElementId Id, long IdValue, string Name)>();
        int skippedMissing = 0;
        int skippedViews   = 0;
        int skippedPinned  = 0;

        foreach (var id in elementIds.Distinct())
        {
            var el = doc.GetElement(new ElementId(id));
            if (el == null)
            {
                skippedMissing++;
                warnings.Add($"Element {id} not found — skipped.");
            }
            else if (el is View)
            {
                skippedViews++;
                var toolHint = el is ViewSheet ? "revit_delete_sheets" : "revit_delete_views";
                warnings.Add($"Element {id} ('{el.Name}') is a {(el is ViewSheet ? "sheet" : "view")} — use {toolHint} instead — skipped.");
            }
            else if (skipPinned && el.Pinned)
            {
                skippedPinned++;
                warnings.Add($"Element {id} ('{el.Name}') is pinned — skipped (skipPinned=true).");
            }
            else
            {
                toDelete.Add((el.Id, id, el.Name));
            }
        }

        if (toDelete.Count == 0)
            return Task.FromResult(Fail(request, "No deletable elements in the selection — see warnings for the reason each element was skipped.", warnings));

        int deleted            = 0;
        int removedAsDependent = 0;
        int totalRemoved       = 0;
        var failures           = new List<object>();

        cancellationToken.ThrowIfCancellationRequested();
        using var t = new Transaction(doc, "Revit MCP - Delete Elements");
        t.Start();
        foreach (var (eid, idValue, name) in toDelete)
        {
            if (doc.GetElement(eid) == null)
            {
                removedAsDependent++; // gone already as a dependent of an earlier delete
                continue;
            }
            try
            {
                var removed = doc.Delete(eid);
                if (removed.Count > 0)
                {
                    deleted++;
                    totalRemoved += removed.Count;
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not delete element '{name}' ({idValue}): {ex.Message}");
                failures.Add(new { elementId = idValue, name, error = ex.Message });
            }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);

        sw.Stop();
        int gone   = deleted + removedAsDependent;
        int failed = toDelete.Count - gone;
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = gone > 0,
            Message    = $"Deleted {gone} of {toDelete.Count} element(s) ({totalRemoved} removed in total including dependents), " +
                         $"skipped {skippedMissing + skippedViews + skippedPinned}, failed {failed}.",
            Data       = new { deleted, removedAsDependent, totalRemoved, skippedMissing, skippedViews, skippedPinned, failed, failures },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg, List<string>? warnings = null) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg, Warnings = warnings ?? new List<string>() };
}

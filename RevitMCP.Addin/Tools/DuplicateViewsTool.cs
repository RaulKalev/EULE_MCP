using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class DuplicateViewsTool : IRevitMcpTool
{
    public string Name => "revit_duplicate_views";
    public string Description =>
        "Duplicates views. Requires approval. " +
        "Required: viewIds (long array). " +
        "Optional: duplicateOption (Duplicate|DuplicateWithDetailing|AsDependent, default DuplicateWithDetailing), " +
        "nameSuffix (string, default \" - Copy\"), namePrefix (string, default \"\"). " +
        "Use revit_preview_duplicate_views to verify first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var viewIds         = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var duplicateOption = ToolArguments.GetString(request.Arguments, "duplicateOption", "DuplicateWithDetailing");
        var nameSuffix      = ToolArguments.GetString(request.Arguments, "nameSuffix", " - Copy");
        var namePrefix      = ToolArguments.GetString(request.Arguments, "namePrefix", "");

        if (viewIds.Length == 0)
            return Task.FromResult(Fail(request, "viewIds is required."));

        if (!Enum.TryParse<ViewDuplicateOption>(duplicateOption, true, out var dupOpt))
            return Task.FromResult(Fail(request, $"Unknown duplicateOption '{duplicateOption}'. Valid: Duplicate, DuplicateWithDetailing, AsDependent."));

        int duplicated = 0;
        var warnings   = new List<string>();
        var results    = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Duplicate Views");
        t.Start();
        foreach (var vid in viewIds)
        {
            if (doc.GetElement(new ElementId(vid)) is not View v)
            {
                warnings.Add($"View {vid} not found."); continue;
            }
            if (!v.CanViewBeDuplicated(dupOpt))
            {
                warnings.Add($"View '{v.Name}' cannot be duplicated with option '{duplicateOption}'."); continue;
            }

            try
            {
                var newId   = v.Duplicate(dupOpt);
                var newView = doc.GetElement(newId) as View;
                var newName = namePrefix + v.Name + nameSuffix;
                if (newView != null)
                {
                    try { newView.Name = newName; } catch { /* name may conflict */ }
                }
                duplicated++;
                results.Add(new { sourceViewId = vid, newViewId = newId.Value, newName = newView?.Name ?? newName });
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to duplicate '{v.Name}': {ex.Message}");
            }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = duplicated > 0,
            Message    = $"Duplicated {duplicated}/{viewIds.Length} view(s).",
            Data       = new { duplicated, results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

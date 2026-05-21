using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class SelectElementsTool : IRevitMcpTool
{
    public string Name => "revit_select_elements";
    public string Description => "Selects elements in the active Revit UI by explicit element IDs. Does not modify model data.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Selection;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null)
            return Task.FromResult(Fail(request, "No active document."));

        var elementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        var replaceSelection = ToolArguments.GetBool(request.Arguments, "replaceSelection", true);
        var zoomToSelection = ToolArguments.GetBool(request.Arguments, "zoomToSelection");

        if (elementIds.Length == 0)
            return Task.FromResult(Fail(request, "elementIds is required."));

        var doc = uidoc.Document;
        var validIds = new List<ElementId>();
        var invalidIds = new List<long>();

        foreach (var id in elementIds)
        {
            var eid = new ElementId(id);
            if (doc.GetElement(eid) != null)
                validIds.Add(eid);
            else
                invalidIds.Add(id);
        }

        if (validIds.Count == 0)
            return Task.FromResult(Fail(request, "None of the provided element IDs exist in the model."));

        // Build final selection set
        ICollection<ElementId> finalIds;
        if (replaceSelection)
        {
            finalIds = validIds;
        }
        else
        {
            var existing = uidoc.Selection.GetElementIds().ToList();
            existing.AddRange(validIds);
            finalIds = existing.Distinct().ToList();
        }

        uidoc.Selection.SetElementIds(finalIds);

        if (zoomToSelection && validIds.Count > 0)
        {
            try { uidoc.ShowElements(validIds); }
            catch { }
        }

        var warnings = new List<string>();
        if (invalidIds.Count > 0)
            warnings.Add($"{invalidIds.Count} element IDs not found: {string.Join(", ", invalidIds.Take(10))}");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Selected {validIds.Count} elements.",
            Data = new
            {
                selectedCount = validIds.Count,
                selectedElementIds = validIds.Select(id => id.Value).ToList(),
                invalidCount = invalidIds.Count
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

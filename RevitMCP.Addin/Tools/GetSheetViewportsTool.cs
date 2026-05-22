using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetSheetViewportsTool : IRevitMcpTool
{
    public string Name => "revit_get_sheet_viewports";
    public string Description =>
        "Returns detailed viewport information for one or more sheets. " +
        "Required: sheetIds (long array) or sheetNumbers (string array). " +
        "Returns per viewport: viewportId, viewId, viewName, viewType, sheetPosition (u,v), " +
        "detailNumber, referenceLabel.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var sheetIds     = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");

        if (sheetIds.Length == 0 && sheetNumbers.Length == 0)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "Provide sheetIds or sheetNumbers." });

        // Resolve sheets
        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .ToList();

        IEnumerable<ViewSheet> targetSheets;
        if (sheetIds.Length > 0)
        {
            var idSet = sheetIds.ToHashSet();
            targetSheets = allSheets.Where(s => idSet.Contains(s.Id.Value));
        }
        else
        {
            var numSet = sheetNumbers.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            targetSheets = allSheets.Where(s => numSet.Contains(s.SheetNumber));
        }

        var resultSheets = new List<object>();
        foreach (var sheet in targetSheets)
        {
            var viewports = new List<object>();
            try
            {
                foreach (var vpId in sheet.GetAllViewports())
                {
                    if (doc.GetElement(vpId) is not Viewport vp) continue;
                    var linkedView = doc.GetElement(vp.ViewId) as View;
                    var center     = vp.GetBoxCenter();

                    viewports.Add(new
                    {
                        viewportId      = vp.Id.Value,
                        viewId          = vp.ViewId.Value,
                        viewName        = linkedView?.Name,
                        viewType        = linkedView?.ViewType.ToString(),
                        sheetPositionU  = Math.Round(center.X, 6),
                        sheetPositionV  = Math.Round(center.Y, 6),
                        detailNumber    = TryGet(vp, BuiltInParameter.VIEWPORT_DETAIL_NUMBER),
                        referenceLabel  = TryGetStr(vp, "Reference Label")
                    });
                }
            }
            catch { }

            resultSheets.Add(new
            {
                sheetId        = sheet.Id.Value,
                sheetNumber    = sheet.SheetNumber,
                sheetName      = sheet.Name,
                viewportCount  = viewports.Count,
                viewports
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Returned viewport info for {resultSheets.Count} sheet(s).",
            Data       = new { sheetCount = resultSheets.Count, sheets = resultSheets },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static string? TryGet(Element e, BuiltInParameter bip)
    {
        try { return e.get_Parameter(bip)?.AsValueString() ?? e.get_Parameter(bip)?.AsString(); }
        catch { return null; }
    }

    private static string? TryGetStr(Element e, string paramName)
    {
        try { return e.LookupParameter(paramName)?.AsString(); }
        catch { return null; }
    }
}

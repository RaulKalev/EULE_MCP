using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListTitleBlocksTool : IRevitMcpTool
{
    public string Name => "revit_list_titleblocks";
    public string Description =>
        "Lists all title block family symbols loaded in the active document. " +
        "Returns: familySymbolId, familyName, typeName, isInUse. " +
        "Use the familySymbolId when creating sheets with revit_create_sheets_from_table.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        // Find which titleblock symbols are actually in use on sheets
        var inUseIds = new HashSet<ElementId>();
        foreach (var sheet in new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
        {
            try
            {
                var tb = new FilteredElementCollector(doc)
                    .OwnedByView(sheet.Id)
                    .OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .Cast<FamilyInstance>()
                    .FirstOrDefault();
                if (tb != null) inUseIds.Add(tb.GetTypeId());
            }
            catch { }
        }

        var titleblocks = new FilteredElementCollector(doc)
            .OfClass(typeof(FamilySymbol))
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .Cast<FamilySymbol>()
            .OrderBy(fs => fs.FamilyName)
            .ThenBy(fs => fs.Name)
            .Select(fs => new
            {
                familySymbolId = fs.Id.Value,
                familyName     = fs.FamilyName,
                typeName       = fs.Name,
                isInUse        = inUseIds.Contains(fs.Id)
            })
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success   = true,
            Message   = $"Returned {titleblocks.Count} title block types.",
            Data      = new { count = titleblocks.Count, titleblocks },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

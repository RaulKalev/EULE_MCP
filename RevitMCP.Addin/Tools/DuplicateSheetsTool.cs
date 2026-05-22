using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class DuplicateSheetsTool : IRevitMcpTool
{
    public string Name => "revit_duplicate_sheets";
    public string Description =>
        "Duplicates sheets (creates new empty sheets with same titleblock and copied parameters). Requires approval. " +
        "Required: sourceSheetIds (long array) OR sourceSheetNumbers (string array). " +
        "Optional: newNumberSuffix (string, default \"_COPY\"), newNameSuffix (string, default \" - Copy\"), " +
        "keepTitleBlock (bool, default true), copyParameters (bool, default true). " +
        "Note: Viewports, legends, schedules are NOT duplicated (empty shell only). " +
        "Use revit_preview_duplicate_sheets to verify first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var sourceIds  = ToolArguments.GetLongArray(request.Arguments, "sourceSheetIds");
        var sourceNums = ToolArguments.GetStringArray(request.Arguments, "sourceSheetNumbers");
        var numSuffix  = ToolArguments.GetString(request.Arguments, "newNumberSuffix", "_COPY");
        var nameSuffix = ToolArguments.GetString(request.Arguments, "newNameSuffix", " - Copy");
        var keepTb     = ToolArguments.GetBool(request.Arguments, "keepTitleBlock", true);
        var copyParams = ToolArguments.GetBool(request.Arguments, "copyParameters", true);

        if (sourceIds.Length == 0 && sourceNums.Length == 0)
            return Task.FromResult(Fail(request, "Provide sourceSheetIds or sourceSheetNumbers."));

        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .ToList();

        IEnumerable<ViewSheet> sources = sourceIds.Length > 0
            ? allSheets.Where(s => sourceIds.ToHashSet().Contains(s.Id.Value))
            : allSheets.Where(s => sourceNums.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase).Contains(s.SheetNumber));

        int created  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Duplicate Sheets");
        t.Start();
        foreach (var src in sources)
        {
            try
            {
                var newNum  = src.SheetNumber + numSuffix;
                var newName = src.Name + nameSuffix;

                // Get titleblock type
                ElementId tbTypeId = ElementId.InvalidElementId;
                if (keepTb)
                {
                    var tb = new FilteredElementCollector(doc)
                        .OwnedByView(src.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .Cast<FamilyInstance>()
                        .FirstOrDefault();
                    if (tb != null) tbTypeId = tb.GetTypeId();
                }

                var newSheet = ViewSheet.Create(doc, tbTypeId);
                newSheet.SheetNumber = newNum;
                newSheet.Name        = newName;

                // Copy instance parameters
                if (copyParams)
                {
                    foreach (Parameter srcParam in src.Parameters)
                    {
                        if (srcParam.IsReadOnly) continue;
                        try
                        {
                            var dstParam = newSheet.get_Parameter(srcParam.Definition);
                            if (dstParam == null || dstParam.IsReadOnly) continue;
                            switch (srcParam.StorageType)
                            {
                                case StorageType.String:  dstParam.Set(srcParam.AsString() ?? ""); break;
                                case StorageType.Integer: dstParam.Set(srcParam.AsInteger()); break;
                                case StorageType.Double:  dstParam.Set(srcParam.AsDouble()); break;
                                case StorageType.ElementId: dstParam.Set(srcParam.AsElementId()); break;
                            }
                        }
                        catch { /* skip unwritable params */ }
                    }
                    // Restore sheet number and name (they get overwritten by param copy)
                    newSheet.SheetNumber = newNum;
                    newSheet.Name        = newName;
                }

                created++;
                results.Add(new { sourceSheetId = src.Id.Value, sourceSheetNumber = src.SheetNumber, newSheetId = newSheet.Id.Value, newSheetNumber = newNum, newSheetName = newName });
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to duplicate sheet '{src.SheetNumber}': {ex.Message}");
            }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = created > 0,
            Message    = $"Duplicated {created} sheet(s).",
            Data       = new { created, results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

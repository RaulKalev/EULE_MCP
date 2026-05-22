using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDuplicateSheetsTool : IRevitMcpTool
{
    public string Name => "revit_preview_duplicate_sheets";
    public string Description =>
        "Previews what would be created when duplicating sheets WITHOUT making changes. " +
        "Required: sourceSheetIds (long array) OR sourceSheetNumbers (string array). " +
        "Optional: newNumberSuffix (string appended to sheet number, default \"_COPY\"), " +
        "newNameSuffix (string appended to name, default \" - Copy\"), " +
        "keepTitleBlock (bool, default true), copyParameters (bool, default true). " +
        "Returns proposals: sourceSheetId, sourceSheetNumber, sourceSheetName, newSheetNumber, newSheetName, titleBlockId.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var sourceIds     = ToolArguments.GetLongArray(request.Arguments, "sourceSheetIds");
        var sourceNums    = ToolArguments.GetStringArray(request.Arguments, "sourceSheetNumbers");
        var numSuffix     = ToolArguments.GetString(request.Arguments, "newNumberSuffix", "_COPY");
        var nameSuffix    = ToolArguments.GetString(request.Arguments, "newNameSuffix", " - Copy");
        var keepTb        = ToolArguments.GetBool(request.Arguments, "keepTitleBlock", true);
        var copyParams    = ToolArguments.GetBool(request.Arguments, "copyParameters", true);

        if (sourceIds.Length == 0 && sourceNums.Length == 0)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "Provide sourceSheetIds or sourceSheetNumbers." });

        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .ToList();

        IEnumerable<ViewSheet> sources;
        if (sourceIds.Length > 0)
        {
            var idSet = sourceIds.ToHashSet();
            sources = allSheets.Where(s => idSet.Contains(s.Id.Value));
        }
        else
        {
            var numSet = sourceNums.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            sources = allSheets.Where(s => numSet.Contains(s.SheetNumber));
        }

        var existingNumbers = allSheets.Select(s => s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposals = new List<object>();
        var warnings  = new List<string>();

        foreach (var s in sources)
        {
            var newNum  = s.SheetNumber + numSuffix;
            var newName = s.Name + nameSuffix;

            // Titleblock
            long? tbId   = null;
            string? tbName = null;
            if (keepTb)
            {
                try
                {
                    var tb = new FilteredElementCollector(doc)
                        .OwnedByView(s.Id)
                        .OfCategory(BuiltInCategory.OST_TitleBlocks)
                        .Cast<FamilyInstance>()
                        .FirstOrDefault();
                    if (tb != null)
                    {
                        tbId   = tb.GetTypeId().Value;
                        tbName = $"{tb.Symbol.FamilyName} : {tb.Symbol.Name}";
                    }
                }
                catch { }
            }

            bool conflict = existingNumbers.Contains(newNum);
            if (conflict)
                warnings.Add($"Sheet number '{newNum}' already exists — adjust newNumberSuffix before applying.");

            proposals.Add(new
            {
                sourceSheetId     = s.Id.Value,
                sourceSheetNumber = s.SheetNumber,
                sourceSheetName   = s.Name,
                newSheetNumber    = newNum,
                newSheetName      = newName,
                titleBlockId      = tbId,
                titleBlockName    = tbName,
                willCopyParameters = copyParams,
                conflict
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {proposals.Count} sheet(s) would be duplicated.",
            Data       = new { count = proposals.Count, proposals },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

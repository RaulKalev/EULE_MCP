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

        var sourceList = sources.ToList();
        if (sourceList.Count == 0)
        {
            // Diagnostic failure — helps the LLM understand what was searched
            var searchedDesc = sourceIds.Length > 0
                ? $"IDs [{string.Join(", ", sourceIds)}]"
                : $"numbers [{string.Join(", ", sourceNums)}]";
            var availableNumbers = allSheets.Take(5).Select(s => s.SheetNumber).ToList();
            var availDesc = availableNumbers.Count > 0
                ? $"Sample available sheet numbers: {string.Join(", ", availableNumbers)} (total {allSheets.Count})"
                : "Model has no sheets.";
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success   = false,
                Message   = $"No sheets found matching {searchedDesc}. {availDesc}. " +
                            $"Use revit_list_sheets to retrieve valid element IDs and sheet numbers."
            });
        }

        // Build collision-detection sets; updated per-iteration to handle multi-sheet batches
        var takenNumbers = allSheets.Select(s => s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenNames   = allSheets.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposals = new List<object>();
        var warnings  = new List<string>();

        foreach (var s in sourceList)
        {
            var rawNum  = s.SheetNumber + numSuffix;
            var rawName = s.Name + nameSuffix;
            var newNum  = ResolveUnique(rawNum,  takenNumbers);
            var newName = ResolveUnique(rawName, takenNames);
            takenNumbers.Add(newNum);
            takenNames.Add(newName);

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

            bool numberResolved = !string.Equals(newNum, rawNum, StringComparison.OrdinalIgnoreCase);
            bool nameResolved   = !string.Equals(newName, rawName, StringComparison.OrdinalIgnoreCase);
            if (numberResolved)
                warnings.Add($"Sheet number '{rawNum}' already exists — will use '{newNum}' instead.");
            if (nameResolved)
                warnings.Add($"Sheet name '{rawName}' already exists — will use '{newName}' instead.");

            proposals.Add(new
            {
                sourceSheetId      = s.Id.Value,
                sourceSheetNumber  = s.SheetNumber,
                sourceSheetName    = s.Name,
                newSheetNumber     = newNum,
                newSheetName       = newName,
                titleBlockId       = tbId,
                titleBlockName     = tbName,
                willCopyParameters = copyParams,
                numberResolved,
                nameResolved
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

    private static string ResolveUnique(string candidate, HashSet<string> taken)
    {
        if (!taken.Contains(candidate)) return candidate;
        for (int i = 1; ; i++)
        {
            var s = $"{candidate} {i}";
            if (!taken.Contains(s)) return s;
        }
    }
}

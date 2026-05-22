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

        var sourceList = sources.ToList();
        if (sourceList.Count == 0)
        {
            var searchedDesc = sourceIds.Length > 0
                ? $"IDs [{string.Join(", ", sourceIds)}]"
                : $"numbers [{string.Join(", ", sourceNums)}]";
            return Task.FromResult(Fail(request,
                $"No sheets found matching {searchedDesc}. " +
                $"Use revit_list_sheets to retrieve valid element IDs and sheet numbers."));
        }

        // Build collision-detection sets; updated per-iteration to handle multi-sheet batches
        var takenNumbers = allSheets.Select(s => s.SheetNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenNames   = allSheets.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Duplicate Sheets");
        t.Start();
        foreach (var src in sourceList)
        {
            try
            {
                var newNum  = ResolveUnique(src.SheetNumber + numSuffix, takenNumbers);
                var newName = ResolveUnique(src.Name + nameSuffix, takenNames);
                // Reserve so subsequent sheets in the same batch don't pick the same values
                takenNumbers.Add(newNum);
                takenNames.Add(newName);

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

                // Copy instance parameters (skip sheet number / name — set explicitly above)
                if (copyParams)
                {
                    foreach (Parameter srcParam in src.Parameters)
                    {
                        if (srcParam.IsReadOnly) continue;
                        // Sheet Number and Sheet Name must not be overwritten from source;
                        // Revit defers uniqueness validation and the whole transaction would
                        // fail at Commit() if we write the source's sheet number to the copy.
                        if (srcParam.Definition is InternalDefinition intDef)
                        {
                            var bip = intDef.BuiltInParameter;
                            if (bip == BuiltInParameter.SHEET_NUMBER ||
                                bip == BuiltInParameter.VIEW_NAME)
                                continue;
                        }
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
                }

                created++;
                results.Add(new { sourceSheetId = src.Id.Value, sourceSheetNumber = src.SheetNumber, newSheetId = newSheet.Id.Value, newSheetNumber = newNum, newSheetName = newName });
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to duplicate sheet '{src.SheetNumber}': {ex.Message}");
            }
        }
        var commitStatus = t.Commit();
        if (commitStatus != TransactionStatus.Committed)
        {
            return Task.FromResult(Fail(request,
                $"Transaction did not commit (status: {commitStatus}). " +
                $"No sheets were created. This can happen if a generated sheet number " +
                $"conflicts with an existing one — run revit_preview_duplicate_sheets first."));
        }

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

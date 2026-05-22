using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetSheetRevisionsTool : IRevitMcpTool
{
    public string Name => "revit_get_sheet_revisions";
    public string Description =>
        "Returns revisions assigned/visible on one or more sheets. " +
        "Accepts: sheetIds (long array), sheetNumbers (string array), includeRevisionDetails (bool, default true). " +
        "Returns: sheetId, sheetNumber, sheetName, revisionCount, revisions (revisionId, sequenceNumber, revisionNumber, revisionDate, description, issuedBy, issuedTo).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var sheetIds             = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var sheetNumbers         = ToolArguments.GetStringArray(request.Arguments, "sheetNumbers");
        var includeRevisionDetails = ToolArguments.GetBool(request.Arguments, "includeRevisionDetails", true);

        if (sheetIds.Length == 0 && sheetNumbers.Length == 0)
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId, Success = false,
                Message = "Provide at least one sheetId or sheetNumber."
            });

        // Resolve sheets
        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .ToList();

        var targetSheets = new List<ViewSheet>();
        var warnings     = new List<string>();

        if (sheetIds.Length > 0)
        {
            foreach (var sid in sheetIds)
            {
                var sheet = allSheets.FirstOrDefault(s => s.Id.Value == sid);
                if (sheet == null) warnings.Add($"Sheet with ID {sid} not found.");
                else targetSheets.Add(sheet);
            }
        }

        if (sheetNumbers.Length > 0)
        {
            foreach (var num in sheetNumbers)
            {
                var sheet = allSheets.FirstOrDefault(s => s.SheetNumber.Equals(num, StringComparison.OrdinalIgnoreCase));
                if (sheet == null) warnings.Add($"Sheet with number '{num}' not found.");
                else if (!targetSheets.Any(s => s.Id == sheet.Id)) targetSheets.Add(sheet);
            }
        }

        // Build revision lookup
        var revisionMap = Revision.GetAllRevisionIds(doc)
            .Select(rid => doc.GetElement(rid))
            .OfType<Revision>()
            .ToDictionary(r => r.Id);

        var results = new List<object>();
        foreach (var sheet in targetSheets)
        {
            var revisionIds = GetSheetRevisionIds(sheet, warnings);

            List<object>? revisionDetails = null;
            if (includeRevisionDetails)
            {
                revisionDetails = new List<object>();
                foreach (var rid in revisionIds)
                {
                    if (!revisionMap.TryGetValue(rid, out var rev))
                    {
                        warnings.Add($"Revision element {rid.Value} referenced by sheet '{sheet.SheetNumber}' not found.");
                        continue;
                    }
                    revisionDetails.Add(new
                    {
                        revisionId     = rev.Id.Value,
                        sequenceNumber = rev.SequenceNumber,
                        revisionNumber = TryGetRevisionNumber(rev),
                        revisionDate   = rev.RevisionDate,
                        description    = rev.Description,
                        issuedBy       = rev.IssuedBy,
                        issuedTo       = rev.IssuedTo
                    });
                }
            }

            results.Add(new Dictionary<string, object?>
            {
                ["sheetId"]       = sheet.Id.Value,
                ["sheetNumber"]   = sheet.SheetNumber,
                ["sheetName"]     = sheet.Name,
                ["revisionCount"] = revisionIds.Count,
                ["revisions"]     = (object?)(includeRevisionDetails ? revisionDetails : null)
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Returned revision data for {results.Count} sheet{(results.Count == 1 ? "" : "s")}.",
            Data       = new { sheetCount = results.Count, sheets = results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static List<ElementId> GetSheetRevisionIds(ViewSheet sheet, List<string> warnings)
    {
        // Try GetAdditionalRevisionIds (Revit 2022+)
        var ids = new HashSet<ElementId>();
        try
        {
            foreach (var id in sheet.GetAdditionalRevisionIds()) ids.Add(id);
        }
        catch (Exception ex)
        {
            warnings.Add($"GetAdditionalRevisionIds not available on sheet '{sheet.SheetNumber}': {ex.Message}");
        }

        // GetCloudRevisionIds is not available in this Revit version; cloud revisions are omitted.

        return ids.ToList();
    }

    private static string TryGetRevisionNumber(Revision r)
    {
        try { return r.RevisionNumber; } catch { return string.Empty; }
    }
}

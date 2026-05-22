using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Tools;

public class PreviewCreateSheetsFromTableTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_sheets_from_table";
    public string Description =>
        "Previews sheet creation from a table WITHOUT making changes. " +
        "Required: rows (array of objects with sheetNumber, sheetName, and any extra parameter key-values), " +
        "titleBlockId (long, use revit_list_titleblocks to get ID). " +
        "Returns for each row: sheetNumber, sheetName, valid (bool), conflict (bool), issues (string array).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var rows         = GetRows(request.Arguments);
        var titleBlockId = ToolArguments.GetLong(request.Arguments, "titleBlockId", 0);

        if (rows.Count == 0)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "rows is required." });

        if (titleBlockId == 0)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "titleBlockId is required (use revit_list_titleblocks)." });

        // Validate titleblock exists
        var tbSymbol = doc.GetElement(new ElementId(titleBlockId)) as FamilySymbol;
        var tbValid  = tbSymbol != null;

        var existingNumbers = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Select(s => s.SheetNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Detect duplicates within the input rows themselves
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var proposals = new List<object>();
        int validCount   = 0;
        int conflictCount = 0;

        foreach (var row in rows)
        {
            var issues   = new List<string>();
            var num  = row.TryGetValue("sheetNumber", out var sn) ? sn?.ToString() ?? "" : "";
            var name = row.TryGetValue("sheetName",   out var sname) ? sname?.ToString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(num))  issues.Add("sheetNumber is empty");
            if (string.IsNullOrWhiteSpace(name)) issues.Add("sheetName is empty");
            if (!tbValid)                        issues.Add($"titleBlockId {titleBlockId} not found");

            bool conflict = existingNumbers.Contains(num) || !seen.Add(num);
            if (conflict) issues.Add($"Sheet number '{num}' already exists or is duplicated in rows");

            bool valid = issues.Count == 0;
            if (valid)          validCount++;
            if (conflict)       conflictCount++;

            proposals.Add(new
            {
                sheetNumber = num,
                sheetName   = name,
                valid,
                conflict,
                issues
            });
        }

        var warnings = new List<string>();
        if (!tbValid)        warnings.Add($"Title block ID {titleBlockId} was not found in the document.");
        if (conflictCount > 0) warnings.Add($"{conflictCount} sheet number(s) already exist — resolve conflicts before applying.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {validCount}/{rows.Count} rows are valid and ready to create.",
            Data       = new { total = rows.Count, valid = validCount, conflicts = conflictCount, proposals },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static List<Dictionary<string, object?>> GetRows(Dictionary<string, object?> args)
    {
        if (!args.TryGetValue("rows", out var val)) return [];
        if (val is JArray ja)
            return ja.Children<JObject>()
                .Select(o => o.Properties().ToDictionary(p => p.Name, p => (object?)p.Value.ToObject<object>()))
                .ToList();
        return [];
    }
}

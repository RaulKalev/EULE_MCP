using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Tools;

public class CreateSheetsFromTableTool : IRevitMcpTool
{
    public string Name => "revit_create_sheets_from_table";
    public string Description =>
        "Creates multiple sheets from a table definition. Requires approval. " +
        "Required: rows (array of objects — each must have sheetNumber and sheetName; additional keys become parameter values), " +
        "titleBlockId (long, use revit_list_titleblocks). " +
        "Use revit_preview_create_sheets_from_table first to check for conflicts.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var rows         = GetRows(request.Arguments);
        var titleBlockId = ToolArguments.GetLong(request.Arguments, "titleBlockId", 0);

        if (rows.Count == 0)
            return Task.FromResult(Fail(request, "rows is required."));
        if (titleBlockId == 0)
            return Task.FromResult(Fail(request, "titleBlockId is required."));

        var tbSymbol = doc.GetElement(new ElementId(titleBlockId)) as FamilySymbol;
        if (tbSymbol == null)
            return Task.FromResult(Fail(request, $"Title block with ID {titleBlockId} not found."));
        if (!tbSymbol.IsActive) tbSymbol.Activate();

        var existingNums = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Select(s => s.SheetNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        int created  = 0;
        int skipped  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Create Sheets from Table");
        t.Start();
        foreach (var row in rows)
        {
            var num  = row.TryGetValue("sheetNumber", out var sn) ? sn?.ToString() ?? "" : "";
            var name = row.TryGetValue("sheetName",   out var sname) ? sname?.ToString() ?? "" : "";

            if (string.IsNullOrWhiteSpace(num) || string.IsNullOrWhiteSpace(name))
            {
                warnings.Add($"Skipped row with empty sheetNumber or sheetName.");
                skipped++;
                continue;
            }
            if (existingNums.Contains(num))
            {
                warnings.Add($"Skipped '{num}' — sheet already exists.");
                skipped++;
                continue;
            }

            try
            {
                var sheet = ViewSheet.Create(doc, tbSymbol.Id);
                sheet.SheetNumber = num;
                sheet.Name        = name;

                foreach (var kv in row.Where(r => r.Key != "sheetNumber" && r.Key != "sheetName"))
                {
                    try
                    {
                        var p = sheet.LookupParameter(kv.Key);
                        if (p == null || p.IsReadOnly) continue;
                        var strVal = kv.Value?.ToString() ?? "";
                        switch (p.StorageType)
                        {
                            case StorageType.String:  p.Set(strVal); break;
                            case StorageType.Integer when int.TryParse(strVal, out var iv): p.Set(iv); break;
                            case StorageType.Double  when double.TryParse(strVal, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var dv): p.Set(dv); break;
                        }
                    }
                    catch { /* skip bad param */ }
                }

                existingNums.Add(num);
                created++;
                results.Add(new { sheetNumber = num, sheetName = name, sheetId = sheet.Id.Value });
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to create sheet '{num}': {ex.Message}");
                skipped++;
            }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = created > 0,
            Message    = $"Created {created} sheet(s), skipped {skipped}.",
            Data       = new { created, skipped, results },
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

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

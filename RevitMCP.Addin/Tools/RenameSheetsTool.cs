using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Naming;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class RenameSheetsTool : IRevitMcpTool
{
    public string Name => "revit_rename_sheets";
    public string Description =>
        "Renames sheets (name, number, or both). Requires approval. " +
        "Required: sheetIds (long array) OR (nameFilter/numberFilter) to select sheets, " +
        "mode (FindReplace|PrefixSuffix|Template|RegexFindReplace), " +
        "target (Name|Number|Both, default Name). " +
        "Mode params: find, replace, prefix, suffix, template with {Name} or {Number}. " +
        "Use revit_preview_rename_sheets first to verify.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var sheetIds     = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var mode         = ToolArguments.GetString(request.Arguments, "mode");
        var target       = ToolArguments.GetString(request.Arguments, "target", "Name");
        var find         = ToolArguments.GetString(request.Arguments, "find");
        var replace      = ToolArguments.GetString(request.Arguments, "replace");
        var prefix       = ToolArguments.GetString(request.Arguments, "prefix");
        var suffix       = ToolArguments.GetString(request.Arguments, "suffix");
        var template     = ToolArguments.GetString(request.Arguments, "template");
        var nameFilter   = ToolArguments.GetString(request.Arguments, "nameFilter");
        var numberFilter = ToolArguments.GetString(request.Arguments, "numberFilter");

        var modeError = RenameEngine.ValidateMode(mode);
        if (modeError != null)
            return Task.FromResult(Fail(request, modeError));

        IEnumerable<ViewSheet> sheets;
        if (sheetIds.Length > 0)
        {
            var idSet = sheetIds.ToHashSet();
            sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => idSet.Contains(s.Id.Value));
        }
        else
        {
            sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>();
            if (!string.IsNullOrWhiteSpace(nameFilter))   sheets = sheets.Where(s => s.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(numberFilter)) sheets = sheets.Where(s => s.SheetNumber.Contains(numberFilter, StringComparison.OrdinalIgnoreCase));
        }

        bool applyToName   = target is "Name" or "Both";
        bool applyToNumber = target is "Number" or "Both";

        int renamed  = 0;
        var warnings = new List<string>();
        var results  = new List<object>();

        using var t = new Transaction(doc, "Revit MCP - Rename Sheets");
        t.Start();
        foreach (var s in sheets)
        {
            var tokens    = new Dictionary<string, string> { ["Number"] = s.SheetNumber };
            var newName   = applyToName   ? RenameEngine.Apply(s.Name, mode, find, replace, prefix, suffix, template, tokens) : null;
            var newNumber = applyToNumber ? RenameEngine.Apply(s.SheetNumber, mode, find, replace, prefix, suffix, template, tokens) : null;

            if (newName == null && newNumber == null) continue;

            var oldName   = s.Name;
            var oldNumber = s.SheetNumber;
            bool changed  = false;

            try
            {
                if (newName != null)   { s.Name        = newName;   changed = true; }
                if (newNumber != null) { s.SheetNumber = newNumber; changed = true; }
                if (changed) { renamed++; results.Add(new { sheetId = s.Id.Value, oldNumber, oldName, newNumber = s.SheetNumber, newName = s.Name }); }
            }
            catch (Exception ex)
            {
                warnings.Add($"Failed to rename sheet '{oldNumber}': {ex.Message}");
                // Attempt rollback of partial change
                try { if (newName != null) s.Name = oldName; } catch { }
                try { if (newNumber != null) s.SheetNumber = oldNumber; } catch { }
            }
        }
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = renamed > 0,
            Message    = $"Renamed {renamed} sheet(s).",
            Data       = new { renamed, results },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

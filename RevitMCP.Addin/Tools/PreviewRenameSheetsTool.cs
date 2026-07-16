using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Naming;
using RevitMCP.Addin.Documentation.Sheets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewRenameSheetsTool : IRevitMcpTool
{
    public string Name => "revit_preview_rename_sheets";
    public string Description =>
        "Previews sheet renames WITHOUT making changes. " +
        "Required: sheetIds (long array) OR nameFilter/numberFilter to select sheets, " +
        "mode (FindReplace|PrefixSuffix|Template|RegexFindReplace), " +
        "target (Name|Number|Both|Parameter, default Name); parameterName is required for target=Parameter. " +
        "Mode params: find, replace, prefix, suffix, template with {Name} or {Number}. " +
        "Returns proposals: sheetId, currentNumber, currentName, newNumber, newName, willChange.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "No active document." });

        var sheetIds      = ToolArguments.GetLongArray(request.Arguments, "sheetIds");
        var mode          = ToolArguments.GetString(request.Arguments, "mode");
        var target        = ToolArguments.GetString(request.Arguments, "target", "Name");
        var parameterName = ToolArguments.GetString(request.Arguments, "parameterName");
        var find          = ToolArguments.GetString(request.Arguments, "find");
        var replace       = ToolArguments.GetString(request.Arguments, "replace");
        var prefix        = ToolArguments.GetString(request.Arguments, "prefix");
        var suffix        = ToolArguments.GetString(request.Arguments, "suffix");
        var template      = ToolArguments.GetString(request.Arguments, "template");
        var nameFilter    = ToolArguments.GetString(request.Arguments, "nameFilter");
        var numberFilter  = ToolArguments.GetString(request.Arguments, "numberFilter");

        var modeError = RenameEngine.ValidateMode(mode);
        if (modeError != null)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = modeError });

        bool applyToName = target.Equals("Name", StringComparison.OrdinalIgnoreCase) ||
                           target.Equals("Both", StringComparison.OrdinalIgnoreCase);
        bool applyToNumber = target.Equals("Number", StringComparison.OrdinalIgnoreCase) ||
                             target.Equals("Both", StringComparison.OrdinalIgnoreCase);
        bool applyToParameter = target.Equals("Parameter", StringComparison.OrdinalIgnoreCase);
        if (!applyToName && !applyToNumber && !applyToParameter)
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "target must be Name, Number, Both, or Parameter." });
        if (applyToParameter && string.IsNullOrWhiteSpace(parameterName))
            return Task.FromResult(new McpToolResult { RequestId = request.RequestId, Success = false, Message = "parameterName is required when target=Parameter." });

        IEnumerable<ViewSheet> sheets;
        if (sheetIds.Length > 0)
        {
            var idSet = sheetIds.ToHashSet();
            sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>()
                .Where(s => idSet.Contains(s.Id.Value));
        }
        else
        {
            sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .Cast<ViewSheet>();
            if (!string.IsNullOrWhiteSpace(nameFilter))
                sheets = sheets.Where(s => s.Name.Contains(nameFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(numberFilter))
                sheets = sheets.Where(s => s.SheetNumber.Contains(numberFilter, StringComparison.OrdinalIgnoreCase));
        }

        var existingNumbers = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Select(s => s.SheetNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var proposals   = new List<object>();
        var warnings    = new List<string>();
        int changeCount = 0;

        foreach (var s in sheets)
        {
            var tokens = new Dictionary<string, string> { ["Number"] = s.SheetNumber };

            var newName   = applyToName   ? RenameEngine.Apply(s.Name, mode, find, replace, prefix, suffix, template, tokens) ?? s.Name : s.Name;
            var newNumber = applyToNumber ? RenameEngine.Apply(s.SheetNumber, mode, find, replace, prefix, suffix, template, tokens) ?? s.SheetNumber : s.SheetNumber;
            var currentParameterValue = applyToParameter ? SheetNamingService.GetTargetValue(s, parameterName) : null;
            var targetError = applyToParameter ? SheetNamingService.ValidateTarget(s, parameterName) : null;
            var proposedParameterValue = applyToParameter
                ? RenameEngine.Apply(currentParameterValue ?? string.Empty, mode, find, replace, prefix, suffix, template, tokens) ?? currentParameterValue
                : null;

            bool nameChanges   = newName   != s.Name;
            bool numberChanges = newNumber != s.SheetNumber;
            bool parameterChanges = applyToParameter && targetError == null && proposedParameterValue != currentParameterValue;
            bool willChange    = nameChanges || numberChanges || parameterChanges;

            if (targetError != null)
            {
                warnings.Add($"Sheet '{s.SheetNumber}': {targetError}");
                willChange = false;
            }

            if (numberChanges && existingNumbers.Contains(newNumber))
            {
                warnings.Add($"Proposed number '{newNumber}' already exists for sheet '{s.SheetNumber}'.");
                willChange = false; newNumber = s.SheetNumber;
            }
            if (willChange) changeCount++;

            proposals.Add(new
            {
                sheetId       = s.Id.Value,
                currentNumber = s.SheetNumber,
                currentName   = s.Name,
                newNumber,
                newName,
                parameterName = applyToParameter ? parameterName : null,
                currentParameterValue,
                proposedParameterValue,
                willChange
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preview: {changeCount}/{proposals.Count} sheet(s) would be renamed.",
            Data       = new { total = proposals.Count, willChange = changeCount, proposals },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

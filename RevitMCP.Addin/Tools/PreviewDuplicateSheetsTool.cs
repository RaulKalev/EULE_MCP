using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Sheets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDuplicateSheetsTool : IRevitMcpTool
{
    public string Name => "revit_preview_duplicate_sheets";
    public string Description =>
        "Previews SheetManager-compatible sheet duplication without changes. " +
        "Accepts the same arguments as revit_duplicate_sheets, including numberOfCopies, " +
        "duplicateMode (EmptySheet|WithSheetDetailing|WithViews), keepLegends, keepSchedules, " +
        "copyRevisions, copyParameters, copyTitleBlockParameters, and viewDuplicateOption. " +
        "Reports source content counts and the content that each proposed duplicate will copy.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var sourceIds = ToolArguments.GetLongArray(request.Arguments, "sourceSheetIds");
        var sourceNumbers = ToolArguments.GetStringArray(request.Arguments, "sourceSheetNumbers");
        var copies = ToolArguments.GetInt(request.Arguments, "numberOfCopies", 1);
        var modeText = ToolArguments.GetString(request.Arguments, "duplicateMode", "EmptySheet");
        var numberSuffix = ToolArguments.GetString(request.Arguments, "newNumberSuffix", "_COPY");
        var nameSuffix = ToolArguments.GetString(request.Arguments, "newNameSuffix", " - Copy");
        var viewOptionText = ToolArguments.GetString(
            request.Arguments,
            "viewDuplicateOption",
            "DuplicateWithDetailing");

        if (sourceIds.Length == 0 && sourceNumbers.Length == 0)
            return Task.FromResult(Fail(request, "Provide sourceSheetIds or sourceSheetNumbers."));
        if (copies < 1 || copies > 50)
            return Task.FromResult(Fail(request, "numberOfCopies must be between 1 and 50."));
        if (!SheetDuplicationService.TryParseMode(modeText, out var duplicateMode))
            return Task.FromResult(Fail(request,
                $"Unknown duplicateMode '{modeText}'. Valid: EmptySheet, WithSheetDetailing, WithViews."));
        if (!SheetDuplicationService.TryParseViewDuplicateOption(viewOptionText, out var viewOption))
            return Task.FromResult(Fail(request,
                $"Unknown viewDuplicateOption '{viewOptionText}'. Valid: Duplicate, DuplicateWithDetailing, AsDependent."));

        var allSheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .ToList();
        var sources = ResolveSources(allSheets, sourceIds, sourceNumbers);
        if (sources.Count == 0)
            return Task.FromResult(Fail(request,
                "No matching sheets were found. Use revit_list_sheets to retrieve valid IDs or numbers."));

        var keepTitleBlock = ToolArguments.GetBool(request.Arguments, "keepTitleBlock", true);
        var keepLegends = ToolArguments.GetBool(request.Arguments, "keepLegends", false);
        var keepSchedules = ToolArguments.GetBool(request.Arguments, "keepSchedules", false);
        var copyRevisions = ToolArguments.GetBool(request.Arguments, "copyRevisions", false);
        var copyParameters = ToolArguments.GetBool(request.Arguments, "copyParameters", true);
        var copyTitleBlockParameters = request.Arguments.ContainsKey("copyTitleBlockParameters")
            ? ToolArguments.GetBool(request.Arguments, "copyTitleBlockParameters", copyParameters)
            : copyParameters;

        var takenNumbers = allSheets.Select(s => s.SheetNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenNames = allSheets.Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proposals = new List<object>();
        var warnings = new List<string>();

        foreach (var source in sources)
        {
            var content = SheetDuplicationService.Inspect(doc, source);
            var duplicableViews = SheetDuplicationService.CountDuplicableModelViews(
                doc,
                source,
                viewOption,
                out var unsupportedViewNames);
            for (var copyIndex = 1; copyIndex <= copies; copyIndex++)
            {
                var rawNumber = source.SheetNumber + ApplyIndex(numberSuffix, copyIndex, copies);
                var rawName = source.Name + ApplyIndex(nameSuffix, copyIndex, copies);
                var newNumber = ResolveUnique(rawNumber, takenNumbers);
                var newName = ResolveUnique(rawName, takenNames);
                takenNumbers.Add(newNumber);
                takenNames.Add(newName);

                if (!string.Equals(rawNumber, newNumber, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"Sheet number '{rawNumber}' already exists; '{newNumber}' will be used.");
                if (!string.Equals(rawName, newName, StringComparison.OrdinalIgnoreCase))
                    warnings.Add($"Sheet name '{rawName}' already exists; '{newName}' will be used.");

                proposals.Add(new
                {
                    sourceSheetId = source.Id.Value,
                    sourceSheetNumber = source.SheetNumber,
                    sourceSheetName = source.Name,
                    copyIndex,
                    newSheetNumber = newNumber,
                    newSheetName = newName,
                    duplicateMode = duplicateMode.ToString(),
                    willKeepTitleBlock = keepTitleBlock,
                    willCopySheetParameters = copyParameters,
                    willCopyTitleBlockParameters = keepTitleBlock && copyTitleBlockParameters,
                    sourceDetailingElements = content.DetailingElementCount,
                    willCopyDetailingElements = duplicateMode == SheetDuplicateMode.EmptySheet
                        ? 0
                        : content.DetailingElementCount,
                    sourceModelViewports = content.ModelViewportCount,
                    willDuplicateModelViews = duplicateMode == SheetDuplicateMode.WithViews
                        ? duplicableViews
                        : 0,
                    unsupportedModelViews = duplicateMode == SheetDuplicateMode.WithViews
                        ? unsupportedViewNames
                        : new List<string>(),
                    viewDuplicateOption = viewOption.ToString(),
                    sourceLegendViewports = content.LegendViewportCount,
                    willPlaceLegends = keepLegends ? content.LegendViewportCount : 0,
                    sourceSchedules = content.ScheduleCount,
                    willPlaceSchedules = keepSchedules ? content.ScheduleCount : 0,
                    sourceAdditionalRevisions = content.RevisionCount,
                    willCopyRevisions = copyRevisions ? content.RevisionCount : 0
                });
            }
        }

        if (!keepTitleBlock && copyTitleBlockParameters)
            warnings.Add("copyTitleBlockParameters has no effect when keepTitleBlock=false.");

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {proposals.Count} sheet(s) would be duplicated using mode {duplicateMode}.",
            Data = new { count = proposals.Count, duplicateMode = duplicateMode.ToString(), proposals },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static List<ViewSheet> ResolveSources(
        List<ViewSheet> allSheets,
        long[] sourceIds,
        string[] sourceNumbers)
    {
        if (sourceIds.Length > 0)
        {
            var ids = sourceIds.ToHashSet();
            return allSheets.Where(s => ids.Contains(s.Id.Value)).ToList();
        }

        var numbers = sourceNumbers.Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allSheets.Where(s => numbers.Contains(s.SheetNumber)).ToList();
    }

    private static string ApplyIndex(string value, int index, int copies)
    {
        if (value.Contains("{index}", StringComparison.OrdinalIgnoreCase))
            return value.Replace("{index}", index.ToString(), StringComparison.OrdinalIgnoreCase);
        return copies > 1 ? $"{value} {index}" : value;
    }

    private static string ResolveUnique(string candidate, HashSet<string> taken)
    {
        if (!taken.Contains(candidate)) return candidate;
        for (var index = 1; ; index++)
        {
            var resolved = $"{candidate} {index}";
            if (!taken.Contains(resolved)) return resolved;
        }
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

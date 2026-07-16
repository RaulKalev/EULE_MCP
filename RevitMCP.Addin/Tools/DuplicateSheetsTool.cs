using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Sheets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class DuplicateSheetsTool : IRevitMcpTool
{
    public string Name => "revit_duplicate_sheets";
    public string Description =>
        "Duplicates sheets with SheetManager-compatible content options. Requires approval. " +
        "Required: sourceSheetIds (long array) OR sourceSheetNumbers (string array). " +
        "Optional: numberOfCopies (1-50, default 1), duplicateMode " +
        "(EmptySheet|WithSheetDetailing|WithViews, default EmptySheet), " +
        "newNumberSuffix (default '_COPY'), newNameSuffix (default ' - Copy'; both support {index}), " +
        "keepTitleBlock (default true), copyParameters (default true), " +
        "copyTitleBlockParameters (defaults to copyParameters), keepLegends (default false), " +
        "keepSchedules (default false), copyRevisions (default false), " +
        "viewDuplicateOption (Duplicate|DuplicateWithDetailing|AsDependent, default DuplicateWithDetailing). " +
        "Viewports, legends, and schedules retain their source sheet positions. " +
        "Use revit_preview_duplicate_sheets first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
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
        var copyParameters = ToolArguments.GetBool(request.Arguments, "copyParameters", true);
        var copyTitleBlockParameters = request.Arguments.ContainsKey("copyTitleBlockParameters")
            ? ToolArguments.GetBool(request.Arguments, "copyTitleBlockParameters", copyParameters)
            : copyParameters;
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

        var options = new SheetDuplicationOptions
        {
            Mode = duplicateMode,
            KeepTitleBlock = ToolArguments.GetBool(request.Arguments, "keepTitleBlock", true),
            CopySheetParameters = copyParameters,
            CopyTitleBlockParameters = copyTitleBlockParameters,
            KeepLegends = ToolArguments.GetBool(request.Arguments, "keepLegends", false),
            KeepSchedules = ToolArguments.GetBool(request.Arguments, "keepSchedules", false),
            CopyRevisions = ToolArguments.GetBool(request.Arguments, "copyRevisions", false),
            ViewDuplicateOption = viewOption
        };

        var takenNumbers = allSheets.Select(s => s.SheetNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var takenNames = allSheets.Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();
        var results = new List<object>();
        var created = 0;
        var copiedDetailing = 0;
        var duplicatedViews = 0;
        var placedLegends = 0;
        var placedSchedules = 0;
        var copiedRevisions = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Duplicate Sheets");
        transaction.Start();

        foreach (var source in sources)
        {
            for (var copyIndex = 1; copyIndex <= copies; copyIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var requestedNumber = source.SheetNumber + ApplyIndex(numberSuffix, copyIndex, copies);
                var requestedName = source.Name + ApplyIndex(nameSuffix, copyIndex, copies);
                var newNumber = ResolveUnique(requestedNumber, takenNumbers);
                var newName = ResolveUnique(requestedName, takenNames);

                using var subTransaction = new SubTransaction(doc);
                subTransaction.Start();
                try
                {
                    var duplicate = SheetDuplicationService.Duplicate(
                        doc,
                        source,
                        newNumber,
                        newName,
                        options);
                    subTransaction.Commit();

                    takenNumbers.Add(newNumber);
                    takenNames.Add(newName);
                    created++;
                    copiedDetailing += duplicate.CopiedDetailingElements;
                    duplicatedViews += duplicate.DuplicatedViews;
                    placedLegends += duplicate.PlacedLegends;
                    placedSchedules += duplicate.PlacedSchedules;
                    copiedRevisions += duplicate.CopiedRevisions;
                    warnings.AddRange(duplicate.Warnings.Select(w => $"Sheet '{source.SheetNumber}': {w}"));

                    results.Add(new
                    {
                        sourceSheetId = source.Id.Value,
                        sourceSheetNumber = source.SheetNumber,
                        newSheetId = duplicate.Sheet.Id.Value,
                        newSheetNumber = duplicate.Sheet.SheetNumber,
                        newSheetName = duplicate.Sheet.Name,
                        copiedDetailingElements = duplicate.CopiedDetailingElements,
                        duplicatedViews = duplicate.DuplicatedViews,
                        placedLegends = duplicate.PlacedLegends,
                        placedSchedules = duplicate.PlacedSchedules,
                        copiedRevisions = duplicate.CopiedRevisions
                    });
                }
                catch (Exception ex)
                {
                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                        subTransaction.RollBack();
                    warnings.Add($"Failed to duplicate sheet '{source.SheetNumber}' copy {copyIndex}: {ex.Message}");
                }
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created > 0,
            Message = $"Duplicated {created}/{sources.Count * copies} sheet(s) using mode {duplicateMode}.",
            Data = new
            {
                created,
                failed = sources.Count * copies - created,
                duplicateMode = duplicateMode.ToString(),
                copiedDetailingElements = copiedDetailing,
                duplicatedViews,
                placedLegends,
                placedSchedules,
                copiedRevisions,
                results
            },
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
            var idSet = sourceIds.ToHashSet();
            return allSheets.Where(s => idSet.Contains(s.Id.Value)).ToList();
        }

        var numberSet = sourceNumbers.Select(n => n.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return allSheets.Where(s => numberSet.Contains(s.SheetNumber)).ToList();
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

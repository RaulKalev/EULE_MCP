using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Sheets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewApplySheetNamingTool : IRevitMcpTool
{
    public string Name => "revit_preview_apply_sheet_naming";
    public string Description =>
        "Previews SheetManager-style parameter-driven sheet naming without changes. " +
        "Required: targetParameter ('Sheet Number', 'Sheet Name', or a writable sheet parameter), " +
        "tokens (ordered array of {type:'Parameter'|'Separator', value:'...'}), and a sheet selector: " +
        "sheetIds, sheetNumbers, nameFilter, numberFilter, or allSheets=true. " +
        "Parameter tokens fall back to Project Information when the parameter is absent on the sheet.";
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

        var target = ToolArguments.GetString(request.Arguments, "targetParameter");
        var tokens = SheetNamingToolSupport.GetTokens(request.Arguments);
        var skipIfEmpty = ToolArguments.GetBool(request.Arguments, "skipIfEmpty", true);
        var sheets = SheetNamingToolSupport.ResolveSheets(doc, request.Arguments, out var selectionError);

        if (selectionError != null) return Task.FromResult(Fail(request, selectionError));
        if (string.IsNullOrWhiteSpace(target))
            return Task.FromResult(Fail(request, "targetParameter is required."));
        if (tokens.Count == 0)
            return Task.FromResult(Fail(request, "tokens must contain at least one naming token."));

        var warnings = new List<string>();
        var proposalData = new List<(ViewSheet Sheet, string Current, string Proposed, bool Apply, string Reason)>();
        foreach (var sheet in sheets)
        {
            var targetError = SheetNamingService.ValidateTarget(sheet, target);
            var current = SheetNamingService.GetTargetValue(sheet, target);
            var proposed = SheetNamingService.BuildValue(doc, sheet, tokens);
            var apply = targetError == null && !string.Equals(current, proposed, StringComparison.Ordinal);
            var reason = targetError ?? (skipIfEmpty && string.IsNullOrEmpty(proposed)
                ? "Skipped because the composed value is empty."
                : apply ? "Value will change." : "Already matches.");
            if (skipIfEmpty && string.IsNullOrEmpty(proposed)) apply = false;
            proposalData.Add((sheet, current, proposed, apply, reason));
        }

        ApplySheetNumberConflictChecks(doc, target, proposalData, warnings);
        var proposals = proposalData.Select(p => new
        {
            sheetId = p.Sheet.Id.Value,
            sheetNumber = p.Sheet.SheetNumber,
            sheetName = p.Sheet.Name,
            targetParameter = target,
            currentValue = p.Current,
            proposedValue = p.Proposed,
            willApply = p.Apply,
            reason = p.Reason
        }).ToList();
        var applyCount = proposalData.Count(p => p.Apply);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: naming would update {applyCount}/{sheets.Count} sheet(s).",
            Data = new { total = sheets.Count, willApply = applyCount, targetParameter = target, proposals },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    internal static void ApplySheetNumberConflictChecks(
        Document doc,
        string target,
        List<(ViewSheet Sheet, string Current, string Proposed, bool Apply, string Reason)> proposals,
        List<string> warnings)
    {
        if (!string.Equals(target, "Sheet Number", StringComparison.OrdinalIgnoreCase)) return;

        var targetIds = proposals.Select(p => p.Sheet.Id).ToHashSet();
        var externalNumbers = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !targetIds.Contains(s.Id))
            .Select(s => s.SheetNumber)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Re-evaluate until stable. When one proposal is rejected, its current number becomes
        // retained and can invalidate another proposed number in the same batch.
        var changed = true;
        while (changed)
        {
            changed = false;
            var duplicateFinalNumbers = proposals
                .GroupBy(p => p.Apply ? p.Proposed : p.Current, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < proposals.Count; index++)
            {
                var proposal = proposals[index];
                if (!proposal.Apply) continue;
                if (!externalNumbers.Contains(proposal.Proposed) &&
                    !duplicateFinalNumbers.Contains(proposal.Proposed))
                    continue;

                var reason = $"Sheet number '{proposal.Proposed}' conflicts with another sheet.";
                proposals[index] = (proposal.Sheet, proposal.Current, proposal.Proposed, false, reason);
                warnings.Add($"Sheet '{proposal.Sheet.SheetNumber}': {reason}");
                changed = true;
            }
        }
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

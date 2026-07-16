using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Sheets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ApplySheetNamingTool : IRevitMcpTool
{
    public string Name => "revit_apply_sheet_naming";
    public string Description =>
        "Applies SheetManager-style parameter-driven naming to sheets in one transaction. Requires approval. " +
        "Arguments match revit_preview_apply_sheet_naming: targetParameter, ordered tokens, sheet selector, " +
        "and skipIfEmpty (default true). Run the preview tool first.";
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
        var proposals = new List<(ViewSheet Sheet, string Current, string Proposed, bool Apply, string Reason)>();
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
            proposals.Add((sheet, current, proposed, apply, reason));
        }
        PreviewApplySheetNamingTool.ApplySheetNumberConflictChecks(doc, target, proposals, warnings);

        var toApply = proposals.Where(p => p.Apply).ToList();
        if (toApply.Count == 0)
            return Task.FromResult(Fail(request, "No sheets require a valid naming update.", warnings));

        var updated = 0;
        var results = new List<object>();
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Apply Sheet Naming");
        transaction.Start();

        if (string.Equals(target, "Sheet Number", StringComparison.OrdinalIgnoreCase))
        {
            // Revit enforces uniqueness on every assignment. Temporary values make swaps and
            // sequences such as A-01 -> A-02, A-02 -> A-03 safe within one transaction.
            using var renumbering = new SubTransaction(doc);
            renumbering.Start();
            try
            {
                for (var index = 0; index < toApply.Count; index++)
                    toApply[index].Sheet.SheetNumber =
                        "~MCP-" + Guid.NewGuid().ToString("N").Substring(0, 16);

                foreach (var proposal in toApply)
                    SheetNamingService.SetTargetValue(proposal.Sheet, target, proposal.Proposed);

                renumbering.Commit();
                foreach (var proposal in toApply)
                {
                    updated++;
                    results.Add(new
                    {
                        sheetId = proposal.Sheet.Id.Value,
                        sheetNumber = proposal.Sheet.SheetNumber,
                        sheetName = proposal.Sheet.Name,
                        targetParameter = target,
                        oldValue = proposal.Current,
                        newValue = proposal.Proposed
                    });
                }
            }
            catch (Exception ex)
            {
                if (renumbering.GetStatus() == TransactionStatus.Started)
                    renumbering.RollBack();
                warnings.Add($"Sheet renumbering was rolled back: {ex.Message}");
            }
        }
        else
        {
        foreach (var proposal in toApply)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                SheetNamingService.SetTargetValue(proposal.Sheet, target, proposal.Proposed);
                updated++;
                results.Add(new
                {
                    sheetId = proposal.Sheet.Id.Value,
                    sheetNumber = proposal.Sheet.SheetNumber,
                    sheetName = proposal.Sheet.Name,
                    targetParameter = target,
                    oldValue = proposal.Current,
                    newValue = proposal.Proposed
                });
            }
            catch (Exception ex)
            {
                warnings.Add($"Sheet '{proposal.Sheet.SheetNumber}' was not updated: {ex.Message}");
            }
        }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = updated > 0,
            Message = $"Applied naming to {updated}/{toApply.Count} sheet(s).",
            Data = new { updated, failed = toApply.Count - updated, targetParameter = target, results },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(
        McpToolRequest request,
        string message,
        List<string>? warnings = null) =>
        new()
        {
            RequestId = request.RequestId,
            Success = false,
            Message = message,
            Warnings = warnings ?? new List<string>()
        };
}

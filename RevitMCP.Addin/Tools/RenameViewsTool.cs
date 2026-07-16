using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Naming;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class RenameViewsTool : IRevitMcpTool
{
    public string Name => "revit_rename_views";
    public string Description =>
        "Applies ViewManager-style transforms to view names or writable view parameters. Requires approval. " +
        "Arguments match revit_preview_rename_views: selector, mode, target (Name|Parameter), parameterName, " +
        "find, replace, prefix, suffix, and template. View-name swaps are handled atomically. Run preview first.";
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

        var mode = ToolArguments.GetString(request.Arguments, "mode");
        var target = ToolArguments.GetString(request.Arguments, "target", "Name");
        var parameterName = ToolArguments.GetString(request.Arguments, "parameterName");
        var find = ToolArguments.GetString(request.Arguments, "find");
        var replace = ToolArguments.GetString(request.Arguments, "replace");
        var prefix = ToolArguments.GetString(request.Arguments, "prefix");
        var suffix = ToolArguments.GetString(request.Arguments, "suffix");
        var template = ToolArguments.GetString(request.Arguments, "template");

        var modeError = RenameEngine.ValidateMode(mode);
        if (modeError != null) return Task.FromResult(Fail(request, modeError));

        var targetName = target.Equals("Name", StringComparison.OrdinalIgnoreCase);
        var targetParameter = target.Equals("Parameter", StringComparison.OrdinalIgnoreCase);
        if (!targetName && !targetParameter)
            return Task.FromResult(Fail(request, "target must be Name or Parameter."));
        if (targetParameter && string.IsNullOrWhiteSpace(parameterName))
            return Task.FromResult(Fail(request, "parameterName is required when target=Parameter."));

        var views = ViewManagerToolSupport.ResolveViews(doc, request.Arguments, out var selectionError);
        if (selectionError != null) return Task.FromResult(Fail(request, selectionError));

        var warnings = new List<string>();
        var proposals = new List<(View View, string Current, string Proposed, bool Apply, string Reason)>();
        foreach (var view in views)
        {
            var current = targetName ? view.Name : ViewParameterService.GetValue(view, parameterName);
            var targetError = targetParameter
                ? ViewParameterService.ValidateWritable(view, parameterName)
                : null;
            var proposed = RenameEngine.Apply(current, mode, find, replace, prefix, suffix, template) ?? current;
            var apply = targetError == null && !string.Equals(current, proposed, StringComparison.Ordinal);
            proposals.Add((view, current, proposed, apply, targetError ?? string.Empty));
        }
        PreviewRenameViewsTool.ApplyViewNameConflictChecks(doc, targetName, proposals, warnings);

        var toApply = proposals.Where(proposal => proposal.Apply).ToList();
        if (toApply.Count == 0)
            return Task.FromResult(Fail(request, "No views require a valid update.", warnings));

        var updated = 0;
        var results = new List<object>();
        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Rename Views");
        transaction.Start();

        if (targetName)
        {
            using var renaming = new SubTransaction(doc);
            renaming.Start();
            try
            {
                foreach (var proposal in toApply)
                    proposal.View.Name = "~MCP-" + Guid.NewGuid().ToString("N").Substring(0, 16);
                foreach (var proposal in toApply)
                    proposal.View.Name = proposal.Proposed;
                renaming.Commit();

                foreach (var proposal in toApply)
                {
                    updated++;
                    results.Add(new
                    {
                        viewId = proposal.View.Id.Value,
                        viewType = proposal.View.ViewType.ToString(),
                        oldValue = proposal.Current,
                        newValue = proposal.Proposed,
                        target = "Name"
                    });
                }
            }
            catch (Exception ex)
            {
                if (renaming.GetStatus() == TransactionStatus.Started) renaming.RollBack();
                warnings.Add($"View renaming was rolled back: {ex.Message}");
            }
        }
        else
        {
            foreach (var proposal in toApply)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    ViewParameterService.SetValue(proposal.View, parameterName, proposal.Proposed);
                    updated++;
                    results.Add(new
                    {
                        viewId = proposal.View.Id.Value,
                        viewName = proposal.View.Name,
                        viewType = proposal.View.ViewType.ToString(),
                        oldValue = proposal.Current,
                        newValue = proposal.Proposed,
                        target = parameterName
                    });
                }
                catch (Exception ex)
                {
                    warnings.Add($"View '{proposal.View.Name}' was not updated: {ex.Message}");
                }
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = updated > 0,
            Message = $"Updated {updated}/{toApply.Count} view(s).",
            Data = new { updated, failed = toApply.Count - updated, results },
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

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Naming;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewRenameViewsTool : IRevitMcpTool
{
    public string Name => "revit_preview_rename_views";
    public string Description =>
        "Previews ViewManager-style view name or parameter transforms without changes. " +
        "Required: a selector (viewIds, viewTypes, nameFilter, or allViews=true), " +
        "mode (FindReplace|PrefixSuffix|Template|RegexFindReplace). " +
        "Optional: target (Name|Parameter, default Name); parameterName is required for target=Parameter; " +
        "find, replace, prefix, suffix, and template with {Name}.";
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
        var proposalData = new List<(View View, string Current, string Proposed, bool Apply, string Reason)>();
        foreach (var view in views)
        {
            var current = targetName ? view.Name : ViewParameterService.GetValue(view, parameterName);
            var targetError = targetParameter
                ? ViewParameterService.ValidateWritable(view, parameterName)
                : null;
            var proposed = RenameEngine.Apply(current, mode, find, replace, prefix, suffix, template) ?? current;
            var apply = targetError == null && !string.Equals(current, proposed, StringComparison.Ordinal);
            var reason = targetError ?? (apply ? "Value will change." : "Already matches.");
            proposalData.Add((view, current, proposed, apply, reason));
        }

        ApplyViewNameConflictChecks(doc, targetName, proposalData, warnings);
        var proposals = proposalData.Select(proposal => new
        {
            viewId = proposal.View.Id.Value,
            viewType = proposal.View.ViewType.ToString(),
            currentName = proposal.View.Name,
            target = targetName ? "Name" : "Parameter",
            parameterName = targetParameter ? parameterName : null,
            currentValue = proposal.Current,
            proposedValue = proposal.Proposed,
            newName = targetName ? proposal.Proposed : proposal.View.Name,
            willChange = proposal.Apply,
            reason = proposal.Reason
        }).ToList();
        var changeCount = proposalData.Count(proposal => proposal.Apply);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {changeCount}/{views.Count} view(s) would be updated.",
            Data = new { total = views.Count, willChange = changeCount, proposals },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    internal static void ApplyViewNameConflictChecks(
        Document doc,
        bool targetName,
        List<(View View, string Current, string Proposed, bool Apply, string Reason)> proposals,
        List<string> warnings)
    {
        if (!targetName) return;

        var targetIds = proposals.Select(proposal => proposal.View.Id).ToHashSet();
        var externalNames = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => view.ViewType != ViewType.DrawingSheet &&
                           view.ViewType != ViewType.Internal &&
                           view.ViewType != ViewType.ProjectBrowser)
            .Where(view => !targetIds.Contains(view.Id))
            .Select(view => view.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var changed = true;
        while (changed)
        {
            changed = false;
            var duplicateFinalNames = proposals
                .GroupBy(
                    proposal => proposal.Apply ? proposal.Proposed : proposal.Current,
                    StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < proposals.Count; index++)
            {
                var proposal = proposals[index];
                if (!proposal.Apply) continue;
                if (!externalNames.Contains(proposal.Proposed) &&
                    !duplicateFinalNames.Contains(proposal.Proposed))
                    continue;

                var reason = $"View name '{proposal.Proposed}' conflicts with another view.";
                proposals[index] = (proposal.View, proposal.Current, proposal.Proposed, false, reason);
                warnings.Add($"View '{proposal.View.Name}': {reason}");
                changed = true;
            }
        }
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

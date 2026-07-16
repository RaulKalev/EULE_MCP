using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDuplicateViewsTool : IRevitMcpTool
{
    public string Name => "revit_preview_duplicate_views";
    public string Description =>
        "Previews ViewManager-compatible view duplication without changes. Accepts the same arguments as " +
        "revit_duplicate_views, including numberOfCopies, fallbackToDuplicate, copyParameters, and parameterOverrides. " +
        "Returns one proposal per copy with requested/actual duplicate option and resolved unique name.";
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

        var viewIds = ToolArguments.GetLongArray(request.Arguments, "viewIds");
        var copies = ToolArguments.GetInt(request.Arguments, "numberOfCopies", 1);
        var optionText = ToolArguments.GetString(request.Arguments, "duplicateOption", "DuplicateWithDetailing");
        var fallback = ToolArguments.GetBool(request.Arguments, "fallbackToDuplicate", true);
        var nameSuffix = ToolArguments.GetString(request.Arguments, "nameSuffix", " - Copy");
        var namePrefix = ToolArguments.GetString(request.Arguments, "namePrefix");
        var copyParameters = ToolArguments.GetBool(request.Arguments, "copyParameters", true);
        var parameterOverrides = ViewManagerToolSupport.GetStringDictionary(
            request.Arguments,
            "parameterOverrides");

        if (viewIds.Length == 0) return Task.FromResult(Fail(request, "viewIds is required."));
        if (copies < 1 || copies > 50)
            return Task.FromResult(Fail(request, "numberOfCopies must be between 1 and 50."));
        if (string.Equals(optionText, "EmptyDetailOnly", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Fail(request,
                "EmptyDetailOnly is not implemented. Valid: Duplicate, DuplicateWithDetailing, AsDependent."));
        if (!ViewManagerToolSupport.TryParseDuplicateOption(optionText, out var requestedOption))
            return Task.FromResult(Fail(request,
                $"Unknown duplicateOption '{optionText}'. Valid: Duplicate, DuplicateWithDetailing, AsDependent."));

        var takenNames = new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(view => view.ViewType != ViewType.DrawingSheet &&
                           view.ViewType != ViewType.Internal &&
                           view.ViewType != ViewType.ProjectBrowser)
            .Select(v => v.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var proposals = new List<object>();
        var warnings = new List<string>();
        var canDuplicate = 0;

        foreach (var viewId in viewIds.Distinct())
        {
            var sourceView = doc.GetElement(new ElementId(viewId)) as View;
            if (!ViewManagerToolSupport.IsManagedView(sourceView))
            {
                var reason = sourceView == null
                    ? "View not found"
                    : "Template, sheet, internal, project-browser, or non-printable view is outside View Manager scope";
                for (var copyIndex = 1; copyIndex <= copies; copyIndex++)
                {
                    proposals.Add(new
                    {
                        viewId,
                        viewName = (string?)null,
                        viewType = (string?)null,
                        copyIndex,
                        newName = (string?)null,
                        canDuplicate = false,
                        reason
                    });
                }
                continue;
            }

            var managedView = sourceView!;

            var actualOption = requestedOption;
            var canDuplicateSource = managedView.CanViewBeDuplicated(actualOption);
            var usedFallback = false;
            if (!canDuplicateSource && fallback && actualOption != ViewDuplicateOption.Duplicate &&
                managedView.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
            {
                actualOption = ViewDuplicateOption.Duplicate;
                canDuplicateSource = true;
                usedFallback = true;
            }

            for (var copyIndex = 1; copyIndex <= copies; copyIndex++)
            {
                var rawName = namePrefix + managedView.Name +
                              ViewManagerToolSupport.ApplyCopyIndex(nameSuffix, copyIndex, copies);
                var newName = ViewManagerToolSupport.ResolveUniqueName(rawName, takenNames);
                if (canDuplicateSource)
                {
                    takenNames.Add(newName);
                    canDuplicate++;
                    if (!string.Equals(rawName, newName, StringComparison.OrdinalIgnoreCase))
                        warnings.Add($"View name '{rawName}' already exists; '{newName}' will be used.");
                }

                proposals.Add(new
                {
                    viewId = managedView.Id.Value,
                    viewName = managedView.Name,
                    viewType = managedView.ViewType.ToString(),
                    copyIndex,
                    newName,
                    canDuplicate = canDuplicateSource,
                    requestedOption = requestedOption.ToString(),
                    actualOption = actualOption.ToString(),
                    usedFallback,
                    willApplyParameterOverrides = copyParameters ? parameterOverrides.Count : 0,
                    reason = canDuplicateSource
                        ? usedFallback
                            ? $"Requested option unsupported; will use {actualOption}."
                            : $"Will duplicate with {actualOption}."
                        : $"View cannot be duplicated with {requestedOption}."
                });
            }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {canDuplicate} view copy/copies can be created.",
            Data = new { total = proposals.Count, canDuplicate, proposals },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class DuplicateViewsTool : IRevitMcpTool
{
    public string Name => "revit_duplicate_views";
    public string Description =>
        "Duplicates views with ViewManager-compatible options. Requires approval. " +
        "Required: viewIds (long array). Optional: numberOfCopies (1-50, default 1), " +
        "duplicateOption (Duplicate|DuplicateWithDetailing|AsDependent, default DuplicateWithDetailing), " +
        "fallbackToDuplicate (default true), nameSuffix (default ' - Copy'; supports {index}), " +
        "namePrefix (default ''), copyParameters (default true), and parameterOverrides (name-to-value object). " +
        "Revit duplication carries native view parameters; copyParameters controls whether explicit overrides are applied. " +
        "Use revit_preview_duplicate_views first.";
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
        var warnings = new List<string>();
        var results = new List<object>();
        var duplicated = 0;
        var fallbackCount = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Duplicate Views");
        transaction.Start();

        foreach (var viewId in viewIds.Distinct())
        {
            var sourceView = doc.GetElement(new ElementId(viewId)) as View;
            if (!ViewManagerToolSupport.IsManagedView(sourceView))
            {
                warnings.Add(sourceView == null
                    ? $"View {viewId} was not found."
                    : $"View '{sourceView.Name}' is a template, sheet, internal, project-browser, or non-printable view and was skipped.");
                continue;
            }

            var managedView = sourceView!;

            var actualOption = requestedOption;
            if (!managedView.CanViewBeDuplicated(actualOption))
            {
                if (fallback && actualOption != ViewDuplicateOption.Duplicate &&
                    managedView.CanViewBeDuplicated(ViewDuplicateOption.Duplicate))
                {
                    actualOption = ViewDuplicateOption.Duplicate;
                    warnings.Add(
                        $"View '{managedView.Name}' does not support {requestedOption}; Duplicate fallback will be used.");
                }
                else
                {
                    warnings.Add($"View '{managedView.Name}' cannot be duplicated with {requestedOption}.");
                    continue;
                }
            }

            for (var copyIndex = 1; copyIndex <= copies; copyIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rawName = namePrefix + managedView.Name +
                              ViewManagerToolSupport.ApplyCopyIndex(nameSuffix, copyIndex, copies);
                var newName = ViewManagerToolSupport.ResolveUniqueName(rawName, takenNames);

                using var subTransaction = new SubTransaction(doc);
                subTransaction.Start();
                try
                {
                    var newId = managedView.Duplicate(actualOption);
                    var newView = doc.GetElement(newId) as View
                                  ?? throw new InvalidOperationException("Revit did not return the duplicated view.");
                    newView.Name = newName;
                    var appliedParameters = copyParameters
                        ? ViewParameterService.ApplyOverrides(newView, parameterOverrides, warnings)
                        : 0;
                    subTransaction.Commit();

                    takenNames.Add(newName);
                    duplicated++;
                    if (actualOption != requestedOption) fallbackCount++;
                    results.Add(new
                    {
                        sourceViewId = managedView.Id.Value,
                        sourceViewName = managedView.Name,
                        newViewId = newView.Id.Value,
                        newViewName = newView.Name,
                        copyIndex,
                        requestedOption = requestedOption.ToString(),
                        actualOption = actualOption.ToString(),
                        usedFallback = actualOption != requestedOption,
                        appliedParameterOverrides = appliedParameters
                    });
                }
                catch (Exception ex)
                {
                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                        subTransaction.RollBack();
                    warnings.Add($"Failed to duplicate '{managedView.Name}' copy {copyIndex}: {ex.Message}");
                }
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);
        var requestedTotal = viewIds.Distinct().Count() * copies;
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = duplicated > 0,
            Message = $"Duplicated {duplicated}/{requestedTotal} view(s).",
            Data = new
            {
                duplicated,
                failed = requestedTotal - duplicated,
                fallbackCount,
                results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

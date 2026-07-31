using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewDuplicateFamilyTypesTool : IRevitMcpTool
{
    public string Name => "revit_preview_duplicate_family_types";

    public string Description =>
        "Previews family type duplication without changing the model. Takes the same arguments as " +
        "revit_duplicate_family_types: typeIds (or familyName/typeName), numberOfCopies, namePrefix, " +
        "nameSuffix, newTypeNames, variants, parameterOverrides. Returns the resolved name of every " +
        "planned copy plus, per parameter, whether it exists, is writable, and can take the value.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var warnings = new List<string>();
        var parsed = FamilyTypeDuplicationPlanner.ParseRequest(request.Arguments);
        var plan = FamilyTypeDuplicationPlanner.Build(doc, parsed, warnings, out var error);
        if (plan == null)
            return Task.FromResult(Fail(request, error!));

        var proposals = new List<object>();
        var canDuplicate = 0;

        foreach (var entry in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Parameter checks run against the source type: the copy inherits its parameter set.
            var parameterChecks = entry.Parameters
                .Select(pair => FamilyTypeSupport.CheckParameter(entry.Source, pair.Key, pair.Value))
                .ToList();

            if (entry.CanDuplicate)
                canDuplicate++;

            if (entry.WasRenamedForUniqueness)
            {
                warnings.Add(
                    $"Type name '{entry.RequestedName}' is already used in this family; " +
                    $"'{entry.ResolvedName}' will be used instead.");
            }

            foreach (var check in parameterChecks.Where(c => !c.WillSucceed))
                warnings.Add($"{entry.ResolvedName}: {check.Name} — {check.Message}");

            proposals.Add(new
            {
                sourceTypeId = entry.Source.Id.Value,
                sourceFamilyName = FamilyTypeSupport.SafeFamilyName(entry.Source),
                sourceTypeName = FamilyTypeSupport.SafeName(entry.Source),
                category = entry.Source.Category?.Name ?? string.Empty,
                kind = FamilyTypeSupport.KindOf(entry.Source),
                copyIndex = entry.CopyIndex,
                requestedName = entry.RequestedName,
                newTypeName = entry.ResolvedName,
                renamedForUniqueness = entry.WasRenamedForUniqueness,
                canDuplicate = entry.CanDuplicate,
                reason = entry.BlockedReason ?? "Will be duplicated.",
                parameters = parameterChecks.Select(c => c.ToPayload()).ToList()
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {canDuplicate} of {plan.Count} family type cop{(plan.Count == 1 ? "y" : "ies")} can be created.",
            Data = new
            {
                total = plan.Count,
                canDuplicate,
                blocked = plan.Count - canDuplicate,
                proposals
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

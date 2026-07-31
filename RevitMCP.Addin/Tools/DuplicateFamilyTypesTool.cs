using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class DuplicateFamilyTypesTool : IRevitMcpTool
{
    public string Name => "revit_duplicate_family_types";

    public string Description =>
        "Duplicates family types and optionally names the copies and sets their type parameters. " +
        "Requires approval. Required: typeIds (or familyName/typeName resolving to one type). " +
        "Naming: newTypeNames (one explicit name per source type), or namePrefix/nameSuffix " +
        "(default ' - Copy') with numberOfCopies (1-50, '{index}' supported in the affixes). " +
        "Values: parameterOverrides (name-to-value, applied to every copy) or variants " +
        "([{name, parameters}], one copy per entry, single source type) for differing values per copy. " +
        "Numeric values are written in project display units ('200' or '200 mm'). " +
        "Optional: requireAllParameters (default false) rolls a copy back if any of its parameters " +
        "cannot be set. Run revit_preview_duplicate_family_types first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
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

        var requireAllParameters = ToolArguments.GetBool(request.Arguments, "requireAllParameters");

        var results = new List<object>();
        var created = 0;
        var parametersSet = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Duplicate Family Types");
        transaction.Start();

        foreach (var entry in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.CanDuplicate)
            {
                warnings.Add($"'{entry.RequestedName}' was skipped: {entry.BlockedReason}");
                results.Add(new
                {
                    sourceTypeId = entry.Source.Id.Value,
                    sourceTypeName = FamilyTypeSupport.SafeName(entry.Source),
                    newTypeId = (long?)null,
                    newTypeName = entry.ResolvedName,
                    created = false,
                    reason = entry.BlockedReason
                });
                continue;
            }

            if (entry.WasRenamedForUniqueness)
            {
                warnings.Add(
                    $"Type name '{entry.RequestedName}' is already used in this family; " +
                    $"'{entry.ResolvedName}' was used instead.");
            }

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                var copy = entry.Source.Duplicate(entry.ResolvedName)
                           ?? throw new InvalidOperationException("Revit did not return the duplicated type.");

                var parameterResults = entry.Parameters
                    .Select(pair => FamilyTypeSupport.SetParameter(doc, copy, pair.Key, pair.Value))
                    .ToList();

                var failed = parameterResults.Where(r => !r.Succeeded).ToList();
                if (failed.Count > 0 && requireAllParameters)
                {
                    subTransaction.RollBack();
                    var reason = string.Join("; ", failed.Select(f => $"{f.Name}: {f.Message}"));
                    warnings.Add($"'{entry.ResolvedName}' was rolled back (requireAllParameters): {reason}");
                    results.Add(new
                    {
                        sourceTypeId = entry.Source.Id.Value,
                        sourceTypeName = FamilyTypeSupport.SafeName(entry.Source),
                        newTypeId = (long?)null,
                        newTypeName = entry.ResolvedName,
                        created = false,
                        reason = $"Rolled back — {reason}",
                        parameters = parameterResults.Select(r => r.ToPayload()).ToList()
                    });
                    continue;
                }

                subTransaction.Commit();

                created++;
                parametersSet += parameterResults.Count(r => r.Succeeded);
                foreach (var failure in failed)
                    warnings.Add($"{entry.ResolvedName}: {failure.Name} — {failure.Message}");

                results.Add(new
                {
                    sourceTypeId = entry.Source.Id.Value,
                    sourceFamilyName = FamilyTypeSupport.SafeFamilyName(entry.Source),
                    sourceTypeName = FamilyTypeSupport.SafeName(entry.Source),
                    newTypeId = copy.Id.Value,
                    newTypeName = FamilyTypeSupport.SafeName(copy),
                    category = copy.Category?.Name ?? string.Empty,
                    copyIndex = entry.CopyIndex,
                    created = true,
                    parameters = parameterResults.Select(r => r.ToPayload()).ToList()
                });
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();

                warnings.Add($"Failed to duplicate '{FamilyTypeSupport.SafeName(entry.Source)}' as '{entry.ResolvedName}': {ex.Message}");
                results.Add(new
                {
                    sourceTypeId = entry.Source.Id.Value,
                    sourceTypeName = FamilyTypeSupport.SafeName(entry.Source),
                    newTypeId = (long?)null,
                    newTypeName = entry.ResolvedName,
                    created = false,
                    reason = ex.Message
                });
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created > 0,
            Message = $"Created {created}/{plan.Count} family type(s); set {parametersSet} parameter value(s).",
            Data = new
            {
                created,
                failed = plan.Count - created,
                parametersSet,
                results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

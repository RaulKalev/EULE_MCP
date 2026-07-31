using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class EditFamilyTypesTool : IRevitMcpTool
{
    public string Name => "revit_edit_family_types";

    public string Description =>
        "Renames family types and sets their type parameter values. Requires approval. " +
        "Either edits ([{typeId, newName, parameters}], each type gets its own name and values) or " +
        "typeIds (or familyName/typeName) with newName and/or parameters applied to all of them. " +
        "Numeric values are written in project display units ('200' or '200 mm'). Renaming a type " +
        "changes it for every placed instance. Run revit_preview_edit_family_types first.";

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
        var plan = FamilyTypeEditPlanner.Build(doc, request.Arguments, warnings, out var error);
        if (plan == null)
            return Task.FromResult(Fail(request, error!));

        var results = new List<object>();
        var edited = 0;
        var renamed = 0;
        var parametersSet = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Edit Family Types");
        transaction.Start();

        foreach (var entry in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var originalName = FamilyTypeSupport.SafeName(entry.Type);

            if (!entry.CanEdit)
            {
                warnings.Add($"{originalName}: {entry.BlockedReason}");
                results.Add(new
                {
                    typeId = entry.Type.Id.Value,
                    typeName = originalName,
                    edited = false,
                    reason = entry.BlockedReason
                });
                continue;
            }

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                var didRename = false;
                if (entry.NewName != null)
                {
                    entry.Type.Name = entry.NewName;
                    didRename = true;
                }

                var parameterResults = entry.Parameters
                    .Select(pair => FamilyTypeSupport.SetParameter(doc, entry.Type, pair.Key, pair.Value))
                    .ToList();

                subTransaction.Commit();

                edited++;
                if (didRename) renamed++;
                parametersSet += parameterResults.Count(r => r.Succeeded);

                foreach (var failure in parameterResults.Where(r => !r.Succeeded))
                    warnings.Add($"{FamilyTypeSupport.SafeName(entry.Type)}: {failure.Name} — {failure.Message}");

                results.Add(new
                {
                    typeId = entry.Type.Id.Value,
                    familyName = FamilyTypeSupport.SafeFamilyName(entry.Type),
                    previousTypeName = originalName,
                    typeName = FamilyTypeSupport.SafeName(entry.Type),
                    renamed = didRename,
                    edited = true,
                    note = entry.RenameNote,
                    parameters = parameterResults.Select(r => r.ToPayload()).ToList()
                });
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();

                warnings.Add($"Failed to edit '{originalName}': {ex.Message}");
                results.Add(new
                {
                    typeId = entry.Type.Id.Value,
                    typeName = originalName,
                    edited = false,
                    reason = ex.Message
                });
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = edited > 0,
            Message = $"Edited {edited}/{plan.Count} family type(s): {renamed} renamed, {parametersSet} parameter value(s) set.",
            Data = new
            {
                edited,
                failed = plan.Count - edited,
                renamed,
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

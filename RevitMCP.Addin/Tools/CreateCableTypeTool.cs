using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CreateCableTypeTool : IRevitMcpTool
{
    public string Name => "revit_create_cable_type";

    public string Description =>
        "Creates cable types by duplicating an existing one (like Duplicate in Revit). Requires approval. " +
        "Source: sourceTypeName or sourceTypeId (see revit_get_available_cable_types). " +
        "New types: newName (+ parameters name-to-value), newNames (batch, shared parameters), " +
        "or items=[{newName, parameters}]. ifExists: skip (default — returns the existing id) | error. " +
        "Numeric values are written in project display units. Returns the new type ids so they can be " +
        "assigned to circuits right away. Falls back to WireType where CableType is unavailable. " +
        "Run revit_preview_create_cable_type first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var warnings = new List<string>();
        var context = CableTypeDuplicator.Build(doc, CableTypeDuplicator.ParseRequest(request.Arguments), warnings, out var error);
        if (context == null)
            return Task.FromResult(Fail(request, error!));

        var results = new List<object>();
        var created = 0;
        var skipped = 0;

        cancellationToken.ThrowIfCancellationRequested();
        using var transaction = new Transaction(doc, "Revit MCP - Create Cable Types");
        transaction.Start();

        foreach (var entry in context.Plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.Item.NewName;

            if (entry.Action == CableTypeCreationAction.SkipExisting)
            {
                skipped++;
                var existing = CableTypeDuplicator.FindExisting(context, name);
                results.Add(new
                {
                    newName = name,
                    action = "skipExisting",
                    id = existing?.Id.Value,
                    name = existing != null ? FamilyTypeSupport.SafeName(existing) : entry.ExistingName,
                    created = false,
                    reason = entry.Reason
                });
                continue;
            }

            if (entry.Action == CableTypeCreationAction.Blocked)
            {
                warnings.Add($"'{name}' was skipped: {entry.Reason}");
                results.Add(new { newName = name, action = "blocked", id = (long?)null, name, created = false, reason = entry.Reason });
                continue;
            }

            using var subTransaction = new SubTransaction(doc);
            subTransaction.Start();
            try
            {
                var copy = CableTypeDuplicator.Duplicate(context, name);
                var parameterResults = entry.Item.Parameters
                    .Select(p => FamilyTypeSupport.SetParameter(doc, copy, p.Key, p.Value))
                    .ToList();
                subTransaction.Commit();

                created++;
                foreach (var failure in parameterResults.Where(r => !r.Succeeded))
                    warnings.Add($"{name}: {failure.Name} — {failure.Message}");

                results.Add(new
                {
                    newName = name,
                    action = "create",
                    id = copy.Id.Value,
                    name = FamilyTypeSupport.SafeName(copy),
                    created = true,
                    parameters = parameterResults.Select(r => r.ToPayload()).ToList()
                });
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();

                warnings.Add($"Failed to create '{name}': {ex.Message}");
                results.Add(new { newName = name, action = "create", id = (long?)null, name, created = false, reason = ex.Message });
            }
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created + skipped > 0,
            Message = $"Created {created} cable type(s) from '{FamilyTypeSupport.SafeName(context.Source)}'; " +
                      $"{skipped} already existed.",
            Data = new
            {
                kind = context.Kind,
                sourceTypeId = context.Source.Id.Value,
                sourceTypeName = FamilyTypeSupport.SafeName(context.Source),
                created,
                skipped,
                failed = context.Plan.Count - created - skipped,
                results
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewCreateCableTypeTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_cable_type";

    public string Description =>
        "Previews cable type creation without changing the model. Takes the same arguments as " +
        "revit_create_cable_type: sourceTypeName or sourceTypeId, newName (+ parameters), newNames, " +
        "or items=[{newName, parameters}], ifExists (skip|error). Returns the resolved source type and, " +
        "per new name, whether it will be created, skipped as existing, or is blocked, plus a check of every parameter.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
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

        var proposals = new List<object>();
        foreach (var entry in context.Plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Parameters are checked on the source: the copy inherits its parameter set.
            var checks = entry.Action == CableTypeCreationAction.Create
                ? entry.Item.Parameters
                    .Select(p => FamilyTypeSupport.CheckParameter(context.Source, p.Key, p.Value))
                    .ToList()
                : [];
            foreach (var check in checks.Where(c => !c.WillSucceed))
                warnings.Add($"{entry.Item.NewName}: {check.Name} — {check.Message}");

            var existing = entry.Action == CableTypeCreationAction.SkipExisting
                ? CableTypeDuplicator.FindExisting(context, entry.Item.NewName)
                : null;

            proposals.Add(new
            {
                newName = entry.Item.NewName,
                action = ActionName(entry.Action),
                reason = entry.Reason,
                existingTypeId = existing?.Id.Value,
                parameters = checks.Select(c => c.ToPayload()).ToList()
            });
        }

        var toCreate = context.Plan.Count(p => p.Action == CableTypeCreationAction.Create);
        var skipped = context.Plan.Count(p => p.Action == CableTypeCreationAction.SkipExisting);
        var blocked = context.Plan.Count(p => p.Action == CableTypeCreationAction.Blocked);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {toCreate} cable type(s) to create, {skipped} already exist, {blocked} blocked.",
            Data = new
            {
                kind = context.Kind,
                sourceTypeId = context.Source.Id.Value,
                sourceTypeName = FamilyTypeSupport.SafeName(context.Source),
                toCreate,
                skipped,
                blocked,
                proposals
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    internal static string ActionName(CableTypeCreationAction action) => action switch
    {
        CableTypeCreationAction.Create => "create",
        CableTypeCreationAction.SkipExisting => "skipExisting",
        _ => "blocked"
    };

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

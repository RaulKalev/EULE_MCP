using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewEditFamilyTypesTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_family_types";

    public string Description =>
        "Previews family type renames and parameter edits without changing the model. Takes the same " +
        "arguments as revit_edit_family_types: edits ([{typeId, newName, parameters}]), or " +
        "typeIds/familyName/typeName with newName and parameters. Returns the planned name change and, " +
        "per parameter, its current value, whether it is writable, and whether the new value parses.";

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
        var plan = FamilyTypeEditPlanner.Build(doc, request.Arguments, warnings, out var error);
        if (plan == null)
            return Task.FromResult(Fail(request, error!));

        var proposals = new List<object>();
        var canEdit = 0;
        var plannedRenames = 0;
        var plannedParameterWrites = 0;

        foreach (var entry in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var parameterChecks = entry.Parameters
                .Select(pair => FamilyTypeSupport.CheckParameter(entry.Type, pair.Key, pair.Value))
                .ToList();

            if (entry.CanEdit)
            {
                canEdit++;
                if (entry.NewName != null) plannedRenames++;
                plannedParameterWrites += parameterChecks.Count(c => c.WillSucceed);
            }
            else
            {
                warnings.Add($"{FamilyTypeSupport.SafeName(entry.Type)}: {entry.BlockedReason}");
            }

            foreach (var check in parameterChecks.Where(c => !c.WillSucceed))
                warnings.Add($"{FamilyTypeSupport.SafeName(entry.Type)}: {check.Name} — {check.Message}");

            proposals.Add(new
            {
                typeId = entry.Type.Id.Value,
                familyName = FamilyTypeSupport.SafeFamilyName(entry.Type),
                currentTypeName = FamilyTypeSupport.SafeName(entry.Type),
                newTypeName = entry.NewName,
                willRename = entry.CanEdit && entry.NewName != null,
                category = entry.Type.Category?.Name ?? string.Empty,
                kind = FamilyTypeSupport.KindOf(entry.Type),
                canEdit = entry.CanEdit,
                reason = entry.BlockedReason ?? entry.RenameNote ?? "Will be edited.",
                parameters = parameterChecks.Select(c => c.ToPayload()).ToList()
            });
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {canEdit} of {plan.Count} type(s) can be edited — " +
                      $"{plannedRenames} rename(s), {plannedParameterWrites} parameter value(s).",
            Data = new
            {
                total = plan.Count,
                canEdit,
                blocked = plan.Count - canEdit,
                plannedRenames,
                plannedParameterWrites,
                proposals
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

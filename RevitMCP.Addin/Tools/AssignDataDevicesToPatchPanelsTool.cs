using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical.PatchPanelAssignment;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class AssignDataDevicesToPatchPanelsTool : IRevitMcpTool
{
    public string Name => "revit_assign_data_devices_to_patch_panels";
    public string Description =>
        "Executes the data-device → patch-panel circuit assignment previewed by " +
        "revit_preview_assign_data_devices_to_patch_panels (pass the same arguments; the plan is rebuilt and " +
        "re-validated at execution time, and reruns are idempotent — already-circuited connectors are never " +
        "duplicated). Creates one Data circuit per planned connector and assigns it to its planned panel, one " +
        "transaction per device inside a TransactionGroup. dryRun=true (default) only reports what would happen; " +
        "atomic=true (default) rolls back everything if any device fails. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var parsed = PatchPanelAssignmentToolHelper.Parse(request);
        if (parsed.Error != null) return Task.FromResult(Fail(request, parsed.Error));

        var dryRun = !request.Arguments.ContainsKey("dryRun") || ToolArguments.GetBool(request.Arguments, "dryRun");
        var atomic = !request.Arguments.ContainsKey("atomic") || ToolArguments.GetBool(request.Arguments, "atomic");

        var (collection, plan, error) = PatchPanelAssignmentToolHelper.BuildPlan(doc, parsed);
        if (plan == null) return Task.FromResult(Fail(request, error ?? "Planning failed."));

        var warnings = parsed.Warnings
            .Concat(collection.Warnings)
            .Concat(plan.Warnings)
            .ToList();

        if (!plan.IsValid)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "validation_failed",
                Message = $"Plan is not executable — nothing was changed: {string.Join(" | ", plan.Errors)}",
                Data = PatchPanelAssignmentToolHelper.PlanToData(parsed, collection, plan),
                Warnings = warnings,
                Errors = plan.Errors,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        if (dryRun)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"DRY RUN — no model changes. Would create {plan.TotalCircuitsPlanned} circuit(s) for " +
                          $"{plan.Devices.Count} device(s). Re-run with dryRun=false to execute.",
                Data = new
                {
                    dryRun = true,
                    atomic,
                    plan = PatchPanelAssignmentToolHelper.PlanToData(parsed, collection, plan)
                },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var outcome = PatchPanelAssignmentService.Execute(doc, plan, atomic);
        var finalUtilization = PatchPanelAssignmentService.ReadUtilization(
            doc, collection.Panels, parsed.MaxCircuitsPerPanel);
        warnings.AddRange(outcome.Warnings);
        warnings.AddRange(outcome.SkippedAlreadyCircuited);

        sw.Stop();
        var data = new
        {
            dryRun = false,
            atomic,
            rolledBack = outcome.RolledBack,
            createdCircuitCount = outcome.Created.Count,
            createdCircuits = outcome.Created.Select(c => new
            {
                circuitId = c.CircuitId,
                deviceElementId = c.DeviceElementId,
                connectorId = c.ConnectorId,
                panelName = c.PanelName,
                panelElementId = c.PanelElementId
            }),
            finalPanelUtilization = finalUtilization.Select(p => new
            {
                panelName = p.PanelName,
                panelElementId = p.PanelElementId,
                capacity = p.Capacity,
                circuits = p.ExistingCircuits,
                spare = p.Spare
            }),
            failures = outcome.Failures
        };

        if (!outcome.Success)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = outcome.RolledBack
                    ? $"Assignment failed and was rolled back completely (atomic=true). First failure: {outcome.Failures.FirstOrDefault()}"
                    : $"Assignment finished with failures. Created {outcome.Created.Count} circuit(s). First failure: {outcome.Failures.FirstOrDefault()}",
                Data = data,
                Warnings = warnings,
                Errors = outcome.Failures,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Created {outcome.Created.Count} circuit(s) for {plan.Devices.Count} device(s). " +
                      $"{outcome.SkippedAlreadyCircuited.Count} connector(s) skipped as already circuited.",
            Data = data,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

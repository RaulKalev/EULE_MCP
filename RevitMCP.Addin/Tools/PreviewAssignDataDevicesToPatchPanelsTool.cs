using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class PreviewAssignDataDevicesToPatchPanelsTool : IRevitMcpTool
{
    public string Name => "revit_preview_assign_data_devices_to_patch_panels";
    public string Description =>
        "Read-only preview of bulk data-device → patch-panel circuit assignment. Collects Data Devices " +
        "(levelName or elementIds), reads their electrical connectors and existing circuits, sorts devices " +
        "clockwise around the floor perimeter (startCorner, default TopLeft), applies connectorRules " +
        "(default: '1 x RJ45'=1 circuit, '2 x RJ45'=2 circuits) and plans one Data circuit per connector onto " +
        "panels (panelNames/panelElementIds, in list order) without exceeding each panel's 'Maximum Amount of " +
        "Circuits' minus its existing circuits. keepDeviceConnectorsTogether keeps both circuits of a 2-port " +
        "device on one panel. Returns the full assignment plan, per-panel utilization and validation report. " +
        "Makes no model changes — run this before revit_assign_data_devices_to_patch_panels.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var parsed = PatchPanelAssignmentToolHelper.Parse(request);
        if (parsed.Error != null) return Task.FromResult(Fail(request, parsed.Error));

        var (collection, plan, error) = PatchPanelAssignmentToolHelper.BuildPlan(doc, parsed);
        if (plan == null) return Task.FromResult(Fail(request, error ?? "Planning failed."));

        var warnings = parsed.Warnings
            .Concat(collection.Warnings)
            .Concat(plan.Warnings)
            .ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = plan.IsValid
                ? $"Plan: {plan.TotalCircuitsPlanned} circuit(s) for {plan.Devices.Count} device(s) across " +
                  $"{plan.Panels.Count(p => p.PlannedNewCircuits > 0)} panel(s). {plan.Skipped.Count} device(s) skipped. " +
                  "Plan is valid — run revit_assign_data_devices_to_patch_panels with the same arguments to execute."
                : $"Plan is NOT executable: {string.Join(" | ", plan.Errors)}",
            Data = PatchPanelAssignmentToolHelper.PlanToData(parsed, collection, plan),
            Warnings = warnings,
            Errors = plan.Errors,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

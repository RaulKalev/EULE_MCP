using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Presets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ValidateViewSheetPresetTool : IRevitMcpTool
{
    public string Name => "revit_validate_view_sheet_preset";
    public string Description =>
        "Validates the structure of a PlaceViews / Sheet Manager preset JSON file and reports errors and suggestions. " +
        "Accepts: presetName (string). " +
        "Returns: isValid, errors[], suggestions[], workflowType. " +
        "Does not modify the model.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw             = Stopwatch.StartNew();
        var presetName     = ToolArguments.GetString(request.Arguments, "presetName");
        var overrideFolder = ToolArguments.GetString(request.Arguments, "overrideFolderPath");

        if (string.IsNullOrWhiteSpace(presetName))
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId, Success = false,
                Message   = "presetName is required."
            });

        var (success, _, parsed, readError) =
            PlaceViewsPresetService.ReadPreset(
                presetName,
                string.IsNullOrWhiteSpace(overrideFolder) ? null : overrideFolder);

        sw.Stop();

        if (!success || parsed == null)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId, Success = false,
                Message    = readError ?? $"Could not read or parse preset '{presetName}'.",
                DurationMs = sw.ElapsedMilliseconds
            });

        var errors       = PlaceViewsPresetService.ValidatePresetStructure(parsed, out var suggestions);
        var workflowType = PlaceViewsPresetService.ClassifyWorkflow(parsed);
        var isValid      = errors.Count == 0;

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = isValid
                ? $"Preset '{presetName}' is structurally valid. Workflow type: {workflowType}."
                : $"Preset '{presetName}' has {errors.Count} validation error{(errors.Count == 1 ? "" : "s")}.",
            Data       = new
            {
                isValid,
                workflowType,
                errors,
                suggestions
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

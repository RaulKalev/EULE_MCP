using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCP.Addin.Documentation.Presets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetViewSheetPresetTool : IRevitMcpTool
{
    public string Name => "revit_get_view_sheet_preset";
    public string Description =>
        "Reads and returns the contents of a named PlaceViews / Sheet Manager preset JSON file. " +
        "Accepts: presetName (string, filename with or without .json extension). " +
        "Returns: fileName, detectedType, workflowType, rawContent, parsedContent. " +
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

        var (success, content, parsed, error) =
            PlaceViewsPresetService.ReadPreset(
                presetName,
                string.IsNullOrWhiteSpace(overrideFolder) ? null : overrideFolder);

        sw.Stop();

        if (!success)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId, Success = false,
                Message    = error ?? "Preset not found.",
                DurationMs = sw.ElapsedMilliseconds
            });

        var workflowType = parsed != null ? PlaceViewsPresetService.ClassifyWorkflow(parsed) : "Unknown";

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Loaded preset '{presetName}'. Detected workflow type: {workflowType}.",
            Data       = new
            {
                fileName      = presetName,
                workflowType,
                parsedContent = parsed != null ? (object)parsed.ToObject<object>()! : content!
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

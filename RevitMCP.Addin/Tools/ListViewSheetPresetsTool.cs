using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Presets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListViewSheetPresetsTool : IRevitMcpTool
{
    public string Name => "revit_list_view_sheet_presets";
    public string Description =>
        "Lists available PlaceViews / Sheet Manager preset JSON files from the RK Tools preset folder " +
        @"(C:\ProgramData\RK Tools\PlaceViews\SheetManagerSettings). " +
        "Returns: fileName, detectedType, sizeBytes, modifiedUtc for each preset. " +
        "Does not modify the model.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Documentation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw              = Stopwatch.StartNew();
        var overrideFolder  = ToolArguments.GetString(request.Arguments, "overrideFolderPath");

        var (success, folderPath, presets, warning) =
            PlaceViewsPresetService.ListPresets(string.IsNullOrWhiteSpace(overrideFolder) ? null : overrideFolder);

        sw.Stop();

        if (!success)
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = warning ?? "Preset folder not found.",
                Data       = new { folderPath },
                DurationMs = sw.ElapsedMilliseconds
            });

        var warnings = new List<string>();
        if (warning != null) warnings.Add(warning);

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Found {presets.Count} preset file{(presets.Count == 1 ? "" : "s")} in '{folderPath}'.",
            Data       = new
            {
                folderPath,
                count   = presets.Count,
                presets = presets.Select(p => new
                {
                    p.FileName,
                    p.DetectedType,
                    p.SizeBytes,
                    modifiedUtc = p.ModifiedUtc.ToString("o")
                }).ToList()
            },
            Warnings   = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

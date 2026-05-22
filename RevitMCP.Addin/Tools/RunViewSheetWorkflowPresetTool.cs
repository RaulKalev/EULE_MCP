using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Presets;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Read-only planning/preview tool. Returns what a preset workflow would do,
/// but does NOT execute any Revit write operations. Actual execution requires
/// using the appropriate create/duplicate/rename tools separately.
/// </summary>
public class RunViewSheetWorkflowPresetTool : IRevitMcpTool
{
    public string Name => "revit_run_view_sheet_workflow_preset";
    public string Description =>
        "Plans a view/sheet workflow from a preset file — returns a structured preview of what the workflow would do. " +
        "Accepts: presetName (string). " +
        "Returns: workflowType, stepCount, steps, notes. " +
        "This tool is READ-ONLY: it does not modify the Revit model. " +
        "Use revit_duplicate_sheets, revit_place_views_on_sheets, etc. to execute the actual workflow steps.";
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

        if (!success || parsed == null)
        {
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId, Success = false,
                Message    = readError ?? $"Could not read or parse preset '{presetName}'.",
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var errors        = PlaceViewsPresetService.ValidatePresetStructure(parsed, out var suggestions);
        var workflowType  = PlaceViewsPresetService.ClassifyWorkflow(parsed);
        var steps         = BuildWorkflowSteps(workflowType, parsed);
        var notes         = new List<string>
        {
            "This tool only previews the workflow. No changes have been made to the model.",
            "Execute workflow steps using the appropriate write tools (revit_duplicate_sheets, revit_place_views_on_sheets, etc.)."
        };
        if (errors.Count > 0)
            notes.Add($"Validation warnings detected: {string.Join("; ", errors)}");
        notes.AddRange(suggestions);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Preset '{presetName}' describes a '{workflowType}' workflow with {steps.Count} planning step{(steps.Count == 1 ? "" : "s")}.",
            Data       = new
            {
                presetName,
                workflowType,
                stepCount = steps.Count,
                steps,
                notes
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static List<object> BuildWorkflowSteps(string workflowType, Newtonsoft.Json.Linq.JObject preset)
    {
        var steps = new List<object>();
        var keys  = preset.Properties().Select(p => p.Name).ToList();

        steps.Add(new { stepNumber = 1, action = "LoadPreset", description = $"Load and parse preset '{workflowType}'." });

        switch (workflowType)
        {
            case "DuplicateSheets":
                steps.Add(new { stepNumber = 2, action = "IdentifySourceSheets", description = "Identify source sheets to duplicate from preset." });
                steps.Add(new { stepNumber = 3, action = "DuplicateSheets", description = "Call revit_duplicate_sheets with parameters from preset. (Not executed here.)" });
                break;
            case "CreateSheets":
                steps.Add(new { stepNumber = 2, action = "PrepareSheetData", description = "Extract sheet rows/data from preset." });
                steps.Add(new { stepNumber = 3, action = "CreateSheets", description = "Call revit_create_sheets_from_table with data from preset. (Not executed here.)" });
                break;
            case "PlaceViews":
                steps.Add(new { stepNumber = 2, action = "ResolveViews", description = "Resolve view names/IDs specified in preset." });
                steps.Add(new { stepNumber = 3, action = "PlaceViewsOnSheets", description = "Call revit_place_views_on_sheets with placement data. (Not executed here.)" });
                break;
            case "RenameWorkflow":
                steps.Add(new { stepNumber = 2, action = "ResolveTargets", description = "Resolve sheets or views to rename." });
                steps.Add(new { stepNumber = 3, action = "RenameElements", description = "Call revit_rename_views or revit_rename_sheets. (Not executed here.)" });
                break;
            case "ParameterMapping":
                steps.Add(new { stepNumber = 2, action = "ResolveParameters", description = "Identify parameter mapping rules in preset." });
                steps.Add(new { stepNumber = 3, action = "SetParameters", description = "Call revit_set_sheet_parameters_bulk or revit_set_view_parameters_bulk. (Not executed here.)" });
                break;
            default:
                steps.Add(new { stepNumber = 2, action = "ReviewPreset", description = $"Preset type '{workflowType}' not automatically mapped to workflow steps. Review preset JSON fields: {string.Join(", ", keys.Take(10))}." });
                break;
        }

        return steps;
    }
}

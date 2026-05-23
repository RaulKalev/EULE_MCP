using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class CreatePanelSchematicSymbolFromDwgTool : IRevitMcpTool
{
    public string Name => "revit_create_panel_schematic_symbol_from_dwg";

    public string Description =>
        "Creates a Detail Item family (.rfa) from a local DWG file using a company preset. " +
        "The generated family is saved to the configured output folder and is NOT loaded into the active project. " +
        "Required: dwgPath (string — full local path to .dwg), userDefinedName (string). " +
        "Optional: presetName (string, default \"DefaultPanelSchematicSymbol\"), " +
        "outputFolder (string, overrides the preset's default output folder). " +
        "Generated family is named Kilp_<userDefinedName>.rfa; if that name already exists, " +
        "a version suffix is applied (_01, _02, ...). " +
        "Config file: %ProgramData%\\RKTools\\MCP\\Config\\DwgDetailItemPresets.json";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory   Category   => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var dwgPath         = ToolArguments.GetString(request.Arguments, "dwgPath");
        var userDefinedName = ToolArguments.GetString(request.Arguments, "userDefinedName");
        var presetName      = ToolArguments.GetString(request.Arguments, "presetName",
                                  "DefaultPanelSchematicSymbol");
        var outputFolder    = ToolArguments.GetString(request.Arguments, "outputFolder");

        if (string.IsNullOrWhiteSpace(dwgPath))
            return Task.FromResult(Fail(request, "dwgPath is required."));
        if (string.IsNullOrWhiteSpace(userDefinedName))
            return Task.FromResult(Fail(request, "userDefinedName is required."));

        var genRequest = new DwgFamilyGenerateRequest
        {
            DwgPath         = dwgPath,
            UserDefinedName = userDefinedName,
            PresetName      = presetName,
            OutputFolder    = outputFolder
        };

        var r = DwgDetailFamilyGenerator.Generate(uiapp, genRequest);

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = r.Success,
            Message    = r.Success
                ? $"Created {r.FileName} at {r.RfaPath}"
                : $"Family creation failed: {string.Join("; ", r.Errors)}",
            Data = r.Success
                ? (object)new
                {
                    familyName = r.FamilyName,
                    fileName   = r.FileName,
                    rfaPath    = r.RfaPath,
                    presetUsed = r.PresetUsed
                }
                : null,
            Warnings   = r.Warnings,
            Errors     = r.Errors,
            DurationMs = r.DurationMs
        });
    }

    private static McpToolResult Fail(McpToolRequest request, string error) =>
        new()
        {
            RequestId = request.RequestId,
            Success   = false,
            Message   = error,
            Errors    = [error]
        };
}

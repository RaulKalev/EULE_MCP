using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class ListCableResistanceProfilesTool : IRevitMcpTool
{
    public string Name => "revit_list_cable_resistance_profiles";
    public string Description => "Lists configured cable resistance profiles used for fire alarm voltage-drop and loop resistance estimates. Profiles are loaded from %AppData%\\RKTools\\RevitMCP\\electrical-cable-profiles.json (created automatically with defaults on first use).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        CableResistanceProfileService.EnsureDefaultFileExists();
        var collection = CableResistanceProfileService.Load();

        var profiles = collection.Profiles.Select(p => (object)new
        {
            matchNameContains = p.MatchNameContains,
            description       = p.Description,
            roundTripOhmPerMeter = p.RoundTripOhmPerMeter
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = $"Loaded {profiles.Count} cable resistance profile(s).",
            Data       = new { profileCount = profiles.Count, configFilePath = CableResistanceProfileService.ConfigFilePath, profiles },
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

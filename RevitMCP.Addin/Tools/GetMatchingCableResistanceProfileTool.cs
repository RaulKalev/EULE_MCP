using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetMatchingCableResistanceProfileTool : IRevitMcpTool
{
    public string Name => "revit_get_matching_cable_resistance_profile";
    public string Description => "Returns the cable resistance profile that would be used for the given cable type name, or null if no profile matches. Accepts: cableTypeName (required string).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var cableTypeName = ToolArguments.GetString(request.Arguments, "cableTypeName");
        if (string.IsNullOrWhiteSpace(cableTypeName))
            return Task.FromResult(Fail(request, "cableTypeName is required."));

        CableResistanceProfileService.EnsureDefaultFileExists();
        var collection = CableResistanceProfileService.Load();
        var match = CableResistanceProfileService.FindMatch(cableTypeName, collection);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = match != null
                ? $"Profile matched for '{cableTypeName}': {match.RoundTripOhmPerMeter} Ω/m."
                : $"No resistance profile found for '{cableTypeName}'. Using fallback resistance if available.",
            Data = match != null
                ? (object)new
                {
                    matched = true,
                    cableTypeName,
                    matchedOn        = match.MatchNameContains,
                    description      = match.Description,
                    roundTripOhmPerMeter = match.RoundTripOhmPerMeter
                }
                : new { matched = false, cableTypeName },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

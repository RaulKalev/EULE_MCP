using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetPanelIssueSummaryTool : IRevitMcpTool
{
    public string Name => "revit_get_panel_issue_summary";
    public string Description => "Returns electrical QA data grouped by panel: circuit count, issue counts, load. Accepts: panelName (optional filter, partial match), includeCircuitDetails (bool), includeIssueDetails (bool), limit (int).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var panelName       = ToolArguments.GetString(request.Arguments, "panelName");
        var includeCircuits = ToolArguments.GetBool(request.Arguments, "includeCircuitDetails", false);
        var includeIssues   = ToolArguments.GetBool(request.Arguments, "includeIssueDetails",   true);
        var limit           = ToolArguments.GetInt(request.Arguments,  "limit",                5000);

        var panels = ElectricalDashboardService.BuildPanelIssueSummary(
            doc, panelName, includeCircuits, includeIssues, limit, cancellationToken);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Panel issue summary returned for {panels.Count} panel(s).",
            Data = new { panelCount = panels.Count, panels },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetElectricalDashboardSummaryTool : IRevitMcpTool
{
    public string Name => "revit_get_electrical_dashboard_summary";
    public string Description => "Returns a compact model-wide electrical QA summary: panel/circuit counts, issue counts per type, top issue panels, system type breakdown, and load summary. Accepts: includePanels (bool), includeSystemTypes (bool), includeTopIssues (bool), includeUncircuitedSummary (bool — scans all circuit elements, slower), includeLoadSummary (bool), limit (int).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var includePanels      = ToolArguments.GetBool(request.Arguments, "includePanels",           true);
        var includeSystemTypes = ToolArguments.GetBool(request.Arguments, "includeSystemTypes",       true);
        var includeTopIssues   = ToolArguments.GetBool(request.Arguments, "includeTopIssues",         true);
        var includeUncircuited = ToolArguments.GetBool(request.Arguments, "includeUncircuitedSummary",true);
        var includeLoad        = ToolArguments.GetBool(request.Arguments, "includeLoadSummary",       true);
        var limit              = ToolArguments.GetInt(request.Arguments,  "limit",                   5000);

        var result = ElectricalDashboardService.Build(
            doc, includePanels, includeSystemTypes, includeTopIssues,
            includeUncircuited, includeLoad, limit, cancellationToken);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = "Electrical dashboard summary generated.",
            Data = new
            {
                model           = result.Model,
                summary         = result.Summary,
                issueBreakdown  = result.IssueBreakdown,
                panelsWithMostIssues = result.PanelsWithMostIssues,
                systemTypeSummary = result.SystemTypeSummary,
                loadSummary     = result.LoadSummary
            },
            Warnings = result.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

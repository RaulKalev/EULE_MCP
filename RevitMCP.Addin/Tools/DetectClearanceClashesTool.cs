using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class DetectClearanceClashesTool : IRevitMcpTool
{
    public string Name => "revit_detect_clearance_clashes";
    public string Description => "Detects clearance violations between two sets of element categories using expanded bounding-box approximation. WARNING: distances reported are conservative estimates, not true surface-to-surface measurements.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashCandidateCollector _collector = new();
    private readonly ClearanceClashDetector _detector = new();
    private readonly ClashRunCacheService _cache = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var args = request.Arguments;
        var sourceCategories = ToolArguments.GetStringArray(args, "sourceCategories");
        var targetCategories = ToolArguments.GetStringArray(args, "targetCategories");
        var clearanceMm = GetDouble(args, "clearanceMm", 50.0);
        var includeLinks = ToolArguments.GetBool(args, "includeLinks", true);
        var includeGenericModels = ToolArguments.GetBool(args, "includeGenericModels", true);
        var includeImportedGeometry = ToolArguments.GetBool(args, "includeImportedGeometry", true);
        var linkNameFilters = ToolArguments.GetStringArray(args, "linkNameFilters");
        var limit = ToolArguments.GetInt(args, "limit", 1000);
        var maxPairs = ToolArguments.GetInt(args, "maxPairs", 100_000);
        var saveAsLastRun = ToolArguments.GetBool(args, "saveAsLastRun", true);
        var ruleName = ToolArguments.GetString(args, "ruleName", "Ad-hoc Clearance");
        var severity = ToolArguments.GetString(args, "severity", "Medium");

        if (sourceCategories.Length == 0) return Task.FromResult(Fail(request, "sourceCategories is required."));
        if (targetCategories.Length == 0) return Task.FromResult(Fail(request, "targetCategories is required."));
        if (clearanceMm <= 0) return Task.FromResult(Fail(request, "clearanceMm must be positive."));

        var (sources, srcWarn) = _collector.Collect(doc, sourceCategories, includeLinks, includeGenericModels, includeImportedGeometry, linkNameFilters, 0);
        var (targets, tgtWarn) = _collector.Collect(doc, targetCategories, includeLinks, includeGenericModels, includeImportedGeometry, linkNameFilters, 0);

        var (clashes, detectWarn) = _detector.Detect(sources, targets, ruleName, severity, clearanceMm, limit, maxPairs, cancellationToken);

        var allWarnings = srcWarn.Concat(tgtWarn).Concat(detectWarn).Distinct().ToList();
        var run = new ClashRunResultDto
        {
            RunId = Guid.NewGuid().ToString("N")[..8].ToUpper(),
            RunAt = DateTime.Now,
            TotalClashes = clashes.Count,
            Clashes = clashes,
            Warnings = allWarnings,
            CountByRule = new Dictionary<string, int> { [ruleName] = clashes.Count }
        };

        if (saveAsLastRun) _cache.Save(run);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Detected {clashes.Count} clearance violation(s) at {clearanceMm}mm clearance (bounding-box approximation). {(saveAsLastRun ? "Saved as last run." : "")}",
            Data = run,
            Warnings = allWarnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static double GetDouble(Dictionary<string, object?> args, string key, double def)
    {
        if (!args.TryGetValue(key, out var val)) return def;
        return val switch
        {
            double d => d,
            float f => f,
            JValue jv => (double)jv,
            _ => def
        };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

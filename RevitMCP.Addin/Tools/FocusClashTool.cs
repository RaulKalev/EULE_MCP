using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Coordination.Clash.Review;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class FocusClashTool : IRevitMcpTool
{
    public string Name => "revit_focus_clash";
    public string Description => "Activates the MCP Clash Review view, scopes the section box to the specified clash, and selects the source and target elements. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashRunCacheService _cache = new();
    private readonly ClashFocusService _focusService = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var args = request.Arguments;
        var clashId = ToolArguments.GetString(args, "clashId");
        if (string.IsNullOrWhiteSpace(clashId)) return Task.FromResult(Fail(request, "clashId is required."));

        var sectionBoxPaddingMm = GetDouble(args, "sectionBoxPaddingMm", 1000.0);

        var run = _cache.Load();
        if (run == null) return Task.FromResult(Fail(request, "No clash run in cache. Run a detection first."));

        var clash = run.Clashes.FirstOrDefault(c =>
            c.ClashId.Equals(clashId, StringComparison.OrdinalIgnoreCase));
        if (clash == null) return Task.FromResult(Fail(request, $"ClashId '{clashId}' not found in last run."));

        using var t = new Transaction(doc, "Revit MCP - Focus Clash");
        t.Start();
        var (message, warnings) = _focusService.Focus(uiapp, doc, clash, sectionBoxPaddingMm, t);
        t.Commit();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = message,
            Data = new { clash, runId = run.RunId },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static double GetDouble(Dictionary<string, object?> args, string key, double def)
    {
        if (!args.TryGetValue(key, out var val)) return def;
        return val switch { double d => d, float f => f, JValue jv => (double)jv, _ => def };
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

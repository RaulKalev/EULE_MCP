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

public class CreateClashReviewViewTool : IRevitMcpTool
{
    public string Name => "revit_create_clash_review_view";
    public string Description => "Creates or reuses the 'MCP Clash Review' 3D view. Optionally scopes the section box to a specific clash by ClashId. Requires approval.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    private readonly ClashReviewViewService _viewService = new();
    private readonly ClashRunCacheService _cache = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        if (uidoc?.Document == null) return Task.FromResult(Fail(request, "No active document."));
        var doc = uidoc.Document;

        var args = request.Arguments;
        var clashId = ToolArguments.GetString(args, "clashId", "");
        var sectionBoxPaddingMm = GetDouble(args, "sectionBoxPaddingMm", 1000.0);

        cancellationToken.ThrowIfCancellationRequested();
        using var t = new Transaction(doc, "Revit MCP - Create Clash Review View");
        t.Start();
        var view = _viewService.CreateOrReuseView(doc);

        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(clashId))
        {
            var run = _cache.Load();
            var clash = run?.Clashes.FirstOrDefault(c =>
                c.ClashId.Equals(clashId, StringComparison.OrdinalIgnoreCase));

            if (clash == null)
                warnings.Add($"ClashId '{clashId}' not found in last run — section box not set.");
            else
                _viewService.SetSectionBox(doc, view, clash, sectionBoxPaddingMm);
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(t);

        uidoc.ActiveView = view;

        sw.Stop();
        var msg = string.IsNullOrWhiteSpace(clashId)
            ? $"'{ClashReviewViewService.ViewName}' view is ready."
            : $"'{ClashReviewViewService.ViewName}' view focused on clash '{clashId}'.";

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = msg,
            Data = new { viewId = view.Id.Value, viewName = view.Name },
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

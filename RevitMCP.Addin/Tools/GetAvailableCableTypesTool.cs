using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetAvailableCableTypesTool : IRevitMcpTool
{
    public string Name => "revit_get_available_cable_types";
    public string Description => "Lists cable types in the project if available. Returns a warning if cable types are not separately defined — use revit_get_available_wire_types in that case.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var (types, warnings) = CableTypeResolver.GetAll(doc);

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = types.Count > 0
                ? $"Found {types.Count} cable type(s)."
                : "No cable types found in this model.",
            Data = new { cableTypes = types },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

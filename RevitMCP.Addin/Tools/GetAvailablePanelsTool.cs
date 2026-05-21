using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Electrical;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetAvailablePanelsTool : IRevitMcpTool
{
    public string Name => "revit_get_available_panels";
    public string Description => "Lists electrical equipment elements (panels/distribution boards) that circuits can be assigned to.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    private readonly PanelResolver _resolver = new();

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active document."));

        var nameContains = ToolArguments.GetString(request.Arguments, "nameContains");
        var limit = ToolArguments.GetInt(request.Arguments, "limit", 500);

        var allPanels = _resolver.GetAllPanels(doc);
        if (!string.IsNullOrWhiteSpace(nameContains))
            allPanels = allPanels
                .Where(p => p.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase))
                .ToList();

        var panels = allPanels.Take(limit).Select(p =>
        {
            var typeId = p.GetTypeId();
            var typeElem = typeId != null && typeId != ElementId.InvalidElementId
                ? doc.GetElement(typeId) : null;

            string level = "";
            try
            {
                var levelParam = p.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM);
                if (levelParam != null)
                {
                    var levelElem = doc.GetElement(levelParam.AsElementId());
                    level = levelElem?.Name ?? "";
                }
            }
            catch { }

            return new
            {
                elementId = p.Id.Value,
                name = p.Name,
                category = p.Category?.Name ?? "",
                family = p.Symbol?.Family?.Name ?? "",
                type = typeElem?.Name ?? "",
                level,
                existingCircuitCount = _resolver.GetCircuitCountForPanel(doc, p)
            };
        }).ToList();

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Found {panels.Count} panel(s).",
            Data = new { panels },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

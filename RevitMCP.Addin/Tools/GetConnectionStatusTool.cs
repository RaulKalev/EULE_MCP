using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class GetConnectionStatusTool : IRevitMcpTool
{
    public string Name => "revit_get_connection_status";
    public string Description => "Returns current Revit connection and document status.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Connection;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var ctx = RevitContextService.Read(uiapp);
        var isDirectEditEnabled = RevitMCP.Addin.App.GetViewModel()?.IsDirectEditEnabled ?? false;

        var data = new
        {
            revitVersion = ctx.RevitVersion,
            connectorStatus = "Running",
            isDocumentOpen = ctx.IsDocumentOpen,
            documentTitle = ctx.DocumentTitle,
            activeViewName = ctx.ActiveViewName,
            isWorkshared = ctx.IsWorkshared,
            centralModelPath = ctx.CentralModelPath,
            localModelPath = ctx.LocalModelPath,
            revitUsername = ctx.RevitUsername,
            selectedElementCount = ctx.SelectedElementCount,
            permissionMode = PermissionModeStatus.GetName(isDirectEditEnabled),
            destructiveActionsRequireManualApproval = PermissionModeStatus.DestructiveActionsRequireManualApproval
        };

        sw.Stop();

        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = ctx.IsDocumentOpen
                ? $"Connected to Revit {ctx.RevitVersion}. Document: {ctx.DocumentTitle}"
                : $"Connected to Revit {ctx.RevitVersion}. No document open.",
            Data = data,
            DurationMs = sw.ElapsedMilliseconds
        });
    }
}

using Autodesk.Revit.UI;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Interfaces;

public interface IRevitMcpTool
{
    string Name { get; }
    string Description { get; }
    ToolPermission Permission { get; }
    ToolCategory Category { get; }

    Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken);
}

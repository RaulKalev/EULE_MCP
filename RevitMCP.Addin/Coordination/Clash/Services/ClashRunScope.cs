using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Coordination.Clash.Services;

/// <summary>The clash run cache of the document a request targets (its <c>document</c> argument, else the active one).</summary>
internal static class ClashRunScope
{
    public static ClashRunCacheService Cache(UIApplication app, McpToolRequest request)
    {
        string scope = string.Empty;
        try
        {
            var target = ToolDocument.Resolve(app, request, out _);
            var doc = target?.Document ?? app.ActiveUIDocument?.Document;
            if (doc != null) scope = CheckpointChangeTracker.ModelIdOf(doc);
        }
        catch
        {
            // No document: the shared "none" scope.
        }
        return new ClashRunCacheService(scope);
    }
}

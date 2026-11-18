using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>MEP connectors of a model family (#174). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyConnectorTools
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewEditModelFamilyConnectorsTool());
        handler.RegisterTool(new EditModelFamilyConnectorsTool());
        ModelFamilyInspect.AddSection("connectors",
            "MEP connectors: id, description, domain, systemType, shape, size (mm), origin (mm), direction, host, isPrimary, linkedConnector, properties, associations.",
            (context, limit) => ModelFamilyConnectorRevit.Inspect(context.FamilyDoc, limit));
    }
}

/// <summary>revit_preview_edit_model_family_connectors: runs the connector edit and rolls it back; changes nothing.</summary>
public sealed class PreviewEditModelFamilyConnectorsTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_model_family_connectors";

    public string Description =>
        "Previews revit_edit_model_family_connectors: runs the same edit (remove, connectors, edit) on the family and rolls " +
        "it back, so it reports what Revit would really create, store and refuse. Changes nothing.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, "edit MEP connectors", ModelFamilyConnectorExecutor.Run, cancellationToken));
}

/// <summary>revit_edit_model_family_connectors: creates, edits and removes MEP connectors of a model family.</summary>
public sealed class EditModelFamilyConnectorsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_model_family_connectors";

    public string Description =>
        "Creates, edits and removes MEP connectors (electrical, duct, pipe, conduit, cable tray) of a model family. " +
        "Requires approval. Runs remove, then connectors (create on a face of a form), then edit; sets system type, " +
        "shape, size, position, direction, primary and linked connectors, connector properties and their association " +
        "with family parameters, and reads every connector back. Run revit_preview_edit_model_family_connectors first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, "edit MEP connectors", ModelFamilyConnectorExecutor.Run, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => ModelFamilyConnectorExecutor.Summary(uiapp, request);
}

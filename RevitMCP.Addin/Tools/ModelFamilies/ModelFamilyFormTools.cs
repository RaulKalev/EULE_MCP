using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Solid and void forms of a model family (#171). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyFormTools
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewEditModelFamilyFormsTool());
        handler.RegisterTool(new EditModelFamilyFormsTool());
        ModelFamilyInspect.AddSection("forms",
            "Solid and void forms: kind, sketch plane (and the reference plane it follows), offsets or angles, profile loops in mm, bounding box, visibility, subcategory, material, planar faces with references, cutBy/cuts and combination.",
            ModelFamilyFormRead.FormsSection);
        ModelFamilyInspect.AddSection("combinations",
            "Geometry combinations (joins and void cuts) and their member forms.",
            ModelFamilyFormRead.CombinationsSection);
    }
}

/// <summary>revit_preview_edit_model_family_forms: runs the form edit and rolls it back; changes nothing.</summary>
public sealed class PreviewEditModelFamilyFormsTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_model_family_forms";

    public string Description =>
        "Previews revit_edit_model_family_forms without changing anything: the same edit runs inside a transaction group and " +
        "is rolled back, so it reports what Revit would build (measured profile sizes, bounding boxes, cuts) or refuse.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, ModelFamilyFormExecutor.What, ModelFamilyFormExecutor.Run, cancellationToken));
}

/// <summary>
/// revit_edit_model_family_forms: creates extrusions, blends, revolves, sweeps and swept blends (solid or void), edits
/// their offsets, angles, visibility and subcategory, and cuts, joins and removes them. The operation lists are
/// parsed by <c>ModelFamilyFormPlan</c> and run in its order.
/// </summary>
public sealed class EditModelFamilyFormsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_model_family_forms";

    public string Description =>
        "Creates and edits solid and void forms in a model family. Requires approval. Runs remove, extrusions, blends, revolves, " +
        "sweeps, sweptBlends, edit, cuts, joins in that order; a failed step rolls the whole edit back.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, ModelFamilyFormExecutor.What, ModelFamilyFormExecutor.Run, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var plan = ModelFamilyFormExecutor.Plan(request);
        return $"Edit forms ({plan}) of {ModelFamilySession.DescribeTarget(request)}.";
    }
}

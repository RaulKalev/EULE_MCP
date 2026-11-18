using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Nested components, symbolic graphics and visibility of a model family (#175). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyComponentTools
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewEditModelFamilyComponentsTool());
        handler.RegisterTool(new EditModelFamilyComponentsTool());
        ModelFamilyComponentInspect.Register();
    }

    public const string Description =
        "Nested families, symbolic graphics and visibility of a loadable 3D family, in this order: remove (ids of nested instances, symbolic lines, " +
        "model lines), subcategories [{name, lineWeightProjection?, lineWeightCut?, color?, linePattern?, material?}], nestedFamilies [{path | fromProject, ifExists?}], " +
        "instances [{family, type?, position, rotation?, view? | workPlane?, parameters?, associate?, typeParameter?, visibleParameter?, visibility?, lockTo?}], " +
        "symbolicLines [{view?, curves, subcategory?, visibility?}], modelLines [{workPlane?, curves, subcategory?, visibility?}], " +
        "setSubcategory [{elements, subcategory | null}], visibility [{elements, visible?, visibleParameter?, detailLevels?, views?}]. " +
        "A later list may name what an earlier one created as {\"created\":\"instances[0]\"}. Millimetres and degrees. See docs/model-family-components.md.";
}

/// <summary>revit_preview_edit_model_family_components: checks an edit of a family's nested components; changes nothing.</summary>
public sealed class PreviewEditModelFamilyComponentsTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_model_family_components";

    public string Description => "Previews revit_edit_model_family_components without changing anything: runs the same edit and rolls it back, so Revit's own refusals show. " + ModelFamilyComponentTools.Description;

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, "edit nested components", ModelFamilyComponentExecutor.Run, cancellationToken));
}

/// <summary>revit_edit_model_family_components: loads and places nested families, draws symbolic and model lines, manages subcategories and visibility.</summary>
public sealed class EditModelFamilyComponentsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_model_family_components";

    public string Description => "Edits the nested components, symbolic graphics and visibility of a loadable 3D family. Requires approval. " + ModelFamilyComponentTools.Description;

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, "edit nested components", ModelFamilyComponentExecutor.Run, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var plan = ModelFamilyComponentPlan.Parse(request.Arguments, new List<string>());
        return $"Edit the nested components of {ModelFamilySession.DescribeTarget(request)}: {plan.Describe()}.";
    }
}

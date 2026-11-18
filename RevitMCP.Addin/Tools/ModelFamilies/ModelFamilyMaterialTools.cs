using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Materials of a model family and the forms they are assigned to (#173). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyMaterialTools
{
    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewEditModelFamilyMaterialsTool());
        handler.RegisterTool(new EditModelFamilyMaterialsTool());

        ModelFamilyInspect.AddSection("materials",
            "Materials of the family: id, name, colour, transparency, class, the elements that use each directly, and the material parameters that default to it per type.",
            (context, limit) => ModelFamilyMaterialInspect.Materials(context, limit));
        ModelFamilyInspect.AddSection("materialAssignments",
            "Every form with a Material property: where its material comes from (direct, parameter, byCategory), the material, and the driving family parameter.",
            (context, limit) => ModelFamilyMaterialInspect.Assignments(context, limit));
    }

    internal const string ArgumentsDescription =
        "Arguments, run in this order (a later list can name what an earlier one made): " +
        "materials [{name, color?:[r,g,b], transparency?:0-100, shininess?:0-128, smoothness?:0-100, materialClass?, duplicateFrom?:<material of the family>, copyFromProject?}] creates or edits materials by exact name; " +
        "assign [{elements:[ids], material:<name|id|null = By Category>, replaceAssociation?}] sets the Material property directly; " +
        "associate [{elements:[ids], parameter:<material family parameter|null = dissociate>, create?:{instance?, group?}}] drives it from a family parameter, adding the parameter with create; " +
        "defaults [{parameter, material:<name|id|null>, type?}] sets the parameter's value per family type (default every type). " +
        "A missing or ambiguous material name is a problem with the candidates.";
}

/// <summary>revit_preview_edit_model_family_materials: checks a materials edit of a model family; changes nothing.</summary>
internal sealed class PreviewEditModelFamilyMaterialsTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_model_family_materials";

    public string Description =>
        "Previews creating or editing the materials of a model family, assigning them to forms and driving them from family parameters, without changing the family or any file. " +
        "Same arguments as revit_edit_model_family_materials: " + ModelFamilyMaterialTools.ArgumentsDescription +
        " Returns ready, problems and what would be created, assigned, associated and defaulted (ids are those of the discarded preview).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilyMaterialExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_edit_model_family_materials: creates and edits materials, assigns or associates them, sets per-type defaults.</summary>
internal sealed class EditModelFamilyMaterialsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_model_family_materials";

    public string Description =>
        "Creates and edits the materials of a model family, assigns them to forms and drives form materials from material family parameters. Requires approval. " +
        ModelFamilyMaterialTools.ArgumentsDescription +
        " Returns materials, assigned, associated, createdParameters, defaults and the final state of every touched element " +
        "(materialSource direct|parameter|byCategory, material, parameter). Run revit_preview_edit_model_family_materials first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilyMaterialExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => ModelFamilyMaterialExecutor.Summary(request);
}

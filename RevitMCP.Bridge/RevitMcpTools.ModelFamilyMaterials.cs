using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Model-family materials (#173, docs/model-family-materials.md): create materials and drive form materials by family parameters.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyMaterialsListDescription =
        "Materials to create or edit, by exact name: [{name, color?:[r,g,b] 0-255, transparency?:0-100, shininess?:0-128, smoothness?:0-100, materialClass?, " +
        "duplicateFrom?:<material of the family>, copyFromProject?:true}]. An existing name is edited.";

    private const string ModelFamilyMaterialsAssignDescription =
        "Direct assignments: [{elements:[ids], material:<name|element id|null = By Category>, replaceAssociation?}]. Refused for a form whose material a family parameter drives unless replaceAssociation.";

    private const string ModelFamilyMaterialsAssociateDescription =
        "Drive the Material property from a material family parameter: [{elements:[ids], parameter:<name|null = dissociate>, create?:{instance?:false, group?:\"Materials and Finishes\"}}]. create adds the parameter when missing.";

    private const string ModelFamilyMaterialsDefaultsDescription =
        "Per-type values of a material family parameter: [{parameter, material:<name|element id|null>, type?:<type name, default every type>}].";

    [McpServerTool(Name = "revit_preview_edit_model_family_materials", ReadOnly = true),
     Description("Previews creating and editing the materials of a model family, assigning them to forms and driving them from family parameters, WITHOUT changing the family or any file. Same arguments as revit_edit_model_family_materials. Returns ready, problems (missing or ambiguous materials with candidates) and what would change. Run lists in order: materials, assign, associate, defaults.")]
    public Task<string> PreviewEditModelFamilyMaterials(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyMaterialsListDescription)] object[]? materials = null,
        [Description(ModelFamilyMaterialsAssignDescription)] object[]? assign = null,
        [Description(ModelFamilyMaterialsAssociateDescription)] object[]? associate = null,
        [Description(ModelFamilyMaterialsDefaultsDescription)] object[]? defaults = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyMaterials("revit_preview_edit_model_family_materials", target, materials, assign, associate, defaults, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_model_family_materials"),
     Description("Creates and edits the materials of a model family, sets a form's Material directly or associates it with a material family parameter, and sets that parameter's default per family type. Requires approval; the target decides what happens to the result (see target). Lists run in this order, so a later one can name what an earlier one made: materials, assign, associate, defaults. Returns material ids, before/after values and the final state of every touched element (materialSource direct|parameter|byCategory). Run revit_preview_edit_model_family_materials first.")]
    public Task<string> EditModelFamilyMaterials(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyMaterialsListDescription)] object[]? materials = null,
        [Description(ModelFamilyMaterialsAssignDescription)] object[]? assign = null,
        [Description(ModelFamilyMaterialsAssociateDescription)] object[]? associate = null,
        [Description(ModelFamilyMaterialsDefaultsDescription)] object[]? defaults = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyMaterials("revit_edit_model_family_materials", target, materials, assign, associate, defaults, document, cancellationToken);

    private async Task<string> SendEditModelFamilyMaterials(string tool, object? target, object[]? materials, object[]? assign,
        object[]? associate, object[]? defaults, string? document, CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["materials"] = ToJToken(materials);
        args["assign"] = ToJToken(assign);
        args["associate"] = ToJToken(associate);
        args["defaults"] = ToJToken(defaults);
        return FormatResult(await pipeClient.SendAsync(tool, args, cancellationToken));
    }
}

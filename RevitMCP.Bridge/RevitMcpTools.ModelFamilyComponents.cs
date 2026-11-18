using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Nested components, symbolic graphics and visibility of a model family (#175, docs/model-family-components.md).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyComponentsSummary =
        "Edits what a loadable 3D family contains besides its forms: nested families, symbolic and model lines, subcategories, visibility. " +
        "The lists run in the order of the parameters; a later list may name what an earlier one created as {\"created\":\"instances[0]\"}. " +
        ModelFamilyUnitsNote + " Run the preview first.";

    private const string ModelFamilyComponentsRemoveDescription =
        "Element ids of nested instances, symbolic lines or model lines to delete (runs first). Other kinds are refused with the tool that owns them.";

    private const string ModelFamilyComponentsSubcategoriesDescription =
        "[{name, lineWeightProjection?: 1-16, lineWeightCut?: 1-16, color?: [r,g,b], linePattern?: \"Solid\" or a pattern name, material?: name}]. Creates or edits subcategories of the family's category.";

    private const string ModelFamilyComponentsNestedFamiliesDescription =
        "[{path: \"C:\\\\X.rfa\" | fromProject: \"<family loaded in the project>\", ifExists?: skip|overwrite|overwriteParameters (default skip)}]. Loads families into the family.";

    private const string ModelFamilyComponentsInstancesDescription =
        "[{family, type?, position?: [x,y,z], rotation?: deg, view? (annotation/detail/symbol families; default the plan) | workPlane? (reference plane, work-plane families), " +
        "parameters?: {name: value}, associate?: {nested parameter: family parameter}, typeParameter?: family-type parameter (created if missing; typeParameterInstance?: bool), " +
        "visibleParameter?, visibility?, lockTo?: [{reference: reference plane, instanceReference: centerLeftRight|left|right|centerFrontBack|front|back|centerElevation|bottom|top}], name?}]. Places nested instances.";

    private const string ModelFamilyComponentsSymbolicLinesDescription =
        "[{view?: plan|front|back|left|right|<view name> (default plan), curves: [{line:{start,end}} | {arc:{start,mid,end}} | {circle:{center,radius}}], subcategory?, visibility?, visibleParameter?}]. " +
        "2D lines on the view's plane, visible only in views parallel to it. Points are projected onto the plane.";

    private const string ModelFamilyComponentsModelLinesDescription =
        "[{workPlane?: reference plane name/id | \"level\" | {origin, normal} (default level), curves: as symbolicLines, subcategory?, visibility?, visibleParameter?}]. 3D model lines.";

    private const string ModelFamilyComponentsSetSubcategoryDescription =
        "[{elements: [ids or {\"created\":\"instances[0]\"}], subcategory: name | null}]. Puts forms, symbolic lines and model lines on a subcategory (null: the family category).";

    private const string ModelFamilyComponentsVisibilityDescription =
        "[{elements: [ids or created refs], visible?: bool, visibleParameter?: yes/no family parameter | null, detailLevels?: {coarse?,medium?,fine?}, views?: {planRcp?,frontBack?,leftRight?,whenCut?}}]. " +
        "A nested instance takes only visible and visibleParameter.";

    [McpServerTool(Name = "revit_preview_edit_model_family_components", ReadOnly = true),
     Description("Previews revit_edit_model_family_components: runs the same edit and rolls it back, so it reports what Revit would do and refuse (ready, problems, result). " + ModelFamilyComponentsSummary)]
    public Task<string> PreviewEditModelFamilyComponents(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyComponentsRemoveDescription)] object? remove = null,
        [Description(ModelFamilyComponentsSubcategoriesDescription)] object[]? subcategories = null,
        [Description(ModelFamilyComponentsNestedFamiliesDescription)] object[]? nestedFamilies = null,
        [Description(ModelFamilyComponentsInstancesDescription)] object[]? instances = null,
        [Description(ModelFamilyComponentsSymbolicLinesDescription)] object[]? symbolicLines = null,
        [Description(ModelFamilyComponentsModelLinesDescription)] object[]? modelLines = null,
        [Description(ModelFamilyComponentsSetSubcategoryDescription)] object[]? setSubcategory = null,
        [Description(ModelFamilyComponentsVisibilityDescription)] object[]? visibility = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyComponents("revit_preview_edit_model_family_components", target, remove, subcategories, nestedFamilies, instances,
            symbolicLines, modelLines, setSubcategory, visibility, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_model_family_components"),
     Description("Edits the nested components, symbolic graphics and visibility of a loadable 3D family. Requires approval. A failed edit changes nothing. " + ModelFamilyComponentsSummary)]
    public Task<string> EditModelFamilyComponents(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyComponentsRemoveDescription)] object? remove = null,
        [Description(ModelFamilyComponentsSubcategoriesDescription)] object[]? subcategories = null,
        [Description(ModelFamilyComponentsNestedFamiliesDescription)] object[]? nestedFamilies = null,
        [Description(ModelFamilyComponentsInstancesDescription)] object[]? instances = null,
        [Description(ModelFamilyComponentsSymbolicLinesDescription)] object[]? symbolicLines = null,
        [Description(ModelFamilyComponentsModelLinesDescription)] object[]? modelLines = null,
        [Description(ModelFamilyComponentsSetSubcategoryDescription)] object[]? setSubcategory = null,
        [Description(ModelFamilyComponentsVisibilityDescription)] object[]? visibility = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyComponents("revit_edit_model_family_components", target, remove, subcategories, nestedFamilies, instances,
            symbolicLines, modelLines, setSubcategory, visibility, document, cancellationToken);

    private async Task<string> SendEditModelFamilyComponents(string toolName, object? target, object? remove, object[]? subcategories,
        object[]? nestedFamilies, object[]? instances, object[]? symbolicLines, object[]? modelLines, object[]? setSubcategory,
        object[]? visibility, string? document, CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["remove"] = ToJToken(remove);
        args["subcategories"] = ToJToken(subcategories);
        args["nestedFamilies"] = ToJToken(nestedFamilies);
        args["instances"] = ToJToken(instances);
        args["symbolicLines"] = ToJToken(symbolicLines);
        args["modelLines"] = ToJToken(modelLines);
        args["setSubcategory"] = ToJToken(setSubcategory);
        args["visibility"] = ToJToken(visibility);
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

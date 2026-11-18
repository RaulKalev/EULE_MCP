using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Model-family parameters, types, formulas and associations (docs/model-family-parameters.md, #172).</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyParametersDescription =
        "parameters: [{name, dataType (text|integer|number|length|angle|yesNo|material|…), group?, instance? (default false), shared? true|{guid}, guid?, " +
        "formula? (null clears), reporting?, tooltip?, rename?, value? (current type)}]: creates the parameter or edits the one of that name. " +
        "A shared parameter is read from the shared parameter file by guid, else name, and keeps its GUID; a family parameter given shared=true is replaced by it when the data types match. " +
        "The data type of an existing parameter cannot change.";

    private const string ModelFamilyParameterTypesDescription =
        "types: [{name, rename?, delete?, values? {\"<parameter>\": value}, makeCurrent?}]: creates a missing type and sets values on each. Lengths mm, angles degrees, yes/no true/false, " +
        "material by name (it must exist in the family). A parameter driven by a formula takes no value.";

    private const string ModelFamilyParameterAssociationsDescription =
        "associations: [{element: id | elements: [ids], parameter (the element parameter's name or BuiltInParameter name, e.g. MATERIAL_ID_PARAM, IS_VISIBLE_PARAM), " +
        "familyParameter: name | null (dissociate)}]. Read back; the data types must match.";

    [McpServerTool(Name = "revit_preview_edit_model_family_parameters", ReadOnly = true),
     Description("Previews revit_edit_model_family_parameters (same arguments): runs it in a rolled-back transaction group. Returns ready, problems (formula cycles, data types, shared GUID conflicts, instance/type rules), impact (what a removal or change affects) and what would be created, changed and set. Changes nothing.")]
    public Task<string> PreviewEditModelFamilyParameters(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description("Family parameter names to remove. Refused while a formula of a remaining parameter uses one; labels and associations are released with a warning.")] string[]? removeParameters = null,
        [Description(ModelFamilyParametersDescription)] object[]? parameters = null,
        [Description(ModelFamilyParameterTypesDescription)] object[]? types = null,
        [Description("[{parameter, formula: string | null}] formulas, applied after the types exist.")] object[]? formulas = null,
        [Description(ModelFamilyParameterAssociationsDescription)] object[]? associations = null,
        [Description("Shared parameter file for shared parameters. Default: the config's, else Revit's setting. Only read.")] string? sharedParameterFile = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyParameters("revit_preview_edit_model_family_parameters", target, removeParameters, parameters, types, formulas,
            associations, sharedParameterFile, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_model_family_parameters"),
     Description("Edits a model family's parameters, types, formulas and element parameter associations in one call. Requires approval. Order: dissociations, removeParameters, parameters, types, formulas, associations. Create and edit type, instance, shared and reporting parameters; set values per type; drive a dimension, visibility or material from a parameter. Everything is checked first; a refusal by Revit rolls the whole edit back. Run revit_preview_edit_model_family_parameters first. " + ModelFamilyUnitsNote)]
    public Task<string> EditModelFamilyParameters(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description("Family parameter names to remove. Refused while a formula of a remaining parameter uses one; labels and associations are released with a warning.")] string[]? removeParameters = null,
        [Description(ModelFamilyParametersDescription)] object[]? parameters = null,
        [Description(ModelFamilyParameterTypesDescription)] object[]? types = null,
        [Description("[{parameter, formula: string | null}] formulas, applied after the types exist.")] object[]? formulas = null,
        [Description(ModelFamilyParameterAssociationsDescription)] object[]? associations = null,
        [Description("Shared parameter file for shared parameters. Default: the config's, else Revit's setting. Only read.")] string? sharedParameterFile = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditModelFamilyParameters("revit_edit_model_family_parameters", target, removeParameters, parameters, types, formulas,
            associations, sharedParameterFile, document, cancellationToken);

    private async Task<string> SendEditModelFamilyParameters(string toolName, object? target, string[]? removeParameters, object[]? parameters,
        object[]? types, object[]? formulas, object[]? associations, string? sharedParameterFile, string? document, CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["removeParameters"] = ToJToken(removeParameters);
        args["parameters"] = ToJToken(parameters);
        args["types"] = ToJToken(types);
        args["formulas"] = ToJToken(formulas);
        args["associations"] = ToJToken(associations);
        args["sharedParameterFile"] = sharedParameterFile ?? string.Empty;
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

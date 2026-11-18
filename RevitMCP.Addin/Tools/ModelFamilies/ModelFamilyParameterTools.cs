using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.ModelFamilies;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>Family parameters, types, formulas and element parameter associations (#172). Registers its tools and the revit_get_model_family sections it owns.</summary>
internal static class ModelFamilyParameterTools
{
    internal const string DescriptionText =
        "Edits the parameters, types, formulas and element parameter associations of a family in one call. Order: associations with " +
        "familyParameter null (dissociate), removeParameters [names] (refused while a formula of a remaining parameter uses it; labels and associations are " +
        "released with a warning), parameters [{name, dataType, group?, instance?, shared? true|{guid}, guid?, formula? (null clears), reporting?, tooltip?, rename?, value?}] " +
        "(create or edit; shared ones are read from the shared parameter file by guid, else name, and keep their GUID; a family parameter becomes shared with " +
        "ReplaceParameter when the data types match), types [{name, rename?, delete?, values? {parameter: value}, makeCurrent?}] (lengths mm, angles degrees, yes/no true/false, " +
        "material by name), formulas [{parameter, formula}], associations [{element|elements, parameter (name or BuiltInParameter), familyParameter}]. " +
        "Formula cycles, data types, instance/type rules and shared GUID conflicts are reported before anything changes; impact lists what a change or removal affects.";

    public static void Register(ExternalEventHandler handler)
    {
        handler.RegisterTool(new PreviewEditModelFamilyParametersTool());
        handler.RegisterTool(new EditModelFamilyParametersTool());

        ModelFamilyInspect.AddSection("parameters",
            "Every family parameter: data type, group, instance, shared+guid, builtIn, reporting, formula, dependsOn/usedBy, dimension labels and the element parameters it drives.",
            ModelFamilyParameterRead.ParametersSection);
        ModelFamilyInspect.AddSection("types",
            "The family types with the value of every parameter (mm, degrees, names) and the current type.",
            ModelFamilyParameterRead.TypesSection);
        ModelFamilyInspect.AddSection("associableParameters",
            "For the elementIds argument: which parameters of each element a family parameter can drive, and what drives them now.",
            ModelFamilyParameterRead.AssociableSection);
    }

    internal static string Summary(McpToolRequest request)
    {
        var spec = ModelFamilyParameterSpecParser.Parse(request.Arguments, new List<string>());
        var parts = new List<string>();
        if (spec.Associations.Any(a => a.Dissociate)) parts.Add($"{spec.Associations.Count(a => a.Dissociate)} dissociation(s)");
        if (spec.RemoveParameters.Count > 0) parts.Add($"remove {spec.RemoveParameters.Count} parameter(s)");
        if (spec.Parameters.Count > 0) parts.Add($"{spec.Parameters.Count} parameter(s)");
        if (spec.Types.Count > 0) parts.Add($"{spec.Types.Count} type(s)");
        if (spec.Formulas.Count > 0) parts.Add($"{spec.Formulas.Count} formula(s)");
        if (spec.Associations.Any(a => !a.Dissociate)) parts.Add($"{spec.Associations.Count(a => !a.Dissociate)} association(s)");
        return $"Edit the family parameters of {ModelFamilySession.DescribeTarget(request)}: {(parts.Count > 0 ? string.Join(", ", parts) : "no operations")}.";
    }
}

/// <summary>revit_preview_edit_model_family_parameters: checks a parameter edit of a model family; changes nothing.</summary>
public sealed class PreviewEditModelFamilyParametersTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_model_family_parameters";

    public string Description => "Previews revit_edit_model_family_parameters (same arguments): runs the edit in a rolled-back transaction group and returns ready, problems, impact and what would be created, changed and set. Changes nothing. " + ModelFamilyParameterTools.DescriptionText;

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: false, "edit parameters", ModelFamilyParameterExecutor.Run, cancellationToken));
}

/// <summary>revit_edit_model_family_parameters: creates and edits parameters, types, formulas and associations of a model family.</summary>
public sealed class EditModelFamilyParametersTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_model_family_parameters";

    public string Description => "Edits a model family's parameters. Requires approval. " + ModelFamilyParameterTools.DescriptionText;

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ModelFamilySession.Edit(uiapp, request, apply: true, "edit parameters", ModelFamilyParameterExecutor.Run, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => ModelFamilyParameterTools.Summary(request);
}

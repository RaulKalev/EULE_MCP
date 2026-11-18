using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Model-family documents (#169, docs/model-family-documents.md): read, create, and change category and settings, save as, reload.</summary>
internal sealed partial class RevitMcpTools
{
    private const string ModelFamilyCreateTargetDescription =
        "The new family: {savePath (folder or .rfa; required), familyName?, templatePath? (.rft; default Metric Generic Model.rft, not hosted), " +
        "overwrite? (replace an existing file), load? (load into the project), overwriteParameterValues?}.";

    private const string ModelFamilyCategoryDescription =
        "Family category to set: built-in name (OST_ElectricalFixtures, OST_CommunicationDevices, OST_DataDevices, OST_FireAlarmDevices, " +
        "OST_SecurityDevices, OST_NurseCallDevices, OST_TelephoneDevices, OST_LightingDevices, OST_GenericModel…) or category name. Set first.";

    private const string ModelFamilySettingsDescription =
        "Family settings, applied after the category and read back: {workPlaneBased?, alwaysVertical?, shared?, cutsWithVoids?, " +
        "roomCalculationPoint? (true/false), partType? (e.g. \"Normal\", \"Junction Box\")}. A setting the category does not have is a problem.";

    [McpServerTool(Name = "revit_get_model_family", ReadOnly = true),
     Description("Reads a loadable model family without changing it. Returns sections (names and summaries) and each included section with element ids for later calls: family (category with OST_ name, placement, isHosted, settings, types, path), views, levels, elements (counts by class and category), plus the reference planes, forms, parameters, types, materials, connectors and nested components sections of the other model-family tools.")]
    public async Task<string> GetModelFamily(
        [Description(ModelFamilyReadTargetDescription)] object? target = null,
        [Description("Sections to return (default all); names are listed in sections.")] string[]? include = null,
        [Description("Most items per list (default 200, 1-2000).")] int limit = 200,
        [Description("Element ids that sections describe in detail (the elements section returns a row for each).")] long[]? elementIds = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = ModelFamilyArgs(target, document);
        args["include"] = ToJToken(include);
        args["limit"] = limit;
        args["elementIds"] = ToJToken(elementIds);
        return FormatResult(await pipeClient.SendAsync("revit_get_model_family", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_model_family", ReadOnly = true),
     Description("Previews revit_create_model_family: builds the family from the template in memory, sets category and settings, reads them back and discards it. Returns ready, problems, placement and placeableWithoutHost. Nothing is saved.")]
    public Task<string> PreviewCreateModelFamily(
        [Description(ModelFamilyCreateTargetDescription)] object? target = null,
        [Description(ModelFamilyCategoryDescription)] string? category = null,
        [Description(ModelFamilySettingsDescription)] object? settings = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendModelFamilyDocument("revit_preview_create_model_family", target, category, settings, document, cancellationToken);

    [McpServerTool(Name = "revit_create_model_family"),
     Description("Creates a loadable model family from a template (default Metric Generic Model.rft, not hosted). Requires approval. Sets category first, then settings, reads each back; saves to target.savePath and, with load=true, loads it into the project. Returns before/after, placement, isHosted, placeableWithoutHost and view and level ids. Run revit_preview_create_model_family first.")]
    public Task<string> CreateModelFamily(
        [Description(ModelFamilyCreateTargetDescription)] object? target = null,
        [Description(ModelFamilyCategoryDescription)] string? category = null,
        [Description(ModelFamilySettingsDescription)] object? settings = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendModelFamilyDocument("revit_create_model_family", target, category, settings, document, cancellationToken);

    [McpServerTool(Name = "revit_preview_edit_model_family", ReadOnly = true),
     Description("Previews revit_edit_model_family: runs the category and settings change and rolls it back. Returns ready, problems, before/after, placement after the edit, savePath and willReplaceFile. Changes nothing.")]
    public Task<string> PreviewEditModelFamily(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyCategoryDescription)] string? category = null,
        [Description(ModelFamilySettingsDescription)] object? settings = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendModelFamilyDocument("revit_preview_edit_model_family", target, category, settings, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_model_family"),
     Description("Changes a model family's category and settings, category first, each read back; nothing else in the family is touched (unchangedCounts shows it). Requires approval. With neither it is the save as / reload tool: target path+savePath saves a copy, family reloads it into the project, load=true loads it. A failed edit changes nothing. Run revit_preview_edit_model_family first.")]
    public Task<string> EditModelFamily(
        [Description(ModelFamilyTargetDescription)] object? target = null,
        [Description(ModelFamilyCategoryDescription)] string? category = null,
        [Description(ModelFamilySettingsDescription)] object? settings = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendModelFamilyDocument("revit_edit_model_family", target, category, settings, document, cancellationToken);

    private async Task<string> SendModelFamilyDocument(string toolName, object? target, string? category, object? settings,
        string? document, CancellationToken cancellationToken)
    {
        var args = ModelFamilyArgs(target, document);
        args["category"] = category ?? string.Empty;
        args["settings"] = ToJToken(settings);
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

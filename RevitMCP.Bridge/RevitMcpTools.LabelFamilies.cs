using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Label families (docs/label-families.md): list them, and make variants of a seed label family.</summary>
internal sealed partial class RevitMcpTools
{
    private const string LabelFamilyTypesDescription =
        "The variant's types: [{name, textSize? (paper mm), font?, bold?, italic?, underline?, widthFactor?, color? (\"#RRGGBB\")}]. " +
        "Seed types not listed are deleted; omit to keep the seed's types. The text fields set the label's text type, which is the same in every type.";

    [McpServerTool(Name = "revit_list_label_families", ReadOnly = true),
     Description("Lists the Generic Annotation families loaded in the document that can serve as label families (labelFamily of revit_create_detail_family): name, id, types. The first `limit` of them, or those named in families, are opened read-only for their editable user-defined parameters (labelParameter candidates: name, dataType, instance, shared), hasLabel and the labels' text types. path reads one .rfa instead.")]
    public async Task<string> ListLabelFamilies(
        [Description("Only these families (names).")] string[]? families = null,
        [Description("How many families to open for their parameters (default 5, max 25). Opening is slow.")] int limit = 5,
        [Description("false: list names and types only, open nothing. Default true.")] bool inspect = true,
        [Description("A .rfa file to read instead of the loaded families.")] string? path = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["families"] = families ?? [],
            ["limit"] = limit,
            ["inspect"] = inspect,
            ["path"] = path ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_label_families", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_label_family", ReadOnly = true),
     Description("Previews revit_create_label_family without changing the model or any file. Same arguments. Opens the seed read-only; returns ready, problems, the seed's types and parameters, the type plan (keep | rename | create | delete), the parameter changes, the label text types the appearance goes to, the save path and the load status.")]
    public Task<string> PreviewCreateLabelFamily(
        [Description("The seed label family: a Generic Annotation family loaded in the document (name) or a .rfa path. Never changed.")] string? seed = null,
        [Description("Where to save: a folder (with familyName) or the full .rfa path. Required: ask the user.")] string? savePath = null,
        [Description("The variant's name (the file name); must differ from the seed's.")] string? familyName = null,
        [Description(LabelFamilyTypesDescription)] object[]? types = null,
        [Description("The seed parameter the label shows, which the changes apply to. Default: renameParameter.from, else the only editable one.")] string? parameter = null,
        [Description("{from?, to}: rename the label's parameter.")] object? renameParameter = null,
        [Description("true: make the label's parameter an instance parameter; false: a type parameter.")] bool? makeInstance = null,
        [Description("The parameter's value in every type (the text the label shows by default).")] string? sampleValue = null,
        [Description("Replace an existing .rfa at savePath (default false).")] bool overwrite = false,
        [Description("Load the saved family into the document.")] bool loadIntoProject = false,
        [Description("A family of the same name is already loaded: skip (default) | overwrite.")] string ifExists = "skip",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLabelFamily("revit_preview_create_label_family", seed, savePath, familyName, types, parameter, renameParameter, makeInstance,
            sampleValue, overwrite, loadIntoProject, ifExists, document, cancellationToken);

    [McpServerTool(Name = "revit_create_label_family"),
     Description("Makes a variant of a label family (a Generic Annotation family whose label shows one of its parameters) and saves it as a new .rfa: other types, label text size/font/style/colour, a renamed parameter, instance or type, a sample value. Requires approval. The seed is never changed: a copy is edited and saved. savePath is required: ask the user. loadIntoProject loads it (ifExists: skip | overwrite). Run revit_preview_create_label_family first; revit_list_label_families shows seeds and their parameters.")]
    public Task<string> CreateLabelFamily(
        [Description("The seed label family: a Generic Annotation family loaded in the document (name) or a .rfa path. Never changed.")] string? seed = null,
        [Description("Where to save: a folder (with familyName) or the full .rfa path. Required: ask the user.")] string? savePath = null,
        [Description("The variant's name (the file name); must differ from the seed's.")] string? familyName = null,
        [Description(LabelFamilyTypesDescription)] object[]? types = null,
        [Description("The seed parameter the label shows, which the changes apply to. Default: renameParameter.from, else the only editable one.")] string? parameter = null,
        [Description("{from?, to}: rename the label's parameter.")] object? renameParameter = null,
        [Description("true: make the label's parameter an instance parameter; false: a type parameter.")] bool? makeInstance = null,
        [Description("The parameter's value in every type (the text the label shows by default).")] string? sampleValue = null,
        [Description("Replace an existing .rfa at savePath (default false).")] bool overwrite = false,
        [Description("Load the saved family into the document.")] bool loadIntoProject = false,
        [Description("A family of the same name is already loaded: skip (default) | overwrite.")] string ifExists = "skip",
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLabelFamily("revit_create_label_family", seed, savePath, familyName, types, parameter, renameParameter, makeInstance,
            sampleValue, overwrite, loadIntoProject, ifExists, document, cancellationToken);

    private async Task<string> SendLabelFamily(string toolName, string? seed, string? savePath, string? familyName, object[]? types,
        string? parameter, object? renameParameter, bool? makeInstance, string? sampleValue, bool overwrite, bool loadIntoProject,
        string ifExists, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["seed"] = seed ?? string.Empty,
            ["savePath"] = savePath ?? string.Empty,
            ["familyName"] = familyName ?? string.Empty,
            ["types"] = ToJToken(types),
            ["parameter"] = parameter ?? string.Empty,
            ["renameParameter"] = ToJToken(renameParameter),
            ["makeInstance"] = makeInstance,
            ["sampleValue"] = sampleValue,
            ["overwrite"] = overwrite,
            ["loadIntoProject"] = loadIntoProject,
            ["ifExists"] = ifExists,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

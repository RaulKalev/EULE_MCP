using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>Families and parameters (#102, docs/families-and-parameters.md): list/load/reload families, purge, project and shared parameters.</summary>
internal sealed partial class RevitMcpTools
{
    // ── Families ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_list_families", ReadOnly = true),
     Description("Lists the document's families: name, category, kind (loadable | system | inPlace), type count, instance count, whether editable, and the file revit_load_family last loaded it from when known. Filters: category, nameContains, kind, unusedOnly. includeTypes adds each family's types with ids and instance counts. Paged (pageSize, page).")]
    public async Task<string> ListFamilies(
        [Description("Category name, e.g. 'Electrical Fixtures'.")] string? category = null,
        [Description("Family name filter (partial, case-insensitive).")] string? nameContains = null,
        [Description("all (default) | loadable | system | inPlace.")] string kind = "all",
        [Description("Only families without a placed instance.")] bool unusedOnly = false,
        [Description("Add each family's types (typeId, name, instanceCount).")] bool includeTypes = false,
        [Description("Families per page (default and maximum from the query limits).")] int pageSize = 0,
        [Description("1-based page number.")] int page = 1,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["category"] = category ?? string.Empty,
            ["nameContains"] = nameContains ?? string.Empty,
            ["kind"] = kind,
            ["unusedOnly"] = unusedOnly,
            ["includeTypes"] = includeTypes,
            ["pageSize"] = pageSize,
            ["page"] = page,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_families", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_load_family", ReadOnly = true),
     Description("Previews loading .rfa family files WITHOUT changes. Same arguments as revit_load_family. Checks each file cheaply (exists, .rfa, file access policy, size, Revit file signature, Revit backups are skipped) and whether a family of the same name (the file name) is already loaded, and states per file willLoad | willSkip | willOverwrite under the ifExists policy. Revit does not open the files.")]
    public Task<string> PreviewLoadFamily(
        [Description("Family files (.rfa), up to 50.")] string[]? paths = null,
        [Description("Folder to load from instead of (or besides) paths.")] string? folder = null,
        [Description("File pattern in folder, default *.rfa.")] string? pattern = null,
        [Description("Include subfolders of folder.")] bool recursive = false,
        [Description("A family of the same name is already loaded: skip (default) | overwrite (keep the project's parameter values) | overwriteParameters (the file's values) | overwriteAll (also shared nested families from the file).")] string ifExists = "skip",
        [Description("Activate every loaded type so placement works at once (default true).")] bool activateTypes = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLoadFamily("revit_preview_load_family", paths, folder, pattern, recursive, ifExists, activateTypes, document, cancellationToken);

    [McpServerTool(Name = "revit_load_family"),
     Description("Loads .rfa family files into the document. Requires approval. paths (up to 50) or folder + pattern (default *.rfa, recursive optional). ifExists for a family of the same name: skip (default) | overwrite (the file's version, the project's parameter values kept) | overwriteParameters (the file's version and values) | overwriteAll (also shared nested families from the file). activateTypes (default true). Each file loads in its own transaction, so a bad file never undoes the good ones; one undo step. Returns per file {path, family, status: loaded | skipped | overwritten | failed, types, reason}. Run revit_preview_load_family first.")]
    public Task<string> LoadFamily(
        [Description("Family files (.rfa), up to 50.")] string[]? paths = null,
        [Description("Folder to load from instead of (or besides) paths.")] string? folder = null,
        [Description("File pattern in folder, default *.rfa.")] string? pattern = null,
        [Description("Include subfolders of folder.")] bool recursive = false,
        [Description("A family of the same name is already loaded: skip (default) | overwrite | overwriteParameters | overwriteAll.")] string ifExists = "skip",
        [Description("Activate every loaded type so placement works at once (default true).")] bool activateTypes = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendLoadFamily("revit_load_family", paths, folder, pattern, recursive, ifExists, activateTypes, document, cancellationToken);

    private async Task<string> SendLoadFamily(string toolName, string[]? paths, string? folder, string? pattern, bool recursive,
        string ifExists, bool activateTypes, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["paths"] = paths ?? [],
            ["folder"] = folder ?? string.Empty,
            ["pattern"] = pattern ?? string.Empty,
            ["recursive"] = recursive,
            ["ifExists"] = ifExists,
            ["activateTypes"] = activateTypes,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    [McpServerTool(Name = "revit_reload_family"),
     Description("Reloads a loaded family from its .rfa file. Requires approval. family (name or element id); path optional — default is the file revit_load_family last loaded it from (Revit does not keep the path; without a known path the tool asks for one). The file name must match the family name. ifExists: overwrite (default, the project's parameter values kept) | overwriteParameters | overwriteAll. Returns the types added and removed and the instance count.")]
    public async Task<string> ReloadFamily(
        [Description("Family name or element id (revit_list_families).")] string family,
        [Description("The .rfa file; default: the path revit_load_family recorded.")] string? path = null,
        [Description("overwrite (default) | overwriteParameters | overwriteAll.")] string ifExists = "overwrite",
        [Description("Activate every type after the reload (default true).")] bool activateTypes = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["family"] = family ?? string.Empty,
            ["path"] = path ?? string.Empty,
            ["ifExists"] = ifExists,
            ["activateTypes"] = activateTypes,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_reload_family", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_purge_unused", ReadOnly = true),
     Description("Previews purging unused family types and families WITHOUT changes. Lists types with no placed instance that Revit's own purge also reports unused, families whose every type is unused, other unused elements with includeOther=true (line/fill patterns, materials, text and dimension types, system types), and what is protected and why (in-place families, company device map types, title blocks on sheets, view templates, types Revit still uses, the last type of a kept family). passes > 1 run in a trial transaction that is rolled back. Returns the ids to pass to revit_purge_unused.")]
    public async Task<string> PreviewPurgeUnused(
        [Description("Limit to these categories, e.g. ['Electrical Fixtures'].")] string[]? categories = null,
        [Description("Limit to families whose name contains this.")] string? familyNameContains = null,
        [Description("Purge passes 1-3 (default 1): purging a type can orphan a nested family that only the next pass finds.")] int passes = 1,
        [Description("Also list other unused elements (patterns, materials, system types, …).")] bool includeOther = false,
        [Description("List every entry (default false: the first 25 of each list, counts per category, and every id in elementIds).")] bool listAll = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["categories"] = categories ?? [],
            ["familyNameContains"] = familyNameContains ?? string.Empty,
            ["passes"] = passes,
            ["includeOther"] = includeOther,
            ["listAll"] = listAll,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_preview_purge_unused", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_purge_unused", Destructive = true),
     Description("DESTRUCTIVE: purges unused family types and families, and with includeOther=true other unused elements. Always requires manual approval — cannot be bypassed by Direct Edit. Same filters as revit_preview_purge_unused; elementIds limits the purge to ids the preview listed. Never purges in-place families, company device map types, title blocks on sheets, view templates, types Revit still uses or the last type of a kept family. Batches of 500, one undo step. Returns deleted, removedAsDependent, totalRemoved, failed, failures[]. Run revit_preview_purge_unused first.")]
    public async Task<string> PurgeUnused(
        [Description("Limit to these categories.")] string[]? categories = null,
        [Description("Limit to families whose name contains this.")] string? familyNameContains = null,
        [Description("Purge passes 1-3 (default 1).")] int passes = 1,
        [Description("Also purge other unused elements (patterns, materials, system types, …).")] bool includeOther = false,
        [Description("Only these ids (from the preview) may be deleted; anything else is left alone.")] long[]? elementIds = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["categories"] = categories ?? [],
            ["familyNameContains"] = familyNameContains ?? string.Empty,
            ["passes"] = passes,
            ["includeOther"] = includeOther,
            ["elementIds"] = elementIds ?? [],
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_purge_unused", args, cancellationToken));
    }

    // ── Parameters ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "revit_list_project_parameters", ReadOnly = true),
     Description("Lists the project's parameter bindings (Project Parameters): name, shared or project, GUID, data type (SpecTypeId and label), parameter group, instance or type binding, bound categories, varies across groups. Filters: nameContains, category, sharedOnly.")]
    public async Task<string> ListProjectParameters(
        [Description("Parameter name filter (partial, case-insensitive).")] string? nameContains = null,
        [Description("Only parameters bound to this category.")] string? category = null,
        [Description("Only shared parameters.")] bool sharedOnly = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["nameContains"] = nameContains ?? string.Empty,
            ["category"] = category ?? string.Empty,
            ["sharedOnly"] = sharedOnly,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_project_parameters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_list_shared_parameters", ReadOnly = true),
     Description("Lists the definitions of the company shared parameter file: groups, names, GUIDs, data types, descriptions, visible/user-modifiable, and whether each is already bound in the document. The file: sharedParameterFile, else parameters.sharedParameterFile in the project/user/company config, else Revit's own setting. The file is never changed and Revit's setting is restored afterwards.")]
    public async Task<string> ListSharedParameters(
        [Description("Shared parameter file (.txt). Default: the config key, else Revit's setting.")] string? sharedParameterFile = null,
        [Description("Only this definition group.")] string? group = null,
        [Description("Definition name filter (partial, case-insensitive).")] string? nameContains = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["sharedParameterFile"] = sharedParameterFile ?? string.Empty,
            ["group"] = group ?? string.Empty,
            ["nameContains"] = nameContains ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_shared_parameters", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_parameters", ReadOnly = true),
     Description("Previews creating project parameters WITHOUT changing the model or any file. Same arguments as revit_create_parameters. Reports per parameter willCreate | willRebind | exists | missingDefinition | unknownCategory | invalidType | invalid, with the categories, data type and group that would be used, and whether the shared parameter file would change.")]
    public Task<string> PreviewCreateParameters(
        [Description("Parameters: [{name, source: shared|project, guid?, group?, dataType, parameterGroup, binding: instance|type, categories[], variesAcrossGroups?, ifExists: skip|rebind}], up to 50.")] object[] parameters,
        [Description("Shared parameter file (.txt). Default: parameters.sharedParameterFile in the config, else Revit's setting.")] string? sharedParameterFile = null,
        [Description("Create a missing shared definition in the file (default false: the file is company property).")] bool createInFileIfMissing = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendCreateParameters("revit_preview_create_parameters", parameters, sharedParameterFile, createInFileIfMissing, document, cancellationToken);

    [McpServerTool(Name = "revit_create_parameters"),
     Description("Creates project parameters and binds them to categories. Requires approval. parameters: up to 50 of {name, source: shared (a definition of the shared parameter file, by guid or by name within group) | project, dataType (text, integer, number, length, area, yesNo, material, … or a SpecTypeId string), parameterGroup (Data, Electrical, Identity Data, Text, …; default Data), binding: instance | type, categories[], variesAcrossGroups?, ifExists: skip | rebind (adds categories to an existing binding, never removes)}. The shared parameter file is never changed unless createInFileIfMissing=true. source=project: Revit has no API for a non-shared project parameter, so it becomes a shared definition in the connector's scratch file (group RevitMCP) and the result says so. One undo step. Run revit_preview_create_parameters first.")]
    public Task<string> CreateParameters(
        [Description("Parameters: [{name, source: shared|project, guid?, group?, dataType, parameterGroup, binding: instance|type, categories[], variesAcrossGroups?, ifExists: skip|rebind}], up to 50.")] object[] parameters,
        [Description("Shared parameter file (.txt). Default: parameters.sharedParameterFile in the config, else Revit's setting.")] string? sharedParameterFile = null,
        [Description("Create a missing shared definition in the file (default false: the file is company property).")] bool createInFileIfMissing = false,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendCreateParameters("revit_create_parameters", parameters, sharedParameterFile, createInFileIfMissing, document, cancellationToken);

    private async Task<string> SendCreateParameters(string toolName, object[]? parameters, string? sharedParameterFile,
        bool createInFileIfMissing, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["parameters"] = ToJToken(parameters),
            ["sharedParameterFile"] = sharedParameterFile ?? string.Empty,
            ["createInFileIfMissing"] = createInFileIfMissing,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    [McpServerTool(Name = "revit_bind_parameter_categories"),
     Description("Adds or removes categories on an existing project or shared parameter binding. Requires approval. name or guid (revit_list_project_parameters), addCategories, removeCategories. The binding kind and group stay. Refuses to remove the last category; removing a category drops the parameter's values on those elements.")]
    public async Task<string> BindParameterCategories(
        [Description("Parameter name.")] string? name = null,
        [Description("Shared parameter GUID (instead of name).")] string? guid = null,
        [Description("Categories to add.")] string[]? addCategories = null,
        [Description("Categories to remove.")] string[]? removeCategories = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["name"] = name ?? string.Empty,
            ["guid"] = guid ?? string.Empty,
            ["addCategories"] = addCategories ?? [],
            ["removeCategories"] = removeCategories ?? [],
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_bind_parameter_categories", args, cancellationToken));
    }

    // ── Family creation (#147, docs/family-creation.md) ──────────────────────

    private const string DetailFamilyParametersDescription =
        "Family parameters: [{name, dataType (text, integer, number, length, area, angle, yesNo, …), group? (Data, Text, Dimensions, Identity Data, …), instance? (default type), value? (length mm, area m², angle degrees), formula?}]. " +
        "A shared parameter: [{name or guid, shared:true, sharedGroup?, group?, instance?, value?}] - an existing definition of the shared parameter file; it is never created.";

    private const string DetailFamilyLabelsDescription =
        "Text: [{text, position:[x,y] (mm, the insertion point), size? (text height on paper, mm, default 2.5), textType? (a text type of the project to copy), alignment? (left | center | right), rotation? (degrees)}]. " +
        "Each distinct text becomes a Generic Annotation family nested in the detail item, so it keeps its paper size at every scale. " +
        "A label that shows a parameter instead: [{parameter (a name in parameters), position, labelFamily? (else the labelFamily argument), labelType?, labelParameter? (the label family's parameter its label shows; needed when several fit), rotation?}]. " +
        "visibleParameter (a yesNo parameter of parameters) switches a label on and off; lines, rectangles, arcs, circles and filledRegions take it too.";

    private const string DetailFamilyLineStylesDescription =
        "Line styles of the family itself: [{name, weight? (Revit line weight number 1-16), color? (#RRGGBB), pattern? (a line pattern of the project, or solid)}]. " +
        "A curve's or region's lineStyle may then name one of these instead of a line style of the project. Fields left out come from the project's style of the same name, else weight 1, black, solid.";

    private const string DetailFamilyFilledRegionsDescription =
        "Filled regions: [{points:[[x,y],…] | min,max (rectangle) | center,radius (circle), holes? ([[x,y],…] or {min,max} or {center,radius} each), type?, lineStyle?, visibleParameter?}] in mm. " +
        "type is a filled region type of the project (copied into the family) or a name defined in filledRegionTypes; without it the family's default type is used. lineStyle is the boundary's line style (a line style of the project).";

    private const string DetailFamilyFilledRegionTypesDescription =
        "Filled region types to create in the family (or edit, when the template has the name): [{name, copyFrom? (a filled region type of the project to start from; default: the project's type of the same name), " +
        "foregroundPattern?, backgroundPattern? (a fill pattern of the project, solid or none), foregroundColor?, backgroundColor? (#RRGGBB), lineWeight? (1-16), isMasking?}].";

    private const string DetailFamilyLabelFamilyDescription =
        "Default label family for labels that show a parameter (labels[].parameter): a Generic Annotation family that already holds a label, loaded in the project or a .rfa path. " +
        "Revit's API cannot create a label, so this family is nested and its label parameter linked to the detail family's parameter.";

    private const string DetailFamilyTypesDescription =
        "Family types: [{name, values?: {\"<parameter name>\": value}}]. Each is created, or updated when the family already has the name. A parameter in values must be in parameters (or, when editing, in the family) and have no formula; values in the units of parameters[].value. " +
        "A parameter's own value is the default for every listed type. Without types, values go on one type named after the family.";

    private const string DetailFamilyRemoveDescription =
        "Removed before anything is added: {curves?, filledRegions?, labels? (nested annotation instances), textNotes?: true (all) | [element ids], parameters?: [names]}. " +
        "A parameter used in another parameter's formula, or linked to an element that stays (visibility, label, dimension), is refused; built-in parameters are never removed.";

    private const string DetailFamilyGeometryDescription =
        "Millimetres on the family's reference level; the origin is the insertion point. Points are [x, y] or {x, y}.";

    [McpServerTool(Name = "revit_preview_create_detail_family", ReadOnly = true),
     Description("Previews creating a Detail Item family WITHOUT changing the model or any file. Same arguments as revit_create_detail_family. Returns ready, problems (to fix first), the resolved save path and whether the file exists, the template, curve counts, each line style matched to the project (project | missing, with weight, colour, pattern), each parameter resolved (shared ones against the shared parameter file), the label annotation families and whether the family would load.")]
    public Task<string> PreviewCreateDetailFamily(
        [Description("Where to save: a folder (with familyName) or the full .rfa path. Required: ask the user, there is no default.")] string? savePath = null,
        [Description("Family name (the file name). Not needed when savePath is a .rfa path.")] string? familyName = null,
        [Description("Lines: [{start:[x,y], end:[x,y], lineStyle?}]. " + DetailFamilyGeometryDescription)] object[]? lines = null,
        [Description("Rectangles: [{min:[x,y], max:[x,y], lineStyle?}] (four lines each).")] object[]? rectangles = null,
        [Description("Arcs: [{center, radius, startAngle, endAngle, lineStyle?}] (degrees, counter-clockwise) or [{start, mid, end, lineStyle?}].")] object[]? arcs = null,
        [Description("Circles: [{center:[x,y], radius, lineStyle?}].")] object[]? circles = null,
        [Description("Default line style for curves without one: the name of a line style of the project.")] string? lineStyle = null,
        [Description("Replace an existing .rfa at savePath (default false).")] bool overwrite = false,
        [Description("Remove unused elements before saving (default true).")] bool purge = true,
        [Description("Family template (.rft). Default: Metric Detail Item.rft from Revit's family template folder.")] string? templatePath = null,
        [Description("Load the saved family into the document.")] bool loadIntoProject = false,
        [Description("A family of the same name is already loaded: skip (default) | overwrite.")] string ifExists = "skip",
        [Description(DetailFamilyParametersDescription)] object[]? parameters = null,
        [Description(DetailFamilyLabelsDescription)] object[]? labels = null,
        [Description("Shared parameter file for shared parameters. Default: parameters.sharedParameterFile in the config, else Revit's setting. Only read, never written.")] string? sharedParameterFile = null,
        [Description("Generic Annotation template (.rft) for labels. Default: Metric Generic Annotation.rft from Revit's family template folder.")] string? annotationTemplatePath = null,
        [Description(DetailFamilyFilledRegionsDescription)] object[]? filledRegions = null,
        [Description(DetailFamilyFilledRegionTypesDescription)] object[]? filledRegionTypes = null,
        [Description(DetailFamilyLabelFamilyDescription)] string? labelFamily = null,
        [Description(DetailFamilyLineStylesDescription)] object[]? lineStyles = null,
        [Description(DetailFamilyTypesDescription)] object[]? types = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendDetailFamily("revit_preview_create_detail_family", savePath, familyName, lines, rectangles, arcs, circles, lineStyle,
            overwrite, purge, templatePath, loadIntoProject, ifExists, parameters, labels, sharedParameterFile, annotationTemplatePath,
            filledRegions, filledRegionTypes, labelFamily, lineStyles, types, document, cancellationToken);

    [McpServerTool(Name = "revit_create_detail_family"),
     Description("Creates a Detail Item family (.rfa) from Metric Detail Item.rft and draws it. Requires approval. savePath is required (a folder with familyName, or the full .rfa path): ask the user where the family goes, there is no default. Geometry in mm on the reference level (origin = insertion point): lines, rectangles, arcs, circles. Each lineStyle must be a line style of the project: the family gets a line style of the same name, weight, colour and pattern, which merges with the project's on load. purge (default true) removes unused elements before saving. loadIntoProject loads it (ifExists: skip | overwrite). parameters adds family parameters, or assigns existing shared parameters from the shared parameter file (never creates one). labels places text through nested Generic Annotation families (fixed text; Revit's API cannot create parameter labels). Run revit_preview_create_detail_family first.")]
    public Task<string> CreateDetailFamily(
        [Description("Where to save: a folder (with familyName) or the full .rfa path. Required: ask the user, there is no default.")] string? savePath = null,
        [Description("Family name (the file name). Not needed when savePath is a .rfa path.")] string? familyName = null,
        [Description("Lines: [{start:[x,y], end:[x,y], lineStyle?}]. " + DetailFamilyGeometryDescription)] object[]? lines = null,
        [Description("Rectangles: [{min:[x,y], max:[x,y], lineStyle?}] (four lines each).")] object[]? rectangles = null,
        [Description("Arcs: [{center, radius, startAngle, endAngle, lineStyle?}] (degrees, counter-clockwise) or [{start, mid, end, lineStyle?}].")] object[]? arcs = null,
        [Description("Circles: [{center:[x,y], radius, lineStyle?}].")] object[]? circles = null,
        [Description("Default line style for curves without one: the name of a line style of the project.")] string? lineStyle = null,
        [Description("Replace an existing .rfa at savePath (default false).")] bool overwrite = false,
        [Description("Remove unused elements before saving (default true).")] bool purge = true,
        [Description("Family template (.rft). Default: Metric Detail Item.rft from Revit's family template folder.")] string? templatePath = null,
        [Description("Load the saved family into the document.")] bool loadIntoProject = false,
        [Description("A family of the same name is already loaded: skip (default) | overwrite.")] string ifExists = "skip",
        [Description(DetailFamilyParametersDescription)] object[]? parameters = null,
        [Description(DetailFamilyLabelsDescription)] object[]? labels = null,
        [Description("Shared parameter file for shared parameters. Default: parameters.sharedParameterFile in the config, else Revit's setting. Only read, never written.")] string? sharedParameterFile = null,
        [Description("Generic Annotation template (.rft) for labels. Default: Metric Generic Annotation.rft from Revit's family template folder.")] string? annotationTemplatePath = null,
        [Description(DetailFamilyFilledRegionsDescription)] object[]? filledRegions = null,
        [Description(DetailFamilyFilledRegionTypesDescription)] object[]? filledRegionTypes = null,
        [Description(DetailFamilyLabelFamilyDescription)] string? labelFamily = null,
        [Description(DetailFamilyLineStylesDescription)] object[]? lineStyles = null,
        [Description(DetailFamilyTypesDescription)] object[]? types = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendDetailFamily("revit_create_detail_family", savePath, familyName, lines, rectangles, arcs, circles, lineStyle,
            overwrite, purge, templatePath, loadIntoProject, ifExists, parameters, labels, sharedParameterFile, annotationTemplatePath,
            filledRegions, filledRegionTypes, labelFamily, lineStyles, types, document, cancellationToken);

    private async Task<string> SendDetailFamily(string toolName, string? savePath, string? familyName, object[]? lines,
        object[]? rectangles, object[]? arcs, object[]? circles, string? lineStyle, bool overwrite, bool purge,
        string? templatePath, bool loadIntoProject, string ifExists, object[]? parameters, object[]? labels,
        string? sharedParameterFile, string? annotationTemplatePath, object[]? filledRegions, object[]? filledRegionTypes,
        string? labelFamily, object[]? lineStyles, object[]? types, string? document, CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["savePath"] = savePath ?? string.Empty,
            ["familyName"] = familyName ?? string.Empty,
            ["lines"] = ToJToken(lines),
            ["rectangles"] = ToJToken(rectangles),
            ["arcs"] = ToJToken(arcs),
            ["circles"] = ToJToken(circles),
            ["lineStyle"] = lineStyle ?? string.Empty,
            ["overwrite"] = overwrite,
            ["purge"] = purge,
            ["templatePath"] = templatePath ?? string.Empty,
            ["loadIntoProject"] = loadIntoProject,
            ["ifExists"] = ifExists,
            ["parameters"] = ToJToken(parameters),
            ["labels"] = ToJToken(labels),
            ["sharedParameterFile"] = sharedParameterFile ?? string.Empty,
            ["annotationTemplatePath"] = annotationTemplatePath ?? string.Empty,
            ["filledRegions"] = ToJToken(filledRegions),
            ["filledRegionTypes"] = ToJToken(filledRegionTypes),
            ["labelFamily"] = labelFamily ?? string.Empty,
            ["lineStyles"] = ToJToken(lineStyles),
            ["types"] = ToJToken(types),
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    // ── Editing an existing detail family ────────────────────────────────────

    private const string DetailFamilyEditTargetDescription =
        "A Detail Item family loaded in the project: its name or element id. Opened with EditFamily and loaded back afterwards. Give family or path.";

    private const string DetailFamilyEditPathDescription =
        "An .rfa file to edit instead of a loaded family (never while it is open in Revit). Saved back only with overwrite=true, else give savePath.";

    private const string DetailFamilyEditSavePathDescription =
        "Save the edited family here (a folder, or the full .rfa path). For family it saves a copy, whose file name must be the family's name.";

    [McpServerTool(Name = "revit_preview_edit_detail_family", ReadOnly = true),
     Description("Previews editing an existing Detail Item family WITHOUT changing the model or any file. Same arguments as revit_edit_detail_family. Opens the family read-only (closed without saving) and returns ready, problems, the family's types, parameters (formulas, linked elements) and removable element counts, the element ids and parameters remove would delete, what would be added (line styles, parameters, regions, labels resolved against the project) and the types with their values.")]
    public Task<string> PreviewEditDetailFamily(
        [Description(DetailFamilyEditTargetDescription)] string? family = null,
        [Description(DetailFamilyEditPathDescription)] string? path = null,
        [Description(DetailFamilyEditSavePathDescription)] string? savePath = null,
        [Description("Replace the file at path or savePath (default false).")] bool overwrite = false,
        [Description("family only: load with the family's parameter values instead of keeping the project's (default false).")] bool overwriteParameterValues = false,
        [Description("Remove unused elements before saving (default false for an edit).")] bool purge = false,
        [Description(DetailFamilyRemoveDescription)] object? remove = null,
        [Description("Lines to add: [{start:[x,y], end:[x,y], lineStyle?}]. " + DetailFamilyGeometryDescription)] object[]? lines = null,
        [Description("Rectangles to add: [{min:[x,y], max:[x,y], lineStyle?}].")] object[]? rectangles = null,
        [Description("Arcs to add: [{center, radius, startAngle, endAngle, lineStyle?}] or [{start, mid, end, lineStyle?}].")] object[]? arcs = null,
        [Description("Circles to add: [{center:[x,y], radius, lineStyle?}].")] object[]? circles = null,
        [Description("Default line style for added curves: a line style of the project.")] string? lineStyle = null,
        [Description(DetailFamilyLineStylesDescription)] object[]? lineStyles = null,
        [Description(DetailFamilyFilledRegionsDescription)] object[]? filledRegions = null,
        [Description(DetailFamilyFilledRegionTypesDescription)] object[]? filledRegionTypes = null,
        [Description(DetailFamilyParametersDescription)] object[]? parameters = null,
        [Description(DetailFamilyLabelsDescription)] object[]? labels = null,
        [Description(DetailFamilyLabelFamilyDescription)] string? labelFamily = null,
        [Description(DetailFamilyTypesDescription)] object[]? types = null,
        [Description("Shared parameter file for shared parameters. Default: the config's, else Revit's setting. Only read.")] string? sharedParameterFile = null,
        [Description("Generic Annotation template (.rft) for text labels. Default: Metric Generic Annotation.rft.")] string? annotationTemplatePath = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditDetailFamily("revit_preview_edit_detail_family", family, path, savePath, overwrite, overwriteParameterValues, purge, remove,
            lines, rectangles, arcs, circles, lineStyle, lineStyles, filledRegions, filledRegionTypes, parameters, labels, labelFamily, types,
            sharedParameterFile, annotationTemplatePath, document, cancellationToken);

    [McpServerTool(Name = "revit_edit_detail_family"),
     Description("Edits an existing Detail Item family. Requires approval. Target: family (loaded in the project; loaded back afterwards as one undo step, keeping the project's parameter values unless overwriteParameterValues) or path (an .rfa: saved back with overwrite=true, or to savePath). remove runs first, then the same additions as revit_create_detail_family (curves, line styles, filled regions and their types, parameters, labels, types), then purge (default false). The family is saved to a temporary file and copied into place, so a failed edit leaves the file and the loaded family as they were. Run revit_preview_edit_detail_family first.")]
    public Task<string> EditDetailFamily(
        [Description(DetailFamilyEditTargetDescription)] string? family = null,
        [Description(DetailFamilyEditPathDescription)] string? path = null,
        [Description(DetailFamilyEditSavePathDescription)] string? savePath = null,
        [Description("Replace the file at path or savePath (default false).")] bool overwrite = false,
        [Description("family only: load with the family's parameter values instead of keeping the project's (default false).")] bool overwriteParameterValues = false,
        [Description("Remove unused elements before saving (default false for an edit).")] bool purge = false,
        [Description(DetailFamilyRemoveDescription)] object? remove = null,
        [Description("Lines to add: [{start:[x,y], end:[x,y], lineStyle?}]. " + DetailFamilyGeometryDescription)] object[]? lines = null,
        [Description("Rectangles to add: [{min:[x,y], max:[x,y], lineStyle?}].")] object[]? rectangles = null,
        [Description("Arcs to add: [{center, radius, startAngle, endAngle, lineStyle?}] or [{start, mid, end, lineStyle?}].")] object[]? arcs = null,
        [Description("Circles to add: [{center:[x,y], radius, lineStyle?}].")] object[]? circles = null,
        [Description("Default line style for added curves: a line style of the project.")] string? lineStyle = null,
        [Description(DetailFamilyLineStylesDescription)] object[]? lineStyles = null,
        [Description(DetailFamilyFilledRegionsDescription)] object[]? filledRegions = null,
        [Description(DetailFamilyFilledRegionTypesDescription)] object[]? filledRegionTypes = null,
        [Description(DetailFamilyParametersDescription)] object[]? parameters = null,
        [Description(DetailFamilyLabelsDescription)] object[]? labels = null,
        [Description(DetailFamilyLabelFamilyDescription)] string? labelFamily = null,
        [Description(DetailFamilyTypesDescription)] object[]? types = null,
        [Description("Shared parameter file for shared parameters. Default: the config's, else Revit's setting. Only read.")] string? sharedParameterFile = null,
        [Description("Generic Annotation template (.rft) for text labels. Default: Metric Generic Annotation.rft.")] string? annotationTemplatePath = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendEditDetailFamily("revit_edit_detail_family", family, path, savePath, overwrite, overwriteParameterValues, purge, remove,
            lines, rectangles, arcs, circles, lineStyle, lineStyles, filledRegions, filledRegionTypes, parameters, labels, labelFamily, types,
            sharedParameterFile, annotationTemplatePath, document, cancellationToken);

    private async Task<string> SendEditDetailFamily(string toolName, string? family, string? path, string? savePath, bool overwrite,
        bool overwriteParameterValues, bool purge, object? remove, object[]? lines, object[]? rectangles, object[]? arcs, object[]? circles,
        string? lineStyle, object[]? lineStyles, object[]? filledRegions, object[]? filledRegionTypes, object[]? parameters, object[]? labels,
        string? labelFamily, object[]? types, string? sharedParameterFile, string? annotationTemplatePath, string? document,
        CancellationToken cancellationToken)
    {
        var args = new Dictionary<string, object?>
        {
            ["family"] = family ?? string.Empty,
            ["path"] = path ?? string.Empty,
            ["savePath"] = savePath ?? string.Empty,
            ["overwrite"] = overwrite,
            ["overwriteParameterValues"] = overwriteParameterValues,
            ["purge"] = purge,
            ["remove"] = ToJToken(remove),
            ["lines"] = ToJToken(lines),
            ["rectangles"] = ToJToken(rectangles),
            ["arcs"] = ToJToken(arcs),
            ["circles"] = ToJToken(circles),
            ["lineStyle"] = lineStyle ?? string.Empty,
            ["lineStyles"] = ToJToken(lineStyles),
            ["filledRegions"] = ToJToken(filledRegions),
            ["filledRegionTypes"] = ToJToken(filledRegionTypes),
            ["parameters"] = ToJToken(parameters),
            ["labels"] = ToJToken(labels),
            ["labelFamily"] = labelFamily ?? string.Empty,
            ["types"] = ToJToken(types),
            ["sharedParameterFile"] = sharedParameterFile ?? string.Empty,
            ["annotationTemplatePath"] = annotationTemplatePath ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }
}

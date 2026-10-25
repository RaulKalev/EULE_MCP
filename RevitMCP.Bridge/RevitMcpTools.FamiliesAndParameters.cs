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
     Description("Previews loading .rfa family files WITHOUT changes. Same arguments as revit_load_family. Checks each file cheaply (exists, .rfa, file access policy, size, Revit backups are skipped) and whether a family of the same name (the file name) is already loaded, and states per file willLoad | willSkip | willOverwrite under the ifExists policy. The files are not opened.")]
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
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["categories"] = categories ?? [],
            ["familyNameContains"] = familyNameContains ?? string.Empty,
            ["passes"] = passes,
            ["includeOther"] = includeOther,
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
}

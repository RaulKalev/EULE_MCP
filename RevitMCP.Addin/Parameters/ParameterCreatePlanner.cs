using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Parameters;

/// <summary>One parameter revit_create_parameters is asked to create (#102).</summary>
public sealed class ParameterRequest
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>shared (a definition of the shared parameter file) | project (a connector-owned scratch definition).</summary>
    public string Source { get; set; } = "shared";
    public string Guid { get; set; } = string.Empty;
    /// <summary>The definition group in the shared parameter file.</summary>
    public string Group { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public string ParameterGroup { get; set; } = string.Empty;
    /// <summary>instance | type.</summary>
    public string Binding { get; set; } = "instance";
    public List<string> Categories { get; set; } = new();
    public bool? VariesAcrossGroups { get; set; }
    /// <summary>skip | rebind.</summary>
    public string IfExists { get; set; } = "skip";

    public bool IsShared => !string.Equals(Source, "project", StringComparison.OrdinalIgnoreCase);
    public bool IsInstance => !string.Equals(Binding, "type", StringComparison.OrdinalIgnoreCase);
    public bool Rebind => string.Equals(IfExists, "rebind", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A parameter already bound in the project (from doc.ParameterBindings).</summary>
public sealed class ExistingBinding
{
    public string Name { get; set; } = string.Empty;
    public string? Guid { get; set; }
    public bool IsShared { get; set; }
    public bool IsInstance { get; set; } = true;
    public string? DataTypeId { get; set; }
    public List<string> Categories { get; set; } = new();
}

/// <summary>A definition of a shared parameter file.</summary>
public sealed class SharedDefinitionInfo
{
    public string Group { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Guid { get; set; } = string.Empty;
    public string DataTypeId { get; set; } = string.Empty;
}

/// <summary>What will happen to one requested parameter.</summary>
public sealed class ParameterPlanItem
{
    public ParameterRequest Request { get; set; } = new();
    /// <summary>willCreate | willRebind | exists | missingDefinition | unknownCategory | invalidType | invalid.</summary>
    public string Action { get; set; } = string.Empty;
    public string? Reason { get; set; }
    /// <summary>The categories to bind, by their Revit names (the union with the existing ones for willRebind).</summary>
    public List<string> Categories { get; set; } = new();
    public List<string> AddedCategories { get; set; } = new();
    public List<string> UnknownCategories { get; set; } = new();
    /// <summary>The definition to bind, when it exists already in the file.</summary>
    public SharedDefinitionInfo? Definition { get; set; }
    /// <summary>True when the definition is created in the file first (createInFileIfMissing or source=project).</summary>
    public bool CreateInFile { get; set; }
    /// <summary>The file group a created definition goes into.</summary>
    public string FileGroup { get; set; } = string.Empty;
    /// <summary>The resolved data type: a forge id the Revit side can use directly.</summary>
    public string? DataTypeId { get; set; }
    /// <summary>The GroupTypeId member name ("" = Other) or a raw group forge id.</summary>
    public string? ParameterGroupKey { get; set; }
    public ExistingBinding? Existing { get; set; }

    public bool WillWrite => Action is "willCreate" or "willRebind";
}

/// <summary>The resolvers the planner needs from the running Revit.</summary>
public sealed class ParameterPlanContext
{
    public List<ExistingBinding> Existing { get; set; } = new();
    /// <summary>Definitions of the shared parameter file (source=shared). Null when no file could be opened.</summary>
    public List<SharedDefinitionInfo>? SharedFile { get; set; }
    public string? SharedFileProblem { get; set; }
    /// <summary>Definitions of the connector's scratch file (source=project).</summary>
    public List<SharedDefinitionInfo> ScratchFile { get; set; } = new();
    public bool CreateInFileIfMissing { get; set; }
    /// <summary>Category name → (Revit's category name, null) or (null, why it cannot be bound).</summary>
    public Func<string, (string? Name, string? Error)> ResolveCategory { get; set; } = name => (name, null);
    /// <summary>dataType argument → forge id of the running Revit, or null when it is not a data type Revit knows.</summary>
    public Func<string, string?> ResolveSpec { get; set; } = _ => null;
    /// <summary>parameterGroup argument → group key ("" = Other) or forge id, or null when unknown.</summary>
    public Func<string, string?> ResolveGroup { get; set; } = ParameterSpecMap.ResolveGroupKey;
}

/// <summary>
/// Validates revit_create_parameters requests and decides, against the project's bindings and the definitions of the
/// shared parameter file, what each one does (#102). Existing bindings are extended (rebind = union of categories),
/// never replaced; the shared parameter file is only written when createInFileIfMissing=true. source=project is a
/// definition in the connector-owned group of a scratch file, because Revit 2024/2026 have no API for a non-shared
/// project parameter. No Revit API: unit tested.
/// </summary>
public static class ParameterCreatePlanner
{
    public const int MaxParametersPerCall = 50;
    public const string ScratchGroup = "RevitMCP";
    public const string DefaultParameterGroup = "Data";

    /// <summary>Characters Revit refuses in parameter names.</summary>
    private const string ForbiddenNameCharacters = "\\:{}[]|;<>?`~";

    /// <summary>Reads parameters[] from the request arguments.</summary>
    public static List<ParameterRequest> Parse(object? raw, out string? error)
    {
        error = null;
        var list = new List<ParameterRequest>();
        JArray? array = raw switch
        {
            JArray a => a,
            string s when s.TrimStart().StartsWith("[", StringComparison.Ordinal) => TryParse(s),
            null => null,
            _ => TryFrom(raw)
        };
        if (array == null)
        {
            error = "parameters is required: an array of {name, source, dataType, parameterGroup, binding, categories, …}.";
            return list;
        }
        var index = 0;
        foreach (var token in array)
        {
            if (token is not JObject item)
            {
                list.Add(new ParameterRequest { Index = index++, Name = token.Type == JTokenType.String ? (string?)token ?? string.Empty : string.Empty, Source = "invalid" });
                continue;
            }
            list.Add(new ParameterRequest
            {
                Index = index++,
                Name = Str(item, "name"),
                Source = Str(item, "source", "shared"),
                Guid = Str(item, "guid"),
                Group = Str(item, "group"),
                DataType = Str(item, "dataType"),
                ParameterGroup = Str(item, "parameterGroup"),
                Binding = Str(item, "binding", "instance"),
                Categories = item["categories"] is JArray cats
                    ? cats.Select(c => ((string?)c ?? string.Empty).Trim()).Where(c => c.Length > 0).ToList()
                    : Str(item, "categories").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
                VariesAcrossGroups = item["variesAcrossGroups"] is JValue v && v.Type == JTokenType.Boolean ? (bool)v : null,
                IfExists = Str(item, "ifExists", "skip")
            });
        }
        if (list.Count == 0) error = "parameters is empty: nothing to create.";
        else if (list.Count > MaxParametersPerCall) error = $"{list.Count} parameters were requested; at most {MaxParametersPerCall} per call.";
        return list;
    }

    public static List<ParameterPlanItem> Plan(IReadOnlyList<ParameterRequest> requests, ParameterPlanContext context)
    {
        var items = new List<ParameterPlanItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            var item = new ParameterPlanItem { Request = request };
            items.Add(item);
            PlanOne(item, context, seen);
        }
        return items;
    }

    private static void PlanOne(ParameterPlanItem item, ParameterPlanContext context, HashSet<string> seen)
    {
        var request = item.Request;
        var name = request.Name.Trim();

        // 1. Shape of the request.
        if (name.Length == 0) { Set(item, "invalid", "name is required."); return; }
        if (name.IndexOfAny(ForbiddenNameCharacters.ToCharArray()) >= 0) { Set(item, "invalid", $"'{name}' contains a character Revit refuses in parameter names ({ForbiddenNameCharacters})."); return; }
        if (!seen.Add(name)) { Set(item, "invalid", $"'{name}' is requested more than once in this call."); return; }
        if (!string.Equals(request.Source, "shared", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Source, "project", StringComparison.OrdinalIgnoreCase))
        { Set(item, "invalid", $"source '{request.Source}' is not shared or project."); return; }
        if (!string.Equals(request.Binding, "instance", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.Binding, "type", StringComparison.OrdinalIgnoreCase))
        { Set(item, "invalid", $"binding '{request.Binding}' is not instance or type."); return; }
        if (!string.Equals(request.IfExists, "skip", StringComparison.OrdinalIgnoreCase) && !string.Equals(request.IfExists, "rebind", StringComparison.OrdinalIgnoreCase))
        { Set(item, "invalid", $"ifExists '{request.IfExists}' is not skip or rebind."); return; }
        if (request.Guid.Length > 0 && !System.Guid.TryParse(request.Guid, out _)) { Set(item, "invalid", $"guid '{request.Guid}' is not a GUID."); return; }
        if (request.VariesAcrossGroups == true && !request.IsInstance) { Set(item, "invalid", "variesAcrossGroups applies to instance bindings only."); return; }

        // 2. Parameter group.
        var groupArgument = request.ParameterGroup.Length > 0 ? request.ParameterGroup : DefaultParameterGroup;
        item.ParameterGroupKey = context.ResolveGroup(groupArgument);
        if (item.ParameterGroupKey == null)
        {
            Set(item, "invalid", $"parameterGroup '{groupArgument}' is not a parameter group Revit knows (e.g. {string.Join(", ", ParameterSpecMap.GroupLabels.Take(8))}).");
            return;
        }

        // 3. Categories.
        if (request.Categories.Count == 0) { Set(item, "invalid", "categories is required: at least one category to bind to."); return; }
        foreach (var category in request.Categories)
        {
            var (resolved, error) = context.ResolveCategory(category);
            if (resolved == null) item.UnknownCategories.Add(error == null ? category : $"{category} ({error})");
            else if (!item.Categories.Contains(resolved, StringComparer.OrdinalIgnoreCase)) item.Categories.Add(resolved);
        }
        if (item.UnknownCategories.Count > 0)
        {
            Set(item, "unknownCategory", $"Unknown or not bindable categor{(item.UnknownCategories.Count == 1 ? "y" : "ies")}: {string.Join(", ", item.UnknownCategories)}.");
            return;
        }

        // 4. Requested data type.
        string? requestedSpec = null;
        if (request.DataType.Length > 0)
        {
            requestedSpec = context.ResolveSpec(request.DataType);
            if (requestedSpec == null)
            {
                var suggestions = ParameterSpecMap.SuggestSpecs(request.DataType);
                Set(item, "invalidType", $"dataType '{request.DataType}' is not a data type Revit knows. Use one of " +
                                         $"{string.Join(", ", suggestions.Count > 0 ? suggestions : ParameterSpecMap.FriendlySpecNames.Take(8))}, or a SpecTypeId string.");
                return;
            }
        }

        // 5. The definition.
        if (request.IsShared)
        {
            if (context.SharedFile == null)
            {
                Set(item, "missingDefinition", context.SharedFileProblem ?? "No shared parameter file is set: pass sharedParameterFile or set parameters.sharedParameterFile in the config.");
                return;
            }
            var found = FindDefinition(context.SharedFile, request, out var ambiguity);
            if (ambiguity != null) { Set(item, "invalid", ambiguity); return; }
            if (found == null)
            {
                if (!context.CreateInFileIfMissing)
                {
                    Set(item, "missingDefinition",
                        $"The shared parameter file has no definition {(request.Guid.Length > 0 ? $"with GUID {request.Guid}" : $"'{name}'")}" +
                        $"{(request.Group.Length > 0 ? $" in group '{request.Group}'" : string.Empty)}. The file is company property and is not changed " +
                        "unless createInFileIfMissing=true.");
                    return;
                }
                if (requestedSpec == null) { Set(item, "invalidType", "dataType is required to create a definition in the shared parameter file."); return; }
                item.CreateInFile = true;
                item.FileGroup = request.Group.Length > 0 ? request.Group : ScratchGroup;
                item.DataTypeId = requestedSpec;
            }
            else
            {
                item.Definition = found;
                item.DataTypeId = found.DataTypeId;
                if (requestedSpec != null && !ParameterSpecMap.SameType(requestedSpec, found.DataTypeId))
                {
                    Set(item, "invalidType", $"The shared parameter file defines '{found.Name}' as {found.DataTypeId}, not {request.DataType}.");
                    return;
                }
            }
        }
        else
        {
            var scratch = context.ScratchFile.FirstOrDefault(d => string.Equals(d.Group, ScratchGroup, StringComparison.OrdinalIgnoreCase) &&
                                                                  string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            item.FileGroup = ScratchGroup;
            if (scratch != null)
            {
                item.Definition = scratch;
                item.DataTypeId = scratch.DataTypeId;
                if (requestedSpec != null && !ParameterSpecMap.SameType(requestedSpec, scratch.DataTypeId))
                {
                    Set(item, "invalidType", $"The connector's scratch definition '{name}' is {scratch.DataTypeId}, not {request.DataType}; pick another name.");
                    return;
                }
            }
            else
            {
                if (requestedSpec == null) { Set(item, "invalidType", "dataType is required for source=project."); return; }
                item.CreateInFile = true;
                item.DataTypeId = requestedSpec;
            }
        }

        // 6. Already bound?
        var guid = item.Definition?.Guid ?? (request.Guid.Length > 0 ? request.Guid : null);
        var existing = context.Existing.FirstOrDefault(e => guid != null && e.Guid != null && string.Equals(e.Guid, guid, StringComparison.OrdinalIgnoreCase))
                       ?? context.Existing.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        item.Existing = existing;
        if (existing == null)
        {
            Set(item, "willCreate", item.CreateInFile
                ? request.IsShared
                    ? $"Creates the definition in group '{item.FileGroup}' of the shared parameter file, then binds it."
                    : $"source=project: Revit has no API for a non-shared project parameter, so it is created as a shared definition in the connector's scratch file (group '{ScratchGroup}') and bound from there."
                : null);
            return;
        }

        if (guid != null && existing.Guid != null && !string.Equals(existing.Guid, guid, StringComparison.OrdinalIgnoreCase))
        {
            Set(item, "invalid", $"A different parameter named '{existing.Name}' is already bound ({(existing.IsShared ? $"shared, GUID {existing.Guid}" : "project parameter")}).");
            return;
        }
        if (guid != null && existing.Guid == null)
        {
            Set(item, "invalid", $"A project (non-shared) parameter named '{existing.Name}' is already bound; Revit cannot bind a shared one of the same name.");
            return;
        }
        if (existing.IsInstance != request.IsInstance)
        {
            Set(item, "invalid", $"'{existing.Name}' is already bound as a {(existing.IsInstance ? "instance" : "type")} parameter. Changing the binding kind would drop its values and is not done here.");
            return;
        }

        var added = item.Categories.Where(c => !existing.Categories.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();
        if (added.Count == 0) { Set(item, "exists", "Already bound to every requested category."); return; }
        if (!request.Rebind)
        {
            Set(item, "exists", $"Already bound, but not to {string.Join(", ", added)}. Pass ifExists=rebind to add them.");
            return;
        }
        item.AddedCategories = added;
        item.Categories = existing.Categories.Concat(added).ToList();
        Set(item, "willRebind", $"Adds {string.Join(", ", added)} to the existing binding (the other {existing.Categories.Count} categor{(existing.Categories.Count == 1 ? "y stays" : "ies stay")}).");
    }

    /// <summary>
    /// The definition a shared request names: by GUID when given, else by name within group, else by name across
    /// groups (ambiguous when several groups hold it).
    /// </summary>
    public static SharedDefinitionInfo? FindDefinition(IEnumerable<SharedDefinitionInfo> definitions, ParameterRequest request, out string? ambiguity)
    {
        ambiguity = null;
        var list = definitions.ToList();
        if (request.Guid.Length > 0)
            return list.FirstOrDefault(d => string.Equals(d.Guid, request.Guid, StringComparison.OrdinalIgnoreCase));
        var byName = list.Where(d => string.Equals(d.Name, request.Name.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(d => request.Group.Length == 0 || string.Equals(d.Group, request.Group, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (byName.Count > 1)
        {
            ambiguity = $"'{request.Name}' is defined in {byName.Count} groups of the shared parameter file ({string.Join(", ", byName.Select(d => d.Group))}); pass group or guid.";
            return null;
        }
        return byName.FirstOrDefault();
    }

    /// <summary>
    /// The new category set of revit_bind_parameter_categories: current + add − remove. Refuses to remove the last
    /// category (Revit cannot hold a binding without one; deleting the parameter is a different decision).
    /// </summary>
    public static CategoryChange PlanCategoryChange(IEnumerable<string> current, IEnumerable<string> add, IEnumerable<string> remove)
    {
        var change = new CategoryChange();
        var result = current.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var category in add.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            if (result.Contains(category, StringComparer.OrdinalIgnoreCase)) change.AlreadyBound.Add(category);
            else { result.Add(category); change.Added.Add(category); }
        }
        foreach (var category in remove.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var match = result.FirstOrDefault(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase));
            if (match == null) change.NotBound.Add(category);
            else if (change.Added.Contains(match, StringComparer.OrdinalIgnoreCase)) change.Error = $"'{category}' is both added and removed.";
            else { result.Remove(match); change.Removed.Add(match); }
        }
        change.Categories = result;
        if (change.Error == null && result.Count == 0)
            change.Error = "That would remove the last category of the binding. Revit cannot keep a binding without categories; keep at least one.";
        return change;
    }

    private static void Set(ParameterPlanItem item, string action, string? reason)
    {
        item.Action = action;
        item.Reason = reason;
    }

    private static string Str(JObject item, string key, string fallback = "")
    {
        var token = item[key];
        if (token == null || token.Type == JTokenType.Null) return fallback;
        var text = token.Type == JTokenType.String ? (string?)token : token.ToString();
        return string.IsNullOrWhiteSpace(text) ? fallback : text!.Trim();
    }

    private static JArray? TryParse(string text)
    {
        try { return JToken.Parse(text) as JArray; }
        catch { return null; }
    }

    private static JArray? TryFrom(object raw)
    {
        try { return JArray.FromObject(raw); }
        catch { return null; }
    }
}

/// <summary>The outcome of a category add/remove on an existing binding.</summary>
public sealed class CategoryChange
{
    public List<string> Categories { get; set; } = new();
    public List<string> Added { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> AlreadyBound { get; } = new();
    public List<string> NotBound { get; } = new();
    public string? Error { get; set; }
    public bool Changes => Added.Count > 0 || Removed.Count > 0;
}

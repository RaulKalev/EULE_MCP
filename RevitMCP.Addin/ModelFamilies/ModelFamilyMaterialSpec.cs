using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>A material named in an argument: by name, by element id, or none (By Category / empty).</summary>
public sealed class ModelFamilyMaterialRef
{
    public string? Name { get; init; }
    public long? Id { get; init; }
    /// <summary>JSON null: By Category for an element, an empty value for a family parameter.</summary>
    public bool IsNone => Name == null && Id == null;

    public override string ToString() => Name != null ? $"'{Name}'" : Id != null ? $"element {Id}" : "none";
}

/// <summary>One entry of <c>materials</c>: create or edit a material of the family (#173).</summary>
public sealed class ModelFamilyMaterialOp
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Red, green, blue, 0-255.</summary>
    public int[]? Color { get; init; }
    public int? Transparency { get; init; }
    public int? Shininess { get; init; }
    public int? Smoothness { get; init; }
    public string? MaterialClass { get; init; }
    public string DuplicateFrom { get; init; } = string.Empty;
    public bool CopyFromProject { get; init; }

    public string Source => $"materials[{Index}]";
    public bool HasProperties => Color != null || Transparency != null || Shininess != null || Smoothness != null || MaterialClass != null;
}

/// <summary>One entry of <c>assign</c>: set the Material property of forms directly.</summary>
public sealed class ModelFamilyMaterialAssignOp
{
    public int Index { get; init; }
    public List<long> Elements { get; init; } = new();
    public ModelFamilyMaterialRef Material { get; init; } = new();
    public bool ReplaceAssociation { get; init; }

    public string Source => $"assign[{Index}]";
}

/// <summary>The <c>create</c> object of an <c>associate</c> entry: a family parameter to add when it is missing.</summary>
public sealed class ModelFamilyMaterialParameterCreate
{
    public bool Instance { get; init; }
    public string Group { get; init; } = ModelFamilyMaterialArgs.DefaultGroup;
}

/// <summary>One entry of <c>associate</c>: drive the Material property of forms from a material family parameter.</summary>
public sealed class ModelFamilyMaterialAssociateOp
{
    public int Index { get; init; }
    public List<long> Elements { get; init; } = new();
    /// <summary>The family parameter; null dissociates.</summary>
    public string? Parameter { get; init; }
    public ModelFamilyMaterialParameterCreate? Create { get; init; }

    public string Source => $"associate[{Index}]";
}

/// <summary>One entry of <c>defaults</c>: the value of a material family parameter per family type.</summary>
public sealed class ModelFamilyMaterialDefaultOp
{
    public int Index { get; init; }
    public string Parameter { get; init; } = string.Empty;
    public ModelFamilyMaterialRef Material { get; init; } = new();
    /// <summary>One family type by name; empty means every type.</summary>
    public string Type { get; init; } = string.Empty;

    public string Source => $"defaults[{Index}]";
}

/// <summary>Everything a materials call asks for, in the order it is carried out.</summary>
public sealed class ModelFamilyMaterialPlan
{
    public List<ModelFamilyMaterialOp> Materials { get; } = new();
    public List<ModelFamilyMaterialAssignOp> Assign { get; } = new();
    public List<ModelFamilyMaterialAssociateOp> Associate { get; } = new();
    public List<ModelFamilyMaterialDefaultOp> Defaults { get; } = new();

    public bool IsEmpty => Materials.Count == 0 && Assign.Count == 0 && Associate.Count == 0 && Defaults.Count == 0;

    /// <summary>"2 materials, 1 assignment, 1 association, 2 defaults" for approval and summary lines.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Materials.Count > 0) parts.Add($"{Materials.Count} material{(Materials.Count == 1 ? string.Empty : "s")}");
        if (Assign.Count > 0) parts.Add($"{Assign.Sum(a => a.Elements.Count)} direct assignment{(Assign.Sum(a => a.Elements.Count) == 1 ? string.Empty : "s")}");
        if (Associate.Count > 0) parts.Add($"{Associate.Sum(a => a.Elements.Count)} association{(Associate.Sum(a => a.Elements.Count) == 1 ? string.Empty : "s")}");
        if (Defaults.Count > 0) parts.Add($"{Defaults.Count} type default{(Defaults.Count == 1 ? string.Empty : "s")}");
        return parts.Count == 0 ? "no changes" : string.Join(", ", parts);
    }
}

/// <summary>
/// Reads the <c>materials</c>, <c>assign</c>, <c>associate</c> and <c>defaults</c> arguments of the material tools
/// (#173). Pure. The steps run in exactly this order, so a material created in <c>materials</c> can be named by every
/// later list, and a family parameter created in <c>associate</c> can receive a value in <c>defaults</c>.
/// </summary>
public static class ModelFamilyMaterialArgs
{
    public const string MaterialsKey = "materials";
    public const string AssignKey = "assign";
    public const string AssociateKey = "associate";
    public const string DefaultsKey = "defaults";

    /// <summary>The family parameter group a created material parameter goes into.</summary>
    public const string DefaultGroup = "Materials and Finishes";

    /// <summary>The argument lists in the order they are carried out.</summary>
    public static readonly string[] Order = { MaterialsKey, AssignKey, AssociateKey, DefaultsKey };

    public const int MaxTransparency = 100;
    public const int MaxShininess = 128;
    public const int MaxSmoothness = 100;

    /// <summary>Characters Revit refuses in a material name.</summary>
    private static readonly char[] ProhibitedInNames = { '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

    private static readonly string[] MaterialKeys = { "name", "color", "transparency", "shininess", "smoothness", "materialClass", "duplicateFrom", "copyFromProject" };
    private static readonly string[] AssignKeys = { "elements", "material", "replaceAssociation" };
    private static readonly string[] AssociateKeys = { "elements", "parameter", "create" };
    private static readonly string[] CreateKeys = { "instance", "group" };
    private static readonly string[] DefaultKeys = { "parameter", "material", "type" };

    public static ModelFamilyMaterialPlan Parse(IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var plan = new ModelFamilyMaterialPlan();

        var materials = ModelFamilyJson.Objects(args, MaterialsKey, problems);
        for (var i = 0; i < materials.Count; i++)
            if (ParseMaterial(materials[i], i, problems) is { } op) plan.Materials.Add(op);
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var op in plan.Materials)
        {
            if (seen.TryGetValue(op.Name, out var first))
                problems.Add($"{op.Source}.name: '{op.Name}' is also in materials[{first}]; list a material once with all its properties.");
            else seen[op.Name] = op.Index;
        }

        var assign = ModelFamilyJson.Objects(args, AssignKey, problems);
        for (var i = 0; i < assign.Count; i++)
            if (ParseAssign(assign[i], i, problems) is { } op) plan.Assign.Add(op);

        var associate = ModelFamilyJson.Objects(args, AssociateKey, problems);
        for (var i = 0; i < associate.Count; i++)
            if (ParseAssociate(associate[i], i, problems) is { } op) plan.Associate.Add(op);

        var defaults = ModelFamilyJson.Objects(args, DefaultsKey, problems);
        for (var i = 0; i < defaults.Count; i++)
            if (ParseDefault(defaults[i], i, problems) is { } op) plan.Defaults.Add(op);

        return plan;
    }

    public static ModelFamilyMaterialOp? ParseMaterial(JObject item, int index, List<string> problems)
    {
        var source = $"materials[{index}]";
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(item, source, problems, MaterialKeys);

        var name = ModelFamilyJson.Str(item, "name");
        if (name.Length == 0) problems.Add($"{source}.name is required: the material's name, for example \"Plastic White\".");
        else if (name.IndexOfAny(ProhibitedInNames) >= 0)
            problems.Add($"{source}.name: '{name}' contains a character Revit does not allow in a material name ({string.Join(" ", ProhibitedInNames)}).");

        var color = ParseColor(item, source, problems);
        var transparency = WholeInRange(item, "transparency", source, 0, MaxTransparency, problems);
        var shininess = WholeInRange(item, "shininess", source, 0, MaxShininess, problems);
        var smoothness = WholeInRange(item, "smoothness", source, 0, MaxSmoothness, problems);

        string? materialClass = null;
        if (ModelFamilyJson.Get(item, "materialClass") != null) materialClass = ModelFamilyJson.Str(item, "materialClass");

        var duplicateFrom = ModelFamilyJson.Str(item, "duplicateFrom");
        var copy = ModelFamilyJson.Bool(item, "copyFromProject", source, problems) ?? false;
        if (duplicateFrom.Length > 0 && copy)
            problems.Add($"{source}: give duplicateFrom (a material of the family) or copyFromProject, not both.");

        return problems.Count > before ? null : new ModelFamilyMaterialOp
        {
            Index = index, Name = name, Color = color, Transparency = transparency, Shininess = shininess,
            Smoothness = smoothness, MaterialClass = materialClass, DuplicateFrom = duplicateFrom, CopyFromProject = copy
        };
    }

    public static ModelFamilyMaterialAssignOp? ParseAssign(JObject item, int index, List<string> problems)
    {
        var source = $"assign[{index}]";
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(item, source, problems, AssignKeys);
        var elements = ParseIds(item, "elements", source, problems);
        var material = ParseMaterialRef(item, "material", source, problems,
            "a material name, an element id, or null for By Category");
        var replace = ModelFamilyJson.Bool(item, "replaceAssociation", source, problems) ?? false;
        return problems.Count > before || material == null ? null : new ModelFamilyMaterialAssignOp
        {
            Index = index, Elements = elements, Material = material, ReplaceAssociation = replace
        };
    }

    public static ModelFamilyMaterialAssociateOp? ParseAssociate(JObject item, int index, List<string> problems)
    {
        var source = $"associate[{index}]";
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(item, source, problems, AssociateKeys);
        var elements = ParseIds(item, "elements", source, problems);

        string? parameter = null;
        var parameterToken = item.GetValue("parameter", StringComparison.OrdinalIgnoreCase);
        if (parameterToken == null)
            problems.Add($"{source}.parameter is required: the name of a material family parameter, or null to dissociate.");
        else if (parameterToken.Type != JTokenType.Null)
        {
            parameter = ModelFamilyJson.Str(item, "parameter");
            if (parameter.Length == 0) problems.Add($"{source}.parameter: give the name of a material family parameter, or null to dissociate.");
        }

        ModelFamilyMaterialParameterCreate? create = null;
        var createToken = ModelFamilyJson.Get(item, "create");
        if (createToken != null)
        {
            if (createToken is not JObject createObject)
                problems.Add($"{source}.create: must be an object like {{\"instance\": true, \"group\": \"Materials and Finishes\"}}.");
            else if (parameter == null)
                problems.Add($"{source}.create: only makes sense with a parameter name.");
            else
            {
                ModelFamilyJson.CheckKeys(createObject, $"{source}.create", problems, CreateKeys);
                var instance = ModelFamilyJson.Bool(createObject, "instance", $"{source}.create", problems) ?? false;
                var group = ModelFamilyJson.Str(createObject, "group", DefaultGroup);
                create = new ModelFamilyMaterialParameterCreate { Instance = instance, Group = group.Length == 0 ? DefaultGroup : group };
            }
        }

        return problems.Count > before ? null : new ModelFamilyMaterialAssociateOp
        {
            Index = index, Elements = elements, Parameter = parameter, Create = create
        };
    }

    public static ModelFamilyMaterialDefaultOp? ParseDefault(JObject item, int index, List<string> problems)
    {
        var source = $"defaults[{index}]";
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(item, source, problems, DefaultKeys);
        var parameter = ModelFamilyJson.Str(item, "parameter");
        if (parameter.Length == 0) problems.Add($"{source}.parameter is required: the name of a material family parameter.");
        var material = ParseMaterialRef(item, "material", source, problems, "a material name, an element id, or null for an empty value");
        var type = ModelFamilyJson.Str(item, "type");
        return problems.Count > before || material == null ? null : new ModelFamilyMaterialDefaultOp
        {
            Index = index, Parameter = parameter, Material = material, Type = type
        };
    }

    /// <summary>
    /// A required material reference: a string is a name, an integer an element id, JSON null means none. A missing
    /// key is a problem (null is meaningful, so it must be written out).
    /// </summary>
    public static ModelFamilyMaterialRef? ParseMaterialRef(JObject item, string key, string source, List<string> problems, string expected)
    {
        var token = item.GetValue(key, StringComparison.OrdinalIgnoreCase);
        if (token == null)
        {
            problems.Add($"{source}.{key} is required: {expected}.");
            return null;
        }
        switch (token.Type)
        {
            case JTokenType.Null:
                return new ModelFamilyMaterialRef();
            case JTokenType.Integer:
                var id = token.Value<long>();
                if (id > 0) return new ModelFamilyMaterialRef { Id = id };
                problems.Add($"{source}.{key}: {id} is not an element id.");
                return null;
            case JTokenType.String:
                var name = ((string?)token ?? string.Empty).Trim();
                if (name.Length > 0) return new ModelFamilyMaterialRef { Name = name };
                problems.Add($"{source}.{key}: the material name is empty; pass {expected}.");
                return null;
            default:
                problems.Add($"{source}.{key}: must be {expected}.");
                return null;
        }
    }

    /// <summary>A required, non-empty list of element ids (a single id counts as a list of one).</summary>
    public static List<long> ParseIds(JObject item, string key, string source, List<string> problems)
    {
        var ids = new List<long>();
        var token = ModelFamilyJson.Get(item, key);
        if (token == null)
        {
            problems.Add($"{source}.{key} is required: a list of element ids (revit_get_model_family lists the forms).");
            return ids;
        }
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        for (var i = 0; i < items.Count; i++)
        {
            var id = ModelFamilyJson.AsId(items[i]);
            if (id == null || id <= 0) problems.Add($"{source}.{key}[{i}]: must be an element id.");
            else if (!ids.Contains(id.Value)) ids.Add(id.Value);
        }
        if (items.Count == 0) problems.Add($"{source}.{key}: the list is empty; give at least one element id.");
        return ids;
    }

    private static int[]? ParseColor(JObject item, string source, List<string> problems)
    {
        var token = ModelFamilyJson.Get(item, "color");
        if (token == null) return null;
        if (token is not JArray array || array.Count != 3)
        {
            problems.Add($"{source}.color: must be [red, green, blue], each 0 to 255, for example [255, 255, 255].");
            return null;
        }
        var values = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var number = ModelFamilyJson.AsNumber(array[i]);
            if (number == null || Math.Abs(number.Value - Math.Round(number.Value)) > 1e-9 || number < 0 || number > 255)
            {
                problems.Add($"{source}.color[{i}]: must be a whole number from 0 to 255.");
                return null;
            }
            values[i] = (int)Math.Round(number.Value);
        }
        return values;
    }

    private static int? WholeInRange(JObject item, string key, string source, int min, int max, List<string> problems)
    {
        if (ModelFamilyJson.Get(item, key) == null) return null;
        var number = ModelFamilyJson.Number(item, key, source, problems);
        if (number == null) return null;
        if (Math.Abs(number.Value - Math.Round(number.Value)) > 1e-9 || number < min || number > max)
        {
            problems.Add($"{source}.{key}: must be a whole number from {min} to {max}.");
            return null;
        }
        return (int)Math.Round(number.Value);
    }
}

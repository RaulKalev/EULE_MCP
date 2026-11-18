using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Parameters;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>One entry of <c>parameters</c>: creates a family parameter, or edits one the family already has (#172).</summary>
public sealed class ModelFamilyParameterEdit
{
    public string Source { get; set; } = string.Empty;
    /// <summary>The parameter's current name. Empty for a shared parameter named by guid only.</summary>
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public SpecResolution? Spec { get; set; }
    public string Group { get; set; } = string.Empty;
    /// <summary>Null = keep (an existing parameter) or type (a new one).</summary>
    public bool? Instance { get; set; }
    /// <summary>True for <c>shared: true</c>, <c>shared: {guid}</c> or a guid; false when <c>shared: false</c> was written.</summary>
    public bool? SharedFlag { get; set; }
    public string Guid { get; set; } = string.Empty;
    public bool Shared => SharedFlag == true || Guid.Length > 0;
    public bool FormulaGiven { get; set; }
    /// <summary>The formula; null with <see cref="FormulaGiven"/> clears it.</summary>
    public string? Formula { get; set; }
    public bool? Reporting { get; set; }
    public string? Tooltip { get; set; }
    public string? Rename { get; set; }
    public JToken? Value { get; set; }
}

/// <summary>One entry of <c>types</c>: a family type to create, rename, delete or set values on.</summary>
public sealed class ModelFamilyTypeEdit
{
    public string Source { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Rename { get; set; }
    public bool Delete { get; set; }
    public bool MakeCurrent { get; set; }
    /// <summary>Parameter name → value, in the order given.</summary>
    public List<KeyValuePair<string, JToken>> Values { get; } = new();
}

/// <summary>One entry of <c>formulas</c>.</summary>
public sealed class ModelFamilyFormulaEdit
{
    public string Source { get; set; } = string.Empty;
    public string Parameter { get; set; } = string.Empty;
    public string? Formula { get; set; }
}

/// <summary>One element parameter to associate with a family parameter, or to dissociate (<c>FamilyParameter</c> null).</summary>
public sealed class ModelFamilyAssociationEdit
{
    public string Source { get; set; } = string.Empty;
    public long ElementId { get; set; }
    /// <summary>The element parameter's name or BuiltInParameter name.</summary>
    public string Parameter { get; set; } = string.Empty;
    public string? FamilyParameter { get; set; }
    public bool Dissociate => FamilyParameter == null;
}

/// <summary>Everything revit_edit_model_family_parameters is asked to do.</summary>
public sealed class ModelFamilyParameterSpec
{
    public List<string> RemoveParameters { get; } = new();
    public List<ModelFamilyParameterEdit> Parameters { get; } = new();
    public List<ModelFamilyTypeEdit> Types { get; } = new();
    public List<ModelFamilyFormulaEdit> Formulas { get; } = new();
    public List<ModelFamilyAssociationEdit> Associations { get; } = new();
    public string SharedParameterFile { get; set; } = string.Empty;

    public bool IsEmpty => RemoveParameters.Count == 0 && Parameters.Count == 0 && Types.Count == 0 &&
                           Formulas.Count == 0 && Associations.Count == 0;
}

/// <summary>Reads the arguments of the family-parameter tools into a <see cref="ModelFamilyParameterSpec"/>. Pure.</summary>
public static class ModelFamilyParameterSpecParser
{
    public const int MaxItems = 300;

    private static readonly string[] ParameterKeys =
        { "name", "dataType", "group", "instance", "shared", "guid", "formula", "reporting", "tooltip", "rename", "value" };

    private static readonly string[] TypeKeys = { "name", "rename", "delete", "values", "makeCurrent" };
    private static readonly string[] FormulaKeys = { "parameter", "formula" };
    private static readonly string[] AssociationKeys = { "element", "elements", "parameter", "familyParameter" };

    public static ModelFamilyParameterSpec Parse(IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var spec = new ModelFamilyParameterSpec();
        spec.SharedParameterFile = ModelFamilyJson.Token(args, "sharedParameterFile") is { } file ? file.ToString().Trim() : string.Empty;

        ReadRemove(args, spec, problems);
        ReadParameters(args, spec, problems);
        ReadTypes(args, spec, problems);
        ReadFormulas(args, spec, problems);
        ReadAssociations(args, spec, problems);

        var total = spec.RemoveParameters.Count + spec.Parameters.Count + spec.Types.Count + spec.Formulas.Count + spec.Associations.Count;
        if (total > MaxItems) problems.Add($"{total} operations were requested; at most {MaxItems} per call. Split the work into several calls.");
        return spec;
    }

    private static void ReadRemove(IReadOnlyDictionary<string, object?> args, ModelFamilyParameterSpec spec, List<string> problems)
    {
        var token = ModelFamilyJson.Token(args, "removeParameters");
        if (token == null) return;
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        for (var i = 0; i < items.Count; i++)
        {
            var name = items[i].Type == JTokenType.String ? ((string?)items[i] ?? string.Empty).Trim() : string.Empty;
            if (name.Length == 0) problems.Add($"removeParameters[{i}]: give the name of a family parameter.");
            else if (!spec.RemoveParameters.Contains(name, StringComparer.OrdinalIgnoreCase)) spec.RemoveParameters.Add(name);
        }
    }

    private static void ReadParameters(IReadOnlyDictionary<string, object?> args, ModelFamilyParameterSpec spec, List<string> problems)
    {
        var items = ModelFamilyJson.Objects(args, "parameters", problems);
        for (var i = 0; i < items.Count; i++)
        {
            var obj = items[i];
            var source = $"parameters[{i}]";
            ModelFamilyJson.CheckKeys(obj, source, problems, ParameterKeys);
            var edit = new ModelFamilyParameterEdit
            {
                Source = source,
                Name = ModelFamilyJson.Str(obj, "name"),
                DataType = ModelFamilyJson.Str(obj, "dataType"),
                Group = ModelFamilyJson.Str(obj, "group"),
                Instance = ModelFamilyJson.Bool(obj, "instance", source, problems),
                Reporting = ModelFamilyJson.Bool(obj, "reporting", source, problems),
                Value = ModelFamilyJson.Get(obj, "value"),
                Guid = ModelFamilyJson.Str(obj, "guid")
            };

            // shared: true | false | {guid}
            var shared = ModelFamilyJson.Get(obj, "shared");
            if (shared is JObject sharedObject)
            {
                ModelFamilyJson.CheckKeys(sharedObject, $"{source}.shared", problems, "guid");
                edit.SharedFlag = true;
                var guid = ModelFamilyJson.Str(sharedObject, "guid");
                if (guid.Length > 0) edit.Guid = guid;
            }
            else if (shared != null)
            {
                edit.SharedFlag = ModelFamilyJson.Bool(obj, "shared", source, problems);
            }
            if (edit.Guid.Length > 0 && !System.Guid.TryParse(edit.Guid, out _))
                problems.Add($"{source}.guid: '{edit.Guid}' is not a GUID (for example 1b5f3a4e-0c52-4a0e-9a49-0d0e5a7c6f11).");
            if (edit.SharedFlag == false && edit.Guid.Length > 0)
                problems.Add($"{source}: shared is false but a guid is given.");

            var formulaProperty = obj.Property("formula", StringComparison.OrdinalIgnoreCase);
            if (formulaProperty != null)
            {
                edit.FormulaGiven = true;
                edit.Formula = formulaProperty.Value.Type == JTokenType.Null ? null : ModelFamilyJson.Str(obj, "formula");
                if (edit.Formula != null && edit.Formula.Trim().Length == 0) edit.Formula = null;
            }
            var tooltip = obj.Property("tooltip", StringComparison.OrdinalIgnoreCase);
            if (tooltip != null) edit.Tooltip = tooltip.Value.Type == JTokenType.Null ? string.Empty : ModelFamilyJson.Str(obj, "tooltip");
            var rename = ModelFamilyJson.Str(obj, "rename");
            if (rename.Length > 0) edit.Rename = rename;

            if (edit.Name.Length == 0 && !edit.Shared)
                problems.Add($"{source}.name is required (only a shared parameter given by guid may leave it out).");
            if (edit.Name.Length > 0 && DetailFamilySpecParser.CheckParameterName(edit.Name) is { } nameError)
                problems.Add($"{source}.name: {nameError}");
            if (edit.Rename != null && DetailFamilySpecParser.CheckParameterName(edit.Rename) is { } renameError)
                problems.Add($"{source}.rename: {renameError}");

            if (edit.DataType.Length > 0)
            {
                edit.Spec = ParameterSpecMap.ResolveSpec(edit.DataType);
                if (edit.Spec == null)
                {
                    var close = ParameterSpecMap.SuggestSpecs(edit.DataType);
                    problems.Add($"{source}.dataType: '{edit.DataType}' is not a known data type. Use one of " +
                                 $"{string.Join(", ", close.Count > 0 ? close.Concat(ParameterSpecMap.FriendlySpecNames.Except(close)).Take(8) : ParameterSpecMap.FriendlySpecNames)}.");
                }
            }
            if (edit.FormulaGiven && edit.Formula != null && edit.Value != null)
                problems.Add($"{source}: give a value or a formula, not both (a formula drives the value).");
            spec.Parameters.Add(edit);
        }

        foreach (var group in spec.Parameters.Where(p => p.Name.Length > 0).GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"parameters: '{group.Key}' is listed {group.Count()} times; merge them into one entry.");
    }

    private static void ReadTypes(IReadOnlyDictionary<string, object?> args, ModelFamilyParameterSpec spec, List<string> problems)
    {
        var items = ModelFamilyJson.Objects(args, "types", problems);
        for (var i = 0; i < items.Count; i++)
        {
            var obj = items[i];
            var source = $"types[{i}]";
            ModelFamilyJson.CheckKeys(obj, source, problems, TypeKeys);
            var edit = new ModelFamilyTypeEdit
            {
                Source = source,
                Name = ModelFamilyJson.Str(obj, "name"),
                Delete = ModelFamilyJson.Bool(obj, "delete", source, problems) ?? false,
                MakeCurrent = ModelFamilyJson.Bool(obj, "makeCurrent", source, problems) ?? false
            };
            var rename = ModelFamilyJson.Str(obj, "rename");
            if (rename.Length > 0) edit.Rename = rename;
            if (edit.Name.Length == 0) problems.Add($"{source}.name is required: the family type to create, change or delete.");
            if (edit.Delete && (edit.Rename != null || ModelFamilyJson.Has(obj, "values") || edit.MakeCurrent))
                problems.Add($"{source}: delete cannot be combined with rename, values or makeCurrent.");

            var values = ModelFamilyJson.Get(obj, "values");
            if (values is JObject valueObject)
            {
                foreach (var property in valueObject.Properties())
                    edit.Values.Add(new KeyValuePair<string, JToken>(property.Name, property.Value));
            }
            else if (values != null)
            {
                problems.Add($"{source}.values: must be an object {{\"<parameter>\": value, …}}. Lengths in mm, angles in degrees, yes/no as true/false, material as its name.");
            }
            spec.Types.Add(edit);
        }
        foreach (var group in spec.Types.Where(t => t.Name.Length > 0).GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"types: '{group.Key}' is listed {group.Count()} times; merge them into one entry.");
    }

    private static void ReadFormulas(IReadOnlyDictionary<string, object?> args, ModelFamilyParameterSpec spec, List<string> problems)
    {
        var items = ModelFamilyJson.Objects(args, "formulas", problems);
        for (var i = 0; i < items.Count; i++)
        {
            var obj = items[i];
            var source = $"formulas[{i}]";
            ModelFamilyJson.CheckKeys(obj, source, problems, FormulaKeys);
            var name = ModelFamilyJson.Str(obj, "parameter");
            var property = obj.Property("formula", StringComparison.OrdinalIgnoreCase);
            if (name.Length == 0) problems.Add($"{source}.parameter is required: the family parameter the formula drives.");
            if (property == null) problems.Add($"{source}.formula is required: a formula string, or null to clear it.");
            if (name.Length == 0 || property == null) continue;
            var formula = property.Value.Type == JTokenType.Null ? null : ModelFamilyJson.Str(obj, "formula");
            spec.Formulas.Add(new ModelFamilyFormulaEdit
            {
                Source = source,
                Parameter = name,
                Formula = string.IsNullOrWhiteSpace(formula) ? null : formula
            });
        }
    }

    private static void ReadAssociations(IReadOnlyDictionary<string, object?> args, ModelFamilyParameterSpec spec, List<string> problems)
    {
        var items = ModelFamilyJson.Objects(args, "associations", problems);
        for (var i = 0; i < items.Count; i++)
        {
            var obj = items[i];
            var source = $"associations[{i}]";
            ModelFamilyJson.CheckKeys(obj, source, problems, AssociationKeys);
            var parameter = ModelFamilyJson.Str(obj, "parameter");
            var family = obj.Property("familyParameter", StringComparison.OrdinalIgnoreCase);
            if (parameter.Length == 0)
                problems.Add($"{source}.parameter is required: the element parameter's name, or its BuiltInParameter name (for example MATERIAL_ID_PARAM, IS_VISIBLE_PARAM).");
            if (family == null)
                problems.Add($"{source}.familyParameter is required: a family parameter name, or null to dissociate.");

            var ids = new List<long>();
            if (ModelFamilyJson.Get(obj, "element") is { } single)
            {
                if (ModelFamilyJson.AsId(single) is { } id) ids.Add(id);
                else problems.Add($"{source}.element: must be an element id of the family document.");
            }
            if (ModelFamilyJson.Get(obj, "elements") is { } many)
            {
                if (many is JArray array)
                {
                    for (var k = 0; k < array.Count; k++)
                    {
                        if (ModelFamilyJson.AsId(array[k]) is { } id) ids.Add(id);
                        else problems.Add($"{source}.elements[{k}]: must be an element id.");
                    }
                }
                else problems.Add($"{source}.elements: must be a list of element ids.");
            }
            if (ids.Count == 0 && !problems.Any(p => p.StartsWith(source, StringComparison.Ordinal)))
                problems.Add($"{source}: give element (an element id) or elements (a list of ids). revit_get_model_family lists them.");
            if (parameter.Length == 0 || family == null) continue;

            string? familyName = null;
            if (family.Value.Type != JTokenType.Null)
            {
                familyName = ModelFamilyJson.Str(obj, "familyParameter");
                if (familyName.Length == 0) problems.Add($"{source}.familyParameter: give a name, or null to dissociate.");
            }
            foreach (var id in ids.Distinct())
                spec.Associations.Add(new ModelFamilyAssociationEdit { Source = source, ElementId = id, Parameter = parameter, FamilyParameter = familyName });
        }
    }
}

using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Families;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>An element named by id, or by the list item of this same call that creates it (<c>{"created":"instances[0]"}</c>).</summary>
public sealed class ComponentElementRef
{
    public long? Id { get; set; }
    public string? Created { get; set; }
    public string Source { get; set; } = string.Empty;
}

/// <summary>Resolves the references one call's later lists make to the elements its earlier lists created (#175).</summary>
public static class ModelFamilyComponentRefs
{
    private static readonly Regex CreatedPattern = new(@"^\s*(instances|symbolicLines|modelLines)\s*\[\s*(\d+)\s*\]\s*$", RegexOptions.Compiled);

    /// <summary>The lists whose items create elements a later list may name.</summary>
    public static readonly string[] CreatingSections = { "instances", "symbolicLines", "modelLines" };

    public static string Format(string section, int index) => $"{section}[{index}]";

    public static bool TryParseCreated(string? text, out string section, out int index)
    {
        section = string.Empty;
        index = -1;
        var match = CreatedPattern.Match(text ?? string.Empty);
        if (!match.Success) return false;
        section = match.Groups[1].Value;
        return int.TryParse(match.Groups[2].Value, out index);
    }

    /// <summary>
    /// Element references: a list of ids (numbers or numeric strings) and <c>{"created":"instances[0]"}</c> entries
    /// (a bare string such as "instances[0]" also counts). <paramref name="counts"/> holds the size of each creating
    /// list so a reference to a missing item is a problem before anything runs.
    /// </summary>
    public static List<ComponentElementRef> Parse(JToken? token, string source, List<string> problems, IReadOnlyDictionary<string, int> counts)
    {
        var result = new List<ComponentElementRef>();
        if (token == null)
        {
            problems.Add($"{source} is required: a list of element ids (or {{\"created\":\"instances[0]\"}} for an element this call creates).");
            return result;
        }
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        if (items.Count == 0) problems.Add($"{source}: give at least one element.");
        for (var i = 0; i < items.Count; i++)
        {
            var at = $"{source}[{i}]";
            var item = items[i];
            string? created = null;
            if (item is JObject obj)
            {
                ModelFamilyJson.CheckKeys(obj, at, problems, "created");
                created = ModelFamilyJson.Str(obj, "created");
                if (created.Length == 0)
                {
                    problems.Add($"{at}: give {{\"created\":\"instances[0]\"}} or an element id.");
                    continue;
                }
            }
            else if (item.Type == JTokenType.String && TryParseCreated((string?)item, out _, out _)) created = (string?)item;

            if (created != null)
            {
                if (!TryParseCreated(created, out var section, out var index))
                {
                    problems.Add($"{at}.created: '{created}' is not one of instances[n], symbolicLines[n], modelLines[n].");
                    continue;
                }
                var count = counts.TryGetValue(section, out var known) ? known : 0;
                if (index >= count)
                {
                    problems.Add($"{at}.created: this call has {count} item(s) in {section}; there is no {section}[{index}].");
                    continue;
                }
                result.Add(new ComponentElementRef { Created = Format(section, index), Source = at });
                continue;
            }
            var id = ModelFamilyJson.AsId(item);
            if (id == null) problems.Add($"{at}: must be an element id or {{\"created\":\"instances[0]\"}}.");
            else result.Add(new ComponentElementRef { Id = id, Source = at });
        }
        return result;
    }

    /// <summary>The element ids a reference means once the earlier lists have run; false with a reason when it cannot be resolved.</summary>
    public static bool Resolve(ComponentElementRef reference, IReadOnlyDictionary<string, List<long>> created, out List<long> ids, out string? error)
    {
        ids = new List<long>();
        error = null;
        if (reference.Id != null)
        {
            ids.Add(reference.Id.Value);
            return true;
        }
        if (reference.Created != null && created.TryGetValue(reference.Created, out var found) && found.Count > 0)
        {
            ids.AddRange(found);
            return true;
        }
        error = $"{reference.Source}: {reference.Created ?? "the reference"} did not create an element in this call, so there is nothing to refer to.";
        return false;
    }
}

public sealed class ComponentSubcategorySpec
{
    public string Name { get; set; } = string.Empty;
    public int? LineWeightProjection { get; set; }
    public int? LineWeightCut { get; set; }
    public int[]? Color { get; set; }
    public string? LinePattern { get; set; }
    public string? Material { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class ComponentNestedFamilySpec
{
    public string Path { get; set; } = string.Empty;
    public string FromProject { get; set; } = string.Empty;
    public FamilyIfExists IfExists { get; set; } = FamilyIfExists.Skip;
    public string Source { get; set; } = string.Empty;
}

public sealed class ComponentLockSpec
{
    public JToken? Reference { get; set; }
    public string InstanceReference { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed class ComponentInstanceSpec
{
    public JToken? Family { get; set; }
    public string Type { get; set; } = string.Empty;
    public double[] Position { get; set; } = new double[3];
    public double RotationDegrees { get; set; }
    public JToken? View { get; set; }
    public JToken? WorkPlane { get; set; }
    public JObject Parameters { get; set; } = new();
    public List<KeyValuePair<string, string>> Associate { get; set; } = new();
    public string TypeParameter { get; set; } = string.Empty;
    public bool TypeParameterInstance { get; set; }
    public ComponentVisibilityRequest? Visibility { get; set; }
    public List<ComponentLockSpec> LockTo { get; set; } = new();
    public string Name { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
}

public sealed class ComponentLineGroupSpec
{
    /// <summary>The view of a symbolic line group.</summary>
    public JToken? View { get; set; }
    /// <summary>The work plane of a model line group.</summary>
    public JToken? WorkPlane { get; set; }
    public List<ComponentCurveSpec> Curves { get; set; } = new();
    public string? Subcategory { get; set; }
    public ComponentVisibilityRequest? Visibility { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class ComponentSetSubcategorySpec
{
    public List<ComponentElementRef> Elements { get; set; } = new();
    /// <summary>The subcategory name; null puts the elements back on the family category itself.</summary>
    public string? Subcategory { get; set; }
    public string Source { get; set; } = string.Empty;
}

public sealed class ComponentVisibilitySpec
{
    public List<ComponentElementRef> Elements { get; set; } = new();
    public ComponentVisibilityRequest Request { get; set; } = new();
    public string Source { get; set; } = string.Empty;
}

/// <summary>
/// Everything revit_edit_model_family_components is asked to do (#175), parsed and checked without Revit. The lists run
/// in <see cref="Order"/>; a later list may name the elements an earlier one created.
/// </summary>
public sealed class ModelFamilyComponentPlan
{
    /// <summary>The order the lists run in.</summary>
    public static readonly string[] Order =
        { "remove", "subcategories", "nestedFamilies", "instances", "symbolicLines", "modelLines", "setSubcategory", "visibility" };

    public List<long> Remove { get; } = new();
    public List<ComponentSubcategorySpec> Subcategories { get; } = new();
    public List<ComponentNestedFamilySpec> NestedFamilies { get; } = new();
    public List<ComponentInstanceSpec> Instances { get; } = new();
    public List<ComponentLineGroupSpec> SymbolicLines { get; } = new();
    public List<ComponentLineGroupSpec> ModelLines { get; } = new();
    public List<ComponentSetSubcategorySpec> SetSubcategory { get; } = new();
    public List<ComponentVisibilitySpec> Visibility { get; } = new();

    public int OperationCount => Remove.Count + Subcategories.Count + NestedFamilies.Count + Instances.Count +
                                 SymbolicLines.Count + ModelLines.Count + SetSubcategory.Count + Visibility.Count;

    public int CountOf(string section) => section switch
    {
        "remove" => Remove.Count,
        "subcategories" => Subcategories.Count,
        "nestedFamilies" => NestedFamilies.Count,
        "instances" => Instances.Count,
        "symbolicLines" => SymbolicLines.Count,
        "modelLines" => ModelLines.Count,
        "setSubcategory" => SetSubcategory.Count,
        "visibility" => Visibility.Count,
        _ => 0
    };

    /// <summary>"2 subcategories, 1 nested family, 3 instances": one line for a summary.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        void Add(int count, string one, string many)
        {
            if (count > 0) parts.Add($"{count} {(count == 1 ? one : many)}");
        }
        Add(Remove.Count, "removal", "removals");
        Add(Subcategories.Count, "subcategory", "subcategories");
        Add(NestedFamilies.Count, "nested family", "nested families");
        Add(Instances.Count, "nested instance", "nested instances");
        Add(SymbolicLines.Count, "symbolic line group", "symbolic line groups");
        Add(ModelLines.Count, "model line group", "model line groups");
        Add(SetSubcategory.Count, "subcategory assignment", "subcategory assignments");
        Add(Visibility.Count, "visibility setting", "visibility settings");
        return parts.Count == 0 ? "nothing to do" : string.Join(", ", parts);
    }

    public static ModelFamilyComponentPlan Parse(IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var plan = new ModelFamilyComponentPlan();
        ParseRemove(plan, args, problems);
        foreach (var obj in ModelFamilyJson.Objects(args, "subcategories", problems).Select((o, i) => (o, i)))
            if (ParseSubcategory(obj.o, $"subcategories[{obj.i}]", problems) is { } sub) plan.Subcategories.Add(sub);
        foreach (var obj in ModelFamilyJson.Objects(args, "nestedFamilies", problems).Select((o, i) => (o, i)))
            if (ParseNestedFamily(obj.o, $"nestedFamilies[{obj.i}]", problems) is { } nested) plan.NestedFamilies.Add(nested);
        foreach (var obj in ModelFamilyJson.Objects(args, "instances", problems).Select((o, i) => (o, i)))
            if (ParseInstance(obj.o, $"instances[{obj.i}]", problems) is { } instance) plan.Instances.Add(instance);
        foreach (var obj in ModelFamilyJson.Objects(args, "symbolicLines", problems).Select((o, i) => (o, i)))
            if (ParseLineGroup(obj.o, $"symbolicLines[{obj.i}]", symbolic: true, problems) is { } group) plan.SymbolicLines.Add(group);
        foreach (var obj in ModelFamilyJson.Objects(args, "modelLines", problems).Select((o, i) => (o, i)))
            if (ParseLineGroup(obj.o, $"modelLines[{obj.i}]", symbolic: false, problems) is { } group) plan.ModelLines.Add(group);

        // The sizes the later lists may refer to. A list item that failed to parse still counts, so one bad item does
        // not hide the problems of the references to it: count the objects as written.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["instances"] = ModelFamilyJson.Objects(args, "instances", new List<string>()).Count,
            ["symbolicLines"] = ModelFamilyJson.Objects(args, "symbolicLines", new List<string>()).Count,
            ["modelLines"] = ModelFamilyJson.Objects(args, "modelLines", new List<string>()).Count
        };
        foreach (var obj in ModelFamilyJson.Objects(args, "setSubcategory", problems).Select((o, i) => (o, i)))
            if (ParseSetSubcategory(obj.o, $"setSubcategory[{obj.i}]", counts, problems) is { } set) plan.SetSubcategory.Add(set);
        foreach (var obj in ModelFamilyJson.Objects(args, "visibility", problems).Select((o, i) => (o, i)))
            if (ParseVisibility(obj.o, $"visibility[{obj.i}]", counts, problems) is { } visibility) plan.Visibility.Add(visibility);
        return plan;
    }

    private static void ParseRemove(ModelFamilyComponentPlan plan, IReadOnlyDictionary<string, object?> args, List<string> problems)
    {
        var token = ModelFamilyJson.Token(args, "remove");
        if (token == null) return;
        var items = token is JArray array ? array.ToList() : new List<JToken> { token };
        for (var i = 0; i < items.Count; i++)
        {
            var id = ModelFamilyJson.AsId(items[i]);
            if (id == null) problems.Add($"remove[{i}]: must be the element id of a nested instance, symbolic line or model line.");
            else if (!plan.Remove.Contains(id.Value)) plan.Remove.Add(id.Value);
        }
    }

    private static ComponentSubcategorySpec? ParseSubcategory(JObject obj, string source, List<string> problems)
    {
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, "name", "lineWeightProjection", "lineWeightCut", "color", "linePattern", "material");
        var spec = new ComponentSubcategorySpec { Source = source, Name = ModelFamilyJson.Str(obj, "name") };
        if (spec.Name.Length == 0) problems.Add($"{source}.name is required: the subcategory's name.");
        spec.LineWeightProjection = Weight(obj, "lineWeightProjection", source, problems);
        spec.LineWeightCut = Weight(obj, "lineWeightCut", source, problems);
        if (ModelFamilyJson.Get(obj, "color") is { } color)
        {
            if (color is not JArray rgb || rgb.Count != 3 || rgb.Any(c => ModelFamilyJson.AsNumber(c) is not { } v || v < 0 || v > 255 || Math.Abs(v - Math.Round(v)) > 1e-9))
                problems.Add($"{source}.color: must be [r, g, b] with whole numbers 0-255.");
            else spec.Color = rgb.Select(c => (int)Math.Round(ModelFamilyJson.AsNumber(c)!.Value)).ToArray();
        }
        if (ModelFamilyJson.Has(obj, "linePattern")) spec.LinePattern = ModelFamilyJson.Str(obj, "linePattern");
        if (ModelFamilyJson.Has(obj, "material")) spec.Material = ModelFamilyJson.Str(obj, "material");
        return problems.Count > before ? null : spec;
    }

    private static int? Weight(JObject obj, string key, string source, List<string> problems)
    {
        var number = ModelFamilyJson.Number(obj, key, source, problems);
        if (number == null) return null;
        if (Math.Abs(number.Value - Math.Round(number.Value)) > 1e-9 || number < 1 || number > 16)
        {
            problems.Add($"{source}.{key}: must be a whole line weight number from 1 to 16.");
            return null;
        }
        return (int)Math.Round(number.Value);
    }

    private static ComponentNestedFamilySpec? ParseNestedFamily(JObject obj, string source, List<string> problems)
    {
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, "path", "fromProject", "ifExists");
        var spec = new ComponentNestedFamilySpec
        {
            Source = source,
            Path = ModelFamilyJson.Str(obj, "path"),
            FromProject = ModelFamilyJson.Str(obj, "fromProject")
        };
        if (spec.Path.Length == 0 == (spec.FromProject.Length == 0))
            problems.Add($"{source}: give exactly one of path (an .rfa file) and fromProject (the name of a family loaded in the project).");
        else if (spec.Path.Length > 0 && !spec.Path.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            problems.Add($"{source}.path: must be a .rfa family file.");
        if (!FamilyLoadPolicy.TryParseIfExists(ModelFamilyJson.Str(obj, "ifExists"), FamilyIfExists.Skip, out var policy, out var error))
            problems.Add($"{source}.{error}");
        spec.IfExists = policy;
        return problems.Count > before ? null : spec;
    }

    /// <summary>The names <c>instanceReference</c> accepts, as Revit's FamilyInstanceReferenceType members.</summary>
    public static readonly string[] InstanceReferenceNames =
    {
        "Left", "CenterLeftRight", "Right", "Front", "CenterFrontBack", "Back", "Bottom", "CenterElevation", "Top", "StrongReference", "WeakReference"
    };

    /// <summary>"centerLeftRight", "center_left_right" and "CenterLeftRight" all mean the same member; null when it is none of them.</summary>
    public static string? NormalizeInstanceReference(string? text)
    {
        var key = (text ?? string.Empty).Trim().Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
        return InstanceReferenceNames.FirstOrDefault(n => string.Equals(n, key, StringComparison.OrdinalIgnoreCase));
    }

    private static ComponentInstanceSpec? ParseInstance(JObject obj, string source, List<string> problems)
    {
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, "family", "type", "position", "rotation", "view", "workPlane", "parameters",
            "associate", "typeParameter", "typeParameterInstance", "visibleParameter", "visibility", "lockTo", "name");
        var spec = new ComponentInstanceSpec
        {
            Source = source,
            Family = ModelFamilyJson.Get(obj, "family"),
            Type = ModelFamilyJson.Str(obj, "type"),
            View = ModelFamilyJson.Get(obj, "view"),
            WorkPlane = ModelFamilyJson.Get(obj, "workPlane"),
            TypeParameter = ModelFamilyJson.Str(obj, "typeParameter"),
            Name = ModelFamilyJson.Str(obj, "name")
        };
        if (spec.Family == null || ModelFamilyJson.Str(obj, "family").Length == 0)
            problems.Add($"{source}.family is required: the name (or element id) of a nested family loaded in the family, for example after nestedFamilies.");
        spec.Position = ModelFamilyJson.Point(obj, "position", source, problems, required: false) ?? new double[3];
        spec.RotationDegrees = ModelFamilyJson.Number(obj, "rotation", source, problems) ?? 0;
        spec.TypeParameterInstance = ModelFamilyJson.Bool(obj, "typeParameterInstance", source, problems) ?? false;
        if (spec.View != null && spec.WorkPlane != null)
            problems.Add($"{source}: give view (for an annotation, detail or symbol family) or workPlane (for a model family), not both.");
        if (ModelFamilyJson.Get(obj, "parameters") is { } parameters)
        {
            if (parameters is JObject map) spec.Parameters = map;
            else problems.Add($"{source}.parameters: must be an object of nested instance parameter name to value.");
        }
        if (ModelFamilyJson.Get(obj, "associate") is { } associate)
        {
            if (associate is not JObject map) problems.Add($"{source}.associate: must be an object of nested parameter (name or BuiltInParameter) to family parameter name.");
            else
                foreach (var property in map.Properties())
                {
                    var target = property.Value.Type == JTokenType.String ? ((string?)property.Value)?.Trim() ?? string.Empty : string.Empty;
                    if (target.Length == 0) problems.Add($"{source}.associate.{property.Name}: give the name of a family parameter.");
                    else spec.Associate.Add(new KeyValuePair<string, string>(property.Name, target));
                }
        }
        if (spec.TypeParameter.Length == 0 && ModelFamilyJson.Has(obj, "typeParameter"))
            problems.Add($"{source}.typeParameter: give the name of a family-type parameter.");
        spec.Visibility = ComponentVisibilityRequest.ParseInline(obj, source, problems);
        if (ModelFamilyJson.Get(obj, "lockTo") is { } lockTo)
        {
            var items = lockTo is JArray array ? array.ToList() : new List<JToken> { lockTo };
            for (var i = 0; i < items.Count; i++)
            {
                var at = $"{source}.lockTo[{i}]";
                if (items[i] is not JObject item)
                {
                    problems.Add($"{at}: must be {{\"reference\":\"<reference plane>\",\"instanceReference\":\"centerLeftRight\"}}.");
                    continue;
                }
                ModelFamilyJson.CheckKeys(item, at, problems, "reference", "instanceReference");
                var reference = ModelFamilyJson.Get(item, "reference");
                if (reference == null) problems.Add($"{at}.reference is required: a reference plane name or id.");
                var named = ModelFamilyJson.Str(item, "instanceReference");
                var normalized = NormalizeInstanceReference(named);
                if (normalized == null)
                    problems.Add($"{at}.instanceReference: '{named}' is not a reference of an instance. Use one of {string.Join(", ", InstanceReferenceNames)}.");
                else if (reference != null)
                    spec.LockTo.Add(new ComponentLockSpec { Reference = reference, InstanceReference = normalized, Source = at });
            }
        }
        return problems.Count > before ? null : spec;
    }

    private static ComponentLineGroupSpec? ParseLineGroup(JObject obj, string source, bool symbolic, List<string> problems)
    {
        var before = problems.Count;
        if (symbolic) ModelFamilyJson.CheckKeys(obj, source, problems, "view", "curves", "subcategory", "visibility", "visibleParameter");
        else ModelFamilyJson.CheckKeys(obj, source, problems, "workPlane", "curves", "subcategory", "visibility", "visibleParameter");
        var group = new ComponentLineGroupSpec
        {
            Source = source,
            View = symbolic ? ModelFamilyJson.Get(obj, "view") : null,
            WorkPlane = symbolic ? null : ModelFamilyJson.Get(obj, "workPlane")
        };
        if (ModelFamilyJson.Has(obj, "subcategory")) group.Subcategory = ModelFamilyJson.Str(obj, "subcategory");
        var curves = ModelFamilyJson.Get(obj, "curves");
        if (curves is not JArray list || list.Count == 0)
            problems.Add($"{source}.curves is required: a list of {{\"line\":{{start,end}}}}, {{\"arc\":{{start,mid,end}}}} or {{\"circle\":{{center,radius}}}} (millimetres).");
        else
            for (var i = 0; i < list.Count; i++)
                if (ComponentCurveSpec.Parse(list[i], $"{source}.curves[{i}]", problems) is { } curve) group.Curves.Add(curve);
        group.Visibility = ComponentVisibilityRequest.ParseInline(obj, source, problems);
        return problems.Count > before ? null : group;
    }

    private static ComponentSetSubcategorySpec? ParseSetSubcategory(JObject obj, string source, IReadOnlyDictionary<string, int> counts, List<string> problems)
    {
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, "elements", "subcategory");
        var spec = new ComponentSetSubcategorySpec { Source = source };
        spec.Elements = ModelFamilyComponentRefs.Parse(ModelFamilyJson.Get(obj, "elements"), $"{source}.elements", problems, counts);
        if (!obj.Properties().Any(p => string.Equals(p.Name, "subcategory", StringComparison.OrdinalIgnoreCase)))
            problems.Add($"{source}.subcategory is required: a subcategory name, or null for the family category itself.");
        else
        {
            var name = ModelFamilyJson.Get(obj, "subcategory") == null ? null : ModelFamilyJson.Str(obj, "subcategory");
            if (name != null && name.Length == 0) problems.Add($"{source}.subcategory: must not be empty; use null for the family category itself.");
            spec.Subcategory = name;
        }
        return problems.Count > before ? null : spec;
    }

    private static ComponentVisibilitySpec? ParseVisibility(JObject obj, string source, IReadOnlyDictionary<string, int> counts, List<string> problems)
    {
        var before = problems.Count;
        ModelFamilyJson.CheckKeys(obj, source, problems, "elements", "visible", "visibleParameter", "detailLevels", "views");
        var spec = new ComponentVisibilitySpec { Source = source };
        spec.Elements = ModelFamilyComponentRefs.Parse(ModelFamilyJson.Get(obj, "elements"), $"{source}.elements", problems, counts);
        var request = ComponentVisibilityRequest.Parse(obj, source, problems);
        if (request == null && problems.Count == before)
            problems.Add($"{source}: say what to change: visible, visibleParameter, detailLevels or views.");
        spec.Request = request ?? new ComponentVisibilityRequest();
        return problems.Count > before ? null : spec;
    }
}

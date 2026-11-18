using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ModelFamilies;

/// <summary>How a material name matched the materials of the family.</summary>
public enum ModelFamilyMaterialMatch
{
    /// <summary>The name is the material's name, character for character.</summary>
    Exact,
    /// <summary>Only the capitalisation differs, and exactly one material matches.</summary>
    CaseInsensitive,
    /// <summary>Several materials differ from the name only by capitalisation.</summary>
    Ambiguous,
    Missing
}

/// <summary>The outcome of looking a material name up, with what the caller needs to explain it.</summary>
public sealed class ModelFamilyMaterialResolution
{
    public ModelFamilyMaterialMatch Match { get; init; }
    public long? Id { get; init; }
    public string? Name { get; init; }
    /// <summary>Every material the name could mean (several when ambiguous).</summary>
    public List<(long Id, string Name)> Candidates { get; init; } = new();
    /// <summary>Close names for a missing material.</summary>
    public List<string> Suggestions { get; init; } = new();

    public bool Found => Match is ModelFamilyMaterialMatch.Exact or ModelFamilyMaterialMatch.CaseInsensitive;
}

/// <summary>
/// The materials of the family as the tools see them, and the rule that decides which one a name means (#173): an
/// exact match wins; otherwise a single case-insensitive match is used with a warning; several are ambiguous; none is
/// missing. Pure; the Revit side only fills the list. Materials created earlier in the same call are added with
/// <see cref="Add"/>, so a later list resolves them.
/// </summary>
public sealed class ModelFamilyMaterialCatalog
{
    private readonly List<(long Id, string Name)> _materials;

    public ModelFamilyMaterialCatalog(IEnumerable<(long Id, string Name)> materials) => _materials = materials.ToList();

    public IReadOnlyList<(long Id, string Name)> Materials => _materials;

    public void Add(long id, string name) => _materials.Add((id, name));

    public (long Id, string Name)? ById(long id)
    {
        foreach (var material in _materials)
            if (material.Id == id) return material;
        return null;
    }

    public ModelFamilyMaterialResolution Resolve(string wanted) => Resolve(wanted, _materials);

    /// <summary>Applies the resolution rule to <paramref name="wanted"/> over <paramref name="known"/>.</summary>
    public static ModelFamilyMaterialResolution Resolve(string wanted, IReadOnlyList<(long Id, string Name)> known)
    {
        var name = wanted.Trim();
        var exact = known.Where(m => string.Equals(m.Name, name, StringComparison.Ordinal)).ToList();
        if (exact.Count == 1)
            return new ModelFamilyMaterialResolution { Match = ModelFamilyMaterialMatch.Exact, Id = exact[0].Id, Name = exact[0].Name, Candidates = exact };
        if (exact.Count > 1)
            return new ModelFamilyMaterialResolution { Match = ModelFamilyMaterialMatch.Ambiguous, Candidates = exact };

        var loose = known.Where(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (loose.Count == 1)
            return new ModelFamilyMaterialResolution { Match = ModelFamilyMaterialMatch.CaseInsensitive, Id = loose[0].Id, Name = loose[0].Name, Candidates = loose };
        if (loose.Count > 1)
            return new ModelFamilyMaterialResolution { Match = ModelFamilyMaterialMatch.Ambiguous, Candidates = loose };

        return new ModelFamilyMaterialResolution
        {
            Match = ModelFamilyMaterialMatch.Missing,
            Suggestions = ModelFamilyJson.Suggest(name, known.Select(m => m.Name))
        };
    }

    /// <summary>
    /// The problem a resolution is for the caller, or null when it found a material. <paramref name="source"/> is the
    /// argument path (for example <c>assign[0].material</c>).
    /// </summary>
    public static string? ProblemOf(ModelFamilyMaterialResolution resolution, string wanted, string source, string where = "the family")
    {
        switch (resolution.Match)
        {
            case ModelFamilyMaterialMatch.Ambiguous:
                return $"{source}: '{wanted}' is ambiguous; {resolution.Candidates.Count} materials match " +
                       $"({string.Join(", ", resolution.Candidates.Select(c => $"'{c.Name}' id {c.Id}"))}). Pass the element id of the one you mean.";
            case ModelFamilyMaterialMatch.Missing:
                return $"{source}: {where} has no material '{wanted}'" +
                       (resolution.Suggestions.Count > 0 ? $"; did you mean {string.Join(", ", resolution.Suggestions.Select(n => $"'{n}'"))}?" : ".") +
                       (where == "the family"
                           ? " Create it in the materials list of this call (or copy it with copyFromProject); revit_get_model_family section materials lists the family's materials."
                           : string.Empty);
            default:
                return null;
        }
    }

    /// <summary>The warning a case-only match is, or null.</summary>
    public static string? WarningOf(ModelFamilyMaterialResolution resolution, string wanted, string source) =>
        resolution.Match == ModelFamilyMaterialMatch.CaseInsensitive
            ? $"{source}: no material is called exactly '{wanted}'; used '{resolution.Name}' (id {resolution.Id}), which differs only in capitalisation."
            : null;

    /// <summary>
    /// Materials whose name differs from <paramref name="name"/> only by capitalisation: Revit allows both, but the
    /// pair is almost always a mistake ("Steel" and "steel").
    /// </summary>
    public List<string> NearDuplicates(string name) =>
        _materials.Select(m => m.Name)
            .Where(n => !string.Equals(n, name, StringComparison.Ordinal) && string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>The warning for creating a material that nearly duplicates another, or null.</summary>
    public static string? NearDuplicateWarning(string source, string name, IReadOnlyList<string> near) =>
        near.Count == 0 ? null
            : $"{source}.name: '{name}' differs only in capitalisation from {string.Join(", ", near.Select(n => $"'{n}'"))}. " +
              "Revit allows both, but they are easily confused; edit the existing material instead if that is what you meant.";
}

/// <summary>Names of the form and element kinds in messages, and the description of an element's material state.</summary>
public static class ModelFamilyMaterialState
{
    public const string Direct = "direct";
    public const string Parameter = "parameter";
    public const string ByCategory = "byCategory";

    /// <summary>
    /// Where an element's material comes from: a family parameter drives it, it is set directly, or it is empty
    /// (By Category). <paramref name="materialId"/> is the element id of the material, or a value below 1 for none.
    /// </summary>
    public static string SourceOf(string? drivingParameter, long materialId) =>
        !string.IsNullOrEmpty(drivingParameter) ? Parameter : materialId > 0 ? Direct : ByCategory;

    /// <summary>The final-state row of an element in a result or an inspection section.</summary>
    public static JObject Describe(long elementId, string description, string? drivingParameter, bool? parameterIsInstance,
        long materialId, string? materialName)
    {
        var row = new JObject
        {
            ["id"] = elementId,
            ["description"] = description,
            ["materialSource"] = SourceOf(drivingParameter, materialId),
            ["materialId"] = materialId > 0 ? new JValue(materialId) : JValue.CreateNull(),
            ["material"] = materialId > 0 ? new JValue(materialName) : JValue.CreateNull(),
            ["parameter"] = string.IsNullOrEmpty(drivingParameter) ? JValue.CreateNull() : new JValue(drivingParameter)
        };
        if (!string.IsNullOrEmpty(drivingParameter))
            row["parameterIsInstance"] = parameterIsInstance;
        return row;
    }

    /// <summary>"extrusion", "blend", ... for a Revit element type name; the type name itself for anything else.</summary>
    public static string KindOf(string revitTypeName) => revitTypeName switch
    {
        "Extrusion" => "extrusion",
        "Blend" => "blend",
        "Revolution" => "revolution",
        "Sweep" => "sweep",
        "SweptBlend" => "swept blend",
        "ImportInstance" => "imported CAD instance",
        "FamilyInstance" => "nested family instance",
        _ => revitTypeName
    };

    /// <summary>The problem for an element with no material parameter, naming its kind and what has one.</summary>
    public static string NoMaterialParameter(string source, long elementId, string revitTypeName) =>
        $"{source}: element {elementId} is a {KindOf(revitTypeName)} and has no Material property. " +
        "Forms (extrusion, blend, revolution, sweep, swept blend) have one; a void form usually does not.";
}

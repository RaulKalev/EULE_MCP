using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ModelFamilies;

namespace RevitMCP.Addin.Tools.ModelFamilies;

/// <summary>
/// The Revit side of the material tools (#173): the family's materials as a <see cref="ModelFamilyMaterialCatalog"/>,
/// the elements that have a Material property, and the state of one such element. Reads only.
/// </summary>
internal static class ModelFamilyMaterialRevit
{
    private static readonly Type[] FormTypes =
    {
        typeof(Extrusion), typeof(Blend), typeof(Revolution), typeof(Sweep), typeof(SweptBlend)
    };

    public static ModelFamilyMaterialCatalog Catalog(Document doc) =>
        new(new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>().Select(m => (m.Id.Value, m.Name)));

    public static Material? MaterialById(Document doc, long id) => id > 0 ? doc.GetElement(new ElementId(id)) as Material : null;

    public static string MaterialName(Document doc, long id) => MaterialById(doc, id)?.Name ?? string.Empty;

    /// <summary>The forms of the family that have a Material property, in element id order.</summary>
    public static List<Element> FormsWithMaterial(Document doc) =>
        FormTypes.SelectMany(t => new FilteredElementCollector(doc).OfClass(t).ToElements())
            .Where(e => MaterialParameter(e) != null)
            .OrderBy(e => e.Id.Value)
            .ToList();

    /// <summary>The Material property of an element (BuiltInParameter.MATERIAL_ID_PARAM), or null when it has none.</summary>
    public static Parameter? MaterialParameter(Element element)
    {
        try { return element.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM); }
        catch { return null; }
    }

    /// <summary>True when the family parameter holds a material (Reference.Material).</summary>
    public static bool IsMaterialParameter(FamilyParameter parameter)
    {
        try
        {
            var type = parameter.Definition.GetDataType();
            return type != null && (type.Equals(SpecTypeId.Reference.Material) ||
                                    (type.TypeId ?? string.Empty).StartsWith("autodesk.spec.aec:reference.material", StringComparison.Ordinal));
        }
        catch { return false; }
    }

    /// <summary>The element's material source, material and driving family parameter, read live.</summary>
    public static JObject? State(Document doc, Element element)
    {
        var parameter = MaterialParameter(element);
        if (parameter == null) return null;
        var driving = ModelFamilyAssociations.Associated(doc, parameter);
        var materialId = SafeId(parameter);
        return ModelFamilyMaterialState.Describe(element.Id.Value, ModelFamilyLookup.Describe(element),
            driving?.Definition.Name, driving?.IsInstance, materialId, MaterialName(doc, materialId));
    }

    /// <summary>The shorter before/after row of an element: where the material comes from and which it is.</summary>
    public static JObject Brief(Document doc, Element element)
    {
        var state = State(doc, element);
        return new JObject
        {
            ["materialSource"] = state?["materialSource"],
            ["material"] = state?["material"],
            ["parameter"] = state?["parameter"]
        };
    }

    public static long SafeId(Parameter parameter)
    {
        try { return parameter.AsElementId().Value; }
        catch { return -1; }
    }

    /// <summary>The editable properties of a material, for before/after.</summary>
    public static JObject Snapshot(Material material)
    {
        var color = material.Color;
        return new JObject
        {
            ["color"] = color != null && color.IsValid ? new JArray((int)color.Red, (int)color.Green, (int)color.Blue) : null,
            ["transparency"] = material.Transparency,
            ["shininess"] = material.Shininess,
            ["smoothness"] = material.Smoothness,
            ["materialClass"] = material.MaterialClass
        };
    }
}

/// <summary>Keeps the destination's materials when a copy from the project meets one of the same name.</summary>
internal sealed class ModelFamilyMaterialKeepDestination : IDuplicateTypeNamesHandler
{
    public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args) =>
        DuplicateTypeAction.UseDestinationTypes;
}

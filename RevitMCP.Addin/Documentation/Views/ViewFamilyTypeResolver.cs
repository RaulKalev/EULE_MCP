using System.Globalization;
using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Documentation.Views;

/// <summary>
/// The one way to pick a <see cref="ViewFamilyType"/> (#104): an exact name (or element id) when given, else the
/// document's default type for the family (<c>Document.GetDefaultElementTypeId(ElementTypeGroup.ViewType…)</c>), else
/// the first type of the family. Used by revit_create_views, the clash review view and the IFC boundary plans.
/// </summary>
public static class ViewFamilyTypeResolver
{
    /// <summary>How a type was chosen, for results.</summary>
    public const string ByName = "name";
    public const string ById = "id";
    public const string ByDocumentDefault = "documentDefault";
    public const string ByFirst = "first";

    /// <summary>Every view family type of a family, in collector order; all when <paramref name="family"/> is null.</summary>
    public static List<ViewFamilyType> OfFamily(Document doc, ViewFamily? family) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .Where(t => family == null || t.ViewFamily == family.Value)
            .ToList();

    /// <summary>The element type group holding the document default for a family; null when Revit keeps none (area plans).</summary>
    public static ElementTypeGroup? DefaultGroup(ViewFamily family) => family switch
    {
        ViewFamily.FloorPlan => ElementTypeGroup.ViewTypeFloorPlan,
        ViewFamily.CeilingPlan => ElementTypeGroup.ViewTypeCeilingPlan,
        ViewFamily.StructuralPlan => ElementTypeGroup.ViewTypeStructuralPlan,
        ViewFamily.Section => ElementTypeGroup.ViewTypeSection,
        ViewFamily.Detail => ElementTypeGroup.ViewTypeDetailView,
        ViewFamily.ThreeDimensional => ElementTypeGroup.ViewType3D,
        ViewFamily.Drafting => ElementTypeGroup.ViewTypeDrafting,
        ViewFamily.Elevation => ElementTypeGroup.ViewTypeElevation,
        ViewFamily.Legend => ElementTypeGroup.ViewTypeLegend,
        ViewFamily.Walkthrough => ElementTypeGroup.ViewTypeWalkthrough,
        ViewFamily.Schedule => ElementTypeGroup.ViewTypeSchedule,
        ViewFamily.Sheet => ElementTypeGroup.ViewTypeSheet,
        _ => null
    };

    /// <summary>The document's default type of the family, or null.</summary>
    public static ViewFamilyType? DocumentDefault(Document doc, ViewFamily family)
    {
        var group = DefaultGroup(family);
        if (group == null) return null;
        try
        {
            var id = doc.GetDefaultElementTypeId(group.Value);
            return id != null && id != ElementId.InvalidElementId && doc.GetElement(id) is ViewFamilyType type && type.ViewFamily == family
                ? type
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The type to use. <paramref name="nameOrId"/> given: an exact (case-insensitive) name, then an element id of a
    /// type of the family; not found is an error naming the available types. Not given: the document default, then
    /// the first type. Null with <paramref name="error"/> set when the family has no type at all.
    /// </summary>
    public static ViewFamilyType? Resolve(Document doc, ViewFamily family, string? nameOrId, out string? error, out string how)
    {
        error = null;
        how = ByFirst;
        var types = OfFamily(doc, family);
        if (!string.IsNullOrWhiteSpace(nameOrId))
        {
            var wanted = nameOrId!.Trim();
            var byName = types.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.Ordinal))
                         ?? types.FirstOrDefault(t => string.Equals(t.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (byName != null)
            {
                how = ByName;
                return byName;
            }
            if (long.TryParse(wanted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                doc.GetElement(new ElementId(id)) is ViewFamilyType byId)
            {
                if (byId.ViewFamily == family)
                {
                    how = ById;
                    return byId;
                }
                error = $"View family type {id} ('{byId.Name}') is a {byId.ViewFamily} type, not {family}. {Describe(types, family)}";
                return null;
            }
            error = $"No {family} view family type '{wanted}'. {Describe(types, family)}";
            return null;
        }

        var preferred = DocumentDefault(doc, family);
        if (preferred != null)
        {
            how = ByDocumentDefault;
            return preferred;
        }
        if (types.Count > 0) return types[0];
        error = $"The document has no {family} view family type.";
        return null;
    }

    /// <summary>Resolve with no name: the document default, else the first. Null when the family has no type.</summary>
    public static ViewFamilyType? ResolveDefault(Document doc, ViewFamily family) => Resolve(doc, family, null, out _, out _);

    /// <summary>"Types: A, B, C." for error messages.</summary>
    public static string Describe(IReadOnlyCollection<ViewFamilyType> types, ViewFamily family) =>
        types.Count == 0
            ? $"The document has no {family} type."
            : $"{family} types: {string.Join(", ", types.Take(25).Select(t => $"'{t.Name}' ({t.Id.Value})"))}{(types.Count > 25 ? ", …" : string.Empty)}.";

    /// <summary>The view family of the type a view uses; Invalid when it has none.</summary>
    public static ViewFamily FamilyOf(View view)
    {
        try
        {
            return view.Document.GetElement(view.GetTypeId()) is ViewFamilyType type ? type.ViewFamily : ViewFamily.Invalid;
        }
        catch
        {
            return ViewFamily.Invalid;
        }
    }
}

using Autodesk.Revit.DB;
using RevitMCP.Addin.Query;

namespace RevitMCP.Addin.Tools;

/// <summary>
/// Revit adapter for <see cref="ParameterResolution"/>: lists an element's (and optionally its type's)
/// parameters as candidates and resolves which one a caller means. Write tools use this instead of a
/// first-partial-match scan so a short name never lands in a different built-in parameter.
/// </summary>
public static class ParameterResolver
{
    public static ParameterResolution Resolve(
        Document doc, Element element, ParameterTarget selector, bool includeInstance = true, bool includeType = false)
    {
        var candidates = new List<ParameterCandidate>();
        if (includeInstance)
            AddCandidates(candidates, element, isType: false);

        if (includeType)
        {
            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId && doc.GetElement(typeId) is Element typeElement)
                AddCandidates(candidates, typeElement, isType: true);
        }

        return ParameterResolution.Resolve(candidates, selector);
    }

    public static Parameter? Selected(ParameterResolution resolution) => resolution.Selected?.Tag as Parameter;

    /// <summary>Display value of a parameter, falling back to its raw stored value.</summary>
    public static string? DisplayValue(Parameter p)
    {
        try
        {
            if (!p.HasValue) return null;
            return p.StorageType switch
            {
                StorageType.String => p.AsString(),
                StorageType.Integer => p.AsValueString() ?? p.AsInteger().ToString(),
                StorageType.Double => p.AsValueString() ?? p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
                StorageType.ElementId => p.AsValueString() ?? p.AsElementId().Value.ToString(),
                _ => p.AsValueString()
            };
        }
        catch
        {
            return null;
        }
    }

    private static void AddCandidates(List<ParameterCandidate> into, Element element, bool isType)
    {
        foreach (Parameter p in element.Parameters)
        {
            var definition = p.Definition;
            if (definition == null) continue;

            string? builtIn = null;
            if (definition is InternalDefinition internalDefinition &&
                internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
                builtIn = internalDefinition.BuiltInParameter.ToString();

            string? guid = null;
            try { if (p.IsShared) guid = p.GUID.ToString(); } catch { }

            into.Add(new ParameterCandidate
            {
                Name = definition.Name ?? string.Empty,
                BuiltInParameter = builtIn,
                Guid = guid,
                StorageType = p.StorageType.ToString(),
                IsReadOnly = p.IsReadOnly,
                IsTypeParameter = isType,
                Tag = p
            });
        }
    }
}

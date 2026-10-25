using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Query;

/// <summary>
/// Reads instance and type parameters from Revit elements.
/// Type parameters are cached by type element ID to avoid redundant reads.
/// Must be called from the Revit API thread.
/// </summary>
public class ParameterReader
{
    private readonly Dictionary<ElementId, List<ParameterValueDto>> _typeCache = new();

    public IReadOnlyList<ParameterValueDto> ReadParameters(Document doc, Element element, ParameterReadOptions options)
    {
        var results = new List<ParameterValueDto>();

        if (options.IncludeInstanceParameters)
            results.AddRange(ReadFromElement(element, "Instance", null, options));

        if (options.IncludeTypeParameters)
        {
            var typeId = element.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                foreach (var parameter in GetTypeParameters(doc, typeId))
                {
                    if (ShouldInclude(parameter.Name, "Type", options))
                        results.Add(parameter);
                }
            }
        }

        return results;
    }

    private List<ParameterValueDto> GetTypeParameters(Document doc, ElementId typeId)
    {
        if (!_typeCache.TryGetValue(typeId, out var cached))
        {
            var typeElement = doc.GetElement(typeId);
            cached = typeElement != null
                ? ReadFromElement(typeElement, "Type", typeId.Value, null)
                : [];
            _typeCache[typeId] = cached;
        }
        return cached;
    }

    private static List<ParameterValueDto> ReadFromElement(
        Element element,
        string scope,
        long? typeElementId,
        ParameterReadOptions? options)
    {
        var result = new List<ParameterValueDto>();
        try
        {
            foreach (Parameter p in element.Parameters)
            {
                try
                {
                    var name = p.Definition?.Name ?? string.Empty;
                    if (options != null && !ShouldInclude(name, scope, options))
                        continue;

                    result.Add(BuildDto(p, scope, typeElementId));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static bool ShouldInclude(string parameterName, string scope, ParameterReadOptions options)
    {
        if (options.ParameterSelectors.Count > 0)
            return options.ParameterSelectors.Any(selector => selector.Matches(parameterName, scope));

        if (options.ParameterNames.Count > 0)
        {
            return options.ParameterNames.Any(name =>
                ParameterMatcher.Matches(parameterName, name, options.ParameterNameMatchMode));
        }

        return true;
    }

    private static ParameterValueDto BuildDto(Parameter p, string scope, long? typeElementId)
    {
        var name = p.Definition?.Name ?? string.Empty;

        string value;
        string? unit = null;
        object? rawValue = null;
        switch (p.StorageType)
        {
            case StorageType.String:
                rawValue = p.AsString();
                value = p.AsString() ?? string.Empty;
                break;
            case StorageType.Integer:
                rawValue = p.AsInteger();
                value = p.AsValueString() is { Length: > 0 } vs ? vs : p.AsInteger().ToString();
                break;
            case StorageType.Double:
                rawValue = p.AsDouble();
                value = p.AsValueString() is { Length: > 0 } vs2 ? vs2 : p.AsDouble().ToString("F4");
                // X3: lengths had no unit. Value stays the plain number (filters compare it); Unit carries the symbol.
                unit = UnitOf(p, value);
                break;
            case StorageType.ElementId:
                // X3: the value was the raw id ("Level": "2123366"). It is the name now; the id stays in RawValue,
                // and filters still match either.
                var elementId = p.AsElementId();
                var eid = elementId?.Value;
                rawValue = eid;
                value = ElementIdText(p, elementId);
                break;
            default:
                value = string.Empty;
                break;
        }

        string? builtInName = null;
        try
        {
            if (p.Id.Value < 0)
                builtInName = ((BuiltInParameter)p.Id.Value).ToString();
        }
        catch { }

        return new ParameterValueDto
        {
            Name = name,
            NormalizedName = ParameterMatcher.Normalize(name),
            Value = value,
            RawValue = rawValue,
            Unit = unit,
            StorageType = p.StorageType.ToString(),
            Scope = scope,
            IsReadOnly = p.IsReadOnly,
            IsShared = p.IsShared,
            Guid = p.IsShared ? p.GUID.ToString() : null,
            ParameterId = p.Id.Value,
            BuiltInParameterName = builtInName,
            TypeElementId = typeElementId
        };
    }

    /// <summary>
    /// The unit symbol of a measured value in the project's units ("mm" for "3200"): Revit formats it once with the
    /// symbol, and the symbol is what follows the plain text. Null when the value is not measurable or has no symbol.
    /// </summary>
    private static string? UnitOf(Parameter p, string plain)
    {
        try
        {
            var spec = p.Definition?.GetDataType();
            if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return null;
            var withSymbol = UnitFormatUtils.Format(p.Element.Document.GetUnits(), spec, p.AsDouble(), false,
                new FormatValueOptions { AppendUnitSymbol = true });
            if (string.IsNullOrWhiteSpace(withSymbol) || !withSymbol.StartsWith(plain, StringComparison.Ordinal)) return null;
            var symbol = withSymbol.Substring(plain.Length).Trim();
            return symbol.Length > 0 ? symbol : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The name an element id stands for: Revit's display text, the element's or category's name, else the id.</summary>
    private static string ElementIdText(Parameter p, ElementId? id)
    {
        if (id == null || id == ElementId.InvalidElementId) return string.Empty;
        try
        {
            if (p.AsValueString() is { Length: > 0 } shown && shown != id.Value.ToString()) return shown;
            var doc = p.Element.Document;
            if (doc.GetElement(id) is { } element && !string.IsNullOrEmpty(element.Name)) return element.Name;
            if (id.Value < 0 && Category.GetCategory(doc, id) is { } category) return category.Name;
        }
        catch
        {
            // fall through to the id
        }
        return id.Value.ToString();
    }

    /// <summary>
    /// Resolves the element's family and type names from its <see cref="ElementType"/>
    /// (or the element itself when it is a type). Backs the Type / Family / Family and Type
    /// filter pseudo-parameters, whose built-in counterparts are ElementId parameters.
    /// </summary>
    public static ElementIdentity ReadIdentity(Document doc, Element element)
    {
        try
        {
            ElementType? type = element as ElementType;
            if (type == null)
            {
                var typeId = element.GetTypeId();
                if (typeId != null && typeId != ElementId.InvalidElementId)
                    type = doc.GetElement(typeId) as ElementType;
            }

            if (type == null)
                return ElementIdentity.Empty;

            var familyName = type.FamilyName;
            if (string.IsNullOrEmpty(familyName) && type is FamilySymbol symbol)
                familyName = symbol.Family?.Name ?? string.Empty;

            return new ElementIdentity
            {
                FamilyName = familyName ?? string.Empty,
                TypeName = type.Name ?? string.Empty
            };
        }
        catch
        {
            return ElementIdentity.Empty;
        }
    }

    public void ClearCache() => _typeCache.Clear();
}

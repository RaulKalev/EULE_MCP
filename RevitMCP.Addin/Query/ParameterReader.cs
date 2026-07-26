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
                break;
            case StorageType.ElementId:
                var eid = p.AsElementId()?.Value;
                rawValue = eid;
                value = eid?.ToString() ?? string.Empty;
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

    public void ClearCache() => _typeCache.Clear();
}

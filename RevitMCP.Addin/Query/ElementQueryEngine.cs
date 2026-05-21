using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace RevitMCP.Addin.Query;

/// <summary>
/// Central query entry point. Must be called from the Revit API thread.
/// </summary>
public class ElementQueryEngine
{
    private readonly CategoryResolver _categoryResolver = new();

    public ElementQueryResult Query(Document doc, UIDocument uidoc, ElementQueryOptions options)
    {
        // --- 1. Determine element source ---
        IEnumerable<ElementId> elementIds;

        if (options.UseSelection)
        {
            var sel = uidoc.Selection.GetElementIds();
            elementIds = sel;
        }
        else if (options.ElementIds.Count > 0)
        {
            elementIds = options.ElementIds.Select(id => new ElementId(id));
        }
        else
        {
            var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();

            if (!string.IsNullOrWhiteSpace(options.Category))
            {
                var resolve = _categoryResolver.Resolve(doc, options.Category);
                if (resolve.Category == null)
                {
                    var sug = resolve.Suggestions.Count > 0
                        ? $" Did you mean: {string.Join(", ", resolve.Suggestions)}?"
                        : string.Empty;
                    return ElementQueryResult.Failure(resolve.Message + sug);
                }
                collector = collector.OfCategoryId(resolve.Category.Id);
            }

            elementIds = collector.ToElementIds();
        }

        // --- 2. Scan & filter ---
        var reader = new ParameterReader();
        var readOpts = new ParameterReadOptions
        {
            IncludeInstanceParameters = options.IncludeInstanceParameters,
            IncludeTypeParameters = options.IncludeTypeParameters
        };

        var results = new List<ElementInfoDto>();
        var warnings = new List<string>();
        int totalMatched = 0;
        int totalScanned = 0;

        foreach (var elementId in elementIds)
        {
            totalScanned++;

            var element = doc.GetElement(elementId);
            if (element?.Category == null) continue;

            var allParams = reader.ReadParameters(doc, element, readOpts);

            if (!PassesFilters(allParams, options.Filters)) continue;

            totalMatched++;

            if (results.Count >= options.Limit) continue;

            // Build parameter map for response
            var paramMap = BuildParameterMap(allParams, options.ReturnParameters, options.ReturnParameterMatchMode);

            var typeId = element.GetTypeId();
            var typeElem = (typeId != null && typeId != ElementId.InvalidElementId) ? doc.GetElement(typeId) : null;

            var info = new ElementInfoDto
            {
                ElementId = element.Id.Value,
                UniqueId = element.UniqueId,
                Category = element.Category.Name,
                Name = element.Name,
                Type = typeElem?.Name ?? string.Empty,
                TypeElementId = typeElem?.Id.Value,
                Level = GetLevelName(doc, element),
                Parameters = paramMap
            };

            if (element is FamilyInstance fi)
                info.Family = fi.Symbol?.Family?.Name ?? string.Empty;

            results.Add(info);
        }

        if (totalScanned > 5000 && string.IsNullOrWhiteSpace(options.Category))
            warnings.Add($"Scanned {totalScanned} elements. Provide a 'category' to narrow the search.");

        if (totalMatched > options.Limit)
            warnings.Add($"Results capped at {options.Limit}. {totalMatched - options.Limit} additional matching elements exist.");

        return new ElementQueryResult
        {
            Success = true,
            Message = $"Matched {totalMatched} elements, returned {results.Count}.",
            Elements = results,
            TotalMatched = totalMatched,
            Warnings = warnings
        };
    }

    private static Dictionary<string, ParameterValueDto> BuildParameterMap(
        IReadOnlyList<ParameterValueDto> allParams,
        List<string> returnNames,
        string matchMode)
    {
        var map = new Dictionary<string, ParameterValueDto>(StringComparer.Ordinal);

        var filtered = returnNames.Count > 0
            ? allParams.Where(p => returnNames.Any(n => ParameterMatcher.Matches(p.Name, n, matchMode)))
            : allParams;

        foreach (var p in filtered)
        {
            var key = map.ContainsKey(p.Name)
                ? $"{p.Name} [{p.Scope}]"
                : p.Name;
            map[key] = p;
        }

        return map;
    }

    private static bool PassesFilters(IReadOnlyList<ParameterValueDto> parameters, List<ParameterFilterDto> filters)
    {
        foreach (var filter in filters)
        {
            var candidates = parameters.Where(p =>
                ScopeMatches(p.Scope, filter.Scope) &&
                ParameterMatcher.Matches(p.Name, filter.ParameterName, filter.MatchMode)
            ).ToList();

            if (candidates.Count == 0)
            {
                if (filter.Operator == "isEmpty") continue;
                return false;
            }

            if (!candidates.Any(p => EvaluateOperator(p.Value, filter.Operator, filter.Value)))
                return false;
        }
        return true;
    }

    private static bool ScopeMatches(string paramScope, string filterScope) =>
        filterScope switch
        {
            "Instance" => paramScope == "Instance",
            "Type" => paramScope == "Type",
            _ => true
        };

    private static bool EvaluateOperator(string value, string op, string filterValue) =>
        op switch
        {
            "equals" => string.Equals(value, filterValue, StringComparison.OrdinalIgnoreCase),
            "notEquals" => !string.Equals(value, filterValue, StringComparison.OrdinalIgnoreCase),
            "contains" => value.Contains(filterValue, StringComparison.OrdinalIgnoreCase),
            "notContains" => !value.Contains(filterValue, StringComparison.OrdinalIgnoreCase),
            "startsWith" => value.StartsWith(filterValue, StringComparison.OrdinalIgnoreCase),
            "endsWith" => value.EndsWith(filterValue, StringComparison.OrdinalIgnoreCase),
            "isEmpty" => string.IsNullOrEmpty(value),
            "isNotEmpty" => !string.IsNullOrEmpty(value),
            "greaterThan" => double.TryParse(value, out var v1) && double.TryParse(filterValue, out var f1) && v1 > f1,
            "lessThan" => double.TryParse(value, out var v2) && double.TryParse(filterValue, out var f2) && v2 < f2,
            _ => false
        };

    private static string GetLevelName(Document doc, Element element)
    {
        try
        {
            var lvlId = element.LevelId;
            if (lvlId != null && lvlId != ElementId.InvalidElementId)
                return (doc.GetElement(lvlId) as Level)?.Name ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }
}

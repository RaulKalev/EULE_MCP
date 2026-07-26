using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.Query;

/// <summary>
/// Central query entry point. Must be called from the Revit API thread.
/// </summary>
public class ElementQueryEngine
{
    private readonly CategoryResolver _categoryResolver = new();

    public ElementQueryResult Query(Document doc, UIDocument uidoc, ElementQueryOptions options, CancellationToken cancellationToken = default)
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

        // --- 2. Summary-only fast path ---
        var reader = new ParameterReader();
        var responseReadOpts = CreateResponseReadOptions(options);
        var filterReadOpts = CreateFilterReadOptions(options);

        if (options.SummaryOnly)
            return BuildSummaryResult(doc, elementIds, options, reader, filterReadOpts, cancellationToken);

        // --- 3. Resolve effective limits (clamp caller values against QueryLimits.Default) ---
        var (effectivePageSize, effectiveMaxParameters, effectiveTruncateLength) =
            QueryGuard.ResolveEffectiveLimits(
                options.PageSize, options.Limit,
                options.MaxParametersPerElement, options.TruncateStringLength);

        int page = Math.Max(0, options.Page);
        int pageStart = page * effectivePageSize; // 0-based index of first element on this page

        // --- 4. Scan & filter ---

        var results = new List<ElementInfoDto>();
        var warnings = new List<string>();
        int totalMatched = 0;
        int totalScanned = 0;
        bool scanTruncated = false;
        ElementTagIndex? tagIndex = null;

        bool isNarrowed = options.UseSelection || options.ElementIds.Count > 0 || !string.IsNullOrWhiteSpace(options.Category);
        // ReadParameters below is a cheap no-op when both flags are false (e.g. a caller
        // grouping only by Category/Family/Type/Level) — such calls only need the generous
        // backstop cap, not the tight unscoped one meant for genuinely expensive scans.
        bool needsParamRead = options.IncludeInstanceParameters || options.IncludeTypeParameters;

        // Warn when caller-supplied values were actually clamped
        var limits = QueryLimits.Default;
        if (options.PageSize > limits.MaxPageSize)
            warnings.Add($"Requested pageSize {options.PageSize} exceeded the maximum ({limits.MaxPageSize}); clamped to {effectivePageSize}.");
        if (options.MaxParametersPerElement > limits.MaxParametersPerElement)
            warnings.Add($"Requested maxParametersPerElement {options.MaxParametersPerElement} exceeded the maximum ({limits.MaxParametersPerElement}); clamped to {effectiveMaxParameters}.");
        if (options.TruncateStringLength > limits.MaxStringLength)
            warnings.Add($"Requested truncateStringLength {options.TruncateStringLength} exceeded the maximum ({limits.MaxStringLength}); clamped to {effectiveTruncateLength}.");

        foreach (var elementId in elementIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Hard scan cap — prevents an unbounded per-element parameter read from
            // freezing/crashing Revit on large models. Checked before incrementing so
            // totalScanned always equals the number of elements actually processed.
            // Unscoped queries that do read parameters get a much tighter cap; queries
            // that skip parameter reads entirely only need the generous backstop.
            if (QueryGuard.ShouldStopScan(totalScanned, isNarrowed, needsParamRead, limits))
            {
                scanTruncated = true;
                break;
            }
            totalScanned++;

            var element = doc.GetElement(elementId);
            if (element?.Category == null) continue;

            // Filtering needs values only for parameters named by the filters.
            // Materializing every value here is especially expensive because this loop
            // continues past the requested page to preserve the exact totalMatched contract.
            if (options.Filters.Count > 0)
            {
                var filterParams = reader.ReadParameters(doc, element, filterReadOpts);
                if (!PassesFilters(filterParams, options.Filters))
                    continue;
            }

            // 0-based index of this matched element (capture before incrementing)
            var matchIndex = totalMatched;
            totalMatched++;

            // Skip elements outside the current page window. Crucially, response
            // parameters are not read for these elements; the scan continues only to
            // retain the existing exact totalMatched/hasMore pagination metadata.
            if (matchIndex < pageStart || results.Count >= effectivePageSize) continue;

            var responseParams = reader.ReadParameters(doc, element, responseReadOpts);

            // Build parameter map for response
            var paramMap = BuildParameterMap(
                responseParams,
                options.ReturnParameters,
                options.ReturnParameterMatchMode,
                effectiveMaxParameters,
                effectiveTruncateLength);

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

            if (options.IncludeTags)
            {
                // Built once for the whole document, only when a returned element needs it.
                tagIndex ??= ElementTagIndex.Build(doc);
                info.Tags = tagIndex.GetTags(info.ElementId) ?? new List<TagInfoDto>();
            }

            results.Add(info);
        }

        if (scanTruncated)
        {
            var suggestion = isNarrowed
                ? "This is a very large result set even within the given scope — narrow further with parameter filters or a smaller category."
                : "Add a category, elementIds, or useSelection to narrow the search, or call revit_count_elements first to see category sizes.";
            warnings.Add($"Stopped scanning after {totalScanned} elements (safety limit reached, not the whole model). {suggestion} Results below are partial.");
        }

        int pageEnd = pageStart + effectivePageSize;
        bool hasMore = totalMatched > pageEnd;

        if (totalMatched > effectivePageSize && page == 0 && results.Count == effectivePageSize)
            warnings.Add($"Results paged: showing {results.Count} of {totalMatched}. Use 'page' and 'pageSize' parameters to navigate.");
        else if (hasMore)
            warnings.Add($"More results available beyond this page ({totalMatched - pageEnd} remaining).");

        return new ElementQueryResult
        {
            Success = true,
            Message = $"Matched {totalMatched} elements, returned {results.Count}.",
            Elements = results,
            TotalMatched = totalMatched,
            Warnings = warnings,
            Page = page,
            PageSize = effectivePageSize,
            HasMore = hasMore,
            NextPageToken = hasMore ? $"page={page + 1}" : null
        };
    }

    private static Dictionary<string, ParameterValueDto> BuildParameterMap(
        IReadOnlyList<ParameterValueDto> allParams,
        List<string> returnNames,
        string matchMode,
        int maxParameters = 0,
        int truncateLength = 0)
    {
        var map = new Dictionary<string, ParameterValueDto>(StringComparer.Ordinal);

        var filtered = returnNames.Count > 0
            ? allParams.Where(p => returnNames.Any(n => ParameterMatcher.Matches(p.Name, n, matchMode)))
            : allParams;

        foreach (var p in filtered)
        {
            if (maxParameters > 0 && map.Count >= maxParameters) break;

            ParameterValueDto value = p;
            if (truncateLength > 0 && p.Value.Length > truncateLength)
            {
                value = new ParameterValueDto
                {
                    Name = p.Name,
                    NormalizedName = p.NormalizedName,
                    Value = QueryGuard.TruncateString(p.Value, truncateLength),
                    RawValue = p.RawValue,
                    StorageType = p.StorageType,
                    Scope = p.Scope,
                    IsReadOnly = p.IsReadOnly,
                    IsShared = p.IsShared,
                    Guid = p.Guid,
                    ParameterId = p.ParameterId,
                    BuiltInParameterName = p.BuiltInParameterName,
                    TypeElementId = p.TypeElementId,
                    TypeName = p.TypeName
                };
            }

            var key = map.ContainsKey(value.Name)
                ? $"{value.Name} [{value.Scope}]"
                : value.Name;
            map[key] = value;
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

    private static ParameterReadOptions CreateResponseReadOptions(ElementQueryOptions options)
    {
        return new ParameterReadOptions
        {
            IncludeInstanceParameters = options.IncludeInstanceParameters,
            IncludeTypeParameters = options.IncludeTypeParameters,
            ParameterNames = options.ReturnParameters,
            ParameterNameMatchMode = options.ReturnParameterMatchMode
        };
    }

    private static ParameterReadOptions CreateFilterReadOptions(ElementQueryOptions options)
    {
        return new ParameterReadOptions
        {
            IncludeInstanceParameters = options.IncludeInstanceParameters,
            IncludeTypeParameters = options.IncludeTypeParameters,
            ParameterSelectors = options.Filters
                .Select(filter => new ParameterSelector
                {
                    Name = filter.ParameterName,
                    MatchMode = filter.MatchMode,
                    Scope = filter.Scope
                })
                .ToList()
        };
    }

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

    /// <summary>
    /// Fast-path for SummaryOnly mode. Scans all matching elements and returns category/family
    /// counts without building any parameter maps or element DTOs.
    /// </summary>
    private ElementQueryResult BuildSummaryResult(
        Document doc,
        IEnumerable<ElementId> elementIds,
        ElementQueryOptions options,
        ParameterReader reader,
        ParameterReadOptions readOpts,
        CancellationToken cancellationToken)
    {
        bool needsParamRead = options.Filters.Count > 0;
        bool isNarrowed = options.UseSelection || options.ElementIds.Count > 0 || !string.IsNullOrWhiteSpace(options.Category);
        var limits = QueryLimits.Default;

        var catCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var famCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        int totalMatched = 0;
        int totalScanned = 0;
        bool scanTruncated = false;

        foreach (var elementId in elementIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Category/family counting alone is cheap even unscoped (no parameter reads),
            // so it only needs the generous backstop cap. Filters require a per-element
            // parameter read to evaluate, so they get the same tight cap as the main query.
            // Checked before incrementing so totalScanned always equals the number of
            // elements actually processed.
            if (QueryGuard.ShouldStopScan(totalScanned, isNarrowed, needsParamRead, limits))
            {
                scanTruncated = true;
                break;
            }
            totalScanned++;

            var element = doc.GetElement(elementId);
            if (element?.Category == null) continue;

            if (needsParamRead)
            {
                var allParams = reader.ReadParameters(doc, element, readOpts);
                if (!PassesFilters(allParams, options.Filters)) continue;
            }

            totalMatched++;

            var cat = element.Category.Name;
            catCounts[cat] = catCounts.GetValueOrDefault(cat) + 1;

            if (element is FamilyInstance fi)
            {
                var fam = fi.Symbol?.Family?.Name ?? string.Empty;
                if (!string.IsNullOrEmpty(fam))
                    famCounts[fam] = famCounts.GetValueOrDefault(fam) + 1;
            }
        }

        var warnings = new List<string>();
        if (scanTruncated)
        {
            var suggestion = needsParamRead
                ? (isNarrowed
                    ? "This is a very large scope for a filtered summary — narrow further with a smaller category or elementIds."
                    : "Filters require reading every element's parameters; add a category, elementIds, or useSelection to narrow the search.")
                : "The model is larger than the safety scan limit — counts below are a partial sample, not the full model.";
            warnings.Add($"Stopped scanning after {totalScanned} elements (safety limit reached). {suggestion}");
        }

        var summary = new ElementQuerySummary
        {
            TotalElements = totalMatched,
            Categories = catCounts
                .OrderByDescending(x => x.Value)
                .Select(x => new CategoryCount { Category = x.Key, Count = x.Value })
                .ToArray(),
            Families = famCounts
                .OrderByDescending(x => x.Value)
                .Take(50)
                .Select(x => new FamilyCount { Family = x.Key, Count = x.Value })
                .ToArray(),
            Message = totalMatched > 0
                ? "Use filters, a specific category, or elementIds for detailed element data."
                : "No matching elements found."
        };

        return new ElementQueryResult
        {
            Success = true,
            Message = $"Summary: {totalMatched} elements across {catCounts.Count} categories.",
            Elements = new List<ElementInfoDto>(),
            TotalMatched = totalMatched,
            Warnings = warnings,
            Summary = summary
        };
    }
}

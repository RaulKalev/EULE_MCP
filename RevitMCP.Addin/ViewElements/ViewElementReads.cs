using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Query;
using RevitMCP.Addin.Tools;
using RevitMCP.Core.Models;
using RevitMCP.Core.Safety;

namespace RevitMCP.Addin.ViewElements;

/// <summary>
/// The <c>view</c> argument of revit_count_elements, revit_get_elements_info and revit_find_elements_by_parameter (#136).
/// The tools hand a call with <c>view</c> over to this class before anything else happens, so a call without it runs
/// the old code path untouched.
/// </summary>
internal static class ViewElementReads
{
    private const string ViewArg = "view";
    private const string ScopeArg = "scope";
    private const string IncludeRoomArg = "includeRoom";

    private static readonly CategoryResolver CategoryResolver = new();
    private static readonly ElementQueryEngine Engine = new();

    public static bool HasView(McpToolRequest request) =>
        !string.IsNullOrWhiteSpace(ToolArguments.GetString(request.Arguments, ViewArg));

    // ── revit_count_elements view=... ─────────────────────────────────────────

    public static McpToolResult Count(
        UIApplication uiapp, RevitDocumentResolver.DocumentTarget target, McpToolRequest request, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var doc = target.Document;
        var args = request.Arguments;
        var viewTarget = ViewTargetResolver.Resolve(uiapp, target, request, ToolArguments.GetString(args, ViewArg), out var failure);
        if (viewTarget == null) return failure!;

        if (!TryScope(request, out var scope, out failure)) return failure!;
        var warnings = new List<string>();
        var category = ToolArguments.GetString(args, "category");
        if (!TryCategory(doc, request, category, warnings, out var categoryId, out failure)) return failure!;

        var collection = ViewElementCollector.Collect(doc, viewTarget, new ViewCollectOptions { CategoryId = categoryId, Scope = scope }, cancellationToken);
        warnings.AddRange(collection.Warnings);
        AddMatchNote(viewTarget, warnings);

        var byFamilyAndType = ToolArguments.GetString(args, "groupBy", "Category").Equals("FamilyAndType", StringComparison.OrdinalIgnoreCase);
        var total = collection.Sources.Sum(s => s.Ids.Count);
        var isSheet = viewTarget.Sheet != null;

        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Counted {total} elements in '{viewTarget.View.Name}'.",
            Data = WithoutNulls(new
            {
                totalCount = total,
                groupBy = byFamilyAndType ? "FamilyAndType" : "Category",
                categoryFilter = category,
                groups = Groups(doc, collection.Sources, byFamilyAndType),
                view = Describe(viewTarget),
                scope = ViewElementRules.ScopeName(scope),
                excluded = collection.Excluded,
                sheetElements = !isSheet ? null : collection.Sources.Where(s => s.IsSheetItself).Select(s => new
                {
                    count = s.Ids.Count,
                    groups = Groups(doc, new[] { s }, byFamilyAndType)
                }).FirstOrDefault(),
                viewports = !isSheet ? null : collection.Sources.Where(s => !s.IsSheetItself).Select(s => new
                {
                    viewportId = s.Viewport?.Id.Value,
                    viewId = s.View?.Id.Value,
                    viewName = s.View?.Name,
                    viewType = s.View?.ViewType.ToString(),
                    problem = s.Problem,
                    count = s.Ids.Count,
                    groups = Groups(doc, new[] { s }, byFamilyAndType)
                }).ToList()
            }),
            Warnings =warnings,
            DurationMs = watch.ElapsedMilliseconds
        };
    }

    private static object Groups(Document doc, IEnumerable<ViewSource> sources, bool byFamilyAndType)
    {
        var ids = sources.SelectMany(s => s.Ids);
        if (!byFamilyAndType)
        {
            return ids.Select(id => doc.GetElement(id)?.Category?.Name ?? string.Empty)
                .GroupBy(name => name)
                .OrderByDescending(g => g.Count())
                .Select(g => new { category = g.Key, count = g.Count() })
                .ToList();
        }
        return ids.Select(id => FamilyAndType(doc, doc.GetElement(id)))
            .GroupBy(name => name)
            .OrderByDescending(g => g.Count())
            .Select(g => new { type = g.Key, count = g.Count() })
            .ToList();
    }

    private static string FamilyAndType(Document doc, Element? element)
    {
        if (element == null) return "?";
        if (element is FamilyInstance fi)
            return $"{fi.Symbol?.Family?.Name ?? "?"} : {fi.Symbol?.Name ?? "?"}";
        var typeElement = doc.GetElement(element.GetTypeId());
        return $"{element.Category?.Name} : {typeElement?.Name ?? element.Name}";
    }

    // ── revit_get_elements_info / revit_find_elements_by_parameter view=... ───

    /// <param name="parameterNamesArg">The tool's argument naming the parameters to return.</param>
    public static McpToolResult List(
        UIApplication uiapp,
        RevitDocumentResolver.DocumentTarget target,
        McpToolRequest request,
        ElementQueryOptions options,
        IReadOnlyList<string> parseWarnings,
        string parameterNamesArg,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var doc = target.Document;
        var args = request.Arguments;
        var viewTarget = ViewTargetResolver.Resolve(uiapp, target, request, ToolArguments.GetString(args, ViewArg), out var failure);
        if (viewTarget == null) return failure!;

        if (!TryScope(request, out var scope, out failure)) return failure!;

        var warnings = new List<string>(parseWarnings);
        if (!TryCategory(doc, request, options.Category, warnings, out var categoryId, out failure)) return failure!;

        HashSet<long>? onlyIds = null;
        if (options.UseSelection)
        {
            var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
            if (uidoc == null) return ToolDocument.ActiveRequired(request, "useSelection", doc.Title);
            onlyIds = new HashSet<long>(uidoc.Selection.GetElementIds().Select(id => id.Value));
        }
        else if (options.ElementIds.Count > 0)
        {
            onlyIds = new HashSet<long>(options.ElementIds);
        }

        // Parameters are on request with view; naming some is a request. The bridge settles this itself, this is for direct calls.
        if (!args.ContainsKey("includeInstanceParameters"))
            options.IncludeInstanceParameters = ToolArguments.GetStringArray(args, parameterNamesArg).Length > 0;
        if (!args.ContainsKey("includeTypeParameters"))
            options.IncludeTypeParameters = false;

        var collection = ViewElementCollector.Collect(doc, viewTarget, new ViewCollectOptions
        {
            CategoryId = categoryId,
            Scope = scope,
            OnlyIds = onlyIds
        }, cancellationToken);
        warnings.AddRange(collection.Warnings);
        AddMatchNote(viewTarget, warnings);

        if (options.Filters.Count > 0)
            ApplyFilters(doc, collection.Sources, options, warnings, cancellationToken);

        var read = new ViewRead(request, viewTarget, collection, collection.Sources.Sum(s => s.Ids.Count), ViewElementRules.ScopeName(scope), warnings, watch);

        if (ViewLargeReadGuard.Applies(new ViewReadShape
            {
                Total = read.Total,
                HasCategory = categoryId != null,
                HasFilters = options.Filters.Count > 0,
                HasElementIds = options.ElementIds.Count > 0,
                UseSelection = options.UseSelection,
                SummaryOnly = options.SummaryOnly,
                RequestedPageSize = options.PageSize,
                RequestedPage = options.Page
            }))
        {
            return LargeRead(read);
        }

        return options.SummaryOnly
            ? Summary(doc, read, cancellationToken)
            : Page(doc, read, options, cancellationToken);
    }

    /// <summary>What every kind of answer to a list read shares.</summary>
    private sealed class ViewRead
    {
        public ViewRead(McpToolRequest request, ViewTarget target, ViewCollection collection, int total, string scope, List<string> warnings, Stopwatch watch)
        {
            Request = request;
            Target = target;
            Collection = collection;
            Total = total;
            Scope = scope;
            Warnings = warnings;
            Watch = watch;
        }

        public McpToolRequest Request { get; }
        public ViewTarget Target { get; }
        public ViewCollection Collection { get; }
        public int Total { get; }
        public string Scope { get; }
        public List<string> Warnings { get; }
        public Stopwatch Watch { get; }
        public bool IsSheet => Target.Sheet != null;
        public int? SheetElementCount => IsSheet ? Collection.Sources.Where(s => s.IsSheetItself).Sum(s => s.Ids.Count) : null;

        public object? ViewportCounts => !IsSheet ? null : Collection.Sources.Where(s => !s.IsSheetItself).Select(s => new
        {
            viewportId = s.Viewport?.Id.Value,
            viewId = s.View?.Id.Value,
            viewName = s.View?.Name,
            viewType = s.View?.ViewType.ToString(),
            problem = s.Problem,
            total = s.Ids.Count
        }).ToList();
    }

    private static void ApplyFilters(Document doc, List<ViewSource> sources, ElementQueryOptions options, List<string> warnings, CancellationToken cancellationToken)
    {
        var filterOptions = new ElementQueryOptions
        {
            Filters = options.Filters,
            // A filter on an instance parameter has to see it even when the response does not ask for parameters.
            IncludeInstanceParameters = true,
            IncludeTypeParameters = options.IncludeTypeParameters
        };
        var distinct = sources.SelectMany(s => s.Ids).GroupBy(id => id.Value).Select(g => g.First()).ToList();
        var matching = Engine.MatchingIds(doc, distinct, filterOptions, out var truncated, cancellationToken);
        foreach (var source in sources)
            source.Ids = source.Ids.Where(id => matching.Contains(id.Value)).ToList();
        if (truncated)
            warnings.Add($"Stopped evaluating filters after {QueryLimits.Default.MaxScanElements} elements (safety limit); results are partial. Add a category or scope.");
    }

    private static McpToolResult LargeRead(ViewRead read)
    {
        var categories = read.Collection.Sources
            .SelectMany(s => s.CategoryCounts)
            .GroupBy(c => c.Key)
            .Select(g => new KeyValuePair<string, int>(g.Key, g.Sum(c => c.Value)))
            .OrderByDescending(c => c.Value)
            .ToList();
        var document = ToolArguments.GetString(read.Request.Arguments, RevitDocumentResolver.ArgumentName);
        return new McpToolResult
        {
            RequestId = read.Request.RequestId,
            Success = true,
            Status = ViewLargeReadGuard.Status,
            Message = $"'{read.Target.View.Name}' shows {read.Total} elements across {categories.Count} categories, so they were not read. Narrow by category or filters, or ask for a page.",
            Data = WithoutNulls(new
            {
                read = false,
                totalMatched = read.Total,
                threshold = ViewLargeReadGuard.Threshold,
                view = Describe(read.Target),
                scope = read.Scope,
                categories = categories.Select(c => new { category = c.Key, count = c.Value }).ToList(),
                sheetElementCount = read.SheetElementCount,
                viewports = read.ViewportCounts,
                excluded = read.Collection.Excluded,
                next = ViewLargeReadGuard.Next(read.Request.ToolName, read.Target.View.Id.Value, document, read.Scope, categories)
                    .Select(c => new { tool = c.Tool, args = c.Args, why = c.Why })
                    .ToList()
            }),
            Warnings =read.Warnings,
            DurationMs = read.Watch.ElapsedMilliseconds
        };
    }

    private static McpToolResult Summary(Document doc, ViewRead read, CancellationToken cancellationToken)
    {
        ElementQuerySummary summary;
        if (read.Total == 0)
        {
            // An empty id list means "the whole model" to the query engine.
            summary = new ElementQuerySummary { Message = "No matching elements found." };
        }
        else
        {
            var result = Engine.Query(doc, null, new ElementQueryOptions
            {
                ElementIds = read.Collection.Sources.SelectMany(s => s.Ids).Select(id => id.Value).ToList(),
                SummaryOnly = true
            }, cancellationToken);
            summary = result.Summary ?? new ElementQuerySummary();
            read.Warnings.AddRange(result.Warnings);
        }

        return new McpToolResult
        {
            RequestId = read.Request.RequestId,
            Success = true,
            Message = $"Summary of {read.Total} elements in '{read.Target.View.Name}'.",
            Data = WithoutNulls(new
            {
                totalMatched = read.Total,
                returned = 0,
                hasMore = false,
                summary,
                view = Describe(read.Target),
                scope = read.Scope,
                excluded = read.Collection.Excluded,
                sheetElementCount = read.SheetElementCount,
                viewports = read.ViewportCounts
            }),
            Warnings =read.Warnings,
            DurationMs = read.Watch.ElapsedMilliseconds
        };
    }

    private static McpToolResult Page(Document doc, ViewRead read, ElementQueryOptions options, CancellationToken cancellationToken)
    {
        var (pageSize, _, _) = QueryGuard.ResolveEffectiveLimits(options.PageSize, options.Limit, options.MaxParametersPerElement, options.TruncateStringLength);
        var page = Math.Max(0, options.Page);
        var limits = QueryLimits.Default;
        if (options.PageSize > limits.MaxPageSize)
            read.Warnings.Add($"Requested pageSize {options.PageSize} exceeded the maximum ({limits.MaxPageSize}); clamped to {pageSize}.");

        var sources = read.Collection.Sources;
        var slices = ViewResultPaging.Slice(sources.Select(s => s.Ids.Count).ToList(), page, pageSize);
        var perSource = new Dictionary<int, List<ElementInfoDto>>();
        ViewElementFields? fields = null;

        foreach (var slice in slices)
        {
            var source = sources[slice.Source];
            var ids = source.Ids.Skip(slice.Skip).Take(slice.Take).ToList();
            var result = Engine.Query(doc, null, new ElementQueryOptions
            {
                ElementIds = ids.Select(id => id.Value).ToList(),
                ReturnParameters = options.ReturnParameters,
                ReturnParameterMatchMode = options.ReturnParameterMatchMode,
                IncludeInstanceParameters = options.IncludeInstanceParameters,
                IncludeTypeParameters = options.IncludeTypeParameters,
                Limit = slice.Take,
                PageSize = slice.Take,
                MaxParametersPerElement = options.MaxParametersPerElement,
                TruncateStringLength = options.TruncateStringLength,
                IncludeModelState = options.IncludeModelState,
                IncludeOwnership = options.IncludeOwnership
            }, cancellationToken);
            if (!result.Success) return Failed(read.Request, result.Message);
            foreach (var warning in result.Warnings)
                if (!read.Warnings.Contains(warning)) read.Warnings.Add(warning);

            fields ??= new ViewElementFields(doc, ToolArguments.GetBool(read.Request.Arguments, IncludeRoomArg), options.IncludeTags);
            foreach (var info in result.Elements)
            {
                var element = doc.GetElement(new ElementId(info.ElementId));
                if (element != null) fields.Apply(info, element, source.View!);
            }
            perSource[slice.Source] = result.Elements;
        }

        var returned = perSource.Values.Sum(list => list.Count);
        var hasMore = ViewResultPaging.HasMore(read.Total, page, pageSize);
        if (hasMore)
            read.Warnings.Add($"Results paged: showing {returned} of {read.Total}. Use 'page' and 'pageSize' to navigate.");

        object Format(int sourceIndex) => ElementQueryResponseFormatter.FormatElements(
            perSource.TryGetValue(sourceIndex, out var list) ? list : new List<ElementInfoDto>(), options.CompactResponse);
        int Returned(int sourceIndex) => perSource.TryGetValue(sourceIndex, out var list) ? list.Count : 0;

        return new McpToolResult
        {
            RequestId = read.Request.RequestId,
            Success = true,
            Message = $"Returned {returned} of {read.Total} elements in '{read.Target.View.Name}'.",
            Data = WithoutNulls(new
            {
                totalMatched = read.Total,
                returned,
                page,
                pageSize,
                hasMore,
                nextPageToken = hasMore ? $"page={page + 1}" : null,
                view = Describe(read.Target),
                scope = read.Scope,
                excluded = read.Collection.Excluded,
                elements = read.IsSheet ? null : Format(0),
                sheetElements = !read.IsSheet ? null : new { total = sources[0].Ids.Count, returned = Returned(0), elements = Format(0) },
                viewports = !read.IsSheet ? null : sources.Select((s, i) => (Source: s, Index: i)).Where(x => !x.Source.IsSheetItself).Select(x => new
                {
                    viewportId = x.Source.Viewport?.Id.Value,
                    viewId = x.Source.View?.Id.Value,
                    viewName = x.Source.View?.Name,
                    viewType = x.Source.View?.ViewType.ToString(),
                    problem = x.Source.Problem,
                    total = x.Source.Ids.Count,
                    returned = Returned(x.Index),
                    elements = Format(x.Index)
                }).ToList()
            }),
            Warnings =read.Warnings,
            DurationMs = read.Watch.ElapsedMilliseconds
        };
    }

    // ── shared ────────────────────────────────────────────────────────────────

    /// <summary>A view read's data as JSON now, so the many fields that only apply to a sheet or a view cost no tokens as nulls.</summary>
    private static JObject WithoutNulls(object data) =>
        JObject.FromObject(data, JsonSerializer.Create(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));

    private static object Describe(ViewTarget target) => new
    {
        id = target.View.Id.Value,
        name = target.View.Name,
        viewType = target.View.ViewType.ToString(),
        kind = target.Sheet == null ? "view" : "sheet",
        sheetNumber = target.Sheet?.SheetNumber,
        matchedBy = target.MatchedBy
    };

    private static void AddMatchNote(ViewTarget target, List<string> warnings)
    {
        if (target.Note != null) warnings.Add(target.Note);
    }

    private static bool TryScope(McpToolRequest request, out ViewElementScope scope, out McpToolResult? failure)
    {
        failure = null;
        var text = ToolArguments.GetString(request.Arguments, ScopeArg);
        if (ViewElementRules.TryParseScope(text, out scope)) return true;
        failure = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = "validation_failed",
            Message = $"scope '{text}' is not valid: use model, annotation or all."
        };
        return false;
    }

    private static bool TryCategory(
        Document doc, McpToolRequest request, string? category, List<string> warnings, out ElementId? categoryId, out McpToolResult? failure)
    {
        categoryId = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(category)) return true;
        var resolve = CategoryResolver.Resolve(doc, category!);
        if (resolve.Category == null)
        {
            var suggestions = resolve.Suggestions.Count > 0 ? $" Did you mean: {string.Join(", ", resolve.Suggestions)}?" : string.Empty;
            failure = Failed(request, resolve.Message + suggestions);
            return false;
        }
        if (resolve.Message.Contains("Resolved") && resolve.Category.Name != category)
            warnings.Add($"Category resolved to '{resolve.Category.Name}'.");
        categoryId = resolve.Category.Id;
        return true;
    }

    private static McpToolResult Failed(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

using Autodesk.Revit.DB;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.ViewFilters;

namespace RevitMCP.Addin.Tools.ViewFilters;

/// <summary>Finds filters, views, categories and parameters in a document for the view filter tools (#134), and reads what a filter holds.</summary>
internal static class ViewFilterModel
{
    public static string KindOf(FilterElement filter) => filter is SelectionFilterElement ? ViewFilterKinds.Selection : ViewFilterKinds.Rule;

    /// <summary>Rule and selection filters by name. Filter classes are collected one by one: FilterElement itself is an abstract base.</summary>
    public static List<FilterElement> Filters(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(ParameterFilterElement)).Cast<FilterElement>()
            .Concat(new FilteredElementCollector(doc).OfClass(typeof(SelectionFilterElement)).Cast<FilterElement>())
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static FilterElement? FindFilter(string reference, List<FilterElement> filters, string path, out ViewFilterIssue? issue)
    {
        issue = null;
        var refs = filters.Select(f => new NamedRef(f.Id.Value, f.Name)).ToList();
        var match = ViewFilterRefMatcher.Match(reference, refs);
        switch (match.Kind)
        {
            case RefMatchKind.Found:
                return filters.First(f => f.Id.Value == match.Found!.Id);
            case RefMatchKind.Ambiguous:
                issue = new ViewFilterIssue(ViewFilterCodes.FilterAmbiguous, $"'{reference}' matches several filters: {ViewFilterRefMatcher.List(match.Candidates)}. Use the id.", path, "filter", reference);
                return null;
            default:
                issue = new ViewFilterIssue(ViewFilterCodes.FilterNotFound, $"No view filter '{reference}' in this document.", path, "filter", reference);
                return null;
        }
    }

    public static List<View> Views(Document doc) =>
        new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
            .Where(v => v.ViewType != ViewType.ProjectBrowser && v.ViewType != ViewType.SystemBrowser && v.ViewType != ViewType.Internal)
            .ToList();

    public static View? FindView(string reference, List<View> views, string path, out ViewFilterIssue? issue)
    {
        issue = null;
        var refs = views.Select(v => new NamedRef(v.Id.Value, v.Name)).ToList();
        var match = ViewFilterRefMatcher.Match(reference, refs);
        switch (match.Kind)
        {
            case RefMatchKind.Found:
                return views.First(v => v.Id.Value == match.Found!.Id);
            case RefMatchKind.Ambiguous:
                issue = new ViewFilterIssue(ViewFilterCodes.ViewAmbiguous, $"'{reference}' matches several views: {ViewFilterRefMatcher.List(match.Candidates)}. Use the id.", path, "view", reference);
                return null;
            default:
                issue = new ViewFilterIssue(ViewFilterCodes.ViewNotFound, $"No view or view template '{reference}' in this document.", path, "view", reference);
                return null;
        }
    }

    public static bool AllowsFilters(View view)
    {
        try { return view.AreGraphicsOverridesAllowed(); }
        catch { return false; }
    }

    public static View? TemplateOf(View view)
    {
        if (view.IsTemplate) return null;
        var id = view.ViewTemplateId;
        return id == null || id == ElementId.InvalidElementId ? null : view.Document.GetElement(id) as View;
    }

    public static bool TemplateControlsFilters(View template)
    {
        const BuiltInParameter filtersSetting = BuiltInParameter.VIS_GRAPHICS_FILTERS;
        return ViewFilterRouting.TemplateControlsFilters(
            template.GetTemplateParameterIds().Select(i => i.Value),
            template.GetNonControlledTemplateParameterIds().Select(i => i.Value),
            (long)filtersSetting);
    }

    public static ViewRoute Route(View view)
    {
        var template = TemplateOf(view);
        return ViewFilterRouting.Route(new ViewRouteInput
        {
            ViewId = view.Id.Value,
            ViewName = view.Name,
            IsTemplate = view.IsTemplate,
            TemplateId = template?.Id.Value ?? 0,
            TemplateName = template?.Name,
            TemplateControlsFilters = template != null && TemplateControlsFilters(template)
        });
    }

    /// <summary>The view whose filter settings apply to <paramref name="view"/>: its template when that controls filters, else itself.</summary>
    public static View EffectiveView(View view)
    {
        var template = TemplateOf(view);
        return template != null && TemplateControlsFilters(template) ? template : view;
    }

    public static List<long> OrderedFilterIds(View view) => view.GetOrderedFilters().Select(i => i.Value).ToList();

    /// <summary>Filter id → the views and templates that have it applied directly.</summary>
    public static Dictionary<long, List<View>> UsageMap(Document doc)
    {
        var usage = new Dictionary<long, List<View>>();
        foreach (var view in Views(doc))
        {
            if (!AllowsFilters(view)) continue;
            ICollection<ElementId> filters;
            try { filters = view.GetFilters(); }
            catch { continue; }
            foreach (var id in filters)
            {
                if (!usage.TryGetValue(id.Value, out var list)) usage[id.Value] = list = new List<View>();
                list.Add(view);
            }
        }
        return usage;
    }

    /// <summary>Template id → the views it is assigned to.</summary>
    public static Dictionary<long, List<View>> ViewsByTemplate(Document doc)
    {
        var map = new Dictionary<long, List<View>>();
        foreach (var view in Views(doc).Where(v => !v.IsTemplate))
        {
            var id = view.ViewTemplateId;
            if (id == null || id == ElementId.InvalidElementId) continue;
            if (!map.TryGetValue(id.Value, out var list)) map[id.Value] = list = new List<View>();
            list.Add(view);
        }
        return map;
    }

    /// <summary>Where a filter is used: views and templates that have it, and the views a using template passes it on to.</summary>
    public static object DescribeUsage(long filterId, Dictionary<long, List<View>> usage, Dictionary<long, List<View>> byTemplate)
    {
        usage.TryGetValue(filterId, out var direct);
        direct ??= new List<View>();
        var templates = direct.Where(v => v.IsTemplate).ToList();
        var inherited = templates
            .Where(TemplateControlsFilters)
            .SelectMany(t => byTemplate.TryGetValue(t.Id.Value, out var list) ? list : new List<View>())
            .GroupBy(v => v.Id.Value).Select(g => g.First())
            .ToList();
        return new
        {
            // A view under a controlling template reports the template's filters as its own: it is listed once, below.
            useCount = direct.Count(v => v.IsTemplate || inherited.All(i => i.Id != v.Id)),
            views = direct.Where(v => !v.IsTemplate && inherited.All(i => i.Id != v.Id)).Select(ViewRef).ToList(),
            templates = templates.Select(t => new { id = t.Id.Value, name = t.Name, controlsFilters = TemplateControlsFilters(t) }).ToList(),
            viewsThroughTemplates = inherited.Select(ViewRef).ToList()
        };
    }

    public static object ViewRef(View view) => new { id = view.Id.Value, name = view.Name, viewType = view.ViewType.ToString() };

    // ── Categories ───────────────────────────────────────────────────────────

    public static string? BuiltInCategoryName(long id) => id < 0 ? Enum.GetName(typeof(BuiltInCategory), (BuiltInCategory)id) : null;

    public static List<NamedRef> FilterableCategories(Document doc)
    {
        var result = new List<NamedRef>();
        foreach (var id in ParameterFilterUtilities.GetAllFilterableCategories())
        {
            var category = Category.GetCategory(doc, id);
            if (category != null) result.Add(new NamedRef(id.Value, category.Name, BuiltInCategoryName(id.Value)));
        }
        return result.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Resolves references to filterable categories; everything else goes to <paramref name="issues"/>.</summary>
    public static List<ElementId> ResolveCategories(Document doc, IEnumerable<string> references, List<ViewFilterIssue> issues, string path)
    {
        var filterable = FilterableCategories(doc);
        var result = new List<ElementId>();
        foreach (var reference in references)
        {
            var id = ResolveCategory(doc, reference, filterable, issues, path);
            if (id != null && !result.Contains(id)) result.Add(id);
        }
        return result;
    }

    public static ElementId? ResolveCategory(Document doc, string reference, List<NamedRef> filterable, List<ViewFilterIssue> issues, string path)
    {
        var match = ViewFilterRefMatcher.Match(reference, filterable);
        switch (match.Kind)
        {
            case RefMatchKind.Found:
                return new ElementId(match.Found!.Id);
            case RefMatchKind.Ambiguous:
                issues.Add(new ViewFilterIssue(ViewFilterCodes.CategoryAmbiguous, $"Category '{reference}' matches {ViewFilterRefMatcher.List(match.Candidates)}. Use the id or the OST_ name.", path, "category", reference));
                return null;
        }

        var all = new List<NamedRef>();
        foreach (Category category in doc.Settings.Categories)
            all.Add(new NamedRef(category.Id.Value, category.Name, BuiltInCategoryName(category.Id.Value)));
        issues.Add(ViewFilterRefMatcher.Match(reference, all).Kind == RefMatchKind.NotFound
            ? new ViewFilterIssue(ViewFilterCodes.CategoryNotFound, $"No category '{reference}' in this document.", path, "category", reference)
            : new ViewFilterIssue(ViewFilterCodes.CategoryNotFilterable, $"Category '{reference}' cannot be used in a view filter.", path, "category", reference));
        return null;
    }

    public static object CategoryRef(Document doc, ElementId id) => new
    {
        id = id.Value,
        name = Category.GetCategory(doc, id)?.Name,
        builtIn = BuiltInCategoryName(id.Value)
    };

    // ── Describing a filter ──────────────────────────────────────────────────

    public const int MaxMembersListed = 1000;

    public static object Describe(Document doc, FilterElement filter, ViewFilterCatalogCache cache, bool includeRules, object? usage)
    {
        if (filter is SelectionFilterElement selection)
        {
            var ids = selection.GetElementIds().Select(i => i.Value).ToList();
            return new
            {
                id = filter.Id.Value,
                name = filter.Name,
                kind = ViewFilterKinds.Selection,
                memberCount = ids.Count,
                members = includeRules ? ids.Take(MaxMembersListed).ToList() : null,
                membersTruncated = includeRules && ids.Count > MaxMembersListed ? true : (bool?)null,
                usage
            };
        }

        var rule = (ParameterFilterElement)filter;
        var categories = rule.GetCategories().ToList();
        var node = includeRules ? ViewFilterReader.Read(doc, rule.GetElementFilter(), categories, cache, forCopy: false) : null;
        return new
        {
            id = filter.Id.Value,
            name = filter.Name,
            kind = ViewFilterKinds.Rule,
            categories = categories.Select(c => CategoryRef(doc, c)).ToList(),
            ruleCount = includeRules ? ViewFilterRuleParser.CountRules(node) : (int?)null,
            rulesText = includeRules ? ViewFilterRuleParser.Describe(node) : null,
            rules = includeRules ? ViewFilterRuleParser.ToJson(node) : null,
            usage
        };
    }
}

/// <summary>The filterable parameters of one set of categories, with what each one holds.</summary>
internal sealed class ViewFilterParameterCatalog
{
    private readonly Dictionary<long, (FilterValueKind Kind, ForgeTypeId? Spec)> _types = new();

    public List<NamedRef> Refs { get; } = new();

    public FilterValueKind KindOf(long id) => _types.TryGetValue(id, out var t) ? t.Kind : FilterValueKind.Unknown;
    public ForgeTypeId? SpecOf(long id) => _types.TryGetValue(id, out var t) ? t.Spec : null;

    public static ViewFilterParameterCatalog Build(Document doc, ICollection<ElementId> categories)
    {
        var catalog = new ViewFilterParameterCatalog();
        var samples = SampleParameters(doc, categories);
        foreach (var id in ParameterFilterUtilities.GetFilterableParametersInCommon(doc, categories))
        {
            catalog.Refs.Add(Identify(doc, id));
            samples.TryGetValue(id.Value, out var sample);
            catalog._types[id.Value] = sample.Kind != FilterValueKind.Unknown ? sample : Declared(doc, id, sample.Spec);
        }
        catalog.Refs.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return catalog;
    }

    /// <summary>The parameter's reference data: the id, its label, BuiltInParameter name and shared parameter GUID.</summary>
    public static NamedRef Identify(Document doc, ElementId id)
    {
        if (id.Value < 0)
        {
            var bip = (BuiltInParameter)id.Value;
            var builtIn = Enum.GetName(typeof(BuiltInParameter), bip);
            string label;
            try { label = LabelUtils.GetLabelFor(bip); }
            catch { label = builtIn ?? id.Value.ToString(); }
            return new NamedRef(id.Value, label, builtIn);
        }

        var element = doc.GetElement(id);
        return new NamedRef(id.Value, element?.Name ?? id.Value.ToString(), null, (element as SharedParameterElement)?.GuidValue.ToString());
    }

    /// <summary>Data types read from one instance and one type per category: Revit has no data type lookup for a built-in parameter.</summary>
    private static Dictionary<long, (FilterValueKind Kind, ForgeTypeId? Spec)> SampleParameters(Document doc, ICollection<ElementId> categories)
    {
        var result = new Dictionary<long, (FilterValueKind, ForgeTypeId?)>();
        foreach (var category in categories)
        {
            var collector = new FilteredElementCollector(doc).OfCategoryId(category);
            Element? instance = null;
            Element? type = null;
            try
            {
                instance = new FilteredElementCollector(doc).OfCategoryId(category).WhereElementIsNotElementType().FirstElement();
                type = collector.WhereElementIsElementType().FirstElement();
            }
            catch
            {
                // A category without elements just gives no sample.
            }

            foreach (var element in new[] { instance, type })
            {
                if (element == null) continue;
                foreach (Parameter parameter in element.Parameters)
                {
                    try
                    {
                        var spec = parameter.Definition?.GetDataType();
                        result[parameter.Id.Value] = (FromStorage(parameter.StorageType, spec), spec);
                    }
                    catch
                    {
                        // Some internal parameters refuse GetDataType; they are declared below.
                    }
                }
            }
        }
        return result;
    }

    private static FilterValueKind FromStorage(StorageType storage, ForgeTypeId? spec) => storage switch
    {
        StorageType.String => FilterValueKind.Text,
        StorageType.Integer => spec != null && spec == SpecTypeId.Boolean.YesNo ? FilterValueKind.YesNo : FilterValueKind.Integer,
        StorageType.Double => FilterValueKind.Number,
        StorageType.ElementId => FilterValueKind.ElementId,
        _ => FilterValueKind.Unknown
    };

    private static (FilterValueKind, ForgeTypeId?) Declared(Document doc, ElementId id, ForgeTypeId? sampled)
    {
        try
        {
            if (id.Value < 0)
                return (FromStorage(doc.get_TypeOfStorage((BuiltInParameter)id.Value), sampled), sampled);

            if (doc.GetElement(id) is not ParameterElement element) return (FilterValueKind.Unknown, sampled);
            var spec = element.GetDefinition().GetDataType();
            if (spec == SpecTypeId.Boolean.YesNo) return (FilterValueKind.YesNo, spec);
            if (spec == SpecTypeId.Int.Integer) return (FilterValueKind.Integer, spec);
            if (spec == SpecTypeId.String.Text || spec == SpecTypeId.String.MultilineText || spec == SpecTypeId.String.Url) return (FilterValueKind.Text, spec);
            if (Category.IsBuiltInCategory(spec) || spec == SpecTypeId.Reference.Material || spec == SpecTypeId.Reference.Image) return (FilterValueKind.ElementId, spec);
            return UnitUtils.IsMeasurableSpec(spec) ? (FilterValueKind.Number, spec) : (FilterValueKind.Unknown, spec);
        }
        catch
        {
            return (FilterValueKind.Unknown, sampled);
        }
    }
}

/// <summary>Catalogs per set of categories, so one call builds each only once.</summary>
internal sealed class ViewFilterCatalogCache
{
    private readonly Dictionary<string, ViewFilterParameterCatalog> _catalogs = new();

    public ViewFilterParameterCatalog For(Document doc, ICollection<ElementId> categories)
    {
        var key = doc.GetHashCode() + ":" + string.Join(",", categories.Select(c => c.Value).OrderBy(v => v));
        if (!_catalogs.TryGetValue(key, out var catalog))
            _catalogs[key] = catalog = ViewFilterParameterCatalog.Build(doc, categories);
        return catalog;
    }
}

/// <summary>Reads Revit's stored rules back into the tree the tools take. It is the reverse of <see cref="ViewFilterRuleBuilder"/>.</summary>
internal static class ViewFilterReader
{
    public static ViewFilterNode? Read(Document doc, ElementFilter? filter, ICollection<ElementId> categories, ViewFilterCatalogCache cache, bool forCopy)
    {
        switch (filter)
        {
            case null:
                return null;
            case LogicalAndFilter and:
                return Group(doc, and.GetFilters(), false, categories, cache, forCopy);
            case LogicalOrFilter or:
                return Group(doc, or.GetFilters(), true, categories, cache, forCopy);
            case ElementParameterFilter parameterFilter:
                return ReadParameterFilter(doc, parameterFilter, categories, cache, forCopy);
            default:
                return Unsupported($"A {filter.GetType().Name} cannot be expressed as a rule");
        }
    }

    private static ViewFilterNode Group(Document doc, IEnumerable<ElementFilter> children, bool isOr, ICollection<ElementId> categories, ViewFilterCatalogCache cache, bool forCopy)
    {
        var group = new ViewFilterGroupNode(isOr);
        foreach (var child in children)
        {
            var node = Read(doc, child, categories, cache, forCopy);
            if (node != null) group.Children.Add(node);
        }
        return group;
    }

    private static ViewFilterNode ReadParameterFilter(Document doc, ElementParameterFilter filter, ICollection<ElementId> categories, ViewFilterCatalogCache cache, bool forCopy)
    {
        var all = filter.GetRules();
        var categoryRule = all.OfType<FilterCategoryRule>().FirstOrDefault();
        var rules = all.Where(r => r is not FilterCategoryRule).ToList();

        string? onlyCategory = null;
        var ruleCategories = categories;
        if (categoryRule != null)
        {
            var ids = categoryRule.GetCategories().ToList();
            if (ids.Count > 0)
            {
                onlyCategory = ViewFilterModel.BuiltInCategoryName(ids[0].Value) ?? ids[0].Value.ToString();
                ruleCategories = ids;
            }
        }

        var nodes = rules.Select(r => ReadRule(doc, r, ruleCategories, cache, forCopy)).ToList();
        foreach (var node in nodes) node.OnlyCategory = onlyCategory;

        if (filter.Inverted)
        {
            if (nodes.Count != 1 || nodes[0].Operator == null) return Unsupported("An inverted rule set cannot be expressed as a rule");
            nodes[0].Operator = ViewFilterOperators.Inverse(nodes[0].Operator!);
        }

        if (nodes.Count == 1) return nodes[0];
        var group = new ViewFilterGroupNode(false);
        group.Children.AddRange(nodes);
        return group;
    }

    private static ViewFilterRuleNode ReadRule(Document doc, FilterRule rule, ICollection<ElementId> categories, ViewFilterCatalogCache cache, bool forCopy)
    {
        switch (rule)
        {
            case FilterInverseRule inverse:
                var inner = ReadRule(doc, inverse.GetInnerRule(), categories, cache, forCopy);
                if (inner.Operator != null) inner.Operator = ViewFilterOperators.Inverse(inner.Operator);
                return inner;
            case SharedParameterApplicableRule shared:
                return new ViewFilterRuleNode { Parameter = shared.ParameterName, Operator = ViewFilterOperators.SharedParameterApplicable };
        }

        var parameterId = rule.GetRuleParameter();
        var identity = ViewFilterParameterCatalog.Identify(doc, parameterId);
        var node = new ViewFilterRuleNode
        {
            Parameter = identity.BuiltInName ?? identity.Guid ?? identity.Name,
            ParameterName = identity.Name,
            ParameterId = identity.Id
        };

        switch (rule)
        {
            case HasValueFilterRule:
                node.Operator = ViewFilterOperators.HasValue;
                break;
            case HasNoValueFilterRule:
                node.Operator = ViewFilterOperators.HasNoValue;
                break;
            case FilterStringRule text:
                node.Operator = OperatorOf(text.GetEvaluator());
                node.Value = text.RuleString;
                break;
            case FilterGlobalParameterAssociationRule global:
                node.Operator = ViewFilterOperators.AssociatedWithGlobalParameter;
                var parameter = doc.GetElement(global.RuleValue);
                node.ValueName = parameter?.Name;
                node.Value = forCopy && parameter != null ? parameter.Name : global.RuleValue.Value;
                break;
            case FilterIntegerRule integer:
                node.Operator = OperatorOf(integer.GetEvaluator());
                node.Value = integer.RuleValue;
                break;
            case FilterDoubleRule number:
                node.Operator = OperatorOf(number.GetEvaluator());
                node.Value = number.RuleValue;
                node.Units = ViewFilterUnits.Internal;
                node.Tolerance = number.Epsilon;
                node.DisplayValue = DisplayValue(doc, number.RuleValue, parameterId, categories, cache);
                break;
            case FilterElementIdRule reference:
                node.Operator = OperatorOf(reference.GetEvaluator());
                var element = reference.RuleValue.Value > 0 ? doc.GetElement(reference.RuleValue) : null;
                node.ValueName = element?.Name;
                node.Value = forCopy && element != null ? element.Name : reference.RuleValue.Value;
                break;
            default:
                node.Unsupported = $"A {rule.GetType().Name} on '{identity.Name}' cannot be expressed as a rule";
                break;
        }

        if (node.Operator == null && node.Unsupported == null)
            node.Unsupported = $"The evaluator of the rule on '{identity.Name}' is unknown";
        return node;
    }

    private static double? DisplayValue(Document doc, double value, ElementId parameter, ICollection<ElementId> categories, ViewFilterCatalogCache cache)
    {
        try
        {
            var spec = cache.For(doc, categories).SpecOf(parameter.Value);
            if (spec == null || !UnitUtils.IsMeasurableSpec(spec)) return null;
            return Math.Round(UnitUtils.ConvertFromInternalUnits(value, doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId()), 6);
        }
        catch
        {
            return null;
        }
    }

    private static string? OperatorOf(FilterStringRuleEvaluator evaluator) => evaluator switch
    {
        FilterStringEquals => ViewFilterOperators.EqualsOp,
        FilterStringContains => ViewFilterOperators.Contains,
        FilterStringBeginsWith => ViewFilterOperators.BeginsWith,
        FilterStringEndsWith => ViewFilterOperators.EndsWith,
        FilterStringGreater => ViewFilterOperators.Greater,
        FilterStringGreaterOrEqual => ViewFilterOperators.GreaterOrEqual,
        FilterStringLess => ViewFilterOperators.Less,
        FilterStringLessOrEqual => ViewFilterOperators.LessOrEqual,
        _ => null
    };

    private static string? OperatorOf(FilterNumericRuleEvaluator evaluator) => evaluator switch
    {
        FilterNumericEquals => ViewFilterOperators.EqualsOp,
        FilterNumericGreater => ViewFilterOperators.Greater,
        FilterNumericGreaterOrEqual => ViewFilterOperators.GreaterOrEqual,
        FilterNumericLess => ViewFilterOperators.Less,
        FilterNumericLessOrEqual => ViewFilterOperators.LessOrEqual,
        _ => null
    };

    private static ViewFilterRuleNode Unsupported(string why) => new() { Parameter = string.Empty, Unsupported = why };
}

/// <summary>Builds Revit's rule objects from a tree. No transaction needed, so a preview reports every failure before anything is written.</summary>
internal sealed class ViewFilterRuleBuilder
{
    /// <summary>Default tolerance of a number rule, in internal units.</summary>
    public const double DefaultTolerance = 1e-6;

    private readonly Document _doc;
    private readonly ViewFilterCatalogCache _cache;
    private readonly List<NamedRef> _filterable;

    public ViewFilterRuleBuilder(Document doc, ViewFilterCatalogCache cache)
    {
        _doc = doc;
        _cache = cache;
        _filterable = ViewFilterModel.FilterableCategories(doc);
    }

    /// <summary>The element filter of a tree for these categories; null when the tree is empty or has issues (all are reported).</summary>
    public ElementFilter? Build(ViewFilterNode? node, ICollection<ElementId> categories, List<ViewFilterIssue> issues, string path)
    {
        if (node == null || categories.Count == 0) return null;

        var before = issues.Count;
        var filter = BuildNode(node, categories, issues);
        if (filter == null || issues.Count > before) return null;

        // Revit's own verdict, on the whole tree.
        try
        {
            if (!ParameterFilterElement.ElementFilterIsAcceptableForParameterFilterElement(_doc, new HashSet<ElementId>(categories), filter))
            {
                issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleNotAcceptable, "Revit does not accept this rule tree for a view filter.", path));
                return null;
            }
            if (!ParameterFilterElement.AllRuleParametersApplicable(_doc, categories, filter))
            {
                issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleParameterNotApplicable, "A rule's parameter does not apply to the filter's categories.", path));
                return null;
            }
        }
        catch (Exception ex)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleNotAcceptable, $"Revit refused the rule tree: {ex.Message}", path));
            return null;
        }
        return filter;
    }

    private ElementFilter? BuildNode(ViewFilterNode node, ICollection<ElementId> categories, List<ViewFilterIssue> issues)
    {
        if (node is ViewFilterGroupNode group)
        {
            var children = new List<ElementFilter>();
            foreach (var child in group.Children)
            {
                var built = BuildNode(child, categories, issues);
                if (built != null) children.Add(built);
            }
            if (children.Count != group.Children.Count) return null;
            return group.IsOr ? new LogicalOrFilter(children) : new LogicalAndFilter(children);
        }

        var ruleNode = (ViewFilterRuleNode)node;
        if (ruleNode.Unsupported != null)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleNotSupported, ruleNode.Unsupported, ruleNode.Path, "rule", ruleNode.ParameterName));
            return null;
        }

        ICollection<ElementId> ruleCategories = categories;
        ElementId? onlyCategory = null;
        if (!string.IsNullOrWhiteSpace(ruleNode.OnlyCategory))
        {
            onlyCategory = ViewFilterModel.ResolveCategory(_doc, ruleNode.OnlyCategory!, _filterable, issues, ruleNode.Path);
            if (onlyCategory == null) return null;
            if (!categories.Contains(onlyCategory))
            {
                issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleNotAcceptable, $"onlyCategory '{ruleNode.OnlyCategory}' is not one of the filter's categories.", ruleNode.Path));
                return null;
            }
            ruleCategories = new List<ElementId> { onlyCategory };
        }

        var rule = BuildRule(ruleNode, ruleCategories, issues);
        if (rule == null) return null;
        return onlyCategory == null
            ? new ElementParameterFilter(rule)
            : new ElementParameterFilter(new List<FilterRule> { new FilterCategoryRule(new List<ElementId> { onlyCategory }), rule });
    }

    private FilterRule? BuildRule(ViewFilterRuleNode node, ICollection<ElementId> categories, List<ViewFilterIssue> issues)
    {
        var op = node.Operator;
        if (op == null)
        {
            // The parser has reported it when the spec came from a request.
            if (!issues.Any(i => i.Code == ViewFilterCodes.OperatorUnknown && i.Path == node.Path))
                issues.Add(new ViewFilterIssue(ViewFilterCodes.OperatorUnknown, $"Unknown operator '{node.RawOperator}'.", node.Path));
            return null;
        }
        if (op == ViewFilterOperators.SharedParameterApplicable)
            return ParameterFilterRuleFactory.CreateSharedParameterApplicableRule(node.Parameter);

        var catalog = _cache.For(_doc, categories);
        var match = ViewFilterRefMatcher.Match(node.Parameter, catalog.Refs);
        if (match.Kind == RefMatchKind.Ambiguous)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.ParameterAmbiguous,
                $"Parameter '{node.Parameter}' matches {ViewFilterRefMatcher.List(match.Candidates)}. Use the id, the BuiltInParameter name or the GUID.", node.Path, "parameter", node.Parameter));
            return null;
        }
        if (match.Kind == RefMatchKind.NotFound)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.ParameterNotFilterable,
                $"Parameter '{node.ParameterName ?? node.Parameter}' cannot be used in a filter for these categories (it is unknown here, or not common to all of them). revit_list_filterable_parameters lists those that can.",
                node.Path, "parameter", node.ParameterName ?? node.Parameter));
            return null;
        }

        var parameterId = new ElementId(match.Found!.Id);
        var kind = catalog.KindOf(match.Found.Id);
        var issue = ViewFilterRuleValidator.Check(node, kind, out var value);
        if (issue != null)
        {
            issues.Add(issue);
            return null;
        }

        try
        {
            switch (op)
            {
                case ViewFilterOperators.HasValue:
                    return ParameterFilterRuleFactory.CreateHasValueParameterRule(parameterId);
                case ViewFilterOperators.HasNoValue:
                    return ParameterFilterRuleFactory.CreateHasNoValueParameterRule(parameterId);
                case ViewFilterOperators.AssociatedWithGlobalParameter:
                case ViewFilterOperators.NotAssociatedWithGlobalParameter:
                    var global = ResolveGlobalParameter(node, value, issues);
                    if (global == null) return null;
                    return op == ViewFilterOperators.AssociatedWithGlobalParameter
                        ? ParameterFilterRuleFactory.CreateIsAssociatedWithGlobalParameterRule(parameterId, global)
                        : ParameterFilterRuleFactory.CreateIsNotAssociatedWithGlobalParameterRule(parameterId, global);
            }

            switch (kind)
            {
                case FilterValueKind.Text:
                    return TextRule(op, parameterId, value.Text!);
                case FilterValueKind.Integer:
                case FilterValueKind.YesNo:
                    return IntegerRule(op, parameterId, value.Integer!.Value);
                case FilterValueKind.Number:
                    var spec = catalog.SpecOf(match.Found.Id);
                    if (!ToInternal(node, spec, value.Number!.Value, issues, out var number)) return null;
                    var tolerance = DefaultTolerance;
                    if (node.Tolerance != null)
                    {
                        if (!ToInternal(node, spec, node.Tolerance.Value, issues, out var high) || !ToInternal(node, spec, 0, issues, out var low)) return null;
                        tolerance = Math.Abs(high - low);
                    }
                    return NumberRule(op, parameterId, number, tolerance);
                case FilterValueKind.ElementId:
                    var id = ResolveElement(node, value, categories, issues);
                    return id == null ? null : ElementIdRule(op, parameterId, id);
            }
        }
        catch (Exception ex)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleNotAcceptable, $"Revit refused the rule on '{node.Parameter}': {ex.Message}", node.Path));
            return null;
        }

        issues.Add(new ViewFilterIssue(ViewFilterCodes.ParameterTypeUnknown, $"No rule can be built for parameter '{node.Parameter}'.", node.Path, "parameter", node.Parameter));
        return null;
    }

    private bool ToInternal(ViewFilterRuleNode node, ForgeTypeId? spec, double value, List<ViewFilterIssue> issues, out double result)
    {
        result = value;
        if (node.Units == ViewFilterUnits.Internal) return true;
        if (spec == null)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleUnitsUnknown,
                $"The units of parameter '{node.Parameter}' could not be determined (no element of these categories has it). Give the value in internal units with units=internal.", node.Path, "parameter", node.Parameter));
            return false;
        }
        if (!UnitUtils.IsMeasurableSpec(spec)) return true;
        result = UnitUtils.ConvertToInternalUnits(value, _doc.GetUnits().GetFormatOptions(spec).GetUnitTypeId());
        return true;
    }

    private ElementId? ResolveGlobalParameter(ViewFilterRuleNode node, ViewFilterRuleValue value, List<ViewFilterIssue> issues)
    {
        var id = value.Id != null ? new ElementId(value.Id.Value) : GlobalParametersManager.FindByName(_doc, value.Name ?? string.Empty);
        if (id != ElementId.InvalidElementId && _doc.GetElement(id) is GlobalParameter) return id;
        var shown = value.Name ?? value.Id?.ToString();
        issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleValueNotFound, $"No global parameter '{shown}' in this document.", node.Path, "value", shown));
        return null;
    }

    /// <summary>An element reference value: an id that exists (or a negative built-in or invalid id), or a unique name among levels, phases, materials and the categories' types.</summary>
    private ElementId? ResolveElement(ViewFilterRuleNode node, ViewFilterRuleValue value, ICollection<ElementId> categories, List<ViewFilterIssue> issues)
    {
        if (value.Id != null)
        {
            var id = new ElementId(value.Id.Value);
            if (value.Id.Value <= 0 || _doc.GetElement(id) != null) return id;
            issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleValueNotFound, $"Element {value.Id} of the rule on '{node.Parameter}' does not exist.", node.Path, "value", value.Id.ToString()));
            return null;
        }

        var name = value.Name!;
        var pools = new Func<IEnumerable<Element>>[]
        {
            () => new FilteredElementCollector(_doc).OfClass(typeof(Level)),
            () => new FilteredElementCollector(_doc).OfClass(typeof(Phase)),
            () => new FilteredElementCollector(_doc).OfClass(typeof(Material)),
            () => categories.SelectMany(c => new FilteredElementCollector(_doc).OfCategoryId(c).WhereElementIsElementType())
        };
        foreach (var pool in pools)
        {
            var found = pool().Where(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (found.Count == 1) return found[0].Id;
            if (found.Count > 1)
            {
                issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleValueAmbiguous,
                    $"'{name}' in the rule on '{node.Parameter}' matches {found.Count} elements: {string.Join(", ", found.Take(10).Select(e => e.Id.Value))}. Use the id.", node.Path, "value", name));
                return null;
            }
        }
        issues.Add(new ViewFilterIssue(ViewFilterCodes.RuleValueNotFound, $"No element '{name}' for the rule on '{node.Parameter}'.", node.Path, "value", name));
        return null;
    }

    private static FilterRule TextRule(string op, ElementId p, string v) => op switch
    {
        ViewFilterOperators.EqualsOp => ParameterFilterRuleFactory.CreateEqualsRule(p, v),
        ViewFilterOperators.NotEquals => ParameterFilterRuleFactory.CreateNotEqualsRule(p, v),
        ViewFilterOperators.Greater => ParameterFilterRuleFactory.CreateGreaterRule(p, v),
        ViewFilterOperators.GreaterOrEqual => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(p, v),
        ViewFilterOperators.Less => ParameterFilterRuleFactory.CreateLessRule(p, v),
        ViewFilterOperators.LessOrEqual => ParameterFilterRuleFactory.CreateLessOrEqualRule(p, v),
        ViewFilterOperators.Contains => ParameterFilterRuleFactory.CreateContainsRule(p, v),
        ViewFilterOperators.NotContains => ParameterFilterRuleFactory.CreateNotContainsRule(p, v),
        ViewFilterOperators.BeginsWith => ParameterFilterRuleFactory.CreateBeginsWithRule(p, v),
        ViewFilterOperators.NotBeginsWith => ParameterFilterRuleFactory.CreateNotBeginsWithRule(p, v),
        ViewFilterOperators.EndsWith => ParameterFilterRuleFactory.CreateEndsWithRule(p, v),
        ViewFilterOperators.NotEndsWith => ParameterFilterRuleFactory.CreateNotEndsWithRule(p, v),
        _ => throw new InvalidOperationException($"No text rule for {op}.")
    };

    private static FilterRule IntegerRule(string op, ElementId p, int v) => op switch
    {
        ViewFilterOperators.EqualsOp => ParameterFilterRuleFactory.CreateEqualsRule(p, v),
        ViewFilterOperators.NotEquals => ParameterFilterRuleFactory.CreateNotEqualsRule(p, v),
        ViewFilterOperators.Greater => ParameterFilterRuleFactory.CreateGreaterRule(p, v),
        ViewFilterOperators.GreaterOrEqual => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(p, v),
        ViewFilterOperators.Less => ParameterFilterRuleFactory.CreateLessRule(p, v),
        ViewFilterOperators.LessOrEqual => ParameterFilterRuleFactory.CreateLessOrEqualRule(p, v),
        _ => throw new InvalidOperationException($"No integer rule for {op}.")
    };

    private static FilterRule NumberRule(string op, ElementId p, double v, double epsilon) => op switch
    {
        ViewFilterOperators.EqualsOp => ParameterFilterRuleFactory.CreateEqualsRule(p, v, epsilon),
        ViewFilterOperators.NotEquals => ParameterFilterRuleFactory.CreateNotEqualsRule(p, v, epsilon),
        ViewFilterOperators.Greater => ParameterFilterRuleFactory.CreateGreaterRule(p, v, epsilon),
        ViewFilterOperators.GreaterOrEqual => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(p, v, epsilon),
        ViewFilterOperators.Less => ParameterFilterRuleFactory.CreateLessRule(p, v, epsilon),
        ViewFilterOperators.LessOrEqual => ParameterFilterRuleFactory.CreateLessOrEqualRule(p, v, epsilon),
        _ => throw new InvalidOperationException($"No number rule for {op}.")
    };

    private static FilterRule ElementIdRule(string op, ElementId p, ElementId v) => op switch
    {
        ViewFilterOperators.EqualsOp => ParameterFilterRuleFactory.CreateEqualsRule(p, v),
        ViewFilterOperators.NotEquals => ParameterFilterRuleFactory.CreateNotEqualsRule(p, v),
        ViewFilterOperators.Greater => ParameterFilterRuleFactory.CreateGreaterRule(p, v),
        ViewFilterOperators.GreaterOrEqual => ParameterFilterRuleFactory.CreateGreaterOrEqualRule(p, v),
        ViewFilterOperators.Less => ParameterFilterRuleFactory.CreateLessRule(p, v),
        ViewFilterOperators.LessOrEqual => ParameterFilterRuleFactory.CreateLessOrEqualRule(p, v),
        _ => throw new InvalidOperationException($"No element id rule for {op}.")
    };
}

/// <summary>Filter graphics: from a spec to OverrideGraphicSettings, back to JSON, and across documents.</summary>
internal static class ViewFilterGraphics
{
    /// <summary>Checks the spec's patterns now and returns what sets them later; null when the spec has a problem (in <paramref name="issues"/>).</summary>
    public static Action<OverrideGraphicSettings>? Resolve(Document doc, ViewFilterGraphicsSpec spec, List<ViewFilterIssue> issues, string path)
    {
        var before = issues.Count;
        var projectionPattern = LineId(doc, spec.ProjectionLines?.Pattern, issues, path + ".projectionLines.pattern");
        var cutPattern = LineId(doc, spec.CutLines?.Pattern, issues, path + ".cutLines.pattern");
        var surfaceFore = FillId(doc, spec.SurfaceForeground?.Pattern, issues, path + ".surfaceForeground.pattern");
        var surfaceBack = FillId(doc, spec.SurfaceBackground?.Pattern, issues, path + ".surfaceBackground.pattern");
        var cutFore = FillId(doc, spec.CutForeground?.Pattern, issues, path + ".cutForeground.pattern");
        var cutBack = FillId(doc, spec.CutBackground?.Pattern, issues, path + ".cutBackground.pattern");
        if (issues.Count > before) return null;

        return ogs =>
        {
            if (spec.Halftone != null) ogs.SetHalftone(spec.Halftone.Value);
            if (spec.Transparency != null) ogs.SetSurfaceTransparency(spec.Transparency.Value);

            if (spec.ProjectionLines?.Color != null) ogs.SetProjectionLineColor(ToColor(spec.ProjectionLines.Color));
            if (spec.ProjectionLines?.Weight != null) ogs.SetProjectionLineWeight(spec.ProjectionLines.Weight.Value);
            if (projectionPattern != null) ogs.SetProjectionLinePatternId(projectionPattern);

            if (spec.CutLines?.Color != null) ogs.SetCutLineColor(ToColor(spec.CutLines.Color));
            if (spec.CutLines?.Weight != null) ogs.SetCutLineWeight(spec.CutLines.Weight.Value);
            if (cutPattern != null) ogs.SetCutLinePatternId(cutPattern);

            if (spec.SurfaceForeground?.Color != null) ogs.SetSurfaceForegroundPatternColor(ToColor(spec.SurfaceForeground.Color));
            if (surfaceFore != null) ogs.SetSurfaceForegroundPatternId(surfaceFore);
            if (spec.SurfaceForeground?.Visible != null) ogs.SetSurfaceForegroundPatternVisible(spec.SurfaceForeground.Visible.Value);

            if (spec.SurfaceBackground?.Color != null) ogs.SetSurfaceBackgroundPatternColor(ToColor(spec.SurfaceBackground.Color));
            if (surfaceBack != null) ogs.SetSurfaceBackgroundPatternId(surfaceBack);
            if (spec.SurfaceBackground?.Visible != null) ogs.SetSurfaceBackgroundPatternVisible(spec.SurfaceBackground.Visible.Value);

            if (spec.CutForeground?.Color != null) ogs.SetCutForegroundPatternColor(ToColor(spec.CutForeground.Color));
            if (cutFore != null) ogs.SetCutForegroundPatternId(cutFore);
            if (spec.CutForeground?.Visible != null) ogs.SetCutForegroundPatternVisible(spec.CutForeground.Visible.Value);

            if (spec.CutBackground?.Color != null) ogs.SetCutBackgroundPatternColor(ToColor(spec.CutBackground.Color));
            if (cutBack != null) ogs.SetCutBackgroundPatternId(cutBack);
            if (spec.CutBackground?.Visible != null) ogs.SetCutBackgroundPatternVisible(spec.CutBackground.Visible.Value);
        };
    }

    private static Color ToColor(ViewFilterColor color) => new(color.R, color.G, color.B);

    /// <summary>A line pattern reference: "solid", "none" (back to no override), a name or an id. Null when not given or not found.</summary>
    private static ElementId? LineId(Document doc, string? reference, List<ViewFilterIssue> issues, string path)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var text = reference!.Trim();
        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase)) return ElementId.InvalidElementId;
        if (string.Equals(text, "solid", StringComparison.OrdinalIgnoreCase)) return LinePatternElement.GetSolidPatternId();
        if (long.TryParse(text, out var id) && doc.GetElement(new ElementId(id)) is LinePatternElement byId) return byId.Id;
        var found = LinePatternElement.GetLinePatternElementByName(doc, text);
        if (found != null) return found.Id;
        issues.Add(new ViewFilterIssue(ViewFilterCodes.PatternNotFound, $"No line pattern '{text}' in this document.", path, "pattern", text));
        return null;
    }

    /// <summary>A fill pattern reference: "solid", "none", a name (drafting patterns first) or an id. Null when not given or not found.</summary>
    private static ElementId? FillId(Document doc, string? reference, List<ViewFilterIssue> issues, string path)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var text = reference!.Trim();
        if (string.Equals(text, "none", StringComparison.OrdinalIgnoreCase)) return ElementId.InvalidElementId;
        if (long.TryParse(text, out var id) && doc.GetElement(new ElementId(id)) is FillPatternElement byId) return byId.Id;
        if (string.Equals(text, "solid", StringComparison.OrdinalIgnoreCase))
        {
            var solid = new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)).Cast<FillPatternElement>()
                .FirstOrDefault(p => p.GetFillPattern().IsSolidFill);
            if (solid != null) return solid.Id;
        }
        var found = FindFill(doc, text);
        if (found != null) return found.Id;
        issues.Add(new ViewFilterIssue(ViewFilterCodes.PatternNotFound, $"No fill pattern '{text}' in this document.", path, "pattern", text));
        return null;
    }

    private static FillPatternElement? FindFill(Document doc, string name) =>
        FillPatternElement.GetFillPatternElementByName(doc, FillPatternTarget.Drafting, name)
        ?? FillPatternElement.GetFillPatternElementByName(doc, FillPatternTarget.Model, name);

    /// <summary>
    /// A filter's graphics in another document: the same settings, with line and fill patterns matched by name.
    /// A pattern the target does not have is left unset and named in <paramref name="missing"/>.
    /// </summary>
    public static OverrideGraphicSettings Translate(OverrideGraphicSettings source, Document from, Document to, List<string> missing)
    {
        var result = new OverrideGraphicSettings(source);
        if (from.Equals(to)) return result;

        void Line(ElementId id, Action<ElementId> set)
        {
            if (id == ElementId.InvalidElementId) return;
            if (id == LinePatternElement.GetSolidPatternId()) return;
            var name = from.GetElement(id)?.Name;
            var match = name == null ? null : LinePatternElement.GetLinePatternElementByName(to, name);
            set(match?.Id ?? ElementId.InvalidElementId);
            if (match == null) missing.Add($"line pattern '{name ?? id.Value.ToString()}'");
        }

        void Fill(ElementId id, Action<ElementId> set)
        {
            if (id == ElementId.InvalidElementId) return;
            var name = from.GetElement(id)?.Name;
            var match = name == null ? null : FindFill(to, name);
            set(match?.Id ?? ElementId.InvalidElementId);
            if (match == null) missing.Add($"fill pattern '{name ?? id.Value.ToString()}'");
        }

        Line(source.ProjectionLinePatternId, id => result.SetProjectionLinePatternId(id));
        Line(source.CutLinePatternId, id => result.SetCutLinePatternId(id));
        Fill(source.SurfaceForegroundPatternId, id => result.SetSurfaceForegroundPatternId(id));
        Fill(source.SurfaceBackgroundPatternId, id => result.SetSurfaceBackgroundPatternId(id));
        Fill(source.CutForegroundPatternId, id => result.SetCutForegroundPatternId(id));
        Fill(source.CutBackgroundPatternId, id => result.SetCutBackgroundPatternId(id));
        return result;
    }

    /// <summary>The settings that differ from "no override", in the shape revit_apply_view_filters takes.</summary>
    public static JObject Describe(Document doc, OverrideGraphicSettings ogs)
    {
        var json = new JObject();
        if (ogs.Halftone) json["halftone"] = true;
        if (ogs.Transparency != 0) json["transparency"] = ogs.Transparency;
        Add(json, "projectionLines", Line(doc, ogs.ProjectionLineColor, ogs.ProjectionLineWeight, ogs.ProjectionLinePatternId));
        Add(json, "cutLines", Line(doc, ogs.CutLineColor, ogs.CutLineWeight, ogs.CutLinePatternId));
        Add(json, "surfaceForeground", Fill(doc, ogs.SurfaceForegroundPatternColor, ogs.SurfaceForegroundPatternId, ogs.IsSurfaceForegroundPatternVisible));
        Add(json, "surfaceBackground", Fill(doc, ogs.SurfaceBackgroundPatternColor, ogs.SurfaceBackgroundPatternId, ogs.IsSurfaceBackgroundPatternVisible));
        Add(json, "cutForeground", Fill(doc, ogs.CutForegroundPatternColor, ogs.CutForegroundPatternId, ogs.IsCutForegroundPatternVisible));
        Add(json, "cutBackground", Fill(doc, ogs.CutBackgroundPatternColor, ogs.CutBackgroundPatternId, ogs.IsCutBackgroundPatternVisible));
        return json;
    }

    private static void Add(JObject json, string key, JObject value)
    {
        if (value.Count > 0) json[key] = value;
    }

    private static JObject Line(Document doc, Color color, int weight, ElementId pattern)
    {
        var json = new JObject();
        if (color.IsValid) json["color"] = Hex(color);
        if (weight != OverrideGraphicSettings.InvalidPenNumber) json["weight"] = weight;
        if (pattern != ElementId.InvalidElementId)
            json["pattern"] = pattern == LinePatternElement.GetSolidPatternId() ? "solid" : doc.GetElement(pattern)?.Name ?? pattern.Value.ToString();
        return json;
    }

    private static JObject Fill(Document doc, Color color, ElementId pattern, bool visible)
    {
        var json = new JObject();
        if (color.IsValid) json["color"] = Hex(color);
        if (pattern != ElementId.InvalidElementId) json["pattern"] = doc.GetElement(pattern)?.Name ?? pattern.Value.ToString();
        if (!visible) json["visible"] = false;
        return json;
    }

    private static string Hex(Color color) => new ViewFilterColor(color.Red, color.Green, color.Blue).ToHex();
}

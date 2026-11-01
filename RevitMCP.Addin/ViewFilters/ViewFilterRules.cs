using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.ViewFilters;

/// <summary>Status codes of the view filter tools (#134), per item and as a result's status.</summary>
public static class ViewFilterCodes
{
    public const string InvalidSpec = "invalid_spec";
    public const string FilterNameExists = "filter_name_exists";
    public const string DuplicateNameInRequest = "duplicate_name_in_request";
    public const string InvalidFilterName = "invalid_filter_name";
    public const string NoCategories = "no_categories";
    public const string CategoryNotFound = "category_not_found";
    public const string CategoryNotFilterable = "category_not_filterable";
    public const string CategoryAmbiguous = "category_ambiguous";
    public const string ParameterNotFilterable = "parameter_not_filterable";
    public const string ParameterAmbiguous = "parameter_ambiguous";
    public const string ParameterTypeUnknown = "parameter_type_unknown";
    public const string OperatorUnknown = "operator_unknown";
    public const string OperatorNotValid = "operator_not_valid_for_type";
    public const string RuleValueMissing = "rule_value_missing";
    public const string RuleValueInvalid = "rule_value_invalid";
    public const string RuleValueNotFound = "rule_value_not_found";
    public const string RuleValueAmbiguous = "rule_value_ambiguous";
    public const string RuleUnitsUnknown = "rule_units_unknown";
    public const string RuleNotAcceptable = "rule_not_acceptable";
    public const string RuleParameterNotApplicable = "rule_parameter_not_applicable";
    public const string RuleNotSupported = "rule_not_supported";
    public const string FilterNotFound = "filter_not_found";
    public const string FilterAmbiguous = "filter_ambiguous";
    public const string FilterKindMismatch = "filter_kind_mismatch";
    public const string FilterAlreadyApplied = "filter_already_applied";
    public const string FilterNotApplied = "filter_not_applied";
    public const string FilterNotCopied = "filter_not_copied";
    public const string OrderFilterNotApplied = "order_filter_not_applied";
    public const string ElementNotFound = "element_not_found";
    public const string ViewNotFound = "view_not_found";
    public const string ViewAmbiguous = "view_ambiguous";
    public const string ViewCannotHaveFilters = "view_cannot_have_filters";
    public const string PatternNotFound = "pattern_not_found";
    public const string GraphicsInvalid = "graphics_invalid";
    public const string SourceDocumentNotFound = "source_document_not_found";
    public const string NothingToDo = "nothing_to_do";
    public const string RevitRefused = "revit_refused";
}

/// <summary>One thing wrong with a spec, with where it is. Kind and Name say what could not be resolved, for a copy report.</summary>
public sealed class ViewFilterIssue
{
    public ViewFilterIssue(string code, string reason, string? path = null, string? kind = null, string? name = null)
    {
        Code = code;
        Reason = reason;
        Path = path;
        Kind = kind;
        Name = name;
    }

    public string Code { get; }
    public string Reason { get; }
    public string? Path { get; }
    public string? Kind { get; }
    public string? Name { get; }

    public object Describe() => new { code = Code, reason = Reason, path = Path, kind = Kind, name = Name };
}

/// <summary>What a filterable parameter holds, which decides the operators and the values a rule may use.</summary>
public enum FilterValueKind
{
    Unknown,
    Text,
    Integer,
    YesNo,
    Number,
    ElementId
}

/// <summary>The rule operators, as ParameterFilterRuleFactory offers them, and which parameter types take which.</summary>
public static class ViewFilterOperators
{
    public const string EqualsOp = "equals";
    public const string NotEquals = "notEquals";
    public const string Greater = "greater";
    public const string GreaterOrEqual = "greaterOrEqual";
    public const string Less = "less";
    public const string LessOrEqual = "lessOrEqual";
    public const string Contains = "contains";
    public const string NotContains = "notContains";
    public const string BeginsWith = "beginsWith";
    public const string NotBeginsWith = "notBeginsWith";
    public const string EndsWith = "endsWith";
    public const string NotEndsWith = "notEndsWith";
    public const string HasValue = "hasValue";
    public const string HasNoValue = "hasNoValue";
    public const string AssociatedWithGlobalParameter = "associatedWithGlobalParameter";
    public const string NotAssociatedWithGlobalParameter = "notAssociatedWithGlobalParameter";
    public const string SharedParameterApplicable = "sharedParameterApplicable";

    private static readonly string[] Comparison = { EqualsOp, NotEquals, Greater, GreaterOrEqual, Less, LessOrEqual, HasValue, HasNoValue };

    public static readonly string[] All =
    {
        EqualsOp, NotEquals, Greater, GreaterOrEqual, Less, LessOrEqual, Contains, NotContains, BeginsWith, NotBeginsWith,
        EndsWith, NotEndsWith, HasValue, HasNoValue, AssociatedWithGlobalParameter, NotAssociatedWithGlobalParameter,
        SharedParameterApplicable
    };

    /// <summary>The canonical operator for a spelling (symbols, case and separators ignored), or null.</summary>
    public static string? Normalize(string? op)
    {
        if (string.IsNullOrWhiteSpace(op)) return null;
        var trimmed = op!.Trim();
        switch (trimmed)
        {
            case "=": case "==": return EqualsOp;
            case "!=": case "<>": return NotEquals;
            case ">": return Greater;
            case ">=": return GreaterOrEqual;
            case "<": return Less;
            case "<=": return LessOrEqual;
        }
        var key = new string(trimmed.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        return key switch
        {
            "equals" or "equal" or "eq" => EqualsOp,
            "notequals" or "notequal" or "doesnotequal" or "ne" => NotEquals,
            "greater" or "greaterthan" or "isgreaterthan" or "gt" => Greater,
            "greaterorequal" or "greaterthanorequal" or "isgreaterthanorequalto" or "gte" or "ge" => GreaterOrEqual,
            "less" or "lessthan" or "islessthan" or "lt" => Less,
            "lessorequal" or "lessthanorequal" or "islessthanorequalto" or "lte" or "le" => LessOrEqual,
            "contains" => Contains,
            "notcontains" or "doesnotcontain" => NotContains,
            "beginswith" or "startswith" => BeginsWith,
            "notbeginswith" or "doesnotbeginwith" or "notstartswith" => NotBeginsWith,
            "endswith" => EndsWith,
            "notendswith" or "doesnotendwith" => NotEndsWith,
            "hasvalue" or "hasavalue" => HasValue,
            "hasnovalue" => HasNoValue,
            "associatedwithglobalparameter" or "isassociatedwithglobalparameter" => AssociatedWithGlobalParameter,
            "notassociatedwithglobalparameter" or "isnotassociatedwithglobalparameter" => NotAssociatedWithGlobalParameter,
            "sharedparameterapplicable" => SharedParameterApplicable,
            _ => null
        };
    }

    /// <summary>The operators Revit's rule factory has for a parameter of this kind.</summary>
    public static IReadOnlyList<string> AllowedFor(FilterValueKind kind)
    {
        switch (kind)
        {
            case FilterValueKind.Text:
                return new[]
                {
                    EqualsOp, NotEquals, Greater, GreaterOrEqual, Less, LessOrEqual, Contains, NotContains, BeginsWith,
                    NotBeginsWith, EndsWith, NotEndsWith, HasValue, HasNoValue
                };
            case FilterValueKind.YesNo:
                return new[] { EqualsOp, NotEquals, HasValue, HasNoValue };
            case FilterValueKind.Integer:
            case FilterValueKind.ElementId:
                return Comparison;
            case FilterValueKind.Number:
                return Comparison.Concat(new[] { AssociatedWithGlobalParameter, NotAssociatedWithGlobalParameter }).ToArray();
            default:
                return Array.Empty<string>();
        }
    }

    public static bool IsAllowed(string op, FilterValueKind kind) => AllowedFor(kind).Contains(op);

    public static bool NeedsValue(string op) => op != HasValue && op != HasNoValue && op != SharedParameterApplicable;

    /// <summary>The operator a FilterInverseRule of <paramref name="op"/> means, as Revit's "does not ..." rules are stored.</summary>
    public static string Inverse(string op) => op switch
    {
        EqualsOp => NotEquals,
        NotEquals => EqualsOp,
        Greater => LessOrEqual,
        GreaterOrEqual => Less,
        Less => GreaterOrEqual,
        LessOrEqual => Greater,
        Contains => NotContains,
        NotContains => Contains,
        BeginsWith => NotBeginsWith,
        NotBeginsWith => BeginsWith,
        EndsWith => NotEndsWith,
        NotEndsWith => EndsWith,
        HasValue => HasNoValue,
        HasNoValue => HasValue,
        AssociatedWithGlobalParameter => NotAssociatedWithGlobalParameter,
        NotAssociatedWithGlobalParameter => AssociatedWithGlobalParameter,
        _ => op
    };
}

/// <summary>A node of a filter's rule tree: an AND or OR group, or one rule.</summary>
public abstract class ViewFilterNode
{
    public string Path { get; set; } = "rules";
}

public sealed class ViewFilterGroupNode : ViewFilterNode
{
    public ViewFilterGroupNode(bool isOr) => IsOr = isOr;

    public bool IsOr { get; }
    public List<ViewFilterNode> Children { get; } = new();
}

public sealed class ViewFilterRuleNode : ViewFilterNode
{
    /// <summary>The parameter reference: label, BuiltInParameter name, id or shared parameter GUID.</summary>
    public string Parameter { get; set; } = string.Empty;

    /// <summary>The canonical operator; null when <see cref="RawOperator"/> is unknown.</summary>
    public string? Operator { get; set; }
    public string? RawOperator { get; set; }
    public JToken? Value { get; set; }

    /// <summary>"project" (the project's display units, as in the dialog; the default) or "internal" (Revit's internal units).</summary>
    public string Units { get; set; } = ViewFilterUnits.Project;

    /// <summary>For a rule on a number: the tolerance, in <see cref="Units"/>.</summary>
    public double? Tolerance { get; set; }

    /// <summary>A rule that only applies to this category of the filter (Revit allows it directly under an OR group).</summary>
    public string? OnlyCategory { get; set; }

    // Read back from a model, for results only.
    public string? ParameterName { get; set; }
    public long? ParameterId { get; set; }
    public string? ValueName { get; set; }
    public double? DisplayValue { get; set; }

    /// <summary>Why Revit's stored rule cannot be expressed as a spec; a copy reports it instead of guessing.</summary>
    public string? Unsupported { get; set; }
}

public static class ViewFilterUnits
{
    public const string Project = "project";
    public const string Internal = "internal";
}

/// <summary>A rule's value after checking it against the parameter's kind.</summary>
public sealed class ViewFilterRuleValue
{
    public string? Text { get; set; }
    public int? Integer { get; set; }
    public double? Number { get; set; }
    public long? Id { get; set; }

    /// <summary>An element reference or global parameter given by name, resolved in the document by the caller.</summary>
    public string? Name { get; set; }
}

/// <summary>
/// Reads a filter's rule tree (#134): any nesting of <c>{"and":[...]}</c> and <c>{"or":[...]}</c> with rules
/// <c>{"parameter","operator","value"}</c> as leaves. Pure: JSON in, nodes and issues out.
/// </summary>
public static class ViewFilterRuleParser
{
    public const int MaxDepth = 32;

    /// <summary>The tree, simplified; null with no issue for "no rules" (null or an empty group).</summary>
    public static ViewFilterNode? Parse(JToken? raw, List<ViewFilterIssue> issues, string path = "rules")
    {
        if (raw == null || raw.Type == JTokenType.Null) return null;
        var node = ParseNode(raw, issues, path, 0);
        return node == null ? null : Simplify(node);
    }

    private static ViewFilterNode? ParseNode(JToken raw, List<ViewFilterIssue> issues, string path, int depth)
    {
        if (depth > MaxDepth)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, $"Rule groups are nested deeper than {MaxDepth} levels.", path));
            return null;
        }

        // A bare array is an AND group, the dialog's default.
        if (raw is JArray bare) return ParseGroup(bare, false, issues, path, depth);

        if (raw is not JObject obj)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "A rule node must be {\"and\":[...]}, {\"or\":[...]} or {\"parameter\",\"operator\",\"value\"}.", path));
            return null;
        }

        var and = Prop(obj, "and");
        var or = Prop(obj, "or");
        var parameter = Prop(obj, "parameter");
        if ((and != null ? 1 : 0) + (or != null ? 1 : 0) + (parameter != null ? 1 : 0) != 1)
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "A rule node needs exactly one of 'and', 'or' or 'parameter'.", path));
            return null;
        }

        if (and != null || or != null)
        {
            if ((and ?? or) is not JArray children)
            {
                issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, $"'{(and != null ? "and" : "or")}' must be an array of rule nodes.", path));
                return null;
            }
            return ParseGroup(children, or != null, issues, path, depth);
        }

        return ParseRule(obj, parameter!, issues, path);
    }

    private static ViewFilterNode ParseGroup(JArray children, bool isOr, List<ViewFilterIssue> issues, string path, int depth)
    {
        var group = new ViewFilterGroupNode(isOr) { Path = path };
        var label = isOr ? "or" : "and";
        for (var i = 0; i < children.Count; i++)
        {
            var child = ParseNode(children[i], issues, $"{path}.{label}[{i}]", depth + 1);
            if (child != null) group.Children.Add(child);
        }
        return group;
    }

    private static ViewFilterRuleNode? ParseRule(JObject obj, JToken parameter, List<ViewFilterIssue> issues, string path)
    {
        var name = parameter.Type is JTokenType.String or JTokenType.Integer ? parameter.ToString() : string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "A rule's 'parameter' must be a name, a BuiltInParameter name, an id or a shared parameter GUID.", path));
            return null;
        }

        var rule = new ViewFilterRuleNode { Parameter = name.Trim(), Path = path, Value = Prop(obj, "value") };
        rule.RawOperator = Prop(obj, "operator")?.ToString();
        rule.Operator = ViewFilterOperators.Normalize(rule.RawOperator);
        if (rule.Operator == null)
            issues.Add(new ViewFilterIssue(ViewFilterCodes.OperatorUnknown,
                $"Unknown operator '{rule.RawOperator}'. Operators: {string.Join(", ", ViewFilterOperators.All)}.", path));

        var units = Prop(obj, "units")?.ToString();
        if (!string.IsNullOrWhiteSpace(units))
        {
            var key = units!.Trim().ToLowerInvariant();
            if (key != ViewFilterUnits.Project && key != ViewFilterUnits.Internal)
                issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, $"A rule's 'units' must be project or internal (got '{units}').", path));
            else rule.Units = key;
        }

        var tolerance = Prop(obj, "tolerance");
        if (tolerance != null)
        {
            if (TryNumber(tolerance, out var t) && t >= 0) rule.Tolerance = t;
            else issues.Add(new ViewFilterIssue(ViewFilterCodes.InvalidSpec, "A rule's 'tolerance' must be a number of 0 or more.", path));
        }

        var only = Prop(obj, "onlyCategory");
        if (only != null) rule.OnlyCategory = only.ToString();
        return rule;
    }

    /// <summary>Removes single-child groups and empty groups: they mean the same to Revit, and its logical filters want two or more children.</summary>
    public static ViewFilterNode? Simplify(ViewFilterNode node)
    {
        if (node is not ViewFilterGroupNode group) return node;
        var simplified = new ViewFilterGroupNode(group.IsOr) { Path = group.Path };
        foreach (var child in group.Children)
        {
            var kept = Simplify(child);
            if (kept != null) simplified.Children.Add(kept);
        }
        return simplified.Children.Count switch
        {
            0 => null,
            1 => simplified.Children[0],
            _ => simplified
        };
    }

    public static int CountRules(ViewFilterNode? node) => node switch
    {
        null => 0,
        ViewFilterGroupNode g => g.Children.Sum(CountRules),
        _ => 1
    };

    public static IEnumerable<ViewFilterRuleNode> Rules(ViewFilterNode? node)
    {
        if (node is ViewFilterRuleNode rule) yield return rule;
        else if (node is ViewFilterGroupNode group)
            foreach (var r in group.Children.SelectMany(Rules)) yield return r;
    }

    /// <summary>One line for summaries and comparing definitions: <c>(Comments contains "x" OR Mark equals "y") AND Level equals "1"</c>.</summary>
    public static string Describe(ViewFilterNode? node, bool top = true)
    {
        switch (node)
        {
            case null:
                return "no rules";
            case ViewFilterGroupNode group:
                var inner = string.Join(group.IsOr ? " OR " : " AND ", group.Children.Select(c => Describe(c, false)));
                return top ? inner : $"({inner})";
            case ViewFilterRuleNode rule:
                if (rule.Unsupported != null) return $"[{rule.Unsupported}]";
                var label = rule.ParameterName ?? rule.Parameter;
                var op = rule.Operator ?? rule.RawOperator;
                var value = rule.ValueName ?? (rule.Value == null || rule.Value.Type == JTokenType.Null ? null : rule.Value.ToString(Newtonsoft.Json.Formatting.None));
                return value == null ? $"{label} {op}" : $"{label} {op} {value}";
            default:
                return string.Empty;
        }
    }

    /// <summary>The tree as the JSON the tools take, so what a read returns can be passed back. Null for no rules.</summary>
    public static JToken? ToJson(ViewFilterNode? node)
    {
        switch (node)
        {
            case ViewFilterGroupNode group:
                return new JObject { [group.IsOr ? "or" : "and"] = new JArray(group.Children.Select(ToJson).Where(c => c != null)) };
            case ViewFilterRuleNode rule:
                var json = new JObject { ["parameter"] = rule.Parameter };
                if (rule.ParameterName != null) json["parameterName"] = rule.ParameterName;
                if (rule.ParameterId != null) json["parameterId"] = rule.ParameterId;
                if (rule.Operator != null) json["operator"] = rule.Operator;
                if (rule.Value != null && rule.Value.Type != JTokenType.Null) json["value"] = rule.Value;
                if (rule.ValueName != null) json["valueName"] = rule.ValueName;
                if (rule.Units != ViewFilterUnits.Project) json["units"] = rule.Units;
                if (rule.DisplayValue != null) json["displayValue"] = rule.DisplayValue;
                if (rule.Tolerance != null) json["tolerance"] = rule.Tolerance;
                if (rule.OnlyCategory != null) json["onlyCategory"] = rule.OnlyCategory;
                if (rule.Unsupported != null) json["unsupported"] = rule.Unsupported;
                return json;
            default:
                return null;
        }
    }

    internal static JToken? Prop(JObject obj, string name)
    {
        var value = obj.GetValue(name, StringComparison.OrdinalIgnoreCase);
        return value == null || value.Type == JTokenType.Null ? null : value;
    }

    internal static bool TryNumber(JToken token, out double value)
    {
        value = 0;
        switch (token.Type)
        {
            case JTokenType.Integer:
            case JTokenType.Float:
                value = token.Value<double>();
                return !double.IsNaN(value) && !double.IsInfinity(value);
            case JTokenType.String:
                return double.TryParse(token.ToString().Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                       && !double.IsNaN(value) && !double.IsInfinity(value);
            default:
                return false;
        }
    }
}

/// <summary>Checks a rule's operator and value against the parameter's kind. Pure.</summary>
public static class ViewFilterRuleValidator
{
    /// <summary>The issue with this rule for a parameter of this kind, or null; <paramref name="value"/> holds the converted value.</summary>
    public static ViewFilterIssue? Check(ViewFilterRuleNode rule, FilterValueKind kind, out ViewFilterRuleValue value)
    {
        value = new ViewFilterRuleValue();
        var op = rule.Operator;
        if (op == null)
            return new ViewFilterIssue(ViewFilterCodes.OperatorUnknown, $"Unknown operator '{rule.RawOperator}'.", rule.Path);
        if (op == ViewFilterOperators.SharedParameterApplicable) return null;
        if (kind == FilterValueKind.Unknown)
            return new ViewFilterIssue(ViewFilterCodes.ParameterTypeUnknown,
                $"The type of parameter '{rule.Parameter}' could not be determined, so no rule can be built for it.", rule.Path, "parameter", rule.Parameter);
        if (!ViewFilterOperators.IsAllowed(op, kind))
            return new ViewFilterIssue(ViewFilterCodes.OperatorNotValid,
                $"Operator {op} does not work on a {kind} parameter ('{rule.Parameter}'). Allowed: {string.Join(", ", ViewFilterOperators.AllowedFor(kind))}.", rule.Path);
        if (!ViewFilterOperators.NeedsValue(op)) return null;

        var raw = rule.Value;
        if (raw == null || raw.Type == JTokenType.Null)
            return new ViewFilterIssue(ViewFilterCodes.RuleValueMissing, $"Operator {op} needs a value ('{rule.Parameter}').", rule.Path);

        if (op is ViewFilterOperators.AssociatedWithGlobalParameter or ViewFilterOperators.NotAssociatedWithGlobalParameter)
            return ReadReference(rule, raw, value, "a global parameter name or id");

        switch (kind)
        {
            case FilterValueKind.Text:
                if (raw.Type is JTokenType.Object or JTokenType.Array or JTokenType.Boolean)
                    return Invalid(rule, "a text value");
                value.Text = raw.ToString();
                return null;
            case FilterValueKind.Number:
                if (!ViewFilterRuleParser.TryNumber(raw, out var number)) return Invalid(rule, "a number");
                value.Number = number;
                return null;
            case FilterValueKind.Integer:
                if (raw.Type == JTokenType.Boolean)
                {
                    value.Integer = raw.Value<bool>() ? 1 : 0;
                    return null;
                }
                if (!ViewFilterRuleParser.TryNumber(raw, out var whole) || whole != Math.Floor(whole) || whole < int.MinValue || whole > int.MaxValue)
                    return Invalid(rule, "a whole number");
                value.Integer = (int)whole;
                return null;
            case FilterValueKind.YesNo:
                if (!TryYesNo(raw, out var flag)) return Invalid(rule, "true or false (or 1, 0, yes, no)");
                value.Integer = flag ? 1 : 0;
                return null;
            case FilterValueKind.ElementId:
                return ReadReference(rule, raw, value, "an element id or an element name");
            default:
                return Invalid(rule, "a value of a known type");
        }
    }

    private static ViewFilterIssue? ReadReference(ViewFilterRuleNode rule, JToken raw, ViewFilterRuleValue value, string wanted)
    {
        if (raw.Type == JTokenType.Integer)
        {
            value.Id = raw.Value<long>();
            return null;
        }
        if (raw.Type != JTokenType.String || string.IsNullOrWhiteSpace(raw.ToString())) return Invalid(rule, wanted);
        var text = raw.ToString().Trim();
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id)) value.Id = id;
        else value.Name = text;
        return null;
    }

    private static ViewFilterIssue Invalid(ViewFilterRuleNode rule, string wanted) =>
        new(ViewFilterCodes.RuleValueInvalid,
            $"Parameter '{rule.Parameter}' needs {wanted}; got {rule.Value?.ToString(Newtonsoft.Json.Formatting.None)}.", rule.Path);

    private static bool TryYesNo(JToken raw, out bool flag)
    {
        flag = false;
        switch (raw.Type)
        {
            case JTokenType.Boolean:
                flag = raw.Value<bool>();
                return true;
            case JTokenType.Integer:
                var n = raw.Value<long>();
                flag = n == 1;
                return n is 0 or 1;
            case JTokenType.String:
                switch (raw.ToString().Trim().ToLowerInvariant())
                {
                    case "true": case "yes": case "1":
                        flag = true;
                        return true;
                    case "false": case "no": case "0":
                        return true;
                }
                return false;
            default:
                return false;
        }
    }
}

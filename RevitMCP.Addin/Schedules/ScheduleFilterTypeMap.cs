using System.Globalization;

namespace RevitMCP.Addin.Schedules;

/// <summary>
/// Maps the filter types an agent writes (equals, notEquals, contains, &gt;=, …) onto Revit's ScheduleFilterType member
/// names, and coerces a filter value to what the field holds (#103). Pure: the add-in turns the canonical name into
/// the enum with Enum.Parse, so a test here covers what Revit receives.
/// </summary>
public static class ScheduleFilterTypeMap
{
    public const string Equal = "Equal";
    public const string NotEqual = "NotEqual";
    public const string GreaterThan = "GreaterThan";
    public const string GreaterThanOrEqual = "GreaterThanOrEqual";
    public const string LessThan = "LessThan";
    public const string LessThanOrEqual = "LessThanOrEqual";
    public const string Contains = "Contains";
    public const string NotContains = "NotContains";
    public const string BeginsWith = "BeginsWith";
    public const string NotBeginsWith = "NotBeginsWith";
    public const string EndsWith = "EndsWith";
    public const string NotEndsWith = "NotEndsWith";
    public const string HasValue = "HasValue";
    public const string HasNoValue = "HasNoValue";
    public const string HasParameter = "HasParameter";

    /// <summary>Value kinds a schedule field can hold, as the add-in reads them from the field's spec.</summary>
    public const string KindString = "string";
    public const string KindDouble = "double";
    public const string KindInteger = "integer";
    public const string KindElementId = "elementId";
    public const string KindUnknown = "unknown";

    private static readonly Dictionary<string, string> Aliases = Build(
        (Equal, new[] { "equals", "equal", "eq", "=", "==", "is" }),
        (NotEqual, new[] { "notequals", "notequal", "ne", "neq", "!=", "<>", "isnot", "doesnotequal" }),
        (GreaterThan, new[] { "greaterthan", "gt", ">", "isgreaterthan" }),
        (GreaterThanOrEqual, new[] { "greaterthanorequal", "greaterthanorequals", "gte", "ge", ">=", "isgreaterthanorequalto" }),
        (LessThan, new[] { "lessthan", "lt", "<", "islessthan" }),
        (LessThanOrEqual, new[] { "lessthanorequal", "lessthanorequals", "lte", "le", "<=", "islessthanorequalto" }),
        (Contains, new[] { "contains", "like" }),
        (NotContains, new[] { "notcontains", "doesnotcontain", "notlike" }),
        (BeginsWith, new[] { "beginswith", "startswith" }),
        (NotBeginsWith, new[] { "notbeginswith", "doesnotbeginwith", "notstartswith", "doesnotstartwith" }),
        (EndsWith, new[] { "endswith" }),
        (NotEndsWith, new[] { "notendswith", "doesnotendwith" }),
        (HasValue, new[] { "hasvalue", "notempty", "isnotempty", "hasavalue" }),
        (HasNoValue, new[] { "hasnovalue", "empty", "isempty" }),
        (HasParameter, new[] { "hasparameter", "exists", "parameterexists" }));

    /// <summary>The filter types accepted, for error messages and the tool descriptions.</summary>
    public const string AcceptedText =
        "equals, notEquals, greaterThan, greaterThanOrEqual, lessThan, lessThanOrEqual, contains, notContains, " +
        "beginsWith, notBeginsWith, endsWith, notEndsWith, hasValue, hasNoValue, hasParameter";

    /// <summary>Canonical ScheduleFilterType name for an alias (case, spaces, '-' and '_' ignored).</summary>
    public static bool TryMap(string? raw, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var key = Normalize(raw!);
        if (Aliases.TryGetValue(key, out var hit))
        {
            canonical = hit;
            return true;
        }
        // The canonical names themselves, as Revit spells them.
        foreach (var name in Aliases.Values.Distinct())
            if (string.Equals(name, raw!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                canonical = name;
                return true;
            }
        return false;
    }

    /// <summary>The camelCase alias an agent would write for a canonical name (reading a definition back).</summary>
    public static string ToAlias(string canonical) => canonical switch
    {
        Equal => "equals",
        NotEqual => "notEquals",
        _ => canonical.Length == 0 ? canonical : char.ToLowerInvariant(canonical[0]) + canonical.Substring(1)
    };

    /// <summary>False for the presence filters, which take no value.</summary>
    public static bool NeedsValue(string canonical) => canonical is not (HasValue or HasNoValue or HasParameter);

    public static bool IsSubstring(string canonical) =>
        canonical is Contains or NotContains or BeginsWith or NotBeginsWith or EndsWith or NotEndsWith;

    public static bool IsComparison(string canonical) =>
        canonical is GreaterThan or GreaterThanOrEqual or LessThan or LessThanOrEqual;

    /// <summary>
    /// Coerces a filter value to the field's value kind. A number for a text field becomes its text; "yes"/"no" for an
    /// integer field become 1/0; a non-numeric value for an element-id field (Level, Phase, a type) is kept as a name for
    /// the add-in to look up. Substring filters only work on text.
    /// </summary>
    public static ScheduleFilterValue Coerce(string canonical, string? text, double? number, string valueKind)
    {
        if (!NeedsValue(canonical))
            return new ScheduleFilterValue { Kind = "none", Note = "This filter type takes no value." };
        if (text == null && !number.HasValue)
            return Error($"A '{ToAlias(canonical)}' filter needs a value.");

        var raw = text ?? number!.Value.ToString(CultureInfo.InvariantCulture);
        var parsed = number ?? ParseNumber(raw);

        switch (valueKind)
        {
            case KindString:
                return new ScheduleFilterValue { Kind = KindString, Text = raw };

            case KindInteger:
                if (IsSubstring(canonical)) return Error($"'{ToAlias(canonical)}' works on text fields only; this field holds whole numbers.");
                var flag = YesNo(raw);
                if (flag.HasValue) return new ScheduleFilterValue { Kind = KindInteger, Integer = flag.Value ? 1 : 0, Note = $"'{raw}' read as {(flag.Value ? 1 : 0)}." };
                if (parsed.HasValue && Math.Abs(parsed.Value - Math.Round(parsed.Value)) < 1e-9 && Math.Abs(parsed.Value) <= int.MaxValue)
                    return new ScheduleFilterValue { Kind = KindInteger, Integer = (int)Math.Round(parsed.Value) };
                return Error($"The field holds whole numbers; '{raw}' is not one.");

            case KindDouble:
                if (IsSubstring(canonical)) return Error($"'{ToAlias(canonical)}' works on text fields only; this field holds numbers.");
                if (parsed.HasValue) return new ScheduleFilterValue { Kind = KindDouble, Double = parsed.Value };
                return Error($"The field holds numbers; '{raw}' is not a number.");

            case KindElementId:
                if (IsSubstring(canonical)) return Error($"'{ToAlias(canonical)}' works on text fields only; this field refers to an element (level, phase, type…).");
                if (parsed.HasValue && Math.Abs(parsed.Value - Math.Round(parsed.Value)) < 1e-9)
                    return new ScheduleFilterValue { Kind = KindElementId, ElementId = (long)Math.Round(parsed.Value), Text = raw, Note = "Read as an element id (looked up by name when no element has it)." };
                return new ScheduleFilterValue { Kind = "elementName", Text = raw, Note = $"'{raw}' is looked up by name (level, phase, type)." };

            default:
                if (number.HasValue && !IsSubstring(canonical))
                    return new ScheduleFilterValue { Kind = KindDouble, Double = number.Value, Note = "The field's value type is unknown; the number is used as is." };
                return new ScheduleFilterValue { Kind = KindString, Text = raw, Note = "The field's value type is unknown; the value is compared as text." };
        }
    }

    private static ScheduleFilterValue Error(string message) => new() { Kind = "error", Error = message };

    internal static double? ParseNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text!.Trim().Replace(',', '.');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static bool? YesNo(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "yes" or "true" or "jah" => true,
        "no" or "false" or "ei" => false,
        _ => null
    };

    private static string Normalize(string raw)
    {
        var chars = raw.Trim().ToLowerInvariant().Where(c => c != ' ' && c != '_' && c != '-').ToArray();
        return new string(chars);
    }

    private static Dictionary<string, string> Build(params (string Canonical, string[] Aliases)[] entries)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (canonical, aliases) in entries)
            foreach (var alias in aliases)
                map[alias] = canonical;
        return map;
    }
}

/// <summary>A filter value after coercion: which ScheduleFilter constructor the add-in calls, and with what.</summary>
public sealed class ScheduleFilterValue
{
    /// <summary>none | string | double | integer | elementId | elementName (look up by name) | error.</summary>
    public string Kind { get; set; } = "none";
    public string? Text { get; set; }
    public double? Double { get; set; }
    public int? Integer { get; set; }
    public long? ElementId { get; set; }
    public string? Note { get; set; }
    public string? Error { get; set; }
    public bool IsError => Kind == "error";
}

/// <summary>The grand-total choices of the schedule's Sorting/Grouping tab.</summary>
public static class ScheduleGrandTotal
{
    public const string None = "none";
    public const string Title = "title";
    public const string Count = "count";
    public const string Totals = "totals";
    public const string TitleCountTotals = "titleCountTotals";

    public const string AcceptedText = "none, title (title and totals), count (count and totals), totals (totals only), titleCountTotals";

    /// <summary>ShowGrandTotal, ShowGrandTotalTitle and ShowGrandTotalCount for a choice.</summary>
    public static bool TryMap(string? raw, out bool show, out bool title, out bool count)
    {
        show = title = count = false;
        var key = (raw ?? string.Empty).Trim().ToLowerInvariant().Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(",", string.Empty).Replace("and", string.Empty);
        switch (key)
        {
            case "none": case "off": case "false": case "no":
                return true;
            case "title": case "titletotals":
                show = title = true;
                return true;
            case "count": case "counttotals":
                show = count = true;
                return true;
            case "totals": case "totalsonly": case "true": case "yes":
                show = true;
                return true;
            case "titlecounttotals": case "all":
                show = title = count = true;
                return true;
            default:
                return false;
        }
    }

    /// <summary>The choice name for the three flags (reading a definition back).</summary>
    public static string Describe(bool show, bool title, bool count) =>
        !show ? None : title && count ? TitleCountTotals : title ? Title : count ? Count : Totals;
}

/// <summary>Short unit names for field formats, mapped to Revit's UnitTypeId property names.</summary>
public static class ScheduleFormatUnits
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mm"] = "Millimeters", ["millimeters"] = "Millimeters", ["millimetres"] = "Millimeters",
        ["cm"] = "Centimeters", ["m"] = "Meters", ["meters"] = "Meters", ["metres"] = "Meters",
        ["ft"] = "Feet", ["feet"] = "Feet", ["in"] = "Inches", ["inches"] = "Inches",
        ["mm2"] = "SquareMillimeters", ["mm²"] = "SquareMillimeters", ["m2"] = "SquareMeters", ["m²"] = "SquareMeters",
        ["m3"] = "CubicMeters", ["m³"] = "CubicMeters",
        ["w"] = "Watts", ["kw"] = "Kilowatts", ["va"] = "VoltAmperes", ["kva"] = "KilovoltAmperes",
        ["a"] = "Amperes", ["ma"] = "Milliamperes", ["ka"] = "Kiloamperes", ["v"] = "Volts", ["kv"] = "Kilovolts",
        ["hz"] = "Hertz", ["lx"] = "Lux", ["lm"] = "Lumens", ["kg"] = "Kilograms",
        ["deg"] = "Degrees", ["°"] = "Degrees", ["degrees"] = "Degrees", ["%"] = "Percentage", ["percent"] = "Percentage",
        ["general"] = "General", ["fixed"] = "Fixed"
    };

    /// <summary>
    /// The UnitTypeId property name for a unit: a known short name, or the text itself when it already looks like a
    /// property name (letters only, starting upper case — the add-in checks it exists).
    /// </summary>
    public static bool TryResolve(string? units, out string propertyName)
    {
        propertyName = string.Empty;
        if (string.IsNullOrWhiteSpace(units)) return false;
        var trimmed = units!.Trim();
        if (Map.TryGetValue(trimmed, out var hit))
        {
            propertyName = hit;
            return true;
        }
        if (char.IsUpper(trimmed[0]) && trimmed.All(char.IsLetter))
        {
            propertyName = trimmed;
            return true;
        }
        return false;
    }
}

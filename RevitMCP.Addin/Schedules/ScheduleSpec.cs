using System.Globalization;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Schedules;

// Schedule specifications (#103): what revit_create_schedule and revit_edit_schedule are asked to build, parsed from
// the tool arguments. Pure — no Revit API — so the parsing and the planner are unit-tested in RevitMCP.Tests.

/// <summary>What a requested field is: a parameter from the schedulable-field catalogue, or a computed column.</summary>
public enum ScheduleFieldKind
{
    Parameter,
    Count,
    Formula,
    Percentage
}

/// <summary>Number formatting of a field. Units are a short name (mm, m2, kW, …) or a UnitTypeId property name.</summary>
public sealed class ScheduleFieldFormatSpec
{
    public string? Units { get; set; }
    public double? Accuracy { get; set; }
    public bool? SuppressTrailingZeros { get; set; }

    public override string ToString()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Units)) parts.Add(Units!);
        if (Accuracy.HasValue) parts.Add("accuracy " + Accuracy.Value.ToString(CultureInfo.InvariantCulture));
        if (SuppressTrailingZeros == true) parts.Add("no trailing zeros");
        return parts.Count == 0 ? "project default" : string.Join(", ", parts);
    }
}

/// <summary>One requested column.</summary>
public sealed class ScheduleFieldSpec
{
    public ScheduleFieldKind Kind { get; set; } = ScheduleFieldKind.Parameter;

    /// <summary>Field name as Revit lists it (GetName), for <see cref="ScheduleFieldKind.Parameter"/>.</summary>
    public string Parameter { get; set; } = string.Empty;

    /// <summary>Optional disambiguation when a name exists in several field types: instance, type, room, fromRoom, …</summary>
    public string? FieldType { get; set; }

    public string? FormulaName { get; set; }
    public string? FormulaExpression { get; set; }

    /// <summary>Percentage fields: the (numeric) field the percentage is of.</summary>
    public string? PercentageOf { get; set; }

    public string? Heading { get; set; }
    public double? WidthMm { get; set; }
    public bool? Hidden { get; set; }
    public bool? Totals { get; set; }
    public ScheduleFieldFormatSpec? Format { get; set; }

    /// <summary>Edit only: 0-based position in the resulting field order.</summary>
    public int? InsertAt { get; set; }

    /// <summary>A short label for messages.</summary>
    public string Label => Kind switch
    {
        ScheduleFieldKind.Count => "Count",
        ScheduleFieldKind.Formula => string.IsNullOrWhiteSpace(FormulaName) ? "formula" : FormulaName!,
        ScheduleFieldKind.Percentage => $"% of {PercentageOf}",
        _ => Parameter
    };
}

/// <summary>One filter: field name, filter type (equals, contains, greaterThan, …) and value.</summary>
public sealed class ScheduleFilterSpec
{
    public string Field { get; set; } = string.Empty;
    public string Type { get; set; } = "equals";

    /// <summary>The value as text (numbers are kept in <see cref="Number"/> too); null when none was given.</summary>
    public string? Value { get; set; }

    /// <summary>The value when it arrived as a JSON number.</summary>
    public double? Number { get; set; }

    public bool HasValue => Value != null || Number.HasValue;
}

/// <summary>One sorting/grouping level.</summary>
public sealed class ScheduleSortSpec
{
    public string Field { get; set; } = string.Empty;
    public bool Descending { get; set; }
    public bool Header { get; set; }
    public bool Footer { get; set; }
    public bool BlankLine { get; set; }
}

/// <summary>revit_create_schedule / revit_preview_create_schedule arguments.</summary>
public sealed class ScheduleSpec
{
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool MultiCategory { get; set; }
    public bool KeySchedule { get; set; }
    public string? KeyName { get; set; }
    public List<ScheduleFieldSpec> Fields { get; set; } = new();
    public List<ScheduleFilterSpec> Filters { get; set; } = new();
    public List<ScheduleSortSpec> Sort { get; set; } = new();
    public bool Itemize { get; set; } = true;

    /// <summary>none | title | count | totals | titleCountTotals; null keeps Revit's default (no grand total).</summary>
    public string? GrandTotal { get; set; }

    public string? Template { get; set; }
    public string? Phase { get; set; }
    public bool AllowMissingFields { get; set; }
}

/// <summary>revit_edit_schedule / revit_preview_edit_schedule arguments.</summary>
public sealed class ScheduleEditSpec
{
    public List<ScheduleFieldSpec> AddFields { get; set; } = new();

    /// <summary>Field names, or 0-based indexes written as numbers.</summary>
    public List<string> RemoveFields { get; set; } = new();

    /// <summary>New order by field name; fields not listed keep their relative order after the listed ones.</summary>
    public List<string> Order { get; set; } = new();

    /// <summary>Field → new column heading, in the order given.</summary>
    public List<KeyValuePair<string, string>> Rename { get; set; } = new();

    public List<string> Hide { get; set; } = new();
    public List<string> Show { get; set; } = new();

    /// <summary>Field → number format.</summary>
    public List<KeyValuePair<string, ScheduleFieldFormatSpec>> Format { get; set; } = new();

    /// <summary>Field → column width in mm.</summary>
    public List<KeyValuePair<string, double>> Widths { get; set; } = new();

    /// <summary>Replaces every filter when not null (an empty list clears them).</summary>
    public List<ScheduleFilterSpec>? SetFilters { get; set; }

    /// <summary>Replaces every sorting/grouping level when not null (an empty list clears them).</summary>
    public List<ScheduleSortSpec>? SetSort { get; set; }

    public bool? Itemize { get; set; }
    public string? GrandTotal { get; set; }
    public bool AllowMissingFields { get; set; }

    public bool IsEmpty =>
        AddFields.Count == 0 && RemoveFields.Count == 0 && Order.Count == 0 && Rename.Count == 0 && Hide.Count == 0 &&
        Show.Count == 0 && Format.Count == 0 && Widths.Count == 0 && SetFilters == null && SetSort == null &&
        Itemize == null && GrandTotal == null;

    /// <summary>True when the edit changes more than the fields (filters, sorting, itemize, grand total).</summary>
    public bool ChangesStructure => SetFilters != null || SetSort != null || Itemize != null || GrandTotal != null;
}

/// <summary>Turns tool arguments (JSON tokens after the pipe, or plain values in tests) into specs.</summary>
public static class ScheduleSpecParser
{
    public static ScheduleSpec ParseCreate(IDictionary<string, object?> args, List<string> errors)
    {
        var spec = new ScheduleSpec
        {
            Name = Text(args, "name") ?? string.Empty,
            Category = Text(args, "category") ?? string.Empty,
            MultiCategory = Flag(args, "multiCategory") ?? false,
            KeySchedule = Flag(args, "keySchedule") ?? false,
            KeyName = NullIfEmpty(Text(args, "keyName")),
            Itemize = Flag(args, "itemize") ?? true,
            GrandTotal = NullIfEmpty(Text(args, "grandTotal")),
            Template = NullIfEmpty(Text(args, "template")),
            Phase = NullIfEmpty(Text(args, "phase")),
            AllowMissingFields = Flag(args, "allowMissingFields") ?? false
        };
        spec.Fields = Fields(Get(args, "fields"), "fields", errors);
        spec.Filters = Filters(Get(args, "filters"), "filters", errors) ?? new List<ScheduleFilterSpec>();
        spec.Sort = Sorts(Get(args, "sort"), "sort", errors) ?? new List<ScheduleSortSpec>();

        if (spec.MultiCategory && spec.KeySchedule)
            errors.Add("multiCategory and keySchedule cannot be combined.");
        if (!spec.MultiCategory && string.IsNullOrWhiteSpace(spec.Category))
            errors.Add("category is required (or multiCategory=true).");
        if (spec.Fields.Count == 0 && !spec.KeySchedule)
            errors.Add("fields is required: list the columns in order, e.g. [\"Mark\", \"Type\", {\"count\": true}].");
        return spec;
    }

    public static ScheduleEditSpec ParseEdit(IDictionary<string, object?> args, List<string> errors)
    {
        var edit = new ScheduleEditSpec
        {
            AddFields = Fields(Get(args, "addFields"), "addFields", errors),
            RemoveFields = Names(Get(args, "removeFields")),
            Order = Names(Get(args, "order")),
            Rename = Pairs(Get(args, "rename"), "rename", errors),
            Hide = Names(Get(args, "hide")),
            Show = Names(Get(args, "show")),
            SetFilters = Filters(Get(args, "setFilters"), "setFilters", errors),
            SetSort = Sorts(Get(args, "setSort"), "setSort", errors),
            Itemize = Flag(args, "itemize"),
            GrandTotal = NullIfEmpty(Text(args, "grandTotal")),
            AllowMissingFields = Flag(args, "allowMissingFields") ?? false
        };

        if (Token(Get(args, "format")) is JObject format)
            foreach (var property in format.Properties())
                if (property.Value is JObject spec)
                    edit.Format.Add(new KeyValuePair<string, ScheduleFieldFormatSpec>(property.Name, ParseFormat(spec)));
                else
                    errors.Add($"format.{property.Name} must be an object {{units, accuracy, suppressTrailingZeros}}.");

        if (Token(Get(args, "widths")) is JObject widths)
            foreach (var property in widths.Properties())
                if (Number(property.Value) is { } width)
                    edit.Widths.Add(new KeyValuePair<string, double>(property.Name, width));
                else
                    errors.Add($"widths.{property.Name} must be a number (mm).");
        return edit;
    }

    // ── Pieces ──────────────────────────────────────────────────────────────

    private static List<ScheduleFieldSpec> Fields(object? raw, string key, List<string> errors)
    {
        var result = new List<ScheduleFieldSpec>();
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return result;
        if (token is not JArray array)
        {
            errors.Add($"{key} must be an array of field names or field objects.");
            return result;
        }

        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i];
            if (item.Type == JTokenType.String)
            {
                var name = item.Value<string>()!.Trim();
                if (name.Length == 0) { errors.Add($"{key}[{i}] is empty."); continue; }
                result.Add(string.Equals(name, "count", StringComparison.OrdinalIgnoreCase)
                    ? new ScheduleFieldSpec { Kind = ScheduleFieldKind.Count }
                    : new ScheduleFieldSpec { Parameter = name });
                continue;
            }
            if (item is not JObject obj)
            {
                errors.Add($"{key}[{i}] must be a field name or an object.");
                continue;
            }

            var field = new ScheduleFieldSpec
            {
                Parameter = (string?)obj["parameter"] ?? (string?)obj["name"] ?? string.Empty,
                FieldType = NullIfEmpty((string?)obj["fieldType"]),
                Heading = NullIfEmpty((string?)obj["heading"]),
                WidthMm = Number(obj["width"]) ?? Number(obj["widthMm"]),
                Hidden = Bool(obj["hidden"]),
                Totals = Bool(obj["totals"]),
                InsertAt = Number(obj["insertAt"]) is { } at ? (int)at : null,
                Format = obj["format"] is JObject format ? ParseFormat(format) : null
            };

            if (Bool(obj["count"]) == true)
                field.Kind = ScheduleFieldKind.Count;
            else if (obj["formula"] is { Type: not JTokenType.Null } formula)
            {
                field.Kind = ScheduleFieldKind.Formula;
                if (formula is JObject f)
                {
                    field.FormulaName = (string?)f["name"];
                    field.FormulaExpression = (string?)f["expression"];
                }
                else field.FormulaExpression = formula.ToString();
                field.FormulaName ??= field.Heading ?? field.Parameter;
            }
            else if (obj["percentage"] is { Type: not JTokenType.Null } percentage)
            {
                field.Kind = ScheduleFieldKind.Percentage;
                field.PercentageOf = percentage is JObject p ? (string?)p["of"] : percentage.Type == JTokenType.String ? percentage.Value<string>() : null;
                if (string.IsNullOrWhiteSpace(field.PercentageOf))
                    errors.Add($"{key}[{i}]: percentage needs the field it is of: {{\"percentage\": {{\"of\": \"Apparent Load\"}}}}.");
            }
            else if (string.IsNullOrWhiteSpace(field.Parameter))
            {
                errors.Add($"{key}[{i}] names no field: give parameter, count=true, formula or percentage.");
                continue;
            }

            if (field.WidthMm is < 0)
                errors.Add($"{key}[{i}]: width must not be negative.");
            result.Add(field);
        }
        return result;
    }

    private static List<ScheduleFilterSpec>? Filters(object? raw, string key, List<string> errors)
    {
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is not JArray array)
        {
            errors.Add($"{key} must be an array of {{field, type, value}}.");
            return null;
        }

        var result = new List<ScheduleFilterSpec>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JObject obj)
            {
                errors.Add($"{key}[{i}] must be an object {{field, type, value}}.");
                continue;
            }
            var filter = new ScheduleFilterSpec
            {
                Field = ((string?)obj["field"] ?? (string?)obj["parameter"] ?? string.Empty).Trim(),
                Type = ((string?)obj["type"] ?? (string?)obj["operator"] ?? "equals").Trim()
            };
            var value = obj["value"];
            if (value != null && value.Type != JTokenType.Null)
            {
                if (value.Type is JTokenType.Integer or JTokenType.Float)
                {
                    filter.Number = value.Value<double>();
                    filter.Value = filter.Number.Value.ToString(CultureInfo.InvariantCulture);
                }
                else if (value.Type == JTokenType.Boolean)
                {
                    filter.Number = value.Value<bool>() ? 1 : 0;
                    filter.Value = value.Value<bool>() ? "true" : "false";
                }
                else filter.Value = value.ToString();
            }
            if (filter.Field.Length == 0)
            {
                errors.Add($"{key}[{i}] names no field.");
                continue;
            }
            result.Add(filter);
        }
        return result;
    }

    private static List<ScheduleSortSpec>? Sorts(object? raw, string key, List<string> errors)
    {
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token is not JArray array)
        {
            errors.Add($"{key} must be an array of field names or {{field, descending, header, footer, blankLine}}.");
            return null;
        }

        var result = new List<ScheduleSortSpec>();
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i];
            if (item.Type == JTokenType.String)
            {
                var name = item.Value<string>()!.Trim();
                if (name.Length > 0) result.Add(new ScheduleSortSpec { Field = name });
                continue;
            }
            if (item is not JObject obj)
            {
                errors.Add($"{key}[{i}] must be a field name or an object.");
                continue;
            }
            var sort = new ScheduleSortSpec
            {
                Field = ((string?)obj["field"] ?? string.Empty).Trim(),
                Descending = Bool(obj["descending"]) ?? false,
                Header = Bool(obj["header"]) ?? false,
                Footer = Bool(obj["footer"]) ?? false,
                BlankLine = Bool(obj["blankLine"]) ?? false
            };
            if (sort.Field.Length == 0)
            {
                errors.Add($"{key}[{i}] names no field.");
                continue;
            }
            result.Add(sort);
        }
        return result;
    }

    private static ScheduleFieldFormatSpec ParseFormat(JObject obj) => new()
    {
        Units = NullIfEmpty((string?)obj["units"]),
        Accuracy = Number(obj["accuracy"]),
        SuppressTrailingZeros = Bool(obj["suppressTrailingZeros"])
    };

    private static List<KeyValuePair<string, string>> Pairs(object? raw, string key, List<string> errors)
    {
        var result = new List<KeyValuePair<string, string>>();
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return result;
        if (token is not JObject obj)
        {
            errors.Add($"{key} must be an object {{\"field\": \"new heading\"}}.");
            return result;
        }
        foreach (var property in obj.Properties())
            result.Add(new KeyValuePair<string, string>(property.Name, property.Value.Type == JTokenType.Null ? string.Empty : property.Value.ToString()));
        return result;
    }

    private static List<string> Names(object? raw)
    {
        var token = Token(raw);
        if (token == null || token.Type == JTokenType.Null) return new List<string>();
        if (token is JArray array)
            return array.Where(t => t.Type != JTokenType.Null).Select(t => t.ToString().Trim()).Where(t => t.Length > 0).ToList();
        var single = token.ToString().Trim();
        return single.Length == 0 ? new List<string>() : single.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    // ── Token helpers ───────────────────────────────────────────────────────

    private static object? Get(IDictionary<string, object?> args, string key)
        => args.TryGetValue(key, out var value) ? value : null;

    /// <summary>A JSON token for any argument value: tokens pass through, JSON text is parsed, plain values wrapped.</summary>
    internal static JToken? Token(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case JToken token:
                return token;
            case string text:
                var trimmed = text.Trim();
                if (trimmed.Length == 0) return null;
                if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
                {
                    try { return JToken.Parse(trimmed); }
                    catch { return new JValue(text); }
                }
                return new JValue(text);
            default:
                try { return JToken.FromObject(value); }
                catch { return null; }
        }
    }

    private static string? Text(IDictionary<string, object?> args, string key)
    {
        var token = Token(Get(args, key));
        return token == null || token.Type == JTokenType.Null ? null : token.ToString().Trim();
    }

    private static bool? Flag(IDictionary<string, object?> args, string key) => Bool(Token(Get(args, key)));

    private static bool? Bool(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type == JTokenType.Boolean) return token.Value<bool>();
        if (token.Type == JTokenType.Integer) return token.Value<long>() != 0;
        var text = token.ToString().Trim();
        if (bool.TryParse(text, out var b)) return b;
        if (text == "1") return true;
        if (text == "0") return false;
        return null;
    }

    private static double? Number(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        if (token.Type is JTokenType.Integer or JTokenType.Float) return token.Value<double>();
        return double.TryParse(token.ToString().Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
}

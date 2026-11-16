using System.Globalization;
using System.IO;
using System.Text;
using ClosedXML.Excel;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Parameters;

/// <summary>One requested write: an element (by id or UniqueId), an optional per-row parameter and the value.</summary>
public sealed class ParameterValueRow
{
    /// <summary>1-based position in the input (inline array index + 1, or the file's row number).</summary>
    public int Row { get; set; }
    public long? ElementId { get; set; }
    public string? UniqueId { get; set; }

    /// <summary>Overrides the call's parameterName for this row; null uses the default.</summary>
    public string? ParameterName { get; set; }
    public string Value { get; set; } = string.Empty;

    public string ElementKey => ElementId?.ToString(CultureInfo.InvariantCulture) ?? UniqueId ?? string.Empty;
}

/// <summary>Where the rows come from when they are in a file.</summary>
public sealed class ParameterValueSource
{
    public string Path { get; set; } = string.Empty;
    public string? Sheet { get; set; }
    public string? IdColumn { get; set; }
    public string? UniqueIdColumn { get; set; }
    public string? ValueColumn { get; set; }
    public string? ParameterColumn { get; set; }
}

/// <summary>A row the tool could not use, with the reason.</summary>
public sealed class ParameterValueProblem
{
    public int Row { get; set; }
    public string Element { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

/// <summary>A value that more than one element would carry.</summary>
public sealed class DuplicateValue
{
    public string Parameter { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public List<string> Elements { get; set; } = new();
}

/// <summary>
/// Pure logic of revit_set_parameter_values (#165): reads the ElementId → value rows from the call or from an
/// xlsx/csv/json file, collapses repeated rows, finds conflicting rows and duplicate values. No Revit API, so it
/// is linked into the tests.
/// </summary>
public static class ParameterValueMapping
{
    public const int MaxRows = 100_000;
    public const int DefaultSampleSize = 20;
    public const int MaxSampleSize = 200;

    private static readonly string[] IdColumnNames = { "elementId", "ElementId", "Element Id", "Id", "id" };
    private static readonly string[] UniqueIdColumnNames = { "uniqueId", "UniqueId", "Unique Id", "GUID" };
    private static readonly string[] ValueColumnNames = { "value", "Value", "Väärtus", "New value", "newValue" };
    private static readonly string[] ParameterColumnNames = { "parameterName", "ParameterName", "Parameter", "Parameeter" };

    // ── Inline rows ─────────────────────────────────────────────────────────

    /// <summary>
    /// Parses <c>values</c>: an array of {elementId | uniqueId, value, parameterName?}, or an object
    /// {"&lt;elementId or uniqueId&gt;": "value"}. A JSON string of either is accepted too.
    /// </summary>
    public static List<ParameterValueRow> ParseInline(object? raw, out string? error)
    {
        error = null;
        var token = ToToken(raw);
        if (token == null || token.Type == JTokenType.Null) return new List<ParameterValueRow>();

        if (token is JObject map && !LooksLikeRow(map)) return FromMap(map, out error);
        if (token is JObject single) token = new JArray(single);
        if (token is not JArray array)
        {
            error = "values must be an array of {elementId | uniqueId, value, parameterName?}.";
            return new List<ParameterValueRow>();
        }
        return FromArray(array, out error);
    }

    private static bool LooksLikeRow(JObject o) =>
        o.Properties().Any(p => Matches(p.Name, IdColumnNames) || Matches(p.Name, UniqueIdColumnNames) || Matches(p.Name, ValueColumnNames));

    private static List<ParameterValueRow> FromMap(JObject map, out string? error)
    {
        error = null;
        var rows = new List<ParameterValueRow>();
        var index = 0;
        foreach (var p in map.Properties())
        {
            index++;
            var row = new ParameterValueRow { Row = index, Value = TokenText(p.Value) ?? string.Empty };
            SetElement(row, p.Name);
            rows.Add(row);
        }
        return CheckCount(rows, ref error);
    }

    private static List<ParameterValueRow> FromArray(JArray array, out string? error)
    {
        error = null;
        var rows = new List<ParameterValueRow>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JObject o)
            {
                error = $"values[{i}] is not an object; expected {{elementId | uniqueId, value}}.";
                return new List<ParameterValueRow>();
            }
            var row = new ParameterValueRow { Row = i + 1 };
            var id = Property(o, IdColumnNames);
            var uid = Property(o, UniqueIdColumnNames);
            if (id != null) SetElement(row, TokenText(id) ?? string.Empty);
            if (row.ElementId == null && uid != null) row.UniqueId = NullIfBlank(TokenText(uid));
            row.ParameterName = NullIfBlank(TokenText(Property(o, ParameterColumnNames)));
            var value = Property(o, ValueColumnNames);
            if (value == null)
            {
                error = $"values[{i}] has no value.";
                return new List<ParameterValueRow>();
            }
            row.Value = TokenText(value) ?? string.Empty;
            rows.Add(row);
        }
        return CheckCount(rows, ref error);
    }

    // ── Files ───────────────────────────────────────────────────────────────

    /// <summary>Reads rows from an .xlsx/.xlsm, .csv/.txt or .json file. The file may be open in Excel.</summary>
    public static List<ParameterValueRow> ReadFile(ParameterValueSource source, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(source.Path)) { error = "sourceFile is empty."; return new(); }
        if (!File.Exists(source.Path)) { error = $"File not found: {source.Path}"; return new(); }

        try
        {
            switch (Path.GetExtension(source.Path).ToLowerInvariant())
            {
                case ".xlsx":
                case ".xlsm":
                    return FromTable(ReadWorkbook(source, out error), source, ref error);
                case ".csv":
                case ".txt":
                    return FromTable(ReadCsv(ReadShared(source.Path)), source, ref error);
                case ".json":
                    var token = JToken.Parse(ReadShared(source.Path));
                    if (token is JObject o && Property(o, new[] { "values", "rows" }) is JArray inner) token = inner;
                    return ParseInline(token, out error);
                default:
                    error = $"Unsupported file type '{Path.GetExtension(source.Path)}'. Use .xlsx, .xlsm, .csv or .json.";
                    return new();
            }
        }
        catch (Exception ex)
        {
            error = $"Could not read '{source.Path}': {ex.Message}";
            return new();
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>The sheet as rows of cell texts, header first. Null with an error when the sheet is missing.</summary>
    private static List<string[]>? ReadWorkbook(ParameterValueSource source, out string? error)
    {
        error = null;
        using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var workbook = new XLWorkbook(stream);
        IXLWorksheet? sheet;
        if (string.IsNullOrWhiteSpace(source.Sheet))
            sheet = workbook.Worksheets.FirstOrDefault(w => w.Visibility == XLWorksheetVisibility.Visible) ?? workbook.Worksheets.FirstOrDefault();
        else if (!workbook.Worksheets.TryGetWorksheet(source.Sheet, out sheet))
        {
            error = $"Sheet '{source.Sheet}' not found. Sheets: {string.Join(", ", workbook.Worksheets.Select(w => w.Name))}.";
            return null;
        }
        if (sheet == null) { error = "The workbook has no sheets."; return null; }

        var used = sheet.RangeUsed();
        if (used == null) return new List<string[]>();
        var firstColumn = used.FirstColumn().ColumnNumber();
        var lastColumn = used.LastColumn().ColumnNumber();
        var rows = new List<string[]>();
        foreach (var r in used.Rows())
        {
            var cells = new string[lastColumn - firstColumn + 1];
            for (var c = firstColumn; c <= lastColumn; c++)
                cells[c - firstColumn] = CellText(sheet.Cell(r.RowNumber(), c));
            rows.Add(cells);
        }
        return rows;
    }

    private static string CellText(IXLCell cell)
    {
        if (cell.IsEmpty()) return string.Empty;
        var v = cell.Value;
        if (v.IsNumber)
        {
            var d = v.GetNumber();
            return d == Math.Floor(d) && Math.Abs(d) < 1e15
                ? ((long)d).ToString(CultureInfo.InvariantCulture)
                : d.ToString(CultureInfo.InvariantCulture);
        }
        return cell.GetString();
    }

    /// <summary>Splits CSV text (separator ; , or tab, from the header line; "quoted" fields with "" escapes).</summary>
    public static List<string[]> ReadCsv(string text)
    {
        var rows = new List<string[]>();
        if (string.IsNullOrEmpty(text)) return rows;
        var firstLine = text.Split('\n')[0];
        var separator = new[] { ';', '\t', ',' }.OrderByDescending(s => firstLine.Count(ch => ch == s)).First();

        var field = new StringBuilder();
        var row = new List<string>();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else field.Append(ch);
            }
            else if (ch == '"' && field.Length == 0) quoted = true;
            else if (ch == separator) { row.Add(field.ToString()); field.Clear(); }
            else if (ch == '\r') { }
            else if (ch == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row.Clear(); }
            else field.Append(ch);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
    }

    /// <summary>Turns a header + data table into rows; columns are matched by name, case-insensitively.</summary>
    public static List<ParameterValueRow> FromTable(List<string[]>? table, ParameterValueSource source, ref string? error)
    {
        if (table == null) return new();
        if (table.Count == 0) { error = "The table is empty."; return new(); }

        var header = table[0].Select(h => h?.Trim() ?? string.Empty).ToArray();
        string? columnError = null;
        int Find(string? requested, string[] defaults, string what, bool required)
        {
            var wanted = requested?.Trim() ?? string.Empty;
            if (wanted.Length > 0)
            {
                var i = Array.FindIndex(header, h => string.Equals(h, wanted, StringComparison.OrdinalIgnoreCase));
                if (i < 0) columnError ??= $"Column '{requested}' ({what}) not found. Columns: {string.Join(", ", header.Where(h => h.Length > 0))}.";
                return i;
            }
            var found = Array.FindIndex(header, h => Matches(h, defaults));
            if (found < 0 && required)
                columnError ??= $"No {what} column. Name it with {(what == "value" ? "valueColumn" : "idColumn or uniqueIdColumn")}. Columns: {string.Join(", ", header.Where(h => h.Length > 0))}.";
            return found;
        }

        var idCol = Find(source.IdColumn, IdColumnNames, "element id", required: false);
        var uidCol = Find(source.UniqueIdColumn, UniqueIdColumnNames, "unique id", required: false);
        if (idCol < 0 && uidCol < 0)
            columnError ??= $"No element id column. Name it with idColumn or uniqueIdColumn. Columns: {string.Join(", ", header.Where(h => h.Length > 0))}.";
        var valueCol = Find(source.ValueColumn, ValueColumnNames, "value", required: true);
        var paramCol = Find(source.ParameterColumn, ParameterColumnNames, "parameter", required: false);
        if (columnError != null) { error = columnError; return new(); }

        var rows = new List<ParameterValueRow>();
        for (var r = 1; r < table.Count; r++)
        {
            var cells = table[r];
            string Cell(int c) => c >= 0 && c < cells.Length ? (cells[c] ?? string.Empty).Trim() : string.Empty;
            var id = Cell(idCol);
            var uid = Cell(uidCol);
            if (id.Length == 0 && uid.Length == 0) continue;   // blank or separator line

            var row = new ParameterValueRow { Row = r + 1, Value = valueCol < cells.Length ? cells[valueCol] ?? string.Empty : string.Empty };
            if (id.Length > 0) SetElement(row, id);
            if (row.ElementId == null && uid.Length > 0) row.UniqueId = uid;
            row.ParameterName = NullIfBlank(Cell(paramCol));
            rows.Add(row);
        }
        return CheckCount(rows, ref error);
    }

    // ── Validation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Rows the tool cannot use (no element, or an id that is not a number) and repeated rows: the same element
    /// and parameter twice with the same value collapses to one row; with different values both are refused.
    /// </summary>
    public static List<ParameterValueRow> Normalize(List<ParameterValueRow> rows, string defaultParameter,
        List<ParameterValueProblem> problems, out int collapsed)
    {
        collapsed = 0;
        var usable = new List<ParameterValueRow>();
        foreach (var row in rows)
        {
            if (row.ElementId == null && string.IsNullOrWhiteSpace(row.UniqueId))
            {
                problems.Add(new ParameterValueProblem { Row = row.Row, Reason = "invalidElementId: no element id or uniqueId" });
                continue;
            }
            if (row.ElementId is <= 0)
            {
                problems.Add(new ParameterValueProblem { Row = row.Row, Element = row.ElementKey, Reason = "invalidElementId: not a positive element id" });
                continue;
            }
            if (row.ParameterName == null && string.IsNullOrWhiteSpace(defaultParameter))
            {
                problems.Add(new ParameterValueProblem { Row = row.Row, Element = row.ElementKey, Reason = "noParameter: give parameterName for the call or the row" });
                continue;
            }
            usable.Add(row);
        }

        var result = new List<ParameterValueRow>();
        foreach (var group in usable.GroupBy(r => (Element: r.ElementKey, Parameter: (r.ParameterName ?? defaultParameter).Trim().ToUpperInvariant())))
        {
            var distinct = group.Select(r => r.Value).Distinct(StringComparer.Ordinal).Count();
            if (distinct > 1)
            {
                foreach (var r in group)
                    problems.Add(new ParameterValueProblem
                    {
                        Row = r.Row,
                        Element = r.ElementKey,
                        Reason = $"conflict: rows {string.Join(", ", group.Select(g => g.Row))} give different values to the same element and parameter"
                    });
                continue;
            }
            result.Add(group.First());
            collapsed += group.Count() - 1;
        }
        return result.OrderBy(r => r.Row).ToList();
    }

    /// <summary>
    /// Values held by more than one element once the writes are done. <paramref name="finalValues"/> is
    /// (parameter, element, value) for every element that counts: the targets with their new values and, for a
    /// model-wide check, every other element with its current value. Empty values never count as duplicates.
    /// </summary>
    public static List<DuplicateValue> FindDuplicates(IEnumerable<(string Parameter, string Element, string? Value)> finalValues)
        => finalValues
            .Where(v => !string.IsNullOrWhiteSpace(v.Value))
            .GroupBy(v => (Parameter: v.Parameter.Trim().ToUpperInvariant(), Value: v.Value!.Trim()))
            .Select(g => new { g.Key, Elements = g.Select(v => v.Element).Distinct().ToList(), g.First().Parameter })
            .Where(g => g.Elements.Count > 1)
            .OrderByDescending(g => g.Elements.Count).ThenBy(g => g.Key.Value, StringComparer.Ordinal)
            .Select(g => new DuplicateValue { Parameter = g.Parameter, Value = g.Key.Value, Elements = g.Elements })
            .ToList();

    /// <summary>Counts per reason prefix (the text before the first ':'), for an aggregated result.</summary>
    public static Dictionary<string, int> CountByReason(IEnumerable<ParameterValueProblem> problems)
        => problems.GroupBy(p => ReasonCode(p.Reason)).ToDictionary(g => g.Key, g => g.Count());

    public static string ReasonCode(string reason)
    {
        var colon = reason.IndexOf(':');
        return colon > 0 ? reason.Substring(0, colon) : reason;
    }

    public static int ClampSample(int requested) =>
        requested <= 0 ? DefaultSampleSize : Math.Min(requested, MaxSampleSize);

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static List<ParameterValueRow> CheckCount(List<ParameterValueRow> rows, ref string? error)
    {
        if (rows.Count > MaxRows)
        {
            error = $"{rows.Count} rows is more than the limit of {MaxRows}; split the input.";
            return new();
        }
        return rows;
    }

    private static void SetElement(ParameterValueRow row, string text)
    {
        text = text.Trim();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) row.ElementId = id;
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == Math.Floor(d)) row.ElementId = (long)d;
        else if (text.Length > 0) row.UniqueId = text;
    }

    private static JToken? ToToken(object? raw) => raw switch
    {
        null => null,
        JToken t => t,
        string s when string.IsNullOrWhiteSpace(s) => null,
        string s => JToken.Parse(s),
        _ => JToken.FromObject(raw)
    };

    private static JToken? Property(JObject o, string[] names)
    {
        foreach (var name in names)
            if (o.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var v)) return v;
        return null;
    }

    private static string? TokenText(JToken? t) => t == null || t.Type == JTokenType.Null ? null
        : t.Type switch
        {
            JTokenType.String => (string?)t,
            JTokenType.Float => ((double)t).ToString(CultureInfo.InvariantCulture),
            JTokenType.Boolean => (bool)t ? "1" : "0",
            _ => t.ToString(Newtonsoft.Json.Formatting.None)
        };

    private static bool Matches(string? name, string[] names)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return names.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static string? NullIfBlank(string? s) => s == null || s.Trim().Length == 0 ? null : s.Trim();
}

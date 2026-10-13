using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RevitMCP.Addin.PanelSchematics;

/// <summary>
/// Adds, changes and removes empty rows in a panel's stored PanelSchematics JSON. The text is edited as JSON, not
/// read into objects and written back, so every other row, the row order and fields this connector does not know
/// stay exactly as stored (the same approach as the plugin's own EmptyRowsJsonEditor). Rea nr is shared with the
/// panel's circuits, so the circuits' numbers are an input and renumbered circuits are an output.
/// No Revit types: unit tested.
/// </summary>
public static class EmptyRowsEditor
{
    public const string Add = "add";
    public const string Update = "update";
    public const string Remove = "remove";

    public sealed class Operation
    {
        public string Action { get; set; } = string.Empty;
        /// <summary>update/remove: the row to act on.</summary>
        public string? UniqueId { get; set; }
        /// <summary>add: where the row goes (end of the table when null). update/remove: selects the row when UniqueId is not given.</summary>
        public int? ReaNr { get; set; }
        /// <summary>When the wanted Rea nr is taken, move the rows from there on down by one instead of failing.</summary>
        public bool Insert { get; set; }
        public Dictionary<string, JsonNode?> Values { get; set; } = new(StringComparer.Ordinal);
    }

    public sealed class CircuitRow
    {
        public CircuitRow(string key, int reaNr)
        {
            Key = key;
            ReaNr = reaNr;
        }

        public string Key { get; }
        public int ReaNr { get; }
    }

    public sealed class FieldChange
    {
        public string Field { get; set; } = string.Empty;
        public string? Before { get; set; }
        public string? After { get; set; }
    }

    public sealed class RowChange
    {
        public string Action { get; set; } = string.Empty;
        public string UniqueId { get; set; } = string.Empty;
        public int ReaNr { get; set; }
        public List<FieldChange> Fields { get; } = new();
    }

    public sealed class CircuitRenumber
    {
        public string Key { get; set; } = string.Empty;
        public int From { get; set; }
        public int To { get; set; }
    }

    public sealed class Result
    {
        public bool Success => Errors.Count == 0;
        /// <summary>True when the stored text has to change.</summary>
        public bool Changed { get; set; }
        /// <summary>The new stored text; null with <see cref="RowCount"/> 0 means the entity is deleted, as the plugin does.</summary>
        public string? Json { get; set; }
        public int RowCount { get; set; }
        public List<RowChange> Changes { get; } = new();
        /// <summary>Circuits pushed down by an inserted row: their 'Rea Nr' parameter has to be written too.</summary>
        public List<CircuitRenumber> CircuitRenumbers { get; } = new();
        public List<string> Errors { get; } = new();
    }

    private static readonly JsonSerializerOptions WriteOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed class Entry
    {
        public JsonObject Row = null!;
        public int OriginalIndex = -1;
        public HashSet<string> Touched = new(StringComparer.Ordinal);
        public RowChange? Change;
    }

    /// <summary>Stored rows as JSON objects. False when the text is not a list of row objects (then nothing may be written).</summary>
    public static bool TryReadRows(string? storedJson, out List<JsonObject> rows, out string? error)
    {
        rows = new List<JsonObject>();
        error = null;
        if (string.IsNullOrWhiteSpace(storedJson))
            return true;

        try
        {
            if (JsonNode.Parse(storedJson!) is not JsonArray array)
            {
                error = "The stored empty rows are not a JSON list.";
                return false;
            }
            foreach (var item in array)
            {
                if (item is not JsonObject row)
                {
                    error = "The stored empty rows contain an entry that is not a row.";
                    rows = new List<JsonObject>();
                    return false;
                }
                rows.Add(row);
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = "The stored empty rows cannot be read: " + ex.Message;
            return false;
        }
    }

    public static Result Apply(string? storedJson, IReadOnlyList<Operation> operations, IReadOnlyList<CircuitRow> circuits,
        long panelId, Func<string>? newId = null)
    {
        var result = new Result();
        newId ??= () => Guid.NewGuid().ToString();

        if (!TryReadRows(storedJson, out var storedRows, out var readError))
        {
            result.Errors.Add(readError + " Nothing can be written to this panel's empty rows until that is fixed in the plugin.");
            return result;
        }
        if (operations.Count == 0)
        {
            result.Errors.Add("No operations were given.");
            return result;
        }

        var entries = storedRows.Select((row, i) => new Entry { Row = row, OriginalIndex = i }).ToList();
        var circuitNumbers = circuits.Select(c => c.ReaNr).ToList();

        for (int i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var where = $"Operation {i + 1} ({op.Action})";
            switch ((op.Action ?? string.Empty).Trim().ToLowerInvariant())
            {
                case Add:
                    ApplyAdd(op, where, entries, circuitNumbers, panelId, newId, result);
                    break;
                case Update:
                    ApplyUpdate(op, where, entries, circuitNumbers, result);
                    break;
                case Remove:
                    ApplyRemove(op, where, entries, result);
                    break;
                default:
                    result.Errors.Add($"{where}: action must be add, update or remove.");
                    break;
            }
        }

        if (result.Errors.Count > 0)
            return result;

        // One Rea nr per row for everything this edit moved or made
        var all = entries.Select(e => ReaNrOf(e.Row)).Concat(circuitNumbers).ToList();
        var checkedNumbers = new HashSet<int>();
        foreach (var entry in entries.Where(e => e.OriginalIndex < 0 || e.Touched.Contains("ReaNr")))
        {
            var number = ReaNrOf(entry.Row);
            if (checkedNumbers.Add(number) && all.Count(n => n == number) > 1)
                result.Errors.Add($"Rea nr {number} would be used by more than one row.");
        }
        for (int i = 0; i < circuits.Count; i++)
        {
            if (circuitNumbers[i] == circuits[i].ReaNr) continue;
            if (checkedNumbers.Add(circuitNumbers[i]) && all.Count(n => n == circuitNumbers[i]) > 1)
                result.Errors.Add($"Rea nr {circuitNumbers[i]} would be used by more than one row.");
            result.CircuitRenumbers.Add(new CircuitRenumber { Key = circuits[i].Key, From = circuits[i].ReaNr, To = circuitNumbers[i] });
        }

        // Nothing but the fields an operation named may differ from what is stored
        TryReadRows(storedJson, out var originalRows, out _);
        foreach (var entry in entries.Where(e => e.OriginalIndex >= 0))
        {
            var original = originalRows[entry.OriginalIndex];
            foreach (var property in original)
            {
                if (entry.Touched.Contains(property.Key)) continue;
                if (!JsonNode.DeepEquals(property.Value, entry.Row[property.Key]))
                    result.Errors.Add($"Internal check failed: field {property.Key} of a row changed without being edited.");
            }
            if (entry.Row.Any(p => !original.ContainsKey(p.Key) && !entry.Touched.Contains(p.Key)))
                result.Errors.Add("Internal check failed: a row gained a field that was not edited.");
        }
        if (result.Errors.Count > 0)
            return result;

        foreach (var entry in entries)
        {
            if (entry.Change == null) continue;
            entry.Change.ReaNr = ReaNrOf(entry.Row);
            if (entry.Change.Action == Add || entry.Change.Fields.Count > 0)
                result.Changes.Add(entry.Change);
        }

        result.RowCount = entries.Count;
        var array = new JsonArray();
        foreach (var entry in entries)
        {
            entry.Row.Parent?.AsArray().Remove(entry.Row);
            array.Add(entry.Row);
        }
        result.Json = entries.Count == 0 ? null : array.ToJsonString(WriteOptions);
        result.Changed = result.Changes.Count > 0 || storedRows.Count != entries.Count;
        return result;
    }

    // ------------------------------------------------------------------ operations

    private static void ApplyAdd(Operation op, string where, List<Entry> entries, List<int> circuitNumbers, long panelId,
        Func<string> newId, Result result)
    {
        if (!TryCoerceValues(op, where, result, out var values))
            return;

        int? wanted = op.ReaNr;
        if (values.TryGetValue("ReaNr", out var givenNumber))
        {
            wanted ??= givenNumber!.GetValue<int>();
            values.Remove("ReaNr");
        }

        int reaNr;
        if (wanted == null)
        {
            reaNr = entries.Select(e => ReaNrOf(e.Row)).Concat(circuitNumbers).DefaultIfEmpty(0).Max() + 1;
        }
        else
        {
            reaNr = wanted.Value;
            if (reaNr <= 0)
            {
                result.Errors.Add($"{where}: Rea nr must be 1 or more.");
                return;
            }
            if (!MakeRoom(reaNr, op.Insert, null, entries, circuitNumbers))
            {
                result.Errors.Add($"{where}: Rea nr {reaNr} is already used. Pass another number, or insert=true to move the rows from {reaNr} on down by one.");
                return;
            }
        }

        var id = newId();
        if (string.IsNullOrWhiteSpace(id) || entries.Any(e => string.Equals(UniqueIdOf(e.Row), id, StringComparison.Ordinal)))
        {
            result.Errors.Add($"{where}: could not make a new row id.");
            return;
        }

        var row = NewBlankRow(id, reaNr, panelId);
        var change = new RowChange { Action = Add, UniqueId = id };
        foreach (var value in values)
        {
            row[value.Key] = value.Value?.DeepClone();
            change.Fields.Add(new FieldChange { Field = value.Key, Before = null, After = Shown(value.Value) });
        }
        entries.Add(new Entry { Row = row, Change = change });
    }

    private static void ApplyUpdate(Operation op, string where, List<Entry> entries, List<int> circuitNumbers, Result result)
    {
        var entry = Find(op, where, entries, result);
        if (entry == null) return;
        if (!TryCoerceValues(op, where, result, out var values))
            return;
        if (values.Count == 0)
        {
            result.Errors.Add($"{where}: values is empty, nothing to change.");
            return;
        }

        entry.Change ??= new RowChange { Action = entry.OriginalIndex < 0 ? Add : Update, UniqueId = UniqueIdOf(entry.Row) ?? string.Empty };

        if (values.TryGetValue("ReaNr", out var numberNode))
        {
            var target = numberNode!.GetValue<int>();
            if (target <= 0)
            {
                result.Errors.Add($"{where}: Rea nr must be 1 or more.");
                return;
            }
            if (target != ReaNrOf(entry.Row) && !MakeRoom(target, op.Insert, entry, entries, circuitNumbers))
            {
                result.Errors.Add($"{where}: Rea nr {target} is already used. Pass another number, or insert=true to move the rows from {target} on down by one.");
                return;
            }
        }

        foreach (var value in values)
            SetField(entry, value.Key, value.Value);
    }

    private static void ApplyRemove(Operation op, string where, List<Entry> entries, Result result)
    {
        var entry = Find(op, where, entries, result);
        if (entry == null) return;

        entries.Remove(entry);
        if (entry.OriginalIndex >= 0)
            result.Changes.Add(new RowChange { Action = Remove, UniqueId = UniqueIdOf(entry.Row) ?? string.Empty, ReaNr = ReaNrOf(entry.Row) });
    }

    private static Entry? Find(Operation op, string where, List<Entry> entries, Result result)
    {
        List<Entry> matches;
        string selector;
        if (!string.IsNullOrWhiteSpace(op.UniqueId))
        {
            selector = "uniqueId " + op.UniqueId;
            matches = entries.Where(e => string.Equals(UniqueIdOf(e.Row), op.UniqueId, StringComparison.Ordinal)).ToList();
        }
        else if (op.ReaNr != null)
        {
            selector = "Rea nr " + op.ReaNr;
            matches = entries.Where(e => ReaNrOf(e.Row) == op.ReaNr.Value).ToList();
        }
        else
        {
            result.Errors.Add($"{where}: give the row's uniqueId (or its reaNr).");
            return null;
        }

        if (matches.Count == 1) return matches[0];
        result.Errors.Add(matches.Count == 0
            ? $"{where}: no empty row with {selector}. Circuit rows are changed with revit_panelschematics_set_circuit_rows."
            : $"{where}: {matches.Count} empty rows have {selector}; use uniqueId.");
        return null;
    }

    /// <summary>
    /// Frees <paramref name="reaNr"/> for <paramref name="mover"/> (or a new row). When taken and
    /// <paramref name="insert"/> is set, the unbroken run of rows from that number on moves down by one, so rows
    /// after the first free number keep theirs.
    /// </summary>
    private static bool MakeRoom(int reaNr, bool insert, Entry? mover, List<Entry> entries, List<int> circuitNumbers)
    {
        bool Taken(int n) => entries.Any(e => e != mover && ReaNrOf(e.Row) == n) || circuitNumbers.Contains(n);

        if (!Taken(reaNr)) return true;
        if (!insert) return false;

        int end = reaNr;
        while (Taken(end)) end++;

        foreach (var entry in entries.Where(e => e != mover))
        {
            var n = ReaNrOf(entry.Row);
            if (n < reaNr || n >= end) continue;
            entry.Change ??= new RowChange { Action = entry.OriginalIndex < 0 ? Add : Update, UniqueId = UniqueIdOf(entry.Row) ?? string.Empty };
            SetField(entry, "ReaNr", JsonValue.Create(n + 1));
        }
        for (int i = 0; i < circuitNumbers.Count; i++)
            if (circuitNumbers[i] >= reaNr && circuitNumbers[i] < end)
                circuitNumbers[i]++;
        return true;
    }

    private static void SetField(Entry entry, string field, JsonNode? value)
    {
        var before = entry.Row[field];
        if (entry.Row.ContainsKey(field) && Equivalent(before, value))
            return;

        var existing = entry.Change!.Fields.FirstOrDefault(f => f.Field == field);
        if (existing == null)
            entry.Change.Fields.Add(new FieldChange { Field = field, Before = entry.Row.ContainsKey(field) ? Shown(before) : null, After = Shown(value) });
        else
            existing.After = Shown(value);

        entry.Row[field] = value?.DeepClone();
        entry.Touched.Add(field);
    }

    /// <summary>Same stored value: 0.0 and 0 are the same number, but "" and null are not the same.</summary>
    private static bool Equivalent(JsonNode? a, JsonNode? b)
    {
        var x = ToElement(a);
        var y = ToElement(b);
        if (x.ValueKind == JsonValueKind.Number && y.ValueKind == JsonValueKind.Number)
            return x.GetDouble().Equals(y.GetDouble());
        var kindX = x.ValueKind == JsonValueKind.Undefined ? JsonValueKind.Null : x.ValueKind;
        var kindY = y.ValueKind == JsonValueKind.Undefined ? JsonValueKind.Null : y.ValueKind;
        return kindX == kindY && Shown(a) == Shown(b);
    }

    // ------------------------------------------------------------------ rows and values

    /// <summary>A blank empty row as the plugin's "Lisa rida" makes one. PanelId and CircuitId are reset by the plugin on load.</summary>
    public static JsonObject NewBlankRow(string uniqueId, int reaNr, long panelId) => new()
    {
        ["PanelId"] = panelId,
        ["CircuitId"] = null,
        ["WireTypeName"] = "",
        ["ReaNr"] = reaNr,
        ["CircuitLength"] = 0.0,
        ["CircuitPhase"] = null,
        ["CircuitVoltage"] = 0.0,
        ["GroupName"] = "",
        ["GroupNr"] = "",
        ["KlemmiNr"] = null,
        ["ApparentLoad"] = 0.0,
        ["ApparentCurrent"] = 0.0,
        ["Calculation"] = null,
        ["Calculation2"] = null,
        ["LoadClassification"] = null,
        ["CircuitBreaker"] = "",
        ["ConductorSize"] = 0.0,
        ["Samm"] = 0,
        ["UniqueId"] = uniqueId,
        ["Symbol"] = null,
        ["ConductorMaterial"] = null,
        ["Rikkevooluga"] = PanelSchematicsContract.No,
        ["IsChecked"] = true,
        ["AmbientTemperature"] = 30.0,
        ["IsSpacerRow"] = false,
        ["LisaKW"] = 0.0,
    };

    public static string? UniqueIdOf(JsonObject row)
        => row["UniqueId"] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static int ReaNrOf(JsonObject row)
    {
        var element = ToElement(row["ReaNr"]);
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var number))
            return (int)Math.Round(number);
        return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }

    /// <summary>A value as text for a preview (null stays null).</summary>
    public static string? Shown(JsonNode? value)
    {
        var element = ToElement(value);
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => element.GetRawText()
        };
    }

    private static bool TryCoerceValues(Operation op, string where, Result result, out Dictionary<string, JsonNode?> values)
    {
        values = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        bool ok = true;
        foreach (var pair in op.Values)
        {
            var field = PanelSchematicsContract.FindRowField(pair.Key);
            if (field == null)
            {
                var hint = PanelSchematicsContract.ProtectedRowProperties.Any(p => string.Equals(p, pair.Key, StringComparison.OrdinalIgnoreCase))
                    ? " is managed by the plugin and cannot be set."
                    : " is not a row field. Fields: " + string.Join(", ", PanelSchematicsContract.RowFields.Select(f => f.Name)) + ".";
                result.Errors.Add($"{where}: '{pair.Key}'{hint}");
                ok = false;
                continue;
            }
            if (values.ContainsKey(field.Name))
            {
                result.Errors.Add($"{where}: {field.Name} is given twice.");
                ok = false;
                continue;
            }
            if (!TryCoerce(field, pair.Value, out var coerced, out var error))
            {
                result.Errors.Add($"{where}: {field.Name} {error}");
                ok = false;
                continue;
            }
            values[field.Name] = coerced;
        }
        return ok;
    }

    /// <summary>Turns an argument into the JSON type the plugin stores for the field.</summary>
    public static bool TryCoerce(PanelSchematicsContract.RowField field, JsonNode? value, out JsonNode? coerced, out string error)
    {
        coerced = null;
        error = string.Empty;
        var element = ToElement(value);
        var text = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            _ => null
        };
        if (text == null)
        {
            error = "must be a single value, not a list or an object.";
            return false;
        }
        text = text.Trim();

        switch (field.Type)
        {
            case PanelSchematicsContract.FieldType.Whole:
                if (!TryParseNumber(text, out var whole) || Math.Abs(whole - Math.Round(whole)) > 1e-9 || Math.Abs(whole) > int.MaxValue)
                {
                    error = $"must be a whole number, got '{text}'.";
                    return false;
                }
                coerced = JsonValue.Create((int)Math.Round(whole));
                return true;

            case PanelSchematicsContract.FieldType.Number:
                if (text.Length == 0)
                {
                    coerced = JsonValue.Create(0.0);
                    return true;
                }
                if (!TryParseNumber(text, out var number))
                {
                    error = $"must be a number, got '{text}'.";
                    return false;
                }
                coerced = JsonValue.Create(number);
                return true;

            case PanelSchematicsContract.FieldType.YesNo:
                if (!TryParseYesNo(text, out var flag))
                {
                    error = $"must be true or false, got '{text}'.";
                    return false;
                }
                coerced = JsonValue.Create(flag);
                return true;

            default:
                if (field.Name == "Rikkevooluga")
                {
                    if (text.Length == 0)
                        text = PanelSchematicsContract.No;
                    else if (TryParseYesNo(text, out var yes))
                        text = yes ? PanelSchematicsContract.Yes : PanelSchematicsContract.No;
                    else
                    {
                        error = $"must be '{PanelSchematicsContract.Yes}' or '{PanelSchematicsContract.No}', got '{text}'.";
                        return false;
                    }
                }
                coerced = JsonValue.Create(text);
                return true;
        }
    }

    private static bool TryParseNumber(string text, out double number)
        => double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
           && !double.IsNaN(number) && !double.IsInfinity(number);

    private static bool TryParseYesNo(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "true": case "jah": case "yes": case "1":
                value = true;
                return true;
            case "false": case "ei": case "no": case "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private static JsonElement ToElement(JsonNode? node)
    {
        if (node == null) return default;
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Fingerprint of stored texts. A preview returns it and the write can require it, so a write based on a
    /// preview is refused when the data changed in between.
    /// </summary>
    public static string StateToken(params string?[] parts)
    {
        var builder = new StringBuilder();
        foreach (var part in parts)
            builder.Append(part == null ? "\u0000null" : part.Length.ToString(CultureInfo.InvariantCulture) + ":" + part).Append('\u0001');
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
        return string.Concat(hash.Take(8).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
    }
}

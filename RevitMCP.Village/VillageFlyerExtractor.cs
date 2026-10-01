using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Village;

/// <summary>What <see cref="VillageFlyerExtractor.Extract"/> found in one tool result.</summary>
public sealed class VillageFlyerExtraction
{
    public List<VillageFlyerItem> Items { get; } = new();

    /// <summary>Distinct element ids seen, including those past the item cap.</summary>
    public int Total { get; set; }

    /// <summary>More ids than the cap, or the walk stopped at its node budget.</summary>
    public bool Truncated { get; set; }
}

/// <summary>
/// Finds the elements in a read tool's result without knowing its shape: a bounded walk over
/// JSON tokens, dictionaries, lists and plain objects that collects records carrying an element
/// id, and id lists such as <c>elementIds</c>. From each record it keeps only the id and the
/// name / category / family / type / level strings. Parameter maps, tags, warnings and graph
/// hints are never entered, so no parameter value can reach a flyer. Never throws, never retains
/// the result. No Revit API dependency.
/// </summary>
public static class VillageFlyerExtractor
{
    /// <summary>Nodes visited before the walk gives up and marks the flyer truncated.</summary>
    public const int MaxVisitedNodes = 50_000;
    public const int MaxDepth = 12;
    public const int MaxTextLength = 120;

    /// <summary>An object with one of these is an element whatever else it holds.</summary>
    private static readonly string[] ElementIdKeys = { "elementId" };

    /// <summary>A bare <c>id</c> counts only next to one of these, so issue numbers and row ids stay out.</summary>
    private static readonly string[] DescriptorKeys = { "name", "category", "categoryName", "kind", "family", "familyName", "type", "typeName" };

    /// <summary>Arrays of plain ids.</summary>
    private static readonly string[] IdListKeys = { "elementIds", "selectedElementIds" };

    /// <summary>Never entered: values, not elements.</summary>
    private static readonly string[] SkipKeys =
    {
        "parameters", "parameter", "tags", "warnings", "errors", "extra", "invalidIds", "missingIds",
        "notFoundIds", "skippedIds", "failedIds", "values", "rawValue", "note"
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static VillageFlyerExtraction Extract(object? data, int maxItems)
    {
        var result = new VillageFlyerExtraction();
        if (data == null || maxItems <= 0) return result;
        var walker = new Walker(result, maxItems);
        try { walker.Visit(data, 0, null); }
        catch { result.Truncated = true; }
        result.Total = walker.Seen.Count;
        if (walker.Seen.Count > result.Items.Count) result.Truncated = true;
        return result;
    }

    private sealed class Walker
    {
        private readonly VillageFlyerExtraction _result;
        private readonly int _maxItems;
        private readonly Dictionary<long, VillageFlyerItem> _byId = new();
        private readonly HashSet<object> _visitedObjects = new(ReferenceComparer.Instance);
        private int _visited;

        public readonly HashSet<long> Seen = new();

        public Walker(VillageFlyerExtraction result, int maxItems)
        {
            _result = result;
            _maxItems = maxItems;
        }

        public void Visit(object? node, int depth, string? key)
        {
            if (node == null || depth > MaxDepth) return;
            if (++_visited > MaxVisitedNodes) { _result.Truncated = true; return; }

            switch (node)
            {
                case string:
                case JValue:
                    return;
                case JObject jo:
                    VisitMap(jo.Properties().Select(p => new KeyValuePair<string, object?>(p.Name, p.Value)), depth);
                    return;
                case JArray ja:
                    VisitList(ja, depth, key);
                    return;
                case IDictionary<string, object?> generic:
                    VisitMap(generic, depth);
                    return;
                case IDictionary plain:
                    VisitMap(PlainEntries(plain), depth);
                    return;
                case IEnumerable list:
                    VisitList(list, depth, key);
                    return;
            }

            var type = node.GetType();
            if (type.IsPrimitive || type.IsEnum || node is ValueType && !IsPlainStruct(type)) return;
            if (!IsPlainObjectType(type)) return;
            if (!_visitedObjects.Add(node)) return;
            VisitMap(PropertyEntries(node, type), depth);
        }

        private void VisitList(IEnumerable list, int depth, string? key)
        {
            var isIdList = key != null && ElementIdList(key);
            foreach (var item in list)
            {
                if (_visited > MaxVisitedNodes) { _result.Truncated = true; return; }
                if (isIdList)
                {
                    _visited++;
                    if (TryId(item, out var id)) Add(id, null);
                    continue;
                }
                Visit(item, depth + 1, key);
            }
        }

        private void VisitMap(IEnumerable<KeyValuePair<string, object?>> entries, int depth)
        {
            // Materialize once: plain objects run their getters here, and the record test and the
            // descent both need the values.
            var list = entries as IList<KeyValuePair<string, object?>> ?? entries.ToList();

            long id = 0;
            var isElement = false;
            var bareId = 0L;
            var hasDescriptor = false;
            foreach (var kv in list)
            {
                if (!isElement && Matches(kv.Key, ElementIdKeys) && TryId(kv.Value, out var eid)) { id = eid; isElement = true; }
                else if (string.Equals(kv.Key, "id", StringComparison.OrdinalIgnoreCase) && TryId(kv.Value, out var bid)) bareId = bid;
                else if (!hasDescriptor && Matches(kv.Key, DescriptorKeys) && Text(kv.Value) != null) hasDescriptor = true;
            }
            if (!isElement && bareId > 0 && hasDescriptor) { id = bareId; isElement = true; }

            if (isElement)
            {
                Add(id, new VillageFlyerItem
                {
                    Id = id,
                    Name = Field(list, "name"),
                    Category = Field(list, "category") ?? Field(list, "categoryName"),
                    Family = Field(list, "family") ?? Field(list, "familyName"),
                    Type = Field(list, "type") ?? Field(list, "typeName"),
                    Level = Field(list, "level") ?? Field(list, "levelName")
                });
            }

            foreach (var kv in list)
            {
                if (kv.Value == null || Matches(kv.Key, SkipKeys)) continue;
                if (kv.Value is string || kv.Value is JValue) continue;
                Visit(kv.Value, depth + 1, kv.Key);
            }
        }

        private void Add(long id, VillageFlyerItem? item)
        {
            if (id <= 0) return;
            Seen.Add(id);
            if (_byId.TryGetValue(id, out var existing))
            {
                if (item == null) return;
                existing.Name ??= item.Name;
                existing.Category ??= item.Category;
                existing.Family ??= item.Family;
                existing.Type ??= item.Type;
                existing.Level ??= item.Level;
                return;
            }
            if (_result.Items.Count >= _maxItems) return;
            var added = item ?? new VillageFlyerItem { Id = id };
            _byId[id] = added;
            _result.Items.Add(added);
        }
    }

    private static bool ElementIdList(string key) => Matches(key, IdListKeys);

    private static bool Matches(string key, string[] names)
    {
        foreach (var n in names)
            if (string.Equals(key, n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string? Field(IList<KeyValuePair<string, object?>> entries, string name)
    {
        foreach (var kv in entries)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return Text(kv.Value);
        return null;
    }

    private static string? Text(object? value)
    {
        string? s = value switch
        {
            string str => str,
            JValue jv when jv.Type == JTokenType.String => jv.Value<string>(),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(s)) return null;
        var cleaned = new string(s!.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= MaxTextLength ? cleaned : cleaned.Substring(0, MaxTextLength);
    }

    /// <summary>A positive element id from a number, a JSON number or a numeric string (graph ids are strings).</summary>
    public static bool TryId(object? value, out long id)
    {
        id = 0;
        switch (value)
        {
            case long l: id = l; break;
            case int i: id = i; break;
            case short s: id = s; break;
            case uint ui: id = ui; break;
            case JValue jv when jv.Type == JTokenType.Integer:
                try { id = jv.Value<long>(); } catch { return false; }
                break;
            case JValue jv when jv.Type == JTokenType.String:
                return TryId(jv.Value<string>(), out id);
            case string str:
                if (str.Length == 0 || str.Length > 19 || !str.All(char.IsDigit)) return false;
                if (!long.TryParse(str, NumberStyles.None, CultureInfo.InvariantCulture, out id)) return false;
                break;
            default:
                return false;
        }
        return id > 0;
    }

    private static IEnumerable<KeyValuePair<string, object?>> PlainEntries(IDictionary plain)
    {
        foreach (DictionaryEntry e in plain)
            if (e.Key is string k) yield return new KeyValuePair<string, object?>(k, e.Value);
    }

    private static IList<KeyValuePair<string, object?>> PropertyEntries(object node, Type type)
    {
        var props = PropertyCache.GetOrAdd(type, t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
            .ToArray());
        var list = new List<KeyValuePair<string, object?>>(props.Length);
        foreach (var p in props)
        {
            object? value;
            try { value = p.GetValue(node); }
            catch { continue; }
            list.Add(new KeyValuePair<string, object?>(p.Name, value));
        }
        return list;
    }

    /// <summary>
    /// Only the add-in's own DTOs and anonymous types are entered. Framework types and anything
    /// from the Revit API are skipped: a Revit getter must never run off the Revit thread.
    /// </summary>
    private static bool IsPlainObjectType(Type type)
    {
        var ns = type.Namespace ?? string.Empty;
        if (ns.StartsWith("Autodesk", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("System", StringComparison.Ordinal)) return false;
        if (ns.StartsWith("Microsoft", StringComparison.Ordinal)) return false;
        if (typeof(Delegate).IsAssignableFrom(type) || typeof(Stream).IsAssignableFrom(type)) return false;
        return true;
    }

    private static bool IsPlainStruct(Type type) => !type.IsPrimitive && !type.IsEnum && IsPlainObjectType(type);

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

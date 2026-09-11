using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Village;

/// <summary>
/// The little the village needs to know about the open document. Built from values the connector
/// already captured for its own logging; the village never reads Revit itself.
/// </summary>
public sealed class VillageProjectContext
{
    public string ModelTitle { get; set; } = string.Empty;
    public string CentralPath { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string RevitVersion { get; set; } = string.Empty;
    public bool IsWorkshared { get; set; }

    public static VillageProjectContext Empty { get; } = new();

    public bool HasDocument => !string.IsNullOrWhiteSpace(ModelTitle);

    /// <summary>Human label for the village. The document title, never a path.</summary>
    public string DisplayName => HasDocument ? VillageEventSerializer.Truncate(ModelTitle) : "No document";

    /// <summary>Model file name without extension, derived from the central path first (matches the graph's model_name).</summary>
    public string ModelName
    {
        get
        {
            var candidate = !string.IsNullOrWhiteSpace(CentralPath) ? CentralPath : LocalPath;
            var stem = VillageModelId.FileStem(candidate);
            return stem.Length > 0 ? stem : ModelTitle;
        }
    }

    public string ComputeModelId() => VillageModelId.Compute(CentralPath, LocalPath, ModelTitle);
}

/// <summary>Stable, non-reversible model identity derived from the model path.</summary>
public static class VillageModelId
{
    public const string NoDocument = "no-document";

    public static string Compute(string? centralPath, string? localPath, string? title)
    {
        var source = FirstNonEmpty(centralPath, localPath, title);
        if (source.Length == 0) return NoDocument;

        using var sha = SHA1.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(source.Trim().ToLowerInvariant()));
        var sb = new StringBuilder(16);
        for (var i = 0; i < 8; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string FileStem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path!.Trim();
        var slash = p.LastIndexOfAny(new[] { '/', '\\' });
        var file = slash >= 0 ? p.Substring(slash + 1) : p;
        var dot = file.LastIndexOf('.');
        return dot > 0 ? file.Substring(0, dot) : file;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v!;
        return string.Empty;
    }
}

/// <summary>
/// Reads the Revit category a request names, so the agent can walk to that category's warehouse
/// instead of the generic area landmark. Only a fixed allow-list of top-level argument keys is
/// consulted, one level deep, and the value is never published as-is: the caller resolves it
/// against the warehouses the graph snapshot already exposes and drops anything that misses.
/// Several different categories in one request return null — there is no single place to walk to.
/// </summary>
public static class VillageCategoryArgument
{
    public static readonly string[] Keys = { "category", "categoryName", "categories", "categoryNames" };

    /// <summary>Longest category name accepted; Revit's own names are far shorter.</summary>
    public const int MaxLength = 100;

    public static string? TryExtract(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (arguments == null || arguments.Count == 0) return null;
        try
        {
            foreach (var key in Keys)
            {
                if (!arguments.TryGetValue(key, out var value) || value == null) continue;
                var found = FromValue(value);
                if (found != null) return found;
            }
        }
        catch
        {
            // A malformed argument must never cost a tool call.
        }
        return null;
    }

    private static string? FromValue(object value)
    {
        switch (value)
        {
            case string s:
                return Clean(s);

            case JValue jv:
                return Clean(jv.Value<string>());

            case JArray ja:
                return Single(ja.Select(t => t is JValue v ? Clean(v.Value<string>()) : null));

            case IEnumerable list when value is not string:
                return Single(list.Cast<object?>().Select(o => o is string s2 ? Clean(s2) : o is JValue v2 ? Clean(v2.Value<string>()) : null));

            default:
                return null;
        }
    }

    /// <summary>One distinct non-empty name, or null when the request spans several categories.</summary>
    private static string? Single(IEnumerable<string?> values)
    {
        string? only = null;
        foreach (var value in values)
        {
            if (value == null) continue;
            if (only == null) only = value;
            else if (!string.Equals(only, value, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return only;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value!.Trim();
        return trimmed.Length > MaxLength ? null : trimmed;
    }
}

/// <summary>
/// Reads an aggregate count from a tool result without serializing or retaining it: only a fixed
/// allow-list of top-level numeric fields is consulted, in priority order, one level deep.
/// </summary>
public static class VillageAffectedCount
{
    /// <summary>Priority order: explicit change counts first, then query counts.</summary>
    public static readonly string[] Keys =
    {
        "affectedCount", "modifiedCount", "updatedCount", "createdCount", "placedCount", "deletedCount",
        "renamedCount", "duplicatedCount", "taggedCount", "retaggedCount", "movedCount", "alignedCount",
        "assignedCount", "convertedCount", "syncedCount", "circuitedCount",
        "returned", "itemsReturned", "totalMatched", "totalAvailable", "count", "total", "totalIssues",
        "clashCount", "nodeCount", "matched"
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> PropertyCache = new();

    public static long? TryExtract(object? data)
    {
        if (data == null) return null;
        try
        {
            switch (data)
            {
                case JObject jobj:
                    foreach (var key in Keys)
                    {
                        var t = jobj[key];
                        if (t != null && TryNumber(t, out var n)) return n;
                    }
                    return null;

                case IDictionary<string, object?> generic:
                    foreach (var key in Keys)
                        if (generic.TryGetValue(key, out var v) && TryNumber(v, out var n)) return n;
                    return null;

                case IDictionary plain:
                    foreach (var key in Keys)
                        if (plain.Contains(key) && TryNumber(plain[key], out var n)) return n;
                    return null;

                case string:
                case ValueType:
                case IEnumerable:
                    return null;
            }

            var props = PropertyCache.GetOrAdd(data.GetType(), t => t
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                .ToArray());

            foreach (var key in Keys)
            {
                foreach (var p in props)
                {
                    if (!string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (TryNumber(p.GetValue(data), out var n)) return n;
                }
            }
        }
        catch
        {
            // Visualization never throws into the caller.
        }
        return null;
    }

    private static bool TryNumber(object? value, out long number)
    {
        number = 0;
        switch (value)
        {
            case null: return false;
            case int i: number = i; return true;
            case long l: number = l; return true;
            case short s: number = s; return true;
            case byte b: number = b; return true;
            case uint ui: number = ui; return true;
            case double d when !double.IsNaN(d) && !double.IsInfinity(d): number = (long)d; return true;
            case float f when !float.IsNaN(f) && !float.IsInfinity(f): number = (long)f; return true;
            case decimal m: number = (long)m; return true;
            case JValue jv when jv.Type == JTokenType.Integer: number = jv.Value<long>(); return true;
            case JValue jv when jv.Type == JTokenType.Float: return TryNumber(jv.Value<double>(), out number);
            default: return false;
        }
    }
}

/// <summary>
/// Builds sanitized <see cref="VillageEvent"/>s from the values the connector already has during
/// normal execution. Arguments, result bodies, messages, warnings, errors and paths never enter an event.
/// </summary>
public sealed class VillageEventFactory
{
    private static readonly Regex ToolNamePattern = new("^[a-z0-9_]{1,100}$", RegexOptions.Compiled);
    private static readonly Regex StatusPattern = new("^[a-z0-9_]{1,40}$", RegexOptions.Compiled);
    private static readonly Regex ControlChars = new(@"\p{C}+", RegexOptions.Compiled);

    private static readonly string[] ApprovalStatuses =
    {
        "approval_required", "approval_rejected", "approval_expired", "approval_context_changed"
    };

    private readonly VillageToolClassifier _classifier;
    private readonly Func<DateTimeOffset> _clock;
    private long _sequence;

    public VillageEventFactory(
        string? sessionId = null,
        VillageToolClassifier? classifier = null,
        Func<DateTimeOffset>? clock = null,
        Func<string, string?>? warehouseResolver = null)
    {
        SessionId = string.IsNullOrWhiteSpace(sessionId) ? Guid.NewGuid().ToString() : sessionId!;
        _classifier = classifier ?? VillageToolClassifier.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _warehouseResolver = warehouseResolver;
    }

    /// <summary>
    /// Maps a category named in a request to an existing warehouse id, or null. Supplied by the
    /// hub, which owns the yard; without it no request ever routes to a warehouse.
    /// </summary>
    private readonly Func<string, string?>? _warehouseResolver;

    /// <summary>
    /// Resolves the request's category argument to a warehouse id. The raw argument is never
    /// stored on the event — only an id the graph snapshot already published can come back.
    /// </summary>
    private string? WarehouseFor(IReadOnlyDictionary<string, object?>? arguments)
    {
        if (_warehouseResolver == null) return null;
        var category = VillageCategoryArgument.TryExtract(arguments);
        if (category == null) return null;
        try { return _warehouseResolver(category); }
        catch { return null; }
    }

    public string SessionId { get; }

    public VillageToolClassifier Classifier => _classifier;

    public VillageEvent SessionStarted(VillageProjectContext? context) =>
        Base(VillageEventTypes.SessionStarted, context, VillageAreas.Project, VillageActivities.Inspect);

    public VillageEvent ProjectChanged(VillageProjectContext? context) =>
        Base(VillageEventTypes.ProjectChanged, context, VillageAreas.Project, VillageActivities.Inspect);

    public VillageEvent ToolStarted(
        string? toolName,
        string? clientName,
        VillageProjectContext? context,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var c = _classifier.Classify(toolName);
        var e = Base(VillageEventTypes.ToolStarted, context, c.Area, c.Activity);
        e.ToolName = SanitizeToolName(toolName);
        e.ClientName = SanitizeClientName(clientName);
        e.Warehouse = WarehouseFor(arguments);
        return e;
    }

    /// <summary>
    /// Completion event. <paramref name="resultData"/> is only consulted through
    /// <see cref="VillageAffectedCount.TryExtract"/> and is not retained.
    /// </summary>
    public VillageEvent ToolCompleted(
        string? toolName,
        string? clientName,
        bool success,
        string? status,
        long durationMs,
        object? resultData,
        VillageProjectContext? context,
        IReadOnlyDictionary<string, object?>? arguments = null)
    {
        var c = _classifier.Classify(toolName);
        var code = SanitizeStatus(status);

        string type;
        string activity;
        if (success)
        {
            type = VillageEventTypes.ToolCompleted;
            activity = c.Activity;
        }
        else if (code != null && Array.IndexOf(ApprovalStatuses, code) >= 0)
        {
            type = VillageEventTypes.ToolDeferred;
            activity = c.Activity;
        }
        else
        {
            type = VillageEventTypes.ToolFailed;
            activity = VillageActivities.Error;
        }

        var e = Base(type, context, c.Area, activity);
        e.ToolName = SanitizeToolName(toolName);
        e.ClientName = SanitizeClientName(clientName);
        e.Success = success;
        e.Status = code;
        e.DurationMs = durationMs < 0 ? 0 : durationMs;
        e.AffectedCount = success ? VillageAffectedCount.TryExtract(resultData) : null;
        e.Warehouse = WarehouseFor(arguments);
        return e;
    }

    public VillageEvent GraphRefreshed(VillageProjectContext? context, bool success, long? nodeCount)
    {
        var e = Base(VillageEventTypes.GraphRefreshed, context, VillageAreas.Graph, VillageActivities.Inspect);
        e.Success = success;
        e.AffectedCount = nodeCount;
        return e;
    }

    public VillageEvent BridgeConnected(string? clientName, VillageProjectContext? context)
    {
        var e = Base(VillageEventTypes.BridgeConnected, context, VillageAreas.Project, VillageActivities.Inspect);
        e.ClientName = SanitizeClientName(clientName);
        return e;
    }

    public VillageEvent BridgeDisconnected(string? clientName, VillageProjectContext? context)
    {
        var e = Base(VillageEventTypes.BridgeDisconnected, context, VillageAreas.Project, VillageActivities.Inspect);
        e.ClientName = SanitizeClientName(clientName);
        return e;
    }

    private VillageEvent Base(string type, VillageProjectContext? context, string area, string activity)
    {
        var ctx = context ?? VillageProjectContext.Empty;
        return new VillageEvent
        {
            EventType = type,
            SessionId = SessionId,
            ModelId = ctx.ComputeModelId(),
            ProjectName = ctx.DisplayName,
            Timestamp = VillageEventSerializer.FormatTimestamp(_clock()),
            Area = area,
            Activity = activity,
            Sequence = Interlocked.Increment(ref _sequence)
        };
    }

    public static string SanitizeToolName(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim().ToLowerInvariant();
        return ToolNamePattern.IsMatch(name) ? name : "unknown_tool";
    }

    public static string? SanitizeStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status)) return null;
        var s = status!.Trim().ToLowerInvariant();
        return StatusPattern.IsMatch(s) ? s : "other";
    }

    public static string? SanitizeClientName(string? clientName)
    {
        if (string.IsNullOrWhiteSpace(clientName)) return null;
        var cleaned = ControlChars.Replace(clientName!, string.Empty).Trim();
        if (cleaned.Length == 0) return null;
        return cleaned.Length <= 60 ? cleaned : cleaned.Substring(0, 60);
    }
}

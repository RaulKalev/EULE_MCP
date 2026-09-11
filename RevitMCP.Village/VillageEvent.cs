using System.Globalization;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Village;

/// <summary>
/// Versioned vocabulary of the sanitized activity events the Project Village consumes.
/// An event says <i>that</i> a tool ran and where it belongs in the village — never what it was
/// asked or what it returned. No Revit API dependency.
/// </summary>
public static class VillageSchema
{
    /// <summary>Bump when a field changes meaning or is removed. Adding optional fields keeps the version.</summary>
    public const int Version = 1;

    /// <summary>Shown in the viewer and in every snapshot so nobody mistakes the village for a control surface.</summary>
    public const string ReadOnlyNotice =
        "The Project Village is a passive visualization. It does not make AI calls, add content to the agent context, " +
        "execute MCP tools, or write to Revit. It displays deterministic summaries of connector activity and read-only graph snapshots.";

    /// <summary>Longest string accepted in any event field. Longer values are truncated, never rejected.</summary>
    public const int MaxStringLength = 200;

    /// <summary>Largest serialized event accepted by the parser (replay/import only; the connector never receives events).</summary>
    public const int MaxSerializedBytes = 16 * 1024;
}

/// <summary>Lifecycle event types. Unknown types are tolerated by the viewer but never produced by the connector.</summary>
public static class VillageEventTypes
{
    public const string SessionStarted     = "session_started";
    public const string ProjectChanged     = "project_changed";
    public const string ToolStarted        = "tool_started";
    public const string ToolCompleted      = "tool_completed";
    /// <summary>A tool returned <c>approval_*</c>: it is waiting for (or was refused by) the user. Not an error.</summary>
    public const string ToolDeferred       = "tool_deferred";
    public const string ToolFailed         = "tool_failed";
    public const string GraphRefreshed     = "graph_refreshed";
    public const string BridgeConnected    = "bridge_connected";
    public const string BridgeDisconnected = "bridge_disconnected";

    public static readonly string[] All =
    {
        SessionStarted, ProjectChanged, ToolStarted, ToolCompleted, ToolDeferred, ToolFailed,
        GraphRefreshed, BridgeConnected, BridgeDisconnected
    };

    public static bool IsKnown(string? value) =>
        !string.IsNullOrEmpty(value) && Array.IndexOf(All, value) >= 0;
}

/// <summary>Broad village areas a tool can belong to. Buildings map onto these (see <c>VillageLayout</c>).</summary>
public static class VillageAreas
{
    public const string Project         = "project";
    public const string Graph           = "graph";
    public const string Sheets          = "sheets";
    public const string Schedules       = "schedules";
    public const string Views           = "views";
    public const string FamiliesTypes   = "families_types";
    public const string TagsAnnotations = "tags_annotations";
    public const string Elements        = "elements";
    public const string FireAlarm       = "fire_alarm";
    public const string Security        = "security";
    public const string Lighting        = "lighting";
    public const string ItAv            = "it_av";
    public const string Electrical      = "electrical";
    public const string Coordination    = "coordination";
    /// <summary>Files, Excel, reports, delivery checks, configuration, standards and skills.</summary>
    public const string Office          = "office";
    public const string Unknown         = "unknown";

    public static readonly string[] All =
    {
        Project, Graph, Sheets, Schedules, Views, FamiliesTypes, TagsAnnotations, Elements,
        FireAlarm, Security, Lighting, ItAv, Electrical, Coordination, Office, Unknown
    };

    public static bool IsKnown(string? value) =>
        !string.IsNullOrEmpty(value) && Array.IndexOf(All, value) >= 0;

    /// <summary>Returns the value when it is part of the vocabulary, otherwise <see cref="Unknown"/>.</summary>
    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return IsKnown(v) ? v : Unknown;
    }
}

/// <summary>What the agent is doing in an area.</summary>
public static class VillageActivities
{
    public const string Inspect    = "inspect";
    public const string Search     = "search";
    public const string Analyze    = "analyze";
    public const string Create     = "create";
    public const string Modify     = "modify";
    public const string Delete     = "delete";
    public const string Export     = "export";
    public const string Validate   = "validate";
    public const string BuildGraph = "build_graph";
    public const string Error      = "error";
    public const string Unknown    = "unknown";

    public static readonly string[] All =
    {
        Inspect, Search, Analyze, Create, Modify, Delete, Export, Validate, BuildGraph, Error, Unknown
    };

    public static bool IsKnown(string? value) =>
        !string.IsNullOrEmpty(value) && Array.IndexOf(All, value) >= 0;

    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim().ToLowerInvariant();
        return IsKnown(v) ? v : Unknown;
    }

    /// <summary>True for activities that change the Revit model or files on disk.</summary>
    public static bool IsWrite(string? activity) =>
        activity == Create || activity == Modify || activity == Delete;
}

/// <summary>
/// One sanitized activity event (schema version <see cref="VillageSchema.Version"/>).
/// Never carries prompts, arguments, results, messages, paths, parameter values or element ids.
/// </summary>
public sealed class VillageEvent
{
    [JsonProperty("schema_version")]
    public int SchemaVersion { get; set; } = VillageSchema.Version;

    [JsonProperty("event_id")]
    public string EventId { get; set; } = Guid.NewGuid().ToString();

    /// <summary>Identifies one connector run (a pipe server lifetime), not an individual AI conversation.</summary>
    [JsonProperty("session_id")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Stable hash of the model's central (or local) path. Never the path itself.</summary>
    [JsonProperty("model_id")]
    public string ModelId { get; set; } = string.Empty;

    [JsonProperty("project_name")]
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>UTC ISO-8601, millisecond precision, always with a trailing <c>Z</c>.</summary>
    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonProperty("event_type")]
    public string EventType { get; set; } = string.Empty;

    [JsonProperty("tool_name")]
    public string? ToolName { get; set; }

    [JsonProperty("activity")]
    public string Activity { get; set; } = VillageActivities.Unknown;

    [JsonProperty("area")]
    public string Area { get; set; } = VillageAreas.Unknown;

    /// <summary>Null until the tool finishes.</summary>
    [JsonProperty("success")]
    public bool? Success { get; set; }

    [JsonProperty("duration_ms")]
    public long? DurationMs { get; set; }

    /// <summary>Aggregate count derived from an allow-listed numeric result field; never element ids.</summary>
    [JsonProperty("affected_count")]
    public long? AffectedCount { get; set; }

    /// <summary>
    /// Id of the category warehouse this tool worked in, or null. Never a raw tool argument: the
    /// category named in the request is only used to look up a warehouse that the graph snapshot
    /// already published, and anything that does not match one is dropped.
    /// </summary>
    [JsonProperty("warehouse")]
    public string? Warehouse { get; set; }

    /// <summary>The MCP client that issued the request (e.g. "Claude Code"). Drives the agent character.</summary>
    [JsonProperty("client_name")]
    public string? ClientName { get; set; }

    /// <summary>Machine-readable result status code (e.g. <c>approval_required</c>). Never free text.</summary>
    [JsonProperty("status")]
    public string? Status { get; set; }

    /// <summary>Monotonic per connector run; lets a reconnecting viewer detect gaps.</summary>
    [JsonProperty("sequence")]
    public long Sequence { get; set; }
}

/// <summary>JSON (de)serialization with defensive parsing for malformed or foreign input.</summary>
public static class VillageEventSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        NullValueHandling = NullValueHandling.Include,
        Formatting = Formatting.None,
        DateParseHandling = DateParseHandling.None
    };

    public static string Serialize(VillageEvent e) => JsonConvert.SerializeObject(e, Settings);

    public static string FormatTimestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses an event. Rejects anything that is not a JSON object with the supported
    /// <c>schema_version</c> and an <c>event_type</c>; every other field is optional, unknown
    /// fields are ignored, wrong-typed fields are dropped and long strings are truncated.
    /// </summary>
    public static bool TryDeserialize(string? json, out VillageEvent? ev, out string? error)
    {
        ev = null;
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "Event is empty.";
            return false;
        }
        if (json!.Length > VillageSchema.MaxSerializedBytes)
        {
            error = $"Event exceeds {VillageSchema.MaxSerializedBytes} bytes.";
            return false;
        }

        JObject obj;
        try
        {
            var token = ParseToken(json);
            if (token is not JObject o)
            {
                error = "Event is not a JSON object.";
                return false;
            }
            obj = o;
        }
        catch (JsonException ex)
        {
            error = "Invalid JSON: " + ex.Message;
            return false;
        }

        var version = obj["schema_version"];
        if (version == null || version.Type != JTokenType.Integer)
        {
            error = "Missing schema_version.";
            return false;
        }
        if (version.Value<int>() != VillageSchema.Version)
        {
            error = $"Unsupported schema_version {version.Value<int>()} (expected {VillageSchema.Version}).";
            return false;
        }

        var type = Str(obj, "event_type");
        if (string.IsNullOrWhiteSpace(type))
        {
            error = "Missing event_type.";
            return false;
        }

        ev = new VillageEvent
        {
            EventType     = type!,
            EventId       = Str(obj, "event_id") ?? Guid.NewGuid().ToString(),
            SessionId     = Str(obj, "session_id") ?? string.Empty,
            ModelId       = Str(obj, "model_id") ?? string.Empty,
            ProjectName   = Str(obj, "project_name") ?? string.Empty,
            Timestamp     = Str(obj, "timestamp") ?? FormatTimestamp(DateTimeOffset.UtcNow),
            ToolName      = Str(obj, "tool_name"),
            Activity      = VillageActivities.Normalize(Str(obj, "activity")),
            Area          = VillageAreas.Normalize(Str(obj, "area")),
            Success       = Bool(obj, "success"),
            DurationMs    = Long(obj, "duration_ms"),
            AffectedCount = Long(obj, "affected_count"),
            Warehouse     = Str(obj, "warehouse"),
            ClientName    = Str(obj, "client_name"),
            Status        = Str(obj, "status"),
            Sequence      = Long(obj, "sequence") ?? 0
        };
        return true;
    }

    /// <summary>
    /// Parses JSON without Newtonsoft's automatic date recognition, so ISO-8601 timestamps stay
    /// strings and round-trip byte-for-byte. Throws <see cref="JsonException"/> on malformed input.
    /// </summary>
    public static JToken ParseToken(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None,
            FloatParseHandling = FloatParseHandling.Double,
            MaxDepth = 32
        };
        var token = JToken.ReadFrom(reader);
        if (reader.Read())
            throw new JsonReaderException("Additional content after the JSON value.");
        return token;
    }

    public static string Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        return value!.Length <= VillageSchema.MaxStringLength
            ? value
            : value.Substring(0, VillageSchema.MaxStringLength);
    }

    private static string? Str(JObject obj, string key)
    {
        var t = obj[key];
        return t != null && t.Type == JTokenType.String ? Truncate(t.Value<string>()) : null;
    }

    private static bool? Bool(JObject obj, string key)
    {
        var t = obj[key];
        return t != null && t.Type == JTokenType.Boolean ? t.Value<bool>() : (bool?)null;
    }

    private static long? Long(JObject obj, string key)
    {
        var t = obj[key];
        if (t == null) return null;
        if (t.Type == JTokenType.Integer) return t.Value<long>();
        if (t.Type == JTokenType.Float)
        {
            var d = t.Value<double>();
            if (double.IsNaN(d) || double.IsInfinity(d)) return null;
            return (long)d;
        }
        return null;
    }
}

using Newtonsoft.Json;

namespace RevitMCP.Addin.Checkpoints;

/// <summary>
/// One Revit DocumentChanged event as seen by an open checkpoint (#98). Ids are ElementId.Value.
/// </summary>
public sealed class ChangeBatch
{
    [JsonProperty("at")] public DateTimeOffset At { get; set; }
    [JsonProperty("transactions")] public List<string> TransactionNames { get; set; } = new();

    /// <summary>Revit's UndoOperation: TransactionCommitted, TransactionUndone, TransactionRedone, ...</summary>
    [JsonProperty("operation")] public string Operation { get; set; } = string.Empty;

    /// <summary>"agent:&lt;tool&gt;", "page", "user" or "unknown".</summary>
    [JsonProperty("by")] public string Attribution { get; set; } = ChangeAttribution.Unknown;

    [JsonProperty("added")] public long[] Added { get; set; } = Array.Empty<long>();
    [JsonProperty("modified")] public long[] Modified { get; set; } = Array.Empty<long>();
    [JsonProperty("deleted")] public long[] Deleted { get; set; } = Array.Empty<long>();

    [JsonIgnore] public int IdCount => Added.Length + Modified.Length + Deleted.Length;
}

/// <summary>Who made a change, as recorded on a <see cref="ChangeBatch"/>.</summary>
public static class ChangeAttribution
{
    public const string AgentPrefix = "agent:";
    public const string Page = "page";
    public const string User = "user";
    public const string Unknown = "unknown";

    public static string Agent(string tool, string? client = null)
        => AgentPrefix + tool + (string.IsNullOrWhiteSpace(client) ? string.Empty : " (" + client!.Trim() + ")");

    public static bool IsAgent(string? attribution)
        => attribution != null && attribution.StartsWith(AgentPrefix, StringComparison.Ordinal);

    /// <summary>The tool name of an agent attribution without the client suffix; null for anything else.</summary>
    public static string? AgentTool(string? attribution)
    {
        if (!IsAgent(attribution)) return null;
        var rest = attribution!.Substring(AgentPrefix.Length);
        var space = rest.IndexOf(' ');
        return space < 0 ? rest : rest.Substring(0, space);
    }

    /// <summary>The client of an agent attribution ("agent:tool (client)" → "client"); null when there is none.</summary>
    public static string? AgentClient(string? attribution)
    {
        if (!IsAgent(attribution)) return null;
        var open = attribution!.IndexOf(" (", AgentPrefix.Length, StringComparison.Ordinal);
        if (open < 0 || !attribution.EndsWith(")", StringComparison.Ordinal)) return null;
        return attribution.Substring(open + 2, attribution.Length - open - 3);
    }
}

/// <summary>Category, name, type and level of an element, kept so a deleted element can still be described.</summary>
public class ElementIdentity
{
    [JsonProperty("category", NullValueHandling = NullValueHandling.Ignore)] public string? Category { get; set; }
    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string? Name { get; set; }
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? TypeName { get; set; }
    [JsonProperty("level", NullValueHandling = NullValueHandling.Ignore)] public string? Level { get; set; }
}

/// <summary>An element's identity, location and parameter values at checkpoint time.</summary>
public sealed class ElementSnapshot : ElementIdentity
{
    /// <summary>LocationPoint xyz in millimetres, or null.</summary>
    [JsonProperty("point_mm", NullValueHandling = NullValueHandling.Ignore)] public double[]? LocationMm { get; set; }

    /// <summary>LocationCurve endpoints [x0, y0, z0, x1, y1, z1] in millimetres, or null.</summary>
    [JsonProperty("line_mm", NullValueHandling = NullValueHandling.Ignore)] public double[]? LocationLineMm { get; set; }

    /// <summary>LocationPoint.Rotation in radians, or null.</summary>
    [JsonProperty("rotation", NullValueHandling = NullValueHandling.Ignore)] public double? RotationRad { get; set; }

    /// <summary>Parameter name → value as display string (the formatting of revit_get_element_parameters).</summary>
    [JsonProperty("parameters")] public Dictionary<string, string?> Parameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Parameter name → stored value in internal units, encoded by <see cref="RawParameterValue"/>, for an exact
    /// restore. Absent for read-only parameters, which cannot be restored.
    /// </summary>
    [JsonProperty("raw")] public Dictionary<string, string> Raw { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// A parameter's stored value as text: "S:" string, "I:" integer, "D:" double in internal units (round-trip),
/// "E:" element id. Restoring from it never goes through display-unit parsing.
/// </summary>
public static class RawParameterValue
{
    public static string String(string? value) => "S:" + (value ?? string.Empty);
    public static string Integer(int value) => "I:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public static string Double(double value) => "D:" + value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
    public static string ElementId(long value) => "E:" + value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Splits an encoded value into its kind (S, I, D, E) and text; false for anything malformed.</summary>
    public static bool TryParse(string? raw, out char kind, out string text)
    {
        kind = '\0';
        text = string.Empty;
        if (raw == null || raw.Length < 2 || raw[1] != ':' || "SIDE".IndexOf(raw[0]) < 0) return false;
        kind = raw[0];
        text = raw.Substring(2);
        return true;
    }
}

/// <summary>The identity of one Revit warning across snapshots.</summary>
public sealed class WarningKey
{
    [JsonProperty("definition")] public string DefinitionId { get; set; } = string.Empty;
    [JsonProperty("description")] public string Description { get; set; } = string.Empty;

    /// <summary>Failing ∪ additional element ids, sorted.</summary>
    [JsonProperty("ids")] public long[] ElementIds { get; set; } = Array.Empty<long>();

    /// <summary>Warning | Error | DocumentCorruption.</summary>
    [JsonProperty("severity")] public string Severity { get; set; } = "Warning";

    [JsonIgnore] public string Key => DefinitionId + "|" + string.Join(",", ElementIds);
}

/// <summary>What a checkpoint snapshotted; empty means only change ids and warnings are tracked.</summary>
public sealed class SnapshotScope
{
    [JsonProperty("element_ids")] public List<long> ElementIds { get; set; } = new();
    [JsonProperty("categories")] public List<string> Categories { get; set; } = new();
    [JsonProperty("view_id", NullValueHandling = NullValueHandling.Ignore)] public long? ViewId { get; set; }
    [JsonProperty("use_selection")] public bool UseSelection { get; set; }

    /// <summary>Parameter names to snapshot; empty means every instance parameter with a value.</summary>
    [JsonProperty("parameters")] public List<string> Parameters { get; set; } = new();

    [JsonIgnore]
    public bool IsEmpty => ElementIds.Count == 0 && Categories.Count == 0 && ViewId == null && !UseSelection;
}

/// <summary>
/// "Now" remembered for one document: its change stamp, warnings and an optional snapshot, plus every change
/// recorded since. Pure — no Revit API; the Revit side fills it (CheckpointChangeTracker, CheckpointSnapshotReader).
/// </summary>
public sealed class Checkpoint
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("model_id")] public string ModelId { get; set; } = string.Empty;
    [JsonProperty("document_title")] public string DocumentTitle { get; set; } = string.Empty;
    [JsonProperty("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonProperty("document_version")] public long DocumentVersion { get; set; }
    [JsonProperty("created_by", NullValueHandling = NullValueHandling.Ignore)] public string? CreatedBy { get; set; }

    /// <summary>The plan this checkpoint belongs to (#99), when any.</summary>
    [JsonProperty("plan_id", NullValueHandling = NullValueHandling.Ignore)] public string? PlanId { get; set; }

    [JsonProperty("warnings")] public List<WarningKey> Warnings { get; set; } = new();
    [JsonProperty("scope", NullValueHandling = NullValueHandling.Ignore)] public SnapshotScope? Scope { get; set; }

    /// <summary>Stored in its own file (written once); see <see cref="CheckpointStore"/>.</summary>
    [JsonIgnore] public Dictionary<long, ElementSnapshot> Snapshot { get; set; } = new();

    [JsonProperty("batches")] public List<ChangeBatch> Batches { get; set; } = new();

    /// <summary>Identity of elements seen in batches, so a later deletion can still be described.</summary>
    [JsonProperty("identities")] public Dictionary<long, ElementIdentity> Identities { get; set; } = new();

    /// <summary>
    /// False once changes may be missing: after a Revit restart (batches since then were not seen) or when
    /// the recording cap was reached. Snapshot comparison stays exact either way.
    /// </summary>
    [JsonProperty("tracking_complete")] public bool TrackingComplete { get; set; } = true;

    /// <summary>Why <see cref="TrackingComplete"/> is false, when it is.</summary>
    [JsonProperty("tracking_note", NullValueHandling = NullValueHandling.Ignore)] public string? TrackingNote { get; set; }

    /// <summary>Total ids recorded over all batches (for the recording cap).</summary>
    [JsonIgnore] public int RecordedIdCount => Batches.Sum(b => b.IdCount);
}

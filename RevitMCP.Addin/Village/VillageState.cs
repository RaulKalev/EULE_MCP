using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Addin.Village;

/// <summary>Agent character states. The viewer animates transitions; the connector only sets them.</summary>
public static class VillageAgentStates
{
    public const string Idle         = "idle";
    public const string Moving       = "moving";
    public const string Inspecting   = "inspecting";
    public const string Working      = "working";
    public const string Success      = "success";
    public const string Error        = "error";
    public const string Disconnected = "disconnected";
}

/// <summary>One agent (an MCP client such as Claude Code), never a Revit element.</summary>
public sealed class VillageAgent
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("state")] public string State { get; set; } = VillageAgentStates.Idle;
    [JsonProperty("area")] public string Area { get; set; } = VillageAreas.Unknown;
    [JsonProperty("building")] public string Building { get; set; } = VillageLayout.Square;
    [JsonProperty("activity")] public string Activity { get; set; } = VillageActivities.Unknown;
    [JsonProperty("last_tool")] public string? LastTool { get; set; }
    [JsonProperty("last_event_at")] public string LastEventAt { get; set; } = string.Empty;
    [JsonProperty("in_progress")] public int InProgress { get; set; }
    [JsonProperty("tool_count")] public long ToolCount { get; set; }
    [JsonProperty("failure_count")] public long FailureCount { get; set; }

    [JsonIgnore] public DateTimeOffset LastEvent { get; set; }
    [JsonIgnore] public DateTimeOffset StateSince { get; set; }
}

/// <summary>Per-area aggregate counters shown when a building is selected.</summary>
public sealed class VillageAreaCounters
{
    [JsonProperty("area")] public string Area { get; set; } = string.Empty;
    [JsonProperty("reads")] public long Reads { get; set; }
    [JsonProperty("writes")] public long Writes { get; set; }
    [JsonProperty("exports")] public long Exports { get; set; }
    [JsonProperty("failures")] public long Failures { get; set; }
    [JsonProperty("deferred")] public long Deferred { get; set; }
    [JsonProperty("affected")] public long Affected { get; set; }
    [JsonProperty("last_tool")] public string? LastTool { get; set; }
    [JsonProperty("last_activity")] public string? LastActivity { get; set; }
    [JsonProperty("last_at")] public string? LastAt { get; set; }

    [JsonIgnore] public long Total => Reads + Writes + Exports + Failures + Deferred;
}

/// <summary>Kinds of story steps the aggregator produces.</summary>
public static class VillageStepKinds
{
    public const string Work     = "work";
    public const string Move     = "move";
    public const string Error    = "error";
    public const string Deferred = "deferred";
    public const string Graph    = "graph";
    public const string Project  = "project";
    public const string Session  = "session";
    public const string Bridge   = "bridge";
}

/// <summary>
/// One displayed unit of activity: many raw events collapse into one step
/// ("24 tags created", "inspected sheets (6 tools)").
/// </summary>
public sealed class VillageStoryStep
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("kind")] public string Kind { get; set; } = VillageStepKinds.Work;
    [JsonProperty("agent")] public string Agent { get; set; } = string.Empty;
    [JsonProperty("area")] public string Area { get; set; } = VillageAreas.Unknown;
    [JsonProperty("building")] public string Building { get; set; } = VillageLayout.Square;
    [JsonProperty("from_area")] public string? FromArea { get; set; }
    [JsonProperty("activity")] public string Activity { get; set; } = VillageActivities.Unknown;
    [JsonProperty("tool_count")] public int ToolCount { get; set; }
    /// <summary>Distinct tool names, at most five.</summary>
    [JsonProperty("tools")] public List<string> Tools { get; set; } = new();
    [JsonProperty("affected")] public long Affected { get; set; }
    [JsonProperty("failures")] public int Failures { get; set; }
    [JsonProperty("deferred")] public int Deferred { get; set; }
    [JsonProperty("label")] public string Label { get; set; } = string.Empty;
    [JsonProperty("started_at")] public string StartedAt { get; set; } = string.Empty;
    [JsonProperty("ended_at")] public string EndedAt { get; set; } = string.Empty;
    [JsonProperty("open")] public bool Open { get; set; }
    /// <summary>First raw sequence number folded into the step (replay ordering).</summary>
    [JsonProperty("first_sequence")] public long FirstSequence { get; set; }
    [JsonProperty("last_sequence")] public long LastSequence { get; set; }

    [JsonIgnore] public DateTimeOffset Started { get; set; }
    [JsonIgnore] public DateTimeOffset LastEvent { get; set; }
    [JsonIgnore] public string Group { get; set; } = string.Empty;

    public VillageStoryStep Clone()
    {
        return new VillageStoryStep
        {
            Id = Id, Kind = Kind, Agent = Agent, Area = Area, Building = Building, FromArea = FromArea,
            Activity = Activity, ToolCount = ToolCount, Tools = new List<string>(Tools), Affected = Affected,
            Failures = Failures, Deferred = Deferred, Label = Label, StartedAt = StartedAt, EndedAt = EndedAt,
            Open = Open, FirstSequence = FirstSequence, LastSequence = LastSequence,
            Started = Started, LastEvent = LastEvent, Group = Group
        };
    }
}

/// <summary>Aggregator counters for the diagnostics panel.</summary>
public sealed class VillageAggregateStats
{
    [JsonProperty("events_processed")] public long EventsProcessed { get; set; }
    [JsonProperty("steps_emitted")] public long StepsEmitted { get; set; }
    [JsonProperty("unknown_tools")] public long UnknownTools { get; set; }
    [JsonProperty("unknown_events")] public long UnknownEvents { get; set; }
    [JsonProperty("catch_up_entries")] public long CatchUpEntries { get; set; }
    [JsonProperty("last_sequence")] public long LastSequence { get; set; }
}

/// <summary>
/// Full state handed to a viewer on connect and re-sent (throttled) while it changes.
/// Everything in it is derived; nothing can be sent back.
/// </summary>
public sealed class VillageStateSnapshot
{
    [JsonProperty("schema_version")] public int SchemaVersion { get; set; } = VillageSchema.Version;
    [JsonProperty("read_only")] public bool ReadOnly { get; set; } = true;
    [JsonProperty("notice")] public string Notice { get; set; } = VillageSchema.ReadOnlyNotice;
    [JsonProperty("session_id")] public string SessionId { get; set; } = string.Empty;
    [JsonProperty("project_name")] public string ProjectName { get; set; } = string.Empty;
    [JsonProperty("model_id")] public string ModelId { get; set; } = string.Empty;
    [JsonProperty("revit_version")] public string RevitVersion { get; set; } = string.Empty;
    [JsonProperty("is_workshared")] public bool IsWorkshared { get; set; }
    [JsonProperty("updated_at")] public string UpdatedAt { get; set; } = string.Empty;
    /// <summary><c>live</c> or <c>catch_up</c>.</summary>
    [JsonProperty("mode")] public string Mode { get; set; } = "live";
    [JsonProperty("agents")] public List<VillageAgent> Agents { get; set; } = new();
    [JsonProperty("areas")] public List<VillageAreaCounters> Areas { get; set; } = new();
    /// <summary>Steps still accumulating (one per active agent).</summary>
    [JsonProperty("current_steps")] public List<VillageStoryStep> CurrentSteps { get; set; } = new();
    /// <summary>Most recent finished steps, newest last.</summary>
    [JsonProperty("recent_steps")] public List<VillageStoryStep> RecentSteps { get; set; } = new();
    [JsonProperty("recent_failures")] public int RecentFailures { get; set; }
    [JsonProperty("buildings")] public List<VillageBuilding> Buildings { get; set; } = new();
    /// <summary>Graph snapshot object (see VillageGraphSnapshot) or null in limited mode.</summary>
    [JsonProperty("graph")] public JToken? Graph { get; set; }
    /// <summary>Theme result object (see VillageThemeResult) or null when no graph is available.</summary>
    [JsonProperty("theme")] public JToken? Theme { get; set; }
    [JsonProperty("stats")] public VillageAggregateStats Stats { get; set; } = new();
    [JsonProperty("queue")] public VillageQueueStats? Queue { get; set; }
    [JsonProperty("viewer_options")] public VillageViewerOptions ViewerOptions { get; set; } = new();
    [JsonProperty("instances")] public List<VillageInstanceLink> Instances { get; set; } = new();
}

/// <summary>Limits and defaults the viewer honours (from <see cref="VillageOptions"/>).</summary>
public sealed class VillageViewerOptions
{
    [JsonProperty("animation_speed")] public double AnimationSpeed { get; set; } = 1.0;
    [JsonProperty("max_buildings")] public int MaxBuildings { get; set; } = 12;
    [JsonProperty("max_effects")] public int MaxEffects { get; set; } = 6;
    [JsonProperty("recent_activity_limit")] public int RecentActivityLimit { get; set; } = 200;
    [JsonProperty("reconnect_backoff_ms")] public int ReconnectBackoffMs { get; set; } = 1000;
    [JsonProperty("reconnect_backoff_max_ms")] public int ReconnectBackoffMaxMs { get; set; } = 15000;
    [JsonProperty("aggregation_window_ms")] public int AggregationWindowMs { get; set; } = 1500;
}

/// <summary>Another Revit instance's village, discovered from the local instance folder.</summary>
public sealed class VillageInstanceLink
{
    [JsonProperty("process_id")] public int ProcessId { get; set; }
    [JsonProperty("revit_version")] public string RevitVersion { get; set; } = string.Empty;
    [JsonProperty("project_name")] public string ProjectName { get; set; } = string.Empty;
    [JsonProperty("url")] public string Url { get; set; } = string.Empty;
    [JsonProperty("is_current")] public bool IsCurrent { get; set; }
}

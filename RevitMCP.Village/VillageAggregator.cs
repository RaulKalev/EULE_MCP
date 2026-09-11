using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RevitMCP.Village;

/// <summary>
/// Deterministic aggregation of raw events into the village state and a story of steps.
/// Rapid activity in one area folds into a single step with counters; errors, approvals and
/// area transitions are always preserved as their own steps. Single-consumer: call
/// <see cref="Process"/>, <see cref="Tick"/> and <see cref="Snapshot"/> from one thread
/// (the hub's consumer loop). No Revit API dependency, no I/O.
/// </summary>
public sealed class VillageAggregator
{
    /// <summary>An agent with nothing in progress becomes idle after this long.</summary>
    public const int IdleAfterMs = 8000;

    /// <summary>Failures inside this window count towards the warning area.</summary>
    public const int RecentFailureWindowMs = 5 * 60 * 1000;

    /// <summary>
    /// A tool_started without a matching completion (well beyond the connector's 30 s request
    /// timeout) is forgotten so the in-progress counter cannot stick.
    /// </summary>
    public const int StuckAfterMs = 120 * 1000;

    /// <summary>Catch-up mode widens the merge window so a backlog collapses into fewer steps.</summary>
    public const int CatchUpWindowMultiplier = 4;

    private const int MaxToolsPerStep = 5;

    private static readonly Regex AgentIdPattern = new("[^a-z0-9]+", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> AreaNouns = new(StringComparer.Ordinal)
    {
        [VillageAreas.Sheets] = "sheets",
        [VillageAreas.Schedules] = "schedules",
        [VillageAreas.Views] = "views",
        [VillageAreas.FamiliesTypes] = "types",
        [VillageAreas.TagsAnnotations] = "tags",
        [VillageAreas.Elements] = "elements",
        [VillageAreas.Electrical] = "circuits",
        [VillageAreas.FireAlarm] = "fire alarm items",
        [VillageAreas.Security] = "security items",
        [VillageAreas.Lighting] = "lighting items",
        [VillageAreas.ItAv] = "data items",
        [VillageAreas.Coordination] = "clashes",
        [VillageAreas.Office] = "files",
        [VillageAreas.Graph] = "nodes",
        [VillageAreas.Project] = "items",
        [VillageAreas.Unknown] = "items"
    };

    private readonly VillageOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Dictionary<string, VillageAgent> _agents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VillageStoryStep> _openSteps = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VillageAreaCounters> _areas = new(StringComparer.Ordinal);
    private readonly LinkedList<VillageStoryStep> _history = new();
    private readonly LinkedList<DateTimeOffset> _recentFailures = new();
    private readonly VillageAggregateStats _stats = new();

    private long _stepId;
    private string _sessionId = string.Empty;
    private string _modelId = VillageModelId.NoDocument;
    private string _projectName = "No document";
    private string _revitVersion = string.Empty;
    private bool _isWorkshared;

    public VillageAggregator(VillageOptions? options = null, Func<DateTimeOffset>? clock = null)
    {
        _options = options ?? VillageOptions.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        Buildings = VillageLayout.Clone();
        foreach (var area in VillageAreas.All)
            if (area != VillageAreas.Unknown)
                _areas[area] = new VillageAreaCounters { Area = area };
    }

    /// <summary>Raised with every finished step (never with open ones).</summary>
    public event Action<VillageStoryStep>? StepEmitted;

    /// <summary>Raised after anything visible changed. Consumers should throttle snapshots.</summary>
    public event Action? StateChanged;

    public bool CatchUp { get; private set; }

    /// <summary>Layout with graph-derived sizes; replaced by the hub when the graph changes.</summary>
    public List<VillageBuilding> Buildings { get; set; }

    /// <summary>Category warehouses; replaced by the hub when the graph changes. Empty without a graph.</summary>
    public List<VillageWarehouse> Warehouses { get; private set; } = new();

    /// <summary>
    /// Replaces the yard. A character standing at a warehouse the rebuild removed walks back to
    /// its area's landmark, so no agent is ever parked on an id the viewer cannot place.
    /// </summary>
    public void SetWarehouses(List<VillageWarehouse>? warehouses)
    {
        Warehouses = warehouses ?? new List<VillageWarehouse>();
        foreach (var agent in _agents.Values)
        {
            if (agent.Warehouse == null || WarehouseFor(agent.Warehouse, agent.Area) != null) continue;
            agent.Warehouse = null;
            agent.Building = VillageLayout.BuildingFor(agent.Area, Buildings);
        }
    }

    public JToken? Graph { get; set; }
    public JToken? Theme { get; set; }
    public VillageQueueStats? QueueStats { get; set; }
    public List<VillageInstanceLink> Instances { get; set; } = new();

    public string ModelId => _modelId;
    public string ProjectName => _projectName;
    public IReadOnlyDictionary<string, VillageAgent> Agents => _agents;
    public IReadOnlyDictionary<string, VillageAreaCounters> Areas => _areas;
    public VillageAggregateStats Stats => _stats;

    /// <summary>Switches catch-up mode. Entering it widens the merge window; leaving it restores live behaviour.</summary>
    public void SetCatchUp(bool catchUp)
    {
        if (CatchUp == catchUp) return;
        CatchUp = catchUp;
        if (catchUp) _stats.CatchUpEntries++;
        StateChanged?.Invoke();
    }

    /// <summary>Seeds the project identity before any event arrives (session start).</summary>
    public void SetProject(VillageProjectContext? context, string? sessionId = null)
    {
        var ctx = context ?? VillageProjectContext.Empty;
        _modelId = ctx.ComputeModelId();
        _projectName = ctx.DisplayName;
        _revitVersion = ctx.RevitVersion;
        _isWorkshared = ctx.IsWorkshared;
        if (!string.IsNullOrEmpty(sessionId)) _sessionId = sessionId!;
    }

    // ─── Event processing ───────────────────────────────────────────────────

    public void Process(VillageEvent e)
    {
        if (e == null) return;
        var now = _clock();
        _stats.EventsProcessed++;
        if (e.Sequence > _stats.LastSequence) _stats.LastSequence = e.Sequence;

        if (!VillageEventTypes.IsKnown(e.EventType))
        {
            _stats.UnknownEvents++;
            return;
        }

        // Foreign or future vocabulary never reaches the state: it is folded into "unknown".
        e.Area = VillageAreas.Normalize(e.Area);
        e.Activity = VillageActivities.Normalize(e.Activity);

        if (!string.IsNullOrEmpty(e.SessionId)) _sessionId = e.SessionId;
        HandleProjectIdentity(e, now);

        switch (e.EventType)
        {
            case VillageEventTypes.SessionStarted:
                Emit(SimpleStep(VillageStepKinds.Session, string.Empty, VillageAreas.Project, "Connector session started", e, now));
                break;

            case VillageEventTypes.ProjectChanged:
                // Handled by HandleProjectIdentity when the model actually changed.
                break;

            case VillageEventTypes.GraphRefreshed:
                Emit(SimpleStep(VillageStepKinds.Graph, string.Empty, VillageAreas.Graph,
                    e.Success == true
                        ? "Graph snapshot refreshed" + (e.AffectedCount.HasValue ? $" ({Num(e.AffectedCount.Value)} nodes)" : string.Empty)
                        : "Graph snapshot unavailable",
                    e, now));
                break;

            case VillageEventTypes.BridgeConnected:
            {
                var agent = GetAgent(e.ClientName, now);
                agent.State = VillageAgentStates.Idle;
                agent.StateSince = now;
                Emit(SimpleStep(VillageStepKinds.Bridge, agent.Id, agent.Area, agent.Name + " connected", e, now));
                break;
            }

            case VillageEventTypes.BridgeDisconnected:
            {
                var agent = GetAgent(e.ClientName, now);
                CloseStep(agent.Id, now);
                agent.State = VillageAgentStates.Disconnected;
                agent.StateSince = now;
                agent.InProgress = 0;
                Emit(SimpleStep(VillageStepKinds.Bridge, agent.Id, agent.Area, agent.Name + " disconnected", e, now));
                break;
            }

            case VillageEventTypes.ToolStarted:
                OnToolStarted(e, now);
                break;

            case VillageEventTypes.ToolCompleted:
            case VillageEventTypes.ToolDeferred:
            case VillageEventTypes.ToolFailed:
                OnToolFinished(e, now);
                break;
        }

        StateChanged?.Invoke();
    }

    /// <summary>
    /// Closes idle steps, moves silent agents to idle/disconnected and prunes the failure window.
    /// Call periodically (every few hundred milliseconds). Returns true when something changed.
    /// </summary>
    public bool Tick()
    {
        var now = _clock();
        var changed = false;
        var window = CurrentWindowMs();

        foreach (var agentId in _openSteps.Keys.ToList())
        {
            var step = _openSteps[agentId];
            if ((now - step.LastEvent).TotalMilliseconds > window)
            {
                CloseStep(agentId, now);
                changed = true;
            }
        }

        foreach (var agent in _agents.Values)
        {
            var silentMs = (now - agent.LastEvent).TotalMilliseconds;
            if (agent.State == VillageAgentStates.Disconnected) continue;

            if (agent.InProgress > 0 && silentMs > StuckAfterMs)
            {
                agent.InProgress = 0;
                changed = true;
            }

            if (agent.State != VillageAgentStates.Idle && agent.InProgress == 0 && silentMs > IdleAfterMs)
            {
                agent.State = VillageAgentStates.Idle;
                agent.Activity = VillageActivities.Unknown;
                agent.StateSince = now;
                changed = true;
            }

            // Finished work with nothing queued: step outside to the overlook on the edge of town.
            // Still nothing after parkAfterSeconds: wander to the park in the middle of the village.
            if (agent.State == VillageAgentStates.Idle && agent.InProgress == 0 && !_openSteps.ContainsKey(agent.Id))
            {
                if (silentMs > _options.ParkAfterSeconds * 1000.0)
                {
                    if (SendAgentTo(agent, VillageLayout.Park, "Heads to the park", now)) changed = true;
                }
                else if (silentMs > IdleAfterMs &&
                         !string.Equals(agent.Building, VillageLayout.Park, StringComparison.Ordinal))
                {
                    if (SendAgentTo(agent, VillageLayout.Overlook, "Heads to the overlook", now)) changed = true;
                }
            }

            if (agent.State == VillageAgentStates.Idle && silentMs > _options.AgentIdleSeconds * 1000.0)
            {
                agent.State = VillageAgentStates.Disconnected;
                agent.StateSince = now;
                agent.InProgress = 0;
                Emit(SimpleStep(VillageStepKinds.Bridge, agent.Id, agent.Area, agent.Name + " disconnected (idle)", null, now));
                changed = true;
            }
        }

        while (_recentFailures.First != null && (now - _recentFailures.First.Value).TotalMilliseconds > RecentFailureWindowMs)
        {
            _recentFailures.RemoveFirst();
            changed = true;
        }

        if (changed) StateChanged?.Invoke();
        return changed;
    }

    /// <summary>Closes every open step (shutdown or project switch).</summary>
    public void Flush()
    {
        var now = _clock();
        foreach (var agentId in _openSteps.Keys.ToList())
            CloseStep(agentId, now);
    }

    // ─── Snapshots ──────────────────────────────────────────────────────────

    public VillageStateSnapshot Snapshot()
    {
        var now = _clock();
        var snapshot = new VillageStateSnapshot
        {
            SessionId = _sessionId,
            ProjectName = _projectName,
            ModelId = _modelId,
            RevitVersion = _revitVersion,
            IsWorkshared = _isWorkshared,
            UpdatedAt = VillageEventSerializer.FormatTimestamp(now),
            Mode = CatchUp ? "catch_up" : "live",
            Buildings = VillageLayout.Clone(Buildings),
            Warehouses = VillageWarehouseYard.Clone(Warehouses),
            Graph = Graph?.DeepClone(),
            Theme = Theme?.DeepClone(),
            Stats = new VillageAggregateStats
            {
                EventsProcessed = _stats.EventsProcessed,
                StepsEmitted = _stats.StepsEmitted,
                UnknownTools = _stats.UnknownTools,
                UnknownEvents = _stats.UnknownEvents,
                CatchUpEntries = _stats.CatchUpEntries,
                LastSequence = _stats.LastSequence
            },
            Queue = QueueStats,
            RecentFailures = _recentFailures.Count,
            Instances = new List<VillageInstanceLink>(Instances),
            ViewerOptions = new VillageViewerOptions
            {
                AnimationSpeed = _options.AnimationSpeed,
                MaxBuildings = _options.MaxBuildings,
                MaxWarehouses = _options.MaxWarehouses,
                MaxEffects = _options.MaxEffects,
                RecentActivityLimit = _options.RecentActivityLimit,
                ReconnectBackoffMs = _options.ReconnectBackoffMs,
                ReconnectBackoffMaxMs = _options.ReconnectBackoffMaxMs,
                AggregationWindowMs = _options.AggregationWindowMs
            }
        };

        foreach (var agent in _agents.Values.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            snapshot.Agents.Add(new VillageAgent
            {
                Id = agent.Id, Name = agent.Name, State = agent.State, Area = agent.Area, Building = agent.Building, Warehouse = agent.Warehouse,
                Activity = agent.Activity, LastTool = agent.LastTool, LastEventAt = agent.LastEventAt,
                InProgress = agent.InProgress, ToolCount = agent.ToolCount, FailureCount = agent.FailureCount,
                LastEvent = agent.LastEvent, StateSince = agent.StateSince
            });
        }

        foreach (var area in VillageAreas.All)
        {
            if (!_areas.TryGetValue(area, out var c)) continue;
            snapshot.Areas.Add(new VillageAreaCounters
            {
                Area = c.Area, Reads = c.Reads, Writes = c.Writes, Exports = c.Exports, Failures = c.Failures,
                Deferred = c.Deferred, Affected = c.Affected, LastTool = c.LastTool, LastActivity = c.LastActivity, LastAt = c.LastAt
            });
        }

        foreach (var step in _openSteps.Values.OrderBy(s => s.Id))
        {
            var clone = step.Clone();
            clone.Open = true;
            clone.EndedAt = VillageEventSerializer.FormatTimestamp(step.LastEvent);
            snapshot.CurrentSteps.Add(clone);
        }

        var recent = new List<VillageStoryStep>();
        var node = _history.Last;
        while (node != null && recent.Count < _options.RecentActivityLimit)
        {
            recent.Add(node.Value.Clone());
            node = node.Previous;
        }
        recent.Reverse();
        snapshot.RecentSteps = recent;
        return snapshot;
    }

    /// <summary>Bounded replay-ready history (oldest first).</summary>
    public List<VillageStoryStep> History() => _history.Select(s => s.Clone()).ToList();

    // ─── Tool handling ──────────────────────────────────────────────────────

    private void OnToolStarted(VillageEvent e, DateTimeOffset now)
    {
        var agent = GetAgent(e.ClientName, now);
        if (e.Area == VillageAreas.Unknown) _stats.UnknownTools++;

        agent.InProgress++;
        agent.LastTool = e.ToolName;
        Touch(agent, now);

        if (ShouldMove(agent, e))
            MoveAgent(agent, e.Area, e, now);

        agent.Activity = e.Activity;
        agent.State = VillageActivities.IsWrite(e.Activity) ? VillageAgentStates.Working : VillageAgentStates.Inspecting;
        agent.StateSince = now;

        var step = GetOrOpenStep(agent, e, now, GroupOf(e));
        step.LastEvent = now;
        step.LastSequence = e.Sequence;
        AddTool(step, e.ToolName);
        step.Label = LabelFor(step);
    }

    private void OnToolFinished(VillageEvent e, DateTimeOffset now)
    {
        var agent = GetAgent(e.ClientName, now);
        if (e.Area == VillageAreas.Unknown && e.EventType == VillageEventTypes.ToolCompleted) _stats.UnknownTools++;

        if (agent.InProgress > 0) agent.InProgress--;
        agent.ToolCount++;
        agent.LastTool = e.ToolName;
        Touch(agent, now);

        if (ShouldMove(agent, e))
            MoveAgent(agent, e.Area, e, now);

        var counters = CountersFor(e.Area);
        counters.LastTool = e.ToolName;
        counters.LastActivity = e.Activity;
        counters.LastAt = e.Timestamp;

        switch (e.EventType)
        {
            case VillageEventTypes.ToolFailed:
            {
                counters.Failures++;
                agent.FailureCount++;
                agent.State = VillageAgentStates.Error;
                agent.Activity = VillageActivities.Error;
                agent.StateSince = now;
                _recentFailures.AddLast(now);
                while (_recentFailures.Count > 1000) _recentFailures.RemoveFirst();

                var step = GetOrOpenStep(agent, e, now, "error:" + (e.ToolName ?? string.Empty));
                step.Kind = VillageStepKinds.Error;
                step.Failures++;
                step.ToolCount++;
                step.LastEvent = now;
                step.LastSequence = e.Sequence;
                AddTool(step, e.ToolName);
                step.Label = LabelFor(step);
                break;
            }

            case VillageEventTypes.ToolDeferred:
            {
                counters.Deferred++;
                agent.State = VillageAgentStates.Idle;
                agent.Activity = e.Activity;
                agent.StateSince = now;

                var step = GetOrOpenStep(agent, e, now, "deferred:" + (e.ToolName ?? string.Empty));
                step.Kind = VillageStepKinds.Deferred;
                step.Deferred++;
                step.ToolCount++;
                step.LastEvent = now;
                step.LastSequence = e.Sequence;
                AddTool(step, e.ToolName);
                step.Label = LabelFor(step);
                break;
            }

            default:
            {
                if (VillageActivities.IsWrite(e.Activity)) counters.Writes++;
                else if (e.Activity == VillageActivities.Export) counters.Exports++;
                else counters.Reads++;
                if (e.AffectedCount.HasValue && e.AffectedCount.Value > 0) counters.Affected += e.AffectedCount.Value;

                agent.State = VillageAgentStates.Success;
                agent.Activity = e.Activity;
                agent.StateSince = now;

                var step = GetOrOpenStep(agent, e, now, GroupOf(e));
                step.Kind = VillageStepKinds.Work;
                step.Activity = e.Activity;
                step.ToolCount++;
                if (e.AffectedCount.HasValue && e.AffectedCount.Value > 0) step.Affected += e.AffectedCount.Value;
                step.LastEvent = now;
                step.LastSequence = e.Sequence;
                AddTool(step, e.ToolName);
                step.Label = LabelFor(step);
                break;
            }
        }
    }

    /// <summary>
    /// The warehouse for this event, or null: only a live warehouse, only for an area that stores
    /// model elements. A stale id (the yard is rebuilt whenever the graph changes) resolves to
    /// null, so the character falls back to the area's landmark rather than walking nowhere.
    /// </summary>
    private VillageWarehouse? WarehouseFor(VillageEvent e) => WarehouseFor(e.Warehouse, e.Area);

    private VillageWarehouse? WarehouseFor(string? id, string? area)
    {
        if (string.IsNullOrEmpty(id) || !VillageLayout.IsStorableArea(area)) return null;
        foreach (var warehouse in Warehouses)
            if (string.Equals(warehouse.Id, id, StringComparison.Ordinal)) return warehouse;
        return null;
    }

    /// <summary>
    /// The character moves when the area changes, and also when a tool names a different category
    /// warehouse inside the same area. A tool that names no category leaves it where it is, so a
    /// follow-up call on the same elements does not walk it back to the area's landmark.
    /// </summary>
    private bool ShouldMove(VillageAgent agent, VillageEvent e)
    {
        if (!string.Equals(agent.Area, e.Area, StringComparison.Ordinal)) return true;
        var warehouse = WarehouseFor(e);
        return warehouse != null && !string.Equals(agent.Building, warehouse.Id, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks a character to a landmark that belongs to no area (the overlook, the park). Returns
    /// false when it is already there, so Tick can call this every few hundred milliseconds
    /// without emitting a step each time. The area becomes unknown, so the next tool moves it back
    /// to wherever that tool's work belongs.
    /// </summary>
    private bool SendAgentTo(VillageAgent agent, string buildingId, string label, DateTimeOffset now)
    {
        if (string.Equals(agent.Building, buildingId, StringComparison.Ordinal)) return false;

        var from = agent.Area;
        agent.Area = VillageAreas.Unknown;
        agent.Warehouse = null;
        agent.Building = buildingId;
        agent.Activity = VillageActivities.Unknown;

        var move = SimpleStep(VillageStepKinds.Move, agent.Id, VillageAreas.Unknown, label, null, now);
        move.Building = buildingId;
        move.FromArea = from;
        Emit(move);
        return true;
    }

    private void MoveAgent(VillageAgent agent, string toArea, VillageEvent e, DateTimeOffset now)
    {
        CloseStep(agent.Id, now);
        var from = agent.Area;
        var warehouse = WarehouseFor(e.Warehouse, toArea);
        agent.Area = toArea;
        agent.Warehouse = warehouse?.Id;
        agent.Building = warehouse?.Id ?? VillageLayout.BuildingFor(toArea, Buildings);
        agent.State = VillageAgentStates.Moving;
        agent.StateSince = now;

        var label = warehouse?.Label ?? VillageLayout.AreaLabel(toArea);
        var move = SimpleStep(VillageStepKinds.Move, agent.Id, toArea, "Heads to " + label, e, now);
        move.Warehouse = warehouse?.Id;
        move.WarehouseLabel = warehouse?.Label;
        if (warehouse != null) move.Building = warehouse.Id;
        move.FromArea = from;
        Emit(move);
    }

    private VillageStoryStep GetOrOpenStep(VillageAgent agent, VillageEvent e, DateTimeOffset now, string group)
    {
        if (_openSteps.TryGetValue(agent.Id, out var open))
        {
            var sameGroup = string.Equals(open.Group, group, StringComparison.Ordinal) &&
                            string.Equals(open.Area, e.Area, StringComparison.Ordinal) &&
                            string.Equals(open.Warehouse, agent.Warehouse, StringComparison.Ordinal);
            var inWindow = (now - open.LastEvent).TotalMilliseconds <= CurrentWindowMs();
            if (sameGroup && inWindow) return open;
            CloseStep(agent.Id, now);
        }

        // The step happens where the character is standing, which stays the warehouse while it
        // keeps working there even if this particular tool named no category.
        var standing = WarehouseFor(agent.Warehouse, e.Area);
        var step = new VillageStoryStep
        {
            Id = ++_stepId,
            Kind = VillageStepKinds.Work,
            Agent = agent.Id,
            Area = e.Area,
            Building = standing?.Id ?? VillageLayout.BuildingFor(e.Area, Buildings),
            Warehouse = standing?.Id,
            WarehouseLabel = standing?.Label,
            Activity = e.Activity,
            Group = group,
            Started = now,
            LastEvent = now,
            StartedAt = VillageEventSerializer.FormatTimestamp(now),
            FirstSequence = e.Sequence,
            LastSequence = e.Sequence,
            Open = true
        };
        _openSteps[agent.Id] = step;
        return step;
    }

    private void CloseStep(string agentId, DateTimeOffset now)
    {
        if (!_openSteps.TryGetValue(agentId, out var step)) return;
        _openSteps.Remove(agentId);
        if (step.ToolCount == 0)
            return; // only tool_started events arrived and nothing finished: nothing worth a story step
        step.Open = false;
        step.EndedAt = VillageEventSerializer.FormatTimestamp(step.LastEvent);
        step.Label = LabelFor(step);
        Emit(step);
    }

    private VillageStoryStep SimpleStep(string kind, string agentId, string area, string label, VillageEvent? e, DateTimeOffset now)
    {
        return new VillageStoryStep
        {
            Id = ++_stepId,
            Kind = kind,
            Agent = agentId,
            Area = area,
            Building = VillageLayout.BuildingFor(area, Buildings),
            Activity = e?.Activity ?? VillageActivities.Unknown,
            Label = label,
            Started = now,
            LastEvent = now,
            StartedAt = VillageEventSerializer.FormatTimestamp(now),
            EndedAt = VillageEventSerializer.FormatTimestamp(now),
            FirstSequence = e?.Sequence ?? _stats.LastSequence,
            LastSequence = e?.Sequence ?? _stats.LastSequence,
            Open = false
        };
    }

    private void Emit(VillageStoryStep step)
    {
        _history.AddLast(step);
        while (_history.Count > _options.HistoryLimit) _history.RemoveFirst();
        _stats.StepsEmitted++;
        StepEmitted?.Invoke(step.Clone());
    }

    private void HandleProjectIdentity(VillageEvent e, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(e.ModelId) || string.Equals(e.ModelId, _modelId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrEmpty(e.ProjectName) && e.ModelId == _modelId) _projectName = e.ProjectName;
            return;
        }

        // A different document is active: finish every story, forget per-project counters, keep history.
        Flush();
        foreach (var c in _areas.Values)
        {
            c.Reads = 0; c.Writes = 0; c.Exports = 0; c.Failures = 0; c.Deferred = 0; c.Affected = 0;
            c.LastTool = null; c.LastActivity = null; c.LastAt = null;
        }
        foreach (var agent in _agents.Values)
        {
            agent.Area = VillageAreas.Unknown;
            agent.Building = VillageLayout.Square;
            agent.InProgress = 0;
            if (agent.State != VillageAgentStates.Disconnected)
            {
                agent.State = VillageAgentStates.Idle;
                agent.StateSince = now;
            }
        }
        _recentFailures.Clear();
        Graph = null;
        Theme = null;

        _modelId = e.ModelId;
        _projectName = string.IsNullOrEmpty(e.ProjectName) ? "No document" : e.ProjectName;
        Emit(SimpleStep(VillageStepKinds.Project, string.Empty, VillageAreas.Project, "Switched to " + _projectName, e, now));
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private VillageAgent GetAgent(string? clientName, DateTimeOffset now)
    {
        var name = string.IsNullOrWhiteSpace(clientName) ? "Agent" : clientName!.Trim();
        var id = AgentId(name);
        if (_agents.TryGetValue(id, out var existing))
        {
            if (existing.State == VillageAgentStates.Disconnected)
            {
                existing.State = VillageAgentStates.Idle;
                existing.StateSince = now;
                Emit(SimpleStep(VillageStepKinds.Bridge, existing.Id, existing.Area, existing.Name + " reconnected", null, now));
            }
            return existing;
        }

        var agent = new VillageAgent
        {
            Id = id,
            Name = name,
            State = VillageAgentStates.Idle,
            LastEvent = now,
            StateSince = now,
            LastEventAt = VillageEventSerializer.FormatTimestamp(now)
        };
        _agents[id] = agent;
        Emit(SimpleStep(VillageStepKinds.Bridge, id, VillageAreas.Unknown, name + " connected", null, now));
        return agent;
    }

    public static string AgentId(string name)
    {
        var id = AgentIdPattern.Replace(name.ToLowerInvariant(), "-").Trim('-');
        return id.Length == 0 ? "agent" : id;
    }

    private static void Touch(VillageAgent agent, DateTimeOffset now)
    {
        agent.LastEvent = now;
        agent.LastEventAt = VillageEventSerializer.FormatTimestamp(now);
    }

    private VillageAreaCounters CountersFor(string area)
    {
        if (_areas.TryGetValue(area, out var c)) return c;
        c = new VillageAreaCounters { Area = area };
        _areas[area] = c;
        return c;
    }

    private double CurrentWindowMs() =>
        _options.AggregationWindowMs * (CatchUp ? CatchUpWindowMultiplier : 1);

    private static string GroupOf(VillageEvent e)
    {
        switch (e.Activity)
        {
            case VillageActivities.Inspect:
            case VillageActivities.Search:
            case VillageActivities.Analyze:
            case VillageActivities.Validate:
                return "read";
            case VillageActivities.Create:
            case VillageActivities.Modify:
            case VillageActivities.Delete:
            case VillageActivities.Export:
            case VillageActivities.BuildGraph:
                return e.Activity;
            default:
                return "unknown";
        }
    }

    private static void AddTool(VillageStoryStep step, string? tool)
    {
        if (string.IsNullOrEmpty(tool)) return;
        if (step.Tools.Contains(tool!)) return;
        if (step.Tools.Count >= MaxToolsPerStep) return;
        step.Tools.Add(tool!);
    }

    private static string Num(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Plural(long count, string singular, string plural) =>
        count == 1 ? singular : plural;

    /// <summary>Deterministic, template-based label. Never free text from tools or agents.</summary>
    public static string LabelFor(VillageStoryStep step)
    {
        // A step that happened at a category warehouse names the category instead of the area.
        var area = step.WarehouseLabel ?? VillageLayout.AreaLabel(step.Area);
        var noun = step.WarehouseLabel ?? (AreaNouns.TryGetValue(step.Area, out var n) ? n : "items");
        var suffix = step.ToolCount > 1 ? $" ({Num(step.ToolCount)} tools)" : string.Empty;
        var tool = step.Tools.Count > 0 ? step.Tools[0] : "tool";

        switch (step.Kind)
        {
            case VillageStepKinds.Error:
                return step.Failures <= 1
                    ? $"{tool} failed in {area}"
                    : $"{Num(step.Failures)} tools failed in {area}";
            case VillageStepKinds.Deferred:
                return step.Deferred <= 1
                    ? $"Awaiting approval: {tool}"
                    : $"Awaiting approval: {tool} ({Num(step.Deferred)} requests)";
            case VillageStepKinds.Move:
                return "Heads to " + area;
            case VillageStepKinds.Work:
                break;
            default:
                return step.Label;
        }

        switch (step.Activity)
        {
            case VillageActivities.Create:
                return step.Affected > 0 ? $"{Num(step.Affected)} {noun} created{suffix}" : $"Created {noun}{suffix}";
            case VillageActivities.Modify:
                return step.Affected > 0 ? $"{Num(step.Affected)} {noun} updated{suffix}" : $"Updated {noun}{suffix}";
            case VillageActivities.Delete:
                return step.Affected > 0 ? $"{Num(step.Affected)} {noun} deleted{suffix}" : $"Deleted {noun}{suffix}";
            case VillageActivities.Export:
                return $"Exported from {area}{suffix}";
            case VillageActivities.BuildGraph:
                return step.Affected > 0 ? $"Graph rebuilt ({Num(step.Affected)} nodes)" : "Graph rebuilt";
            case VillageActivities.Search:
                return $"Searched {area}{suffix}";
            case VillageActivities.Analyze:
                return $"Analyzed {area}{suffix}";
            case VillageActivities.Validate:
                return $"Checked {area}{suffix}";
            case VillageActivities.Inspect:
                return $"Inspected {area}{suffix}";
            default:
                return $"Worked in {area}{suffix}";
        }
    }
}

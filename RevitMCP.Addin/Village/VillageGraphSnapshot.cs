using System.Globalization;
using Newtonsoft.Json;

namespace RevitMCP.Addin.Village;

/// <summary>Freshness as the village can know it without touching Revit.</summary>
public static class VillageGraphFreshnessStatus
{
    /// <summary>No graph file exists for this model.</summary>
    public const string Missing = "missing";
    /// <summary>A graph exists but no graph tool has reported freshness since the connector started.</summary>
    public const string Unknown = "unknown";
    /// <summary>The last graph tool response said the graph matches the document.</summary>
    public const string Fresh = "fresh";
    /// <summary>The last graph tool response said the graph is stale.</summary>
    public const string Stale = "stale";
    /// <summary>Reported fresh, but write tools have succeeded since — treat as stale until re-checked.</summary>
    public const string PossiblyStale = "possibly_stale";
}

/// <summary>What the connector learned from the most recent graph tool response and write activity.</summary>
public sealed class VillageFreshnessHint
{
    public bool? Stale { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset? ReportedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    /// <summary>Time of the most recent successful write tool on this model, if any.</summary>
    public DateTimeOffset? LastWriteAt { get; set; }
}

public sealed class VillageGraphFreshness
{
    [JsonProperty("status")] public string Status { get; set; } = VillageGraphFreshnessStatus.Missing;
    [JsonProperty("reason")] public string Reason { get; set; } = string.Empty;
    [JsonProperty("built_at")] public string? BuiltAt { get; set; }
    [JsonProperty("age_seconds")] public long? AgeSeconds { get; set; }
    [JsonProperty("reported_at")] public string? ReportedAt { get; set; }
    [JsonProperty("source")] public string Source { get; set; } = "none";
    [JsonProperty("checked_at")] public string CheckedAt { get; set; } = string.Empty;

    /// <summary>Pure combination of the graph's own metadata with the connector's hint.</summary>
    public static VillageGraphFreshness Evaluate(bool exists, string? builtAt, VillageFreshnessHint? hint, DateTimeOffset now)
    {
        var f = new VillageGraphFreshness { CheckedAt = VillageEventSerializer.FormatTimestamp(now), BuiltAt = builtAt };
        if (!exists)
        {
            f.Status = VillageGraphFreshnessStatus.Missing;
            f.Reason = "No graph has been built for this model. The village runs in limited mode.";
            return f;
        }

        DateTimeOffset? built = null;
        if (!string.IsNullOrWhiteSpace(builtAt) &&
            DateTimeOffset.TryParse(builtAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            built = parsed;
            f.AgeSeconds = Math.Max(0, (long)(now - parsed).TotalSeconds);
        }

        if (hint == null || !hint.Stale.HasValue)
        {
            f.Status = VillageGraphFreshnessStatus.Unknown;
            f.Reason = "Freshness is unknown until revit_graph_status (or another graph tool) runs.";
            f.Source = "graph file";
            if (hint?.LastWriteAt != null && built != null && hint.LastWriteAt > built)
            {
                f.Status = VillageGraphFreshnessStatus.PossiblyStale;
                f.Reason = "Write tools succeeded after the graph was built; freshness has not been re-checked.";
            }
            return f;
        }

        f.Source = string.IsNullOrEmpty(hint.Source) ? "graph tool" : hint.Source;
        f.ReportedAt = hint.ReportedAt.HasValue ? VillageEventSerializer.FormatTimestamp(hint.ReportedAt.Value) : null;

        if (hint.Stale == true)
        {
            f.Status = VillageGraphFreshnessStatus.Stale;
            f.Reason = string.IsNullOrWhiteSpace(hint.Reason) ? "The last graph tool reported the graph as stale." : hint.Reason;
            return f;
        }

        var reference = hint.ReportedAt ?? built;
        if (hint.LastWriteAt != null && reference != null && hint.LastWriteAt > reference)
        {
            f.Status = VillageGraphFreshnessStatus.PossiblyStale;
            f.Reason = "Reported fresh, but write tools have succeeded since. Re-check with revit_graph_status.";
            return f;
        }

        f.Status = VillageGraphFreshnessStatus.Fresh;
        f.Reason = string.IsNullOrWhiteSpace(hint.Reason) ? "The last graph tool reported the graph as matching the document." : hint.Reason;
        return f;
    }
}

public sealed class VillageGraphCounts
{
    [JsonProperty("nodes")] public long Nodes { get; set; }
    [JsonProperty("edges")] public long Edges { get; set; }
    [JsonProperty("elements")] public long Elements { get; set; }
    [JsonProperty("types")] public long Types { get; set; }
    [JsonProperty("sheets")] public long Sheets { get; set; }
    [JsonProperty("views")] public long Views { get; set; }
    [JsonProperty("schedules")] public long Schedules { get; set; }
    [JsonProperty("levels")] public long Levels { get; set; }
    [JsonProperty("spaces")] public long Spaces { get; set; }
    [JsonProperty("worksets")] public long Worksets { get; set; }
    [JsonProperty("panels")] public long Panels { get; set; }
    [JsonProperty("circuits")] public long Circuits { get; set; }
    [JsonProperty("tags")] public long Tags { get; set; }
}

public sealed class VillageGraphHealth
{
    [JsonProperty("circuits_without_panel")] public long CircuitsWithoutPanel { get; set; }
    [JsonProperty("circuits_without_elements")] public long CircuitsWithoutElements { get; set; }
    [JsonProperty("elements_without_location")] public long ElementsWithoutLocation { get; set; }
    [JsonProperty("issues")] public long Issues => CircuitsWithoutPanel + CircuitsWithoutElements;
}

/// <summary>
/// Read-only, aggregate view of one model graph for the village. Contains counts, category
/// and level distributions, health indicators and freshness — never node ids, names of
/// elements, parameter values, paths or user names.
/// </summary>
public sealed class VillageGraphSnapshot
{
    [JsonProperty("exists")] public bool Exists { get; set; }
    [JsonProperty("file_name")] public string? FileName { get; set; }
    [JsonProperty("root_source")] public string? RootSource { get; set; }
    [JsonProperty("model_name")] public string? ModelName { get; set; }
    [JsonProperty("schema_version")] public int? SchemaVersion { get; set; }
    [JsonProperty("is_workshared")] public bool? IsWorkshared { get; set; }
    [JsonProperty("built_at")] public string? BuiltAt { get; set; }
    [JsonProperty("freshness")] public VillageGraphFreshness Freshness { get; set; } = new();
    [JsonProperty("counts")] public VillageGraphCounts Counts { get; set; } = new();
    [JsonProperty("health")] public VillageGraphHealth Health { get; set; } = new();
    [JsonProperty("categories")] public List<VillageThemeEvidence> Categories { get; set; } = new();
    [JsonProperty("levels")] public List<VillageThemeEvidence> Levels { get; set; } = new();
    [JsonProperty("read_ms")] public long ReadMs { get; set; }
    [JsonProperty("used_cache")] public bool UsedCache { get; set; }
    [JsonProperty("error")] public string? Error { get; set; }
    [JsonProperty("read_at")] public string ReadAt { get; set; } = string.Empty;
    [JsonProperty("note")] public string Note { get; set; } =
        "Graph values are routing and visualization metadata captured at build time, not live Revit values.";

    /// <summary>Snapshot for a model without a graph (limited mode).</summary>
    public static VillageGraphSnapshot Missing(DateTimeOffset now, VillageFreshnessHint? hint) => new()
    {
        Exists = false,
        Freshness = VillageGraphFreshness.Evaluate(false, null, hint, now),
        ReadAt = VillageEventSerializer.FormatTimestamp(now)
    };
}

/// <summary>Scales landmark sizes from graph counts. Deterministic and bounded (1–4).</summary>
public static class VillageLayoutSizer
{
    public static List<VillageBuilding> Apply(IReadOnlyList<VillageBuilding> buildings, VillageGraphSnapshot? graph, VillageThemeResult? theme = null)
    {
        var sized = VillageLayout.Clone(buildings);
        foreach (var b in sized) b.Size = b.BaseSize;
        if (graph == null || !graph.Exists) return sized;

        var c = graph.Counts;
        foreach (var b in sized)
        {
            switch (b.Id)
            {
                case VillageLayout.TownHall:      b.Size = Math.Max(b.BaseSize, c.Nodes >= 50_000 ? 4 : 3); break;
                case VillageLayout.Archive:       b.Size = Scale(c.Sheets, 25, 250); break;
                case VillageLayout.Lookout:       b.Size = Scale(c.Views, 25, 250); break;
                case VillageLayout.Market:        b.Size = Scale(c.Schedules, 10, 100); break;
                case VillageLayout.Workshop:      b.Size = Scale(c.Types, 50, 500); break;
                case VillageLayout.SignShop:      b.Size = Scale(c.Tags, 100, 2000); break;
                case VillageLayout.Houses:        b.Size = Scale(c.Elements, 2500, 25_000); break;
                case VillageLayout.Utility:       b.Size = Scale(c.Panels + c.Circuits, 20, 200); break;
                case VillageLayout.Survey:        b.Size = b.BaseSize; break;
                case VillageLayout.Office:        b.Size = b.BaseSize; break;
                case VillageLayout.Warning:
                    b.Size = graph.Health.Issues > 0 || graph.Freshness.Status == VillageGraphFreshnessStatus.Stale ? 2 : 1;
                    break;
            }
        }
        return sized;
    }

    /// <summary>1 when nothing, 2 below <paramref name="medium"/>, 3 below <paramref name="large"/>, 4 otherwise.</summary>
    public static int Scale(long count, long medium, long large)
    {
        if (count <= 0) return 1;
        if (count < medium) return 2;
        if (count < large) return 3;
        return 4;
    }
}

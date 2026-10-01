using Newtonsoft.Json;

namespace RevitMCP.Village;

/// <summary>Where a flyer is on the board.</summary>
public static class VillageFlyerStates
{
    /// <summary>Pinned today; cleared when the local day changes.</summary>
    public const string New = "new";

    /// <summary>Kept by the user; cleared after <see cref="VillageOptions.FlyerArchiveDays"/>.</summary>
    public const string Archived = "archived";
}

/// <summary>
/// One element a read tool returned: its id and the few routing fields that identify it in a list.
/// Never a parameter value — the extractor reads only the fields named here.
/// </summary>
public sealed class VillageFlyerItem
{
    [JsonProperty("id")] public long Id { get; set; }
    [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)] public string? Name { get; set; }
    [JsonProperty("category", NullValueHandling = NullValueHandling.Ignore)] public string? Category { get; set; }
    [JsonProperty("family", NullValueHandling = NullValueHandling.Ignore)] public string? Family { get; set; }
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type { get; set; }
    [JsonProperty("level", NullValueHandling = NullValueHandling.Ignore)] public string? Level { get; set; }
}

/// <summary>A category and how many of the flyer's elements are in it.</summary>
public sealed class VillageFlyerCount
{
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("count")] public int Count { get; set; }
}

/// <summary>
/// The elements one read tool call returned, pinned to the notice board at the overlook. Built by
/// the connector from a result the agent already received, so it costs no tokens; the agent never
/// sees it. Holds element ids and names only, for the model it was captured in.
/// </summary>
public sealed class VillageFlyer
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("title")] public string Title { get; set; } = string.Empty;
    [JsonProperty("tool")] public string Tool { get; set; } = string.Empty;
    [JsonProperty("client", NullValueHandling = NullValueHandling.Ignore)] public string? Client { get; set; }
    [JsonProperty("model_id")] public string ModelId { get; set; } = string.Empty;
    [JsonProperty("project_name")] public string ProjectName { get; set; } = string.Empty;
    [JsonProperty("created_at")] public DateTimeOffset CreatedAt { get; set; }
    [JsonProperty("updated_at")] public DateTimeOffset UpdatedAt { get; set; }
    [JsonProperty("archived_at", NullValueHandling = NullValueHandling.Ignore)] public DateTimeOffset? ArchivedAt { get; set; }
    [JsonProperty("state")] public string State { get; set; } = VillageFlyerStates.New;

    /// <summary>Distinct elements found in the result; more than <see cref="Items"/> holds when truncated.</summary>
    [JsonProperty("total")] public int Total { get; set; }

    /// <summary>True when the result held more elements than the flyer keeps, or was too large to read in full.</summary>
    [JsonProperty("truncated")] public bool Truncated { get; set; }

    /// <summary>Largest categories first, at most four; the rest of the breakdown is in the items.</summary>
    [JsonProperty("categories")] public List<VillageFlyerCount> Categories { get; set; } = new();

    /// <summary>Null in the board listing; present when one flyer is opened.</summary>
    [JsonProperty("items", NullValueHandling = NullValueHandling.Ignore)] public List<VillageFlyerItem>? Items { get; set; }

    /// <summary>The same flyer without its items, for the board listing.</summary>
    public VillageFlyer Summary() => new()
    {
        Id = Id, Title = Title, Tool = Tool, Client = Client, ModelId = ModelId, ProjectName = ProjectName,
        CreatedAt = CreatedAt, UpdatedAt = UpdatedAt, ArchivedAt = ArchivedAt, State = State,
        Total = Total, Truncated = Truncated, Categories = new List<VillageFlyerCount>(Categories), Items = null
    };

    /// <summary>"revit_get_elements_info" → "Get elements info".</summary>
    public static string TitleFor(string? toolName)
    {
        var name = (toolName ?? string.Empty).Trim();
        if (name.StartsWith("revit_", StringComparison.OrdinalIgnoreCase)) name = name.Substring(6);
        name = name.Replace('_', ' ').Trim();
        if (name.Length == 0) return "Query";
        return char.ToUpperInvariant(name[0]) + name.Substring(1);
    }

    /// <summary>Top categories by count, largest first; ties by name.</summary>
    public static List<VillageFlyerCount> CountCategories(IEnumerable<VillageFlyerItem> items, int max = 4)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Category)) continue;
            counts[item.Category!] = counts.TryGetValue(item.Category!, out var n) ? n + 1 : 1;
        }
        return counts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(kv => new VillageFlyerCount { Name = kv.Key, Count = kv.Value })
            .ToList();
    }
}

/// <summary>What the viewer gets from <c>GET /flyers</c> and the <c>flyers</c> SSE event.</summary>
public sealed class VillageFlyerBoardView
{
    [JsonProperty("enabled")] public bool Enabled { get; set; }

    /// <summary>False when the add-in has no Revit handler (tests, or the handler failed to start).</summary>
    [JsonProperty("show_available")] public bool ShowAvailable { get; set; }

    [JsonProperty("flyers")] public List<VillageFlyer> Flyers { get; set; } = new();
}

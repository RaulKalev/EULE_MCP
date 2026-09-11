namespace RevitMCP.Village;

/// <summary>Where to look for a model's graph. Built by the host from values already in hand.</summary>
public sealed class VillageGraphSource
{
    /// <summary>Exact database path learned from a graph tool response. Wins when set and present.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>Roots to search (configured shared folder, local fallback). Searched one level deep.</summary>
    public List<string> Roots { get; set; } = new();

    /// <summary>Model file name without extension; the graph file is <c>&lt;ModelName&gt;.graph.db</c>.</summary>
    public string ModelName { get; set; } = string.Empty;

    public string RootSource { get; set; } = string.Empty;
    public VillageFreshnessHint? Hint { get; set; }
}

public sealed class VillageGraphReadResult
{
    public VillageGraphSnapshot Snapshot { get; set; } = new();
    public VillageThemeResult? Theme { get; set; }

    /// <summary>False when the file was unchanged and the cached result was returned.</summary>
    public bool Changed { get; set; }
    public string? DatabasePath { get; set; }
}

/// <summary>
/// Supplies read-only graph snapshots to <see cref="VillageStateHub"/>.
///
/// The implementation lives with whoever owns the graph database format (the Revit add-in), so
/// this library never depends on it. An implementation must be safe by construction: it may only
/// read, must never hold the published file open, and must never throw — failures belong in
/// <see cref="VillageGraphSnapshot.Error"/>.
/// </summary>
public interface IVillageGraphReader
{
    VillageGraphReadResult Read(VillageGraphSource source);
}

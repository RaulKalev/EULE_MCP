using System.Globalization;

namespace RevitMCP.Addin.Graph;

public sealed class GraphFreshnessResult
{
    public bool Stale { get; set; }
    public string Reason { get; set; } = string.Empty;
    public string? BuiltAt { get; set; }
    public string? StoredVersion { get; set; }
    public string? CurrentVersion { get; set; }
    public string? VersionSource { get; set; }
    public long? StoredElementCount { get; set; }
    public long? CurrentElementCount { get; set; }
}

/// <summary>
/// Decides whether a stored graph still matches the open document. Pure comparison of the
/// stored meta against the <see cref="ModelVersionSignal"/> captured on the Revit thread.
/// See docs/model-graph.md for which Revit signal feeds the version and its limitations.
/// </summary>
public static class GraphFreshness
{
    public static GraphFreshnessResult Evaluate(GraphMeta? meta, ModelVersionSignal current)
    {
        var r = new GraphFreshnessResult
        {
            CurrentVersion = current.Value,
            CurrentElementCount = current.ElementCount,
            VersionSource = current.Source
        };

        if (meta == null)
        {
            r.Stale = true;
            r.Reason = "No graph has been built for this model yet. Run revit_graph_build.";
            return r;
        }

        r.BuiltAt = meta.BuiltAt;
        r.StoredVersion = meta.CentralVersion;
        r.StoredElementCount = meta.ElementCount;

        if (meta.SchemaVersion != GraphSchema.SchemaVersion)
        {
            r.Stale = true;
            r.Reason = $"Graph schema version {meta.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} " +
                       $"does not match the connector's schema version {GraphSchema.SchemaVersion}. Rebuild the graph.";
            return r;
        }

        if (!string.IsNullOrWhiteSpace(meta.ModelName) &&
            !string.IsNullOrWhiteSpace(current.ModelName) &&
            !string.Equals(meta.ModelName, current.ModelName, StringComparison.OrdinalIgnoreCase))
        {
            r.Stale = true;
            r.Reason = $"Graph was built for model '{meta.ModelName}' but the open document is '{current.ModelName}'.";
            return r;
        }

        var storedVersion = meta.CentralVersion ?? string.Empty;
        var currentVersion = current.Value ?? string.Empty;
        var versionKnown = storedVersion.Length > 0 && currentVersion.Length > 0;

        if (versionKnown && !string.Equals(storedVersion, currentVersion, StringComparison.Ordinal))
        {
            r.Stale = true;
            r.Reason = $"Model version changed since the graph was built ({storedVersion} → {currentVersion}; source: {current.Source}).";
            return r;
        }

        if (meta.ElementCount.HasValue && meta.ElementCount.Value != current.ElementCount)
        {
            r.Stale = true;
            r.Reason = $"Element count changed from {meta.ElementCount.Value} to {current.ElementCount} " +
                       "(unsaved edits, or elements added/removed since the build).";
            return r;
        }

        r.Stale = false;
        r.Reason = versionKnown
            ? $"Model version and element count match the graph (source: {current.Source})."
            : $"Model version signal unavailable ({(string.IsNullOrWhiteSpace(current.Source) ? "unknown" : current.Source)}); " +
              $"element count matches ({current.ElementCount}). Treat as fresh with caution.";
        return r;
    }
}

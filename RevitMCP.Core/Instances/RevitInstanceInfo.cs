namespace RevitMCP.Core.Instances;

/// <summary>
/// Describes a running Revit process that hosts a RevitMCP pipe server.
/// Serialized to a per-process registration file so the bridge can discover
/// which Revit instances are available and route requests to the right one.
/// </summary>
public class RevitInstanceInfo
{
    public int ProcessId { get; set; }

    /// <summary>Revit major version, e.g. "2024" or "2026".</summary>
    public string RevitVersion { get; set; } = string.Empty;

    /// <summary>The unique named pipe this instance is listening on.</summary>
    public string PipeName { get; set; } = string.Empty;

    /// <summary>Title of the active document, if known.</summary>
    public string? DocumentTitle { get; set; }

    /// <summary>UTC timestamp of the last registration update.</summary>
    public DateTime UpdatedUtc { get; set; }
}

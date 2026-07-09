namespace RevitMCP.Core.Configuration;

public static class RevitMcpDefaults
{
    /// <summary>
    /// Legacy shared pipe name kept as a connection fallback for older add-in builds.
    /// Current add-in builds host a unique per-process pipe (see <see cref="BuildPipeName"/>)
    /// so multiple Revit instances (e.g. 2024 and 2026 side by side) never contend
    /// for the same pipe.
    /// </summary>
    public const string PipeName = "RKTools.RevitMCP.2026";
    public const int ConnectTimeoutMs = 5000;
    public const int RequestTimeoutMs = 30000;
    public const string ClientName = "Unknown MCP Client";

    /// <summary>Builds the unique pipe name for one Revit process, e.g. "RKTools.RevitMCP.2026.12345".</summary>
    public static string BuildPipeName(string revitVersion, int processId) =>
        $"RKTools.RevitMCP.{revitVersion}.{processId}";
}

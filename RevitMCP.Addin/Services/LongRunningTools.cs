namespace RevitMCP.Addin.Services;

/// <summary>
/// Tools whose work routinely takes longer than <see cref="QueryLimits.TimeoutSeconds"/>: link reloads
/// (an IFC link is converted to an intermediate .ifc.RVT first), saves and synchronize-with-central of
/// large models (#90). The bridge, the pipe server and the approval re-dispatch wait this long for them
/// instead of reporting a timeout while Revit is still working.
/// Pure and linked into RevitMCP.Bridge and the tests. It is deliberately not in RevitMCP.Core: a hot-reloaded
/// add-in shares the RevitMCP.Core already loaded in Revit, so new Core types are missing there.
/// </summary>
public static class LongRunningTools
{
    /// <summary>Timeout for long-running tools, in seconds.</summary>
    public const int TimeoutSeconds = 900;

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        "revit_reload_links_from",
        "revit_remove_links",
        "revit_save_document",
        "revit_sync_with_central",
        "revit_activate_document"
    };

    public static bool IsLongRunning(string? toolName) => toolName != null && Names.Contains(toolName);

    /// <summary>Execution timeout for <paramref name="toolName"/> in milliseconds.</summary>
    public static int TimeoutMsFor(string? toolName, int defaultTimeoutMs) =>
        IsLongRunning(toolName) ? Math.Max(defaultTimeoutMs, TimeoutSeconds * 1000) : defaultTimeoutMs;
}

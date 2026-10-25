namespace RevitMCP.Addin.UI.ViewModels;

/// <summary>How urgent the compact strip's headline is; maps to the Status.* brushes.</summary>
public enum HeadlineSeverity
{
    Ok,
    Info,
    Warning,
    Error
}

/// <summary>
/// The one line the collapsed connector window shows: the most important thing right now, highest first —
/// disconnected, requests waiting for approval, a running job, the agent calling tools, then the model and selection.
/// Pure, so the priority rule is unit-tested without WPF.
/// </summary>
public static class CompactHeadline
{
    /// <param name="jobTitle">Title of a running job, or null when none runs.</param>
    /// <returns>Text, severity, and the count for the pill (0 = no pill).</returns>
    public static (string Text, HeadlineSeverity Severity, int Count) Compute(
        bool isRunning,
        int pendingCount,
        string? jobTitle,
        int jobDone,
        int jobTotal,
        bool agentActive,
        string? modelTitle,
        int selectedCount)
    {
        if (!isRunning)
            return ("Disconnected", HeadlineSeverity.Error, 0);

        if (pendingCount > 0)
            return ($"{pendingCount} waiting for approval", HeadlineSeverity.Warning, pendingCount);

        if (jobTitle != null)
        {
            var title = string.IsNullOrWhiteSpace(jobTitle) ? "Job" : jobTitle!.Trim();
            return (jobTotal > 0 ? $"{title} · {jobDone}/{jobTotal}" : title, HeadlineSeverity.Info, 0);
        }

        if (agentActive)
            return ("Agent working…", HeadlineSeverity.Info, 0);

        var model = string.IsNullOrWhiteSpace(modelTitle) ? "No document open" : modelTitle!.Trim();
        return (selectedCount > 0 ? $"{model} · {selectedCount} selected" : model, HeadlineSeverity.Ok, 0);
    }
}

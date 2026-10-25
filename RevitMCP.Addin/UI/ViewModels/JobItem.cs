namespace RevitMCP.Addin.UI.ViewModels;

/// <summary>A job (#100) as the JOBS tab shows it.</summary>
public class JobItem
{
    public string JobId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Client { get; init; } = string.Empty;
    public string Document { get; init; } = string.Empty;

    /// <summary>RUNNING, WAITING, PAUSED, SUSPENDED, COMPLETED, FAILED or CANCELLED.</summary>
    public string StateLabel { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public bool IsBad { get; init; }

    /// <summary>"7 / 14 · 3.2 min".</summary>
    public string Progress { get; init; } = string.Empty;

    /// <summary>Steps done and total, for the progress meter.</summary>
    public int Done { get; init; }
    public int Total { get; init; }
    public string StopReason { get; init; } = string.Empty;
    public bool HasStopReason => StopReason.Length > 0;

    public bool CanPause { get; init; }
    public bool CanResume { get; init; }
    public bool CanCancel { get; init; }
    public bool HasReport { get; init; }
}

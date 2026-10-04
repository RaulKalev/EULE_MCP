namespace RevitMCP.Addin.UI.ViewModels;

/// <summary>
/// UI display model for a decided approval request, shown under "Recent decisions" in the Pending tab,
/// so a request that was approved but did not run (model changed, expired, Revit busy) or failed is
/// visible where the user clicked Approve.
/// </summary>
public class ApprovalOutcomeItem
{
    /// <summary>HH:mm:ss of the last state change.</summary>
    public string Time { get; init; } = string.Empty;
    public string Tool { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;

    /// <summary>RUNNING | DONE | FAILED | REJECTED.</summary>
    public string StateLabel { get; init; } = string.Empty;

    /// <summary>Result message (for failures: why it did not run or what went wrong).</summary>
    public string Message { get; init; } = string.Empty;

    public bool IsSucceeded { get; init; }
    public bool IsFailed { get; init; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
}

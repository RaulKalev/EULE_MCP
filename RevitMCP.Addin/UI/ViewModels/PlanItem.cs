namespace RevitMCP.Addin.UI.ViewModels;

/// <summary>A plan (#99) as the Plans section of the Pending tab shows it.</summary>
public class PlanItem
{
    public string PlanId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Client { get; init; } = string.Empty;
    public string Document { get; init; } = string.Empty;

    /// <summary>ACTIVE, EXHAUSTED, EXPIRED, REVOKED or CLOSED.</summary>
    public string StateLabel { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public bool CanRevoke { get; init; }

    /// <summary>"12/100 calls · 340/2000 elements · 1 violation · expires in 84 min".</summary>
    public string Progress { get; init; } = string.Empty;
    public string StateReason { get; init; } = string.Empty;
    public bool HasStateReason => StateReason.Length > 0;
}

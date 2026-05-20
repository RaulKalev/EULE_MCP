namespace RevitMCP.Addin.UI.ViewModels;

/// <summary>
/// Lightweight UI display model for one JSONL log entry, shown in the Activity tab DataGrid.
/// </summary>
public class ActivityLogItem
{
    /// <summary>HH:mm:ss timestamp.</summary>
    public string Time { get; init; } = string.Empty;

    /// <summary>Tool name (e.g. revit_group_by_parameter).</summary>
    public string Tool { get; init; } = string.Empty;

    /// <summary>Success / Failed / Info.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Execution time in milliseconds.</summary>
    public long DurationMs { get; init; }

    /// <summary>Result message — shown as row ToolTip.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>True when Status == "Success" — drives green foreground.</summary>
    public bool IsSuccess { get; init; }

    /// <summary>True when Status == "Failed" — drives red foreground.</summary>
    public bool IsError { get; init; }
}

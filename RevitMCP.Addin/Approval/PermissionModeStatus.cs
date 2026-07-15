namespace RevitMCP.Addin.Approval;

/// <summary>
/// Provides the externally reported permission mode without weakening the separate
/// manual-approval requirement for destructive tools.
/// </summary>
public static class PermissionModeStatus
{
    public const bool DestructiveActionsRequireManualApproval = true;

    public static string GetName(bool isDirectEditEnabled)
    {
        return isDirectEditEnabled ? "DirectEdit" : "ApprovalRequired";
    }
}

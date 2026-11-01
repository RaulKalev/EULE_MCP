namespace RevitMCP.Addin.Interfaces;

/// <summary>
/// A tool that reads its targets by id again when it runs and skips those that are gone, so its approval stays
/// meaningful while the user is away (#138): it survives other changes to the model and lives for
/// <see cref="RevitMCP.Addin.Approval.ApprovalService.LongLivedApprovalLifetime"/>. Still bound to its document.
/// </summary>
public interface IApprovalRevalidatesTargets
{
}

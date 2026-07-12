using Autodesk.Revit.DB;

namespace RevitMCP.Addin;

/// <summary>
/// Ensures a Revit transaction actually reached the committed state.
/// Transaction.Commit can return a non-committed status without throwing.
/// </summary>
public static class TransactionCommitGuard
{
    public static void CommitOrThrow(Transaction transaction)
    {
        var status = transaction.Commit();
        if (status != TransactionStatus.Committed)
        {
            throw new InvalidOperationException(
                $"Revit transaction '{transaction.GetName()}' did not commit (status: {status}).");
        }
    }
}

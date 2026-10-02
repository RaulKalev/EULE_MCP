using Autodesk.Revit.DB;
using RevitMCP.Addin.Transactions;

namespace RevitMCP.Addin;

/// <summary>
/// Ensures a Revit transaction actually reached the committed state.
/// Transaction.Commit can return a non-committed status without throwing.
/// </summary>
public static class TransactionCommitGuard
{
    public static void CommitOrThrow(Transaction transaction)
    {
        // An error posted during the transaction would otherwise open Revit's modal failure dialog
        // on the API thread and block the connector; roll back instead and report the message.
        var messages = new List<string>();
        try { RollBackOnErrorPreprocessor.Attach(transaction, messages); }
        catch { /* never block the commit on diagnostics */ }

        var status = transaction.Commit();
        if (status != TransactionStatus.Committed)
        {
            var detail = messages.Count > 0 ? " Revit reported: " + string.Join(" ", messages.Distinct()) : string.Empty;
            throw new InvalidOperationException(
                $"Revit transaction '{transaction.GetName()}' did not commit (status: {status}).{detail}");
        }
    }
}

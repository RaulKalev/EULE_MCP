namespace RevitMCP.Addin.Transactions;

/// <summary>
/// Mirrors Autodesk.Revit.DB.TransactionStatus by name so the orchestration
/// logic (and its tests) carry no Revit API dependency.
/// </summary>
public enum TxStatus
{
    Uninitialized,
    Started,
    RolledBack,
    Committed,
    Pending,
    Error,
    Proceed
}

/// <summary>
/// Minimal seam over a Revit Transaction/SubTransaction so the commit/rollback
/// ordering rules can be unit-tested without Revit.
/// </summary>
public interface ITransactionHandle
{
    string Name { get; }
    TxStatus Start();
    TxStatus GetStatus();
    TxStatus Commit();
    TxStatus RollBack();

    /// <summary>
    /// Failure-processing messages Revit reported during Commit (empty when
    /// none were raised or the handle cannot capture them, e.g. SubTransaction).
    /// </summary>
    IReadOnlyList<string> GetFailureMessages();
}

/// <summary>
/// Everything a caller needs to understand why a model write did or didn't land.
/// The original failure is never replaced by a rollback failure.
/// </summary>
public sealed class TransactionDiagnostics
{
    public string TransactionName { get; init; } = string.Empty;
    /// <summary>"Transaction" or "SubTransaction" (when an outer transaction already owns the document).</summary>
    public string Mode { get; init; } = string.Empty;
    public bool DocumentIsModifiable { get; init; }
    public bool DocumentIsReadOnly { get; init; }
    /// <summary>TxStatus returned by Start(), or "NotAttempted" / "ExceptionOnStart".</summary>
    public string StartStatus { get; set; } = "NotAttempted";
    /// <summary>TxStatus returned by Commit(), when a commit was attempted.</summary>
    public string? CommitStatus { get; set; }
    /// <summary>The real failure — from Start, the body, or a non-committing Commit. Never masked.</summary>
    public string? OriginalError { get; set; }
    /// <summary>A rollback failure, reported separately so it can never hide <see cref="OriginalError"/>.</summary>
    public string? RollbackError { get; set; }
    /// <summary>Messages captured from Revit failure processing during Commit.</summary>
    public List<string> FailureMessages { get; } = new();

    public List<string> ToErrorLines()
    {
        var lines = new List<string>();
        if (OriginalError != null)
            lines.Add($"Original error: {OriginalError}");
        lines.Add($"Transaction '{TransactionName}' [{Mode}]: startStatus={StartStatus}"
                  + (CommitStatus != null ? $", commitStatus={CommitStatus}" : "")
                  + $", doc.IsModifiable={DocumentIsModifiable}, doc.IsReadOnly={DocumentIsReadOnly}");
        foreach (var fm in FailureMessages)
            lines.Add($"Revit failure: {fm}");
        if (RollbackError != null)
            lines.Add($"Rollback error (secondary, did not mask the original error): {RollbackError}");
        return lines;
    }
}

/// <summary>
/// Runs a model-writing body inside a transaction handle with strict state rules:
/// Start() status is validated before the body runs, Commit/RollBack are only
/// invoked while the handle reports Started, and a rollback failure can never
/// mask the original exception.
/// </summary>
public static class SafeTransactionCore
{
    public static (bool Success, TransactionDiagnostics Diagnostics) Run(
        ITransactionHandle handle,
        bool documentIsModifiable,
        bool documentIsReadOnly,
        string mode,
        Action body)
    {
        var diag = new TransactionDiagnostics
        {
            TransactionName = handle.Name,
            Mode = mode,
            DocumentIsModifiable = documentIsModifiable,
            DocumentIsReadOnly = documentIsReadOnly
        };

        TxStatus startStatus;
        try
        {
            startStatus = handle.Start();
        }
        catch (Exception ex)
        {
            diag.StartStatus = "ExceptionOnStart";
            diag.OriginalError = $"{handle.Name}: Start() threw {ex.GetType().Name}: {ex.Message}";
            return (false, diag);
        }

        diag.StartStatus = startStatus.ToString();
        if (startStatus != TxStatus.Started)
        {
            diag.OriginalError =
                $"Transaction '{handle.Name}' could not start (Start() returned {startStatus}). " +
                "No model changes were attempted.";
            // The transaction never started — committing or rolling back here would throw.
            return (false, diag);
        }

        try
        {
            body();

            TxStatus commitStatus;
            try
            {
                commitStatus = handle.Commit();
            }
            catch (Exception ex)
            {
                diag.CommitStatus = "ExceptionOnCommit";
                diag.OriginalError = $"{handle.Name}: Commit() threw {ex.GetType().Name}: {ex.Message}";
                CollectFailureMessages(handle, diag);
                SafeRollBack(handle, diag);
                return (false, diag);
            }

            diag.CommitStatus = commitStatus.ToString();
            CollectFailureMessages(handle, diag);

            if (commitStatus == TxStatus.Committed)
                return (true, diag);

            // Commit returned without committing — typically Revit failure processing
            // already rolled the transaction back. Only roll back ourselves if the
            // handle still reports Started.
            diag.OriginalError =
                $"Transaction '{handle.Name}' did not commit (Commit() returned {commitStatus})."
                + (diag.FailureMessages.Count > 0
                    ? " Revit failure processing reported: " + string.Join(" | ", diag.FailureMessages)
                    : "");
            SafeRollBack(handle, diag);
            return (false, diag);
        }
        catch (Exception ex)
        {
            diag.OriginalError = $"{ex.GetType().Name}: {ex.Message}";
            CollectFailureMessages(handle, diag);
            SafeRollBack(handle, diag);
            return (false, diag);
        }
    }

    private static void SafeRollBack(ITransactionHandle handle, TransactionDiagnostics diag)
    {
        TxStatus current;
        try
        {
            current = handle.GetStatus();
        }
        catch (Exception ex)
        {
            diag.RollbackError = $"GetStatus() threw {ex.GetType().Name}: {ex.Message}";
            return;
        }

        if (current != TxStatus.Started)
            return; // Nothing to roll back — rolling back here is what masked the original error before.

        try
        {
            handle.RollBack();
        }
        catch (Exception ex)
        {
            diag.RollbackError = $"RollBack() threw {ex.GetType().Name}: {ex.Message}";
        }
    }

    private static void CollectFailureMessages(ITransactionHandle handle, TransactionDiagnostics diag)
    {
        try
        {
            foreach (var m in handle.GetFailureMessages())
                if (!diag.FailureMessages.Contains(m))
                    diag.FailureMessages.Add(m);
        }
        catch
        {
            // Failure-message capture is best-effort diagnostics only.
        }
    }
}

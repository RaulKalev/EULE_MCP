using Autodesk.Revit.DB;
using RevitMCP.Addin.Transactions;

namespace RevitMCP.Addin.Families;

/// <summary>
/// One transaction per item inside one transaction group (#102): a family file, a parameter or a delete batch that
/// Revit refuses rolls back alone, and the group is assimilated into a single undo step. Each transaction carries
/// <see cref="RollBackOnErrorPreprocessor"/>, so an error Revit posts on commit is reported instead of opening a modal
/// dialog, and warnings are captured. Needs an idle document (no open transaction).
/// </summary>
internal sealed class ItemTransactions : IDisposable
{
    private readonly Document _doc;
    private readonly TransactionGroup _group;

    private ItemTransactions(Document doc, TransactionGroup group)
    {
        _doc = doc;
        _group = group;
    }

    public int Committed { get; private set; }

    /// <summary>Warnings Revit raised (and the preprocessor dismissed) on the commits that went through.</summary>
    public List<string> Warnings { get; } = new();

    public static ItemTransactions? Start(Document doc, string name, out string? error)
    {
        error = null;
        if (doc.IsReadOnly)
        {
            error = "The document is read-only; nothing was changed.";
            return null;
        }
        if (doc.IsModifiable)
        {
            error = "The document has an open transaction (another edit is in progress); nothing was changed. Try again when Revit is idle.";
            return null;
        }
        var group = new TransactionGroup(doc, name);
        if (group.Start() != TransactionStatus.Started)
        {
            group.Dispose();
            error = "Revit did not start a transaction group; nothing was changed.";
            return null;
        }
        return new ItemTransactions(doc, group);
    }

    /// <summary>
    /// Runs <paramref name="body"/> in its own transaction. The body returns null to commit, or the reason to roll the
    /// item back. Returns null when the item was committed, else why it was not.
    /// </summary>
    public string? Run(string name, Func<string?> body)
    {
        var messages = new List<string>();
        using var transaction = new Transaction(_doc, name);
        try { RollBackOnErrorPreprocessor.Attach(transaction, messages); }
        catch { /* diagnostics only */ }

        if (transaction.Start() != TransactionStatus.Started)
            return "Revit did not start a transaction.";

        string? reason;
        try
        {
            reason = body();
        }
        catch (OperationCanceledException)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            throw;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
        }

        if (reason != null)
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            return reason;
        }

        var status = transaction.Commit();
        if (status != TransactionStatus.Committed)
        {
            var detail = messages.Count > 0 ? ": " + string.Join(" ", messages.Distinct()) : ".";
            return $"Revit rolled the change back (status {status}){detail}";
        }

        Committed++;
        Warnings.AddRange(messages.Distinct());
        return null;
    }

    /// <summary>Keeps what was committed as one undo step, or closes the group untouched when nothing was. Returns an error or null.</summary>
    public string? Finish()
    {
        try
        {
            if (Committed == 0)
            {
                _group.RollBack();
                return null;
            }
            var status = _group.Assimilate();
            return status == TransactionStatus.Committed ? null : $"The transaction group did not commit (status {status}).";
        }
        catch (Exception ex)
        {
            return $"The transaction group could not be closed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        try
        {
            if (_group.GetStatus() == TransactionStatus.Started) _group.RollBack();
        }
        catch { /* already closed */ }
        _group.Dispose();
    }
}

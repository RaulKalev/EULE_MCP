using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Transactions;

/// <summary>
/// Runs a model-writing body under the safe transaction rules of
/// <see cref="SafeTransactionCore"/>, choosing the correct ownership strategy:
/// a regular Transaction when the document is idle, a SubTransaction when an
/// outer transaction already has the document modifiable (starting a nested
/// Transaction there is invalid). Captures Revit failure-processing messages
/// raised during Commit so a rolled-back commit reports its real cause.
/// </summary>
public static class RevitTransactionRunner
{
    public static (bool Success, TransactionDiagnostics Diagnostics) Run(
        Document doc, string name, Action body)
    {
        bool isModifiable, isReadOnly;
        try
        {
            isModifiable = doc.IsModifiable;
            isReadOnly = doc.IsReadOnly;
        }
        catch (Exception ex)
        {
            var diag = new TransactionDiagnostics
            {
                TransactionName = name,
                Mode = "Unknown",
                OriginalError = $"Could not read document state: {ex.GetType().Name}: {ex.Message}"
            };
            return (false, diag);
        }

        if (isReadOnly)
        {
            var diag = new TransactionDiagnostics
            {
                TransactionName = name,
                Mode = "None",
                DocumentIsModifiable = isModifiable,
                DocumentIsReadOnly = true,
                OriginalError = "The document is read-only — no transaction can be started. " +
                                "No model changes were attempted."
            };
            return (false, diag);
        }

        if (isModifiable)
        {
            // An outer transaction owns the document. A nested Transaction cannot be
            // started; a SubTransaction is the supported ownership strategy here.
            using var sub = new SubTransaction(doc);
            var handle = new SubTransactionHandle(sub, name);
            return SafeTransactionCore.Run(handle, true, false, "SubTransaction", body);
        }

        using var trans = new Transaction(doc, name);
        var failureMessages = new List<string>();
        try
        {
            var options = trans.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new FailureCapturePreprocessor(failureMessages));
            options.SetClearAfterRollback(true);
            trans.SetFailureHandlingOptions(options);
        }
        catch
        {
            // Failure capture is diagnostics only — never block the write on it.
        }
        var transHandle = new TransactionHandle(trans, failureMessages);
        return SafeTransactionCore.Run(transHandle, false, false, "Transaction", body);
    }

    private sealed class TransactionHandle : ITransactionHandle
    {
        private readonly Transaction _trans;
        private readonly List<string> _failureMessages;

        public TransactionHandle(Transaction trans, List<string> failureMessages)
        {
            _trans = trans;
            _failureMessages = failureMessages;
        }

        public string Name => _trans.GetName();
        public TxStatus Start() => Map(_trans.Start());
        public TxStatus GetStatus() => Map(_trans.GetStatus());
        public TxStatus Commit() => Map(_trans.Commit());
        public TxStatus RollBack() => Map(_trans.RollBack());
        public IReadOnlyList<string> GetFailureMessages() => _failureMessages;
    }

    /// <summary>
    /// SubTransaction exposes no GetStatus(), so the handle tracks the state it
    /// drove the sub-transaction through. Failure processing runs on the outer
    /// transaction's commit, not here, so no messages are captured.
    /// </summary>
    private sealed class SubTransactionHandle : ITransactionHandle
    {
        private readonly SubTransaction _sub;
        private TxStatus _status = TxStatus.Uninitialized;

        public SubTransactionHandle(SubTransaction sub, string name)
        {
            _sub = sub;
            Name = name;
        }

        public string Name { get; }

        public TxStatus Start()
        {
            _status = Map(_sub.Start());
            return _status;
        }

        public TxStatus GetStatus() => _status;

        public TxStatus Commit()
        {
            _status = Map(_sub.Commit());
            return _status;
        }

        public TxStatus RollBack()
        {
            _status = Map(_sub.RollBack());
            return _status;
        }

        public IReadOnlyList<string> GetFailureMessages() => Array.Empty<string>();
    }

    private static TxStatus Map(TransactionStatus status) =>
        Enum.TryParse<TxStatus>(status.ToString(), out var mapped) ? mapped : TxStatus.Error;

    /// <summary>
    /// Records every failure message Revit raises during commit and deletes
    /// plain warnings so they don't block the write. Errors are left for Revit
    /// to resolve or roll back — the recorded text explains what happened.
    /// </summary>
    private sealed class FailureCapturePreprocessor : IFailuresPreprocessor
    {
        private readonly List<string> _messages;

        public FailureCapturePreprocessor(List<string> messages) => _messages = messages;

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            try
            {
                foreach (var failure in failuresAccessor.GetFailureMessages())
                {
                    var severity = failure.GetSeverity();
                    string text;
                    try { text = failure.GetDescriptionText(); }
                    catch { text = failure.GetFailureDefinitionId()?.Guid.ToString() ?? "(unknown failure)"; }
                    _messages.Add($"[{severity}] {text}");

                    if (severity == FailureSeverity.Warning)
                        failuresAccessor.DeleteWarning(failure);
                }
            }
            catch
            {
                // Diagnostics only — never fail the commit from the preprocessor.
            }
            return FailureProcessingResult.Continue;
        }
    }
}

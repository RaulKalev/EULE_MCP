using Autodesk.Revit.DB;

namespace RevitMCP.Addin.Transactions;

/// <summary>
/// Failure preprocessor for connector transactions. Records every failure message Revit raises
/// during commit, deletes plain warnings, and rolls the transaction back when an error is posted.
/// Without it an unresolved error (e.g. "Sheet Number is already in use") opens Revit's modal
/// failure dialog on the API thread, and every MCP request then times out until someone clicks it.
/// </summary>
public sealed class RollBackOnErrorPreprocessor : IFailuresPreprocessor
{
    private readonly List<string> _messages;

    public RollBackOnErrorPreprocessor(List<string> messages) => _messages = messages;

    public bool RolledBack { get; private set; }

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        var hasError = false;
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
                else
                    hasError = true;
            }
        }
        catch
        {
            // Diagnostics only — the rollback decision below still applies.
        }

        if (!hasError) return FailureProcessingResult.Continue;
        RolledBack = true;
        return FailureProcessingResult.ProceedWithRollBack;
    }

    /// <summary>Installs the preprocessor on a transaction that has not been committed yet.</summary>
    public static RollBackOnErrorPreprocessor Attach(Transaction transaction, List<string> messages)
    {
        var preprocessor = new RollBackOnErrorPreprocessor(messages);
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(preprocessor);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
        return preprocessor;
    }
}

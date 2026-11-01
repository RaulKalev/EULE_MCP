using System.Diagnostics;
using Autodesk.Revit.DB;
using RevitMCP.Addin.Transactions;
using RevitMCP.Addin.ViewFilters;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ViewFilters;

/// <summary>One thing a view filter write does: everything wrong with it is known before a transaction opens.</summary>
internal abstract class ViewFilterWorkItem
{
    public List<ViewFilterIssue> Issues { get; } = new();

    /// <summary>Left alone on purpose, with this code and reason. Not a failure.</summary>
    public string? SkipCode { get; set; }
    public string? SkipReason { get; set; }

    public bool Attempted { get; set; }
    public bool Done { get; set; }
    public string? Error { get; set; }
    public bool RolledBack { get; set; }
    public List<string> Notes { get; } = new();

    public bool IsInvalid => Issues.Count > 0;
    public bool IsSkipped => !IsInvalid && SkipCode != null;
    public bool IsReady => !IsInvalid && SkipCode == null;
    public bool IsFailed => Attempted && Error != null;

    public string? Code => Issues.FirstOrDefault()?.Code ?? SkipCode ?? (IsFailed ? ViewFilterCodes.RevitRefused : null);
    public string? Reason => Issues.Count > 0 ? string.Join(" ", Issues.Select(i => i.Reason)) : SkipReason ?? Error;

    public string Status(string verb, bool applied)
    {
        if (IsInvalid) return "invalid";
        if (IsSkipped) return "skipped";
        if (IsFailed) return "failed";
        if (!applied) return "will" + Capitalize(verb);
        if (RolledBack) return "rolledBack";
        return Done ? Past(verb) : "notAttempted";
    }

    private static string Capitalize(string verb) => char.ToUpperInvariant(verb[0]) + verb.Substring(1);
    private static string Past(string verb) => verb.EndsWith("e", StringComparison.Ordinal) ? verb + "d"
        : verb.EndsWith("y", StringComparison.Ordinal) ? verb.Substring(0, verb.Length - 1) + "ied"
        : verb + "ed";
}

/// <summary>
/// The shape every view filter write shares (#134), after revit_create_views: the preview tries the same writes in a
/// transaction it rolls back, so Revit's refusals show up too; the real run has one transaction (one Undo) and a
/// sub-transaction per item; atomic=true rejects the request on any invalid item and rolls back on any refusal.
/// </summary>
internal static class ViewFilterBatch
{
    public static McpToolResult Fail(McpToolRequest request, string message, string status) =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };

    public static McpToolResult Run<T>(
        McpToolRequest request, Document doc, string transactionName, string verb, bool apply, bool atomic, string itemsKey,
        List<T> items, Action<T> perform, Func<T, bool, object> describe, Dictionary<string, object?>? extra, Stopwatch sw,
        CancellationToken cancellationToken) where T : ViewFilterWorkItem
    {
        return apply
            ? RunReal(request, doc, transactionName, verb, atomic, itemsKey, items, perform, describe, extra, sw, cancellationToken)
            : RunPreview(request, doc, transactionName, verb, atomic, itemsKey, items, perform, describe, extra, sw, cancellationToken);
    }

    private static McpToolResult RunPreview<T>(
        McpToolRequest request, Document doc, string transactionName, string verb, bool atomic, string itemsKey,
        List<T> items, Action<T> perform, Func<T, bool, object> describe, Dictionary<string, object?>? extra, Stopwatch sw,
        CancellationToken cancellationToken) where T : ViewFilterWorkItem
    {
        string? trialNote = null;
        var trialRan = false;
        var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", true);

        if (!dryRun) trialNote = "dryRun=false: only the static checks ran; Revit was not asked to try the writes.";
        else if (!items.Any(i => i.IsReady)) trialNote = "No trial run: nothing is ready to be written.";
        else if (doc.IsReadOnly || doc.IsModifiable) trialNote = "No trial run: the document is read-only or has an open transaction. Only the static checks ran.";
        else
        {
            using var transaction = new Transaction(doc, transactionName + " (preview, rolled back)");
            try
            {
                RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
                if (transaction.Start() == TransactionStatus.Started)
                {
                    trialRan = true;
                    RunItems(doc, items, perform, atomic: false, cancellationToken);
                }
                else trialNote = "No trial run: Revit did not start a transaction. Only the static checks ran.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                trialNote = $"The trial run could not complete ({ex.GetType().Name}: {ex.Message}). Only the static checks are reliable.";
            }
            finally
            {
                if (transaction.GetStatus() == TransactionStatus.Started)
                    transaction.RollBack();
            }
            trialNote ??= "Trial run rolled back: the model is unchanged.";
        }

        var invalid = items.Count(i => i.IsInvalid);
        var skipped = items.Count(i => i.IsSkipped);
        var failed = items.Count(i => i.IsFailed);
        var ready = items.Count(i => i.IsReady && !i.IsFailed);
        var rejected = atomic && (invalid > 0 || failed > 0);

        var data = new Dictionary<string, object?>
        {
            ["total"] = items.Count,
            ["ready"] = ready,
            ["invalid"] = invalid,
            ["skipped"] = skipped,
            ["trialFailed"] = failed,
            ["atomic"] = atomic,
            ["trialRun"] = trialRan,
            ["trialNote"] = trialNote
        };
        Merge(data, extra);
        data[itemsKey] = items.Select(i => describe(i, false)).ToList();

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Status = ready == 0 ? FirstCode(items) : null,
            Message = $"Preview: {ready} of {items.Count} would be written" +
                      (invalid > 0 ? $", {invalid} invalid" : string.Empty) +
                      (skipped > 0 ? $", {skipped} skipped" : string.Empty) +
                      (failed > 0 ? $", {failed} refused by Revit in the trial run" : string.Empty) + "." +
                      (rejected ? " With atomic=true the request would be rejected as a whole." : string.Empty),
            Data = data,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult RunReal<T>(
        McpToolRequest request, Document doc, string transactionName, string verb, bool atomic, string itemsKey,
        List<T> items, Action<T> perform, Func<T, bool, object> describe, Dictionary<string, object?>? extra, Stopwatch sw,
        CancellationToken cancellationToken) where T : ViewFilterWorkItem
    {
        var warnings = new List<string>();
        var invalid = items.Count(i => i.IsInvalid);

        if (atomic && invalid > 0)
            return Result(request, verb, atomic, itemsKey, items, describe, extra, warnings, sw, false, FirstCode(items),
                $"Nothing was written: atomic=true and {invalid} of {items.Count} item(s) are invalid. See each item's code and reason, or pass atomic=false to write the rest.", null);

        if (!items.Any(i => i.IsReady))
        {
            var allSkipped = invalid == 0;
            return Result(request, verb, atomic, itemsKey, items, describe, extra, warnings, sw, allSkipped, allSkipped ? null : FirstCode(items),
                allSkipped ? "Nothing to write: every item is already as asked." : "Nothing was written: no item is valid.", null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (success, diagnostics) = RevitTransactionRunner.Run(doc, transactionName, () => RunItems(doc, items, perform, atomic, cancellationToken));

        if (!success)
        {
            foreach (var item in items.Where(i => i.Done))
            {
                item.Done = false;
                item.RolledBack = true;
            }
            warnings.AddRange(diagnostics.ToErrorLines());
            return Result(request, verb, atomic, itemsKey, items, describe, extra, warnings, sw, false, ViewFilterCodes.RevitRefused,
                atomic
                    ? "Rolled back: atomic=true and Revit refused an item, so nothing was written. The model is unchanged."
                    : "The transaction did not commit, so nothing was written. The model is unchanged.", diagnostics);
        }

        warnings.AddRange(diagnostics.FailureMessages);
        var done = items.Count(i => i.Done);
        var failed = items.Count(i => i.IsFailed);
        var skipped = items.Count(i => i.IsSkipped);
        return Result(request, verb, atomic, itemsKey, items, describe, extra, warnings, sw, done > 0, done > 0 ? null : FirstCode(items),
            $"Done: {done} of {items.Count}" +
            (failed > 0 ? $"; Revit refused {failed}" : string.Empty) +
            (invalid > 0 ? $"; {invalid} invalid" : string.Empty) +
            (skipped > 0 ? $"; {skipped} skipped" : string.Empty) + ". One Undo reverts it.", diagnostics);
    }

    private static void RunItems<T>(Document doc, List<T> items, Action<T> perform, bool atomic, CancellationToken cancellationToken) where T : ViewFilterWorkItem
    {
        foreach (var item in items.Where(i => i.IsReady))
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.Attempted = true;
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                perform(item);
                sub.Commit();
                item.Done = true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                item.Error = ex.Message;
                if (atomic) throw new InvalidOperationException($"atomic=true: Revit refused an item ({ex.Message}), so the whole batch was undone.");
            }
        }
    }

    private static McpToolResult Result<T>(
        McpToolRequest request, string verb, bool atomic, string itemsKey, List<T> items, Func<T, bool, object> describe,
        Dictionary<string, object?>? extra, List<string> warnings, Stopwatch sw, bool success, string? status, string message,
        TransactionDiagnostics? diagnostics) where T : ViewFilterWorkItem
    {
        var data = new Dictionary<string, object?>
        {
            ["total"] = items.Count,
            ["done"] = items.Count(i => i.Done),
            ["failed"] = items.Count(i => i.IsFailed),
            ["invalid"] = items.Count(i => i.IsInvalid),
            ["skipped"] = items.Count(i => i.IsSkipped),
            ["atomic"] = atomic
        };
        Merge(data, extra);
        data[itemsKey] = items.Select(i => describe(i, true)).ToList();
        data["transaction"] = diagnostics;

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = success,
            Status = status,
            Message = message,
            Data = data,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static void Merge(Dictionary<string, object?> data, Dictionary<string, object?>? extra)
    {
        if (extra == null) return;
        foreach (var pair in extra) data[pair.Key] = pair.Value;
    }

    private static string? FirstCode<T>(IEnumerable<T> items) where T : ViewFilterWorkItem
        => items.Where(i => i.IsInvalid || i.IsFailed).Select(i => i.Code).FirstOrDefault(c => c != null)
           ?? items.Select(i => i.Code).FirstOrDefault(c => c != null);
}

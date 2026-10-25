using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Families;

/// <summary>revit_preview_purge_unused (#102): what a purge would remove, from a trial run that is rolled back.</summary>
public sealed class PreviewPurgeUnusedTool : IRevitMcpTool
{
    public string Name => "revit_preview_purge_unused";

    public string Description =>
        "Previews purging unused family types and families, without changing the model. Same arguments as " +
        "revit_purge_unused: categories, familyNameContains, passes (1-3), includeOther. Lists the family types with no " +
        "placed instance that Revit's own purge also reports unused, the families whose every type is unused, other unused " +
        "elements with includeOther=true (line and fill patterns, materials, text and dimension types, system types), and " +
        "what is protected and why: in-place families, company device map types, title blocks on sheets, view templates, " +
        "types Revit still uses, the last type of a family that stays. With passes > 1 the passes run in a trial " +
        "transaction that is rolled back. Returns elementIds to pass to revit_purge_unused.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PurgeExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_purge_unused (#102): DESTRUCTIVE — deletes unused family types, families and (opt-in) other unused elements.</summary>
public sealed class PurgeUnusedTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_purge_unused";

    public string Description =>
        "DESTRUCTIVE: purges unused family types and families, and with includeOther=true other unused elements. Always " +
        "requires manual approval. Options: categories, familyNameContains, passes (1-3, default 1: purging a type can " +
        "orphan a nested family that only the next pass finds), includeOther, elementIds (limit the purge to ids the " +
        "preview listed; anything else is never deleted). Never purges in-place families, company device map types, " +
        "title blocks on sheets, view templates, types Revit still uses or the last type of a family that stays. Deletes " +
        "in batches of 500, one undo step. Returns deleted, removedAsDependent, totalRemoved, failed and failures[], like " +
        "revit_delete. Run revit_preview_purge_unused first.";

    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.FamilyCreation;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PurgeExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var options = PurgeExecutor.Options(request);
        var plan = PurgeExecutor.PlanPass(target.Document, options, PurgeExecutor.Restriction(request), out _);
        var summary = PurgePlanner.Summary(plan);
        return options.Passes > 1 ? summary.Replace(". Manual approval required.", $", up to {options.Passes} passes. Manual approval required.") : summary;
    }
}

internal static class PurgeExecutor
{
    public static PurgeOptions Options(McpToolRequest request) => new()
    {
        Categories = ToolArguments.GetStringArray(request.Arguments, "categories").Select(c => c.Trim()).Where(c => c.Length > 0).ToList(),
        FamilyNameContains = ToolArguments.GetString(request.Arguments, "familyNameContains").Trim(),
        Passes = PurgePlanner.NormalizePasses(ToolArguments.GetInt(request.Arguments, "passes", 1)),
        IncludeOther = ToolArguments.GetBool(request.Arguments, "includeOther")
    };

    /// <summary>elementIds of revit_purge_unused: only these may be deleted. Null = no restriction.</summary>
    public static HashSet<long>? Restriction(McpToolRequest request)
    {
        var ids = ToolArguments.GetLongArray(request.Arguments, "elementIds");
        return ids.Length == 0 ? null : new HashSet<long>(ids);
    }

    /// <summary>One pass: snapshot, plan, and the elementIds restriction applied.</summary>
    public static PurgePlan PlanPass(Document doc, PurgeOptions options, HashSet<long>? only, out PurgeSnapshot snapshot)
    {
        snapshot = FamilyInventory.Snapshot(doc, options.IncludeOther);
        var plan = PurgePlanner.Plan(snapshot.Types, snapshot.Families, snapshot.Others, options);
        if (only == null) return plan;

        var restricted = new PurgePlan();
        var keptFamilies = plan.Families.Where(f => only.Contains(f.Id)).ToList();
        var keptFamilyIds = new HashSet<long>(keptFamilies.Select(f => f.Id));
        restricted.Families.AddRange(keptFamilies);
        // A family that is not deleted keeps its types unless they were named one by one.
        restricted.Types.AddRange(plan.Types.Where(t => keptFamilyIds.Contains(t.FamilyId) || only.Contains(t.Id)));
        restricted.Other.AddRange(plan.Other.Where(o => only.Contains(o.Id)));
        restricted.Protected.AddRange(plan.Protected);
        restricted.Notes.AddRange(plan.Notes);
        return restricted;
    }

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var options = Options(request);
        var only = apply ? Restriction(request) : null;
        var warnings = new List<string>();

        if (options.Categories.Count > 0)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Category category in doc.Settings.Categories) known.Add(category.Name);
            var unknown = options.Categories.Where(c => !known.Contains(c)).ToList();
            if (unknown.Count > 0)
                return Fail(request, $"Unknown categor{(unknown.Count == 1 ? "y" : "ies")}: {string.Join(", ", unknown)}. Use the category names Revit shows (e.g. 'Electrical Fixtures').");
        }

        if (apply && only != null && options.Passes > 1)
            warnings.Add("elementIds limits every pass to the listed ids; a later pass removes only listed ids it finds unused.");

        var passes = new List<PassOutcome>();
        PurgeSnapshot? firstSnapshot = null;
        string? transactionNote = null;

        if (!apply && options.Passes == 1)
        {
            // One pass needs no trial run: nothing is deleted, so nothing has to be rolled back.
            var plan = PlanPass(doc, options, null, out var snapshot);
            firstSnapshot = snapshot;
            passes.Add(new PassOutcome { Plan = plan });
        }
        else if (!apply)
        {
            transactionNote = TrialRun(doc, options, passes, ref firstSnapshot, cancellationToken);
        }
        else
        {
            using var transactions = ItemTransactions.Start(doc, "Revit MCP - Purge Unused", out var startError);
            if (transactions == null) return Fail(request, startError!);
            var batch = 0;
            PurgePlanner.RunPasses(options.Passes,
                _ =>
                {
                    var plan = PlanPass(doc, options, only, out var snapshot);
                    firstSnapshot ??= snapshot;
                    passes.Add(new PassOutcome { Plan = plan });
                    return plan;
                },
                plan =>
                {
                    var outcome = passes[passes.Count - 1];
                    foreach (var ids in PurgePlanner.Batches(plan.DeletionIds()))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        batch++;
                        DeleteBatch(doc, ids, outcome, (name, body) => transactions.Run($"{name} {batch}", body));
                    }
                    return outcome.Deleted;
                });
            var groupError = transactions.Finish();
            warnings.AddRange(transactions.Warnings.Distinct().Select(w => "Revit: " + w));
            if (groupError != null)
            {
                sw.Stop();
                return new McpToolResult
                {
                    RequestId = request.RequestId,
                    Success = false,
                    Status = "transaction_failed",
                    Message = $"Nothing was purged: {groupError}",
                    Warnings = warnings,
                    DurationMs = sw.ElapsedMilliseconds
                };
            }

            var purgedFamilies = passes.SelectMany(p => p.Plan.Families).Select(f => f.Name).ToList();
            if (purgedFamilies.Count > 0)
            {
                try { FamilyPathStore.Default().Forget(CheckpointChangeTracker.ModelIdOf(doc), purgedFamilies); }
                catch { /* the store only holds hints */ }
            }
        }

        if (passes.Count == 0) passes.Add(new PassOutcome());
        if (firstSnapshot is { RevitPurgeAvailable: false })
            warnings.Add(firstSnapshot.RevitPurgeProblem ?? "Revit's own unused list was not available.");

        sw.Stop();
        return apply ? ApplyResult(request, doc, options, only, passes, warnings, sw) : PreviewResult(request, doc, options, passes, firstSnapshot, transactionNote, warnings, sw);
    }

    /// <summary>Runs every pass in one transaction that is always rolled back, so later passes see what earlier ones removed.</summary>
    private static string TrialRun(Document doc, PurgeOptions options, List<PassOutcome> passes, ref PurgeSnapshot? firstSnapshot, CancellationToken cancellationToken)
    {
        if (doc.IsReadOnly || doc.IsModifiable)
        {
            var plan = PlanPass(doc, options, null, out var snapshot);
            firstSnapshot = snapshot;
            passes.Add(new PassOutcome { Plan = plan });
            return "Only the first pass was planned: the document is read-only or has an open transaction, so no trial run was possible.";
        }

        PurgeSnapshot? first = null;
        using var transaction = new Transaction(doc, "Revit MCP - Purge Unused (preview, rolled back)");
        try
        {
            RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
            if (transaction.Start() != TransactionStatus.Started)
                return "No trial run: Revit did not start a transaction.";
            PurgePlanner.RunPasses(options.Passes,
                _ =>
                {
                    var plan = PlanPass(doc, options, null, out var snapshot);
                    first ??= snapshot;
                    passes.Add(new PassOutcome { Plan = plan });
                    return plan;
                },
                plan =>
                {
                    var outcome = passes[passes.Count - 1];
                    foreach (var ids in PurgePlanner.Batches(plan.DeletionIds()))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        DeleteBatch(doc, ids, outcome, (_, body) => InSubTransaction(doc, body));
                    }
                    return outcome.Deleted;
                });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"The trial run stopped early ({ex.GetType().Name}: {ex.Message}); the passes listed are what it reached.";
        }
        finally
        {
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            firstSnapshot = first;
        }
        return "Trial run rolled back: the model is unchanged.";
    }

    private static string? InSubTransaction(Document doc, Func<string?> body)
    {
        using var sub = new SubTransaction(doc);
        sub.Start();
        string? reason;
        try { reason = body(); }
        catch (Exception ex) { reason = ex.Message; }
        if (reason == null) sub.Commit();
        else sub.RollBack();
        return reason;
    }

    /// <summary>
    /// Deletes one batch. When Revit refuses the batch as a whole, the elements are deleted one by one so a single
    /// refusal does not keep the rest; what still fails is reported per element.
    /// </summary>
    private static void DeleteBatch(Document doc, List<long> ids, PassOutcome outcome, Func<string, Func<string?>, string?> run)
    {
        var present = new List<long>();
        foreach (var id in ids)
        {
            if (doc.GetElement(new ElementId(id)) == null) outcome.RemovedAsDependent++;
            else present.Add(id);
        }
        if (present.Count == 0) return;

        var removedTotal = 0;
        var batchError = run("Revit MCP - Purge batch", () =>
        {
            removedTotal = doc.Delete(present.Select(id => new ElementId(id)).ToList()).Count;
            return null;
        });
        if (batchError == null)
        {
            outcome.Deleted += present.Count;
            outcome.TotalRemoved += removedTotal;
            return;
        }

        foreach (var id in present)
        {
            if (doc.GetElement(new ElementId(id)) == null)
            {
                outcome.RemovedAsDependent++;
                continue;
            }
            var removed = 0;
            var error = run("Revit MCP - Purge element", () =>
            {
                removed = doc.Delete(new ElementId(id)).Count;
                return null;
            });
            if (error == null)
            {
                outcome.Deleted++;
                outcome.TotalRemoved += removed;
            }
            else
            {
                outcome.Failures.Add(new { elementId = id, name = NameOf(doc, id), error });
            }
        }
    }

    private static McpToolResult PreviewResult(McpToolRequest request, Document doc, PurgeOptions options, List<PassOutcome> passes,
        PurgeSnapshot? snapshot, string? transactionNote, List<string> warnings, Stopwatch sw)
    {
        var plans = passes.Select(p => p.Plan).ToList();
        var types = plans.Sum(p => p.Types.Count);
        var families = plans.Sum(p => p.Families.Count);
        var other = plans.Sum(p => p.Other.Count);
        var familiesTouched = plans.SelectMany(p => p.Types.Select(t => t.FamilyId)).Distinct().Count();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {types} unused family type(s) in {familiesTouched} famil{(familiesTouched == 1 ? "y" : "ies")} " +
                      $"({families} whole famil{(families == 1 ? "y" : "ies")})" +
                      $"{(options.IncludeOther ? $", {other} other unused element(s)" : string.Empty)} would be purged over {passes.Count} pass(es); " +
                      $"{plans[0].Protected.Count} protected.",
            Data = new
            {
                documentTitle = doc.Title,
                method = snapshot?.RevitPurgeAvailable == true
                    ? "instance counts, confirmed by Revit's own unused list (Document.GetUnusedElements)"
                    : "instance counts only (Document.GetUnusedElements was not available)",
                passesRequested = options.Passes,
                trial = transactionNote,
                approvalSummary = PurgePlanner.Summary(types, familiesTouched, other),
                elementIds = plans.SelectMany(p => p.DeletionIds()).Distinct().ToList(),
                passes = passes.Select((p, i) => new
                {
                    pass = i + 1,
                    types = p.Plan.Types.Select(Describe).ToList(),
                    families = p.Plan.Families.Select(Describe).ToList(),
                    other = p.Plan.Other.Select(Describe).ToList()
                }).ToList(),
                @protected = plans[0].Protected.Select(Describe).ToList(),
                notes = plans.SelectMany(p => p.Notes).Distinct().ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static McpToolResult ApplyResult(McpToolRequest request, Document doc, PurgeOptions options, HashSet<long>? only,
        List<PassOutcome> passes, List<string> warnings, Stopwatch sw)
    {
        var deleted = passes.Sum(p => p.Deleted);
        var removedAsDependent = passes.Sum(p => p.RemovedAsDependent);
        var totalRemoved = passes.Sum(p => p.TotalRemoved);
        var failures = passes.SelectMany(p => p.Failures).ToList();
        var planned = passes.Sum(p => p.Plan.DeletionIds().Count);
        if (only != null)
        {
            var listed = new HashSet<long>(passes.SelectMany(p => p.Plan.DeletionIds()));
            var refused = only.Where(id => !listed.Contains(id)).ToList();
            if (refused.Count > 0)
                warnings.Add($"{refused.Count} of the elementIds are not (or no longer) purgeable and were left alone: {string.Join(", ", refused.Take(20))}{(refused.Count > 20 ? ", …" : string.Empty)}.");
        }
        foreach (var failure in failures) warnings.Add($"Could not purge: {Newtonsoft.Json.JsonConvert.SerializeObject(failure)}");

        var gone = deleted + removedAsDependent;
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = gone > 0 || planned == 0,
            Message = planned == 0
                ? "Nothing to purge: no unused family types match."
                : $"Purged {gone} of {planned} element(s) over {passes.Count} pass(es) ({totalRemoved} removed in total including dependents), failed {failures.Count}.",
            Data = new
            {
                documentTitle = doc.Title,
                deleted,
                removedAsDependent,
                totalRemoved,
                failed = failures.Count,
                failures,
                passes = passes.Select((p, i) => new
                {
                    pass = i + 1,
                    types = p.Plan.Types.Count,
                    families = p.Plan.Families.Select(f => f.Name).ToList(),
                    other = p.Plan.Other.Count,
                    deleted = p.Deleted
                }).ToList(),
                @protected = passes.Count > 0 ? passes[0].Plan.Protected.Count : 0,
                undo = "One undo step in Revit: 'Revit MCP - Purge Unused'."
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(PurgeCandidate candidate) => new
    {
        id = candidate.Id,
        kind = candidate.Kind,
        name = candidate.Name,
        family = candidate.Kind == "other" ? null : candidate.FamilyName,
        elementClass = candidate.Kind == "other" ? candidate.FamilyName : null,
        category = candidate.Category,
        reason = candidate.Reason
    };

    private static string NameOf(Document doc, long id)
    {
        try { return doc.GetElement(new ElementId(id))?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = message };

    private sealed class PassOutcome
    {
        public PurgePlan Plan { get; set; } = new();
        public int Deleted { get; set; }
        public int RemovedAsDependent { get; set; }
        public int TotalRemoved { get; set; }
        public List<object> Failures { get; } = new();
    }
}

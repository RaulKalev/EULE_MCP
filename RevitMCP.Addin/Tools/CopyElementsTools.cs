using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>revit_preview_copy_elements (#91): what a copy would do, including a rolled-back trial run.</summary>
public class PreviewCopyElementsTool : IRevitMcpTool
{
    public string Name => "revit_preview_copy_elements";

    public string Description =>
        "Previews copying elements, without changing the model. Same arguments as revit_copy_elements. " +
        "Works for every element Revit can copy — family instances, Detail Items, detail and model lines, text " +
        "notes, tags, dimensions, walls, pipes and other curve-based elements, hosted and face-based instances. " +
        "Required: elementIds or useSelection=true. Translation in mm: deltaXmm/deltaYmm/deltaZmm (model axes) or " +
        "deltaRightMm/deltaUpMm (the view's right and up — for view-specific elements, their owner view). " +
        "Optional: targetViewId (copy view-specific elements into another compatible view), sourceViewId, atomic. " +
        "Returns per element whether it can be copied and why not, the Revit operation that would be used, and — " +
        "from a trial run that is rolled back (dryRun, default true) — how many elements Revit would create, " +
        "including dependents. Views, sheets and family types are pointed to revit_duplicate.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CopyElementsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_copy_elements (#91): copies elements with Revit's own copy operations.</summary>
public class CopyElementsTool : IRevitMcpTool
{
    public string Name => "revit_copy_elements";

    public string Description =>
        "Copies elements by a translation. Requires approval. Works for every element Revit can copy — family " +
        "instances, Detail Items, detail and model lines, text notes, tags, dimensions, walls, pipes and other " +
        "curve-based elements, hosted and face-based instances; there is no category whitelist and no LocationPoint " +
        "requirement. Required: elementIds or useSelection=true. Translation in mm: deltaXmm/deltaYmm/deltaZmm along " +
        "the model axes, or deltaRightMm/deltaUpMm along a view's right and up (view-specific elements use their " +
        "owner view; model elements need sourceViewId). View-specific elements are copied within their owner view, " +
        "or into targetViewId when given (both views must be plans, sections, elevations or drafting views; the " +
        "translation must lie in the destination view's plane). Model elements are copied in the model, or into " +
        "targetViewId's level/plane when sourceViewId is given. Originals are untouched. Returns the source-to-copy " +
        "id pairs, every new element id including dependents Revit copied along, and a result per element. " +
        "atomic=true (default): each set is copied together (relationships between copies are kept) and any refusal " +
        "undoes everything; atomic=false: element by element, keeping what succeeds. One transaction, one undo. " +
        "Views, sheets and family types are not copied here — use revit_duplicate. Run revit_preview_copy_elements first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CopyElementsExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class CopyElementsExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc == null)
            return Fail(request, "No active document.");

        var copyRequest = CopyElementsMath.Parse(request.Arguments, out var error);
        if (copyRequest == null)
            return Fail(request, error!);

        var elementIds = copyRequest.UseSelection
            ? uidoc!.Selection.GetElementIds().Select(id => id.Value).ToList()
            : copyRequest.ElementIds;
        if (elementIds.Count == 0)
            return Fail(request, "The selection is empty — there is nothing to copy.");
        if (elementIds.Count > CopyElementsMath.MaxElements)
            return Fail(request, $"The selection holds {elementIds.Count} elements; the limit is {CopyElementsMath.MaxElements} per request.");

        var warnings = new List<string>();
        var (items, batches) = CopyElementsService.Plan(doc, copyRequest, elementIds);
        var blocked = items.Count(item => item.IsFailure);

        foreach (var batch in batches.Where(batch => batch.Error == null && batch.TranslationMm is { Length: < MoveElementsMath.NegligibleMoveMm } && !batch.CrossView))
        {
            warnings.Add("The translation is zero: the copies land exactly on their originals. Revit allows it and warns " +
                         "about identical instances in the same place.");
            break;
        }

        return apply
            ? Copy(request, doc, copyRequest, items, batches, blocked, warnings, sw, cancellationToken)
            : Preview(request, doc, copyRequest, items, batches, blocked, warnings, sw, cancellationToken);
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private static McpToolResult Preview(
        McpToolRequest request, Document doc, CopyRequest copyRequest, List<CopyItem> items, List<CopyBatch> batches,
        int blocked, List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", true);
        var wouldBeRejected = CopyElementsMath.ShouldRollBack(copyRequest.Atomic, blocked);
        CopyRunResult? trial = null;
        string? trialNote = null;

        if (dryRun && items.Any(item => item.CanCopy) && !wouldBeRejected)
            trial = TrialRun(doc, copyRequest, items, batches, warnings, cancellationToken, out trialNote);
        else if (!dryRun)
            trialNote = "dryRun=false: only the static checks ran, Revit was not asked to try the copy.";
        else if (wouldBeRejected)
            trialNote = "No trial run: atomic=true and some elements cannot be copied, so the request would be rejected as a whole.";

        var failures = items.Count(item => item.IsFailure);
        var ready = items.Count(item => item.Status is CopyStatus.Ready or CopyStatus.Copied);

        // The trial copies were rolled back, so their ids mean nothing afterwards: report what they
        // were, not which ids they briefly had.
        foreach (var item in items.Where(item => item.Status == CopyStatus.Copied))
            item.Status = CopyStatus.Ready;

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Preview: {ready} of {items.Count} element(s) would be copied" +
                      $"{(failures > 0 ? $", {failures} cannot" : string.Empty)}" +
                      $"{(trial != null ? $"; Revit would create {trial.Created.Count} element(s)" : string.Empty)}." +
                      (failures > 0 && copyRequest.Atomic ? " With atomic=true the whole request would be rejected." : string.Empty),
            Data = new
            {
                total = items.Count,
                canCopy = ready,
                blocked = failures,
                atomic = copyRequest.Atomic,
                trialRun = trial != null,
                trialNote,
                wouldCreate = trial == null ? null : new
                {
                    elements = trial.Created.Count,
                    dependents = trial.Dependencies.Count,
                    byCategory = CountByCategory(trial.Created),
                    dependentsByCategory = CountByCategory(trial.Dependencies)
                },
                batches = batches.Select(DescribeBatch).ToList(),
                elements = items.Select(item => DescribeItem(item, includeCopyIds: false)).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Asks Revit to actually perform the copy inside a transaction that is always rolled back. This
    /// is the only way to know what Revit will accept and how many dependents it brings along; the
    /// model is left exactly as it was and nothing reaches the undo stack.
    /// </summary>
    private static CopyRunResult? TrialRun(
        Document doc, CopyRequest copyRequest, List<CopyItem> items, List<CopyBatch> batches,
        List<string> warnings, CancellationToken cancellationToken, out string? note)
    {
        note = null;
        if (doc.IsReadOnly || doc.IsModifiable)
        {
            note = "No trial run: the document is read-only or has an open transaction. Only the static checks ran.";
            return null;
        }

        CopyRunResult? run = null;
        using var transaction = new Transaction(doc, "Revit MCP - Copy Elements (preview, rolled back)");
        try
        {
            RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
            if (transaction.Start() != TransactionStatus.Started)
            {
                note = "No trial run: Revit did not start a transaction. Only the static checks ran.";
                return null;
            }

            try
            {
                run = CopyElementsService.Execute(doc, items, batches, copyRequest.Atomic, cancellationToken);
            }
            catch (CopyBatchAbortedException ex)
            {
                warnings.Add(ex.Message);
                note = "The trial run failed: Revit refused the copy. See the per-element reasons.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            note = $"The trial run could not complete ({ex.GetType().Name}: {ex.Message}). Only the static checks are reliable.";
        }
        finally
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
                transaction.RollBack();
        }

        note ??= "Trial run rolled back: the model is unchanged. Warnings Revit raises only on commit (for example " +
                 "identical instances in the same place) appear when the copy is really made.";
        return run;
    }

    // ── Copy ─────────────────────────────────────────────────────────────────

    private static McpToolResult Copy(
        McpToolRequest request, Document doc, CopyRequest copyRequest, List<CopyItem> items, List<CopyBatch> batches,
        int blocked, List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        foreach (var item in items.Where(item => item.IsFailure && item.Reason != null))
            warnings.Add($"Element {item.ElementId}: {item.Reason}");

        if (CopyElementsMath.ShouldRollBack(copyRequest.Atomic, blocked))
        {
            // Rejected before a transaction opens, so the undo stack stays untouched.
            foreach (var item in items.Where(item => item.CanCopy))
                item.Status = CopyStatus.NotAttempted;

            sw.Stop();
            return Result(request, copyRequest, items, batches, null, warnings, sw, success: false,
                $"Nothing was copied: atomic=true and {blocked} of {items.Count} element(s) cannot be copied. " +
                "Fix or drop them, or pass atomic=false to copy the rest.", null);
        }

        if (!items.Any(item => item.CanCopy))
        {
            sw.Stop();
            return Result(request, copyRequest, items, batches, null, warnings, sw, success: false,
                "Nothing was copied: none of the elements can be copied as requested.", null);
        }

        CopyRunResult? run = null;
        cancellationToken.ThrowIfCancellationRequested();
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Copy Elements", () =>
        {
            run = CopyElementsService.Execute(doc, items, batches, copyRequest.Atomic, cancellationToken);
        });

        if (!txSuccess)
        {
            foreach (var item in items.Where(item => item.Status == CopyStatus.Copied))
            {
                item.Status = CopyStatus.RolledBack;
                item.Copy = null;
                item.Mapping = null;
            }
            foreach (var item in items.Where(item => item.CanCopy && item.Status == CopyStatus.Ready))
                item.Status = CopyStatus.NotAttempted;

            warnings.AddRange(diagnostics.ToErrorLines());
            sw.Stop();
            return Result(request, copyRequest, items, batches, null, warnings, sw, success: false,
                copyRequest.Atomic
                    ? "Rolled back: atomic=true and Revit refused a copy, so nothing was created. The model is unchanged."
                    : "The transaction did not commit, so no copy was created. The model is unchanged.", diagnostics);
        }

        // Warnings Revit raised on commit and dismissed (identical instances, and the like).
        warnings.AddRange(diagnostics.FailureMessages);
        foreach (var item in items.Where(item => item.Status == CopyStatus.Failed && item.Reason != null))
            warnings.Add($"Element {item.ElementId} could not be copied: {item.Reason}");

        var copied = items.Count(item => item.Status == CopyStatus.Copied);
        var failed = items.Count(item => item.IsFailure);
        sw.Stop();
        return Result(request, copyRequest, items, batches, run, warnings, sw, success: copied > 0,
            $"Copied {copied} of {items.Count} element(s); Revit created {run?.Created.Count ?? 0} new element(s)" +
            $"{(run?.Dependencies.Count > 0 ? $", {run.Dependencies.Count} of them dependents" : string.Empty)}" +
            $"{(failed > 0 ? $"; {failed} could not be copied" : string.Empty)}.", diagnostics);
    }

    private static McpToolResult Result(
        McpToolRequest request, CopyRequest copyRequest, List<CopyItem> items, List<CopyBatch> batches, CopyRunResult? run,
        List<string> warnings, Stopwatch sw, bool success, string message, TransactionDiagnostics? diagnostics) => new()
    {
        RequestId = request.RequestId,
        Success = success,
        Message = message,
        Data = new
        {
            total = items.Count,
            copied = items.Count(item => item.Status == CopyStatus.Copied),
            failed = items.Count(item => item.IsFailure),
            atomic = copyRequest.Atomic,
            copies = items.Where(item => item.Copy != null)
                .Select(item => new { sourceId = item.ElementId, copyId = item.Copy!.Id, mapping = item.Mapping })
                .ToList(),
            newElementIds = run?.Created.Select(created => created.Id).ToList() ?? new List<long>(),
            dependents = run?.Dependencies.Select(DescribeSnapshot).ToList() ?? new List<object>(),
            batches = batches.Select(DescribeBatch).ToList(),
            elements = items.Select(item => DescribeItem(item, includeCopyIds: true)).ToList(),
            transaction = diagnostics
        },
        Warnings = warnings,
        DurationMs = sw.ElapsedMilliseconds
    };

    // ── Shaping ──────────────────────────────────────────────────────────────

    private static object DescribeBatch(CopyBatch batch) => new
    {
        operation = batch.Operation,
        revitCall = batch.Operation == CopyOperation.View
            ? "ElementTransformUtils.CopyElements(sourceView, ids, destinationView, transform, options)"
            : "ElementTransformUtils.CopyElements(document, ids, document, transform, options)",
        sourceViewId = batch.Operation == CopyOperation.View ? batch.SourceView?.Id.Value : null,
        sourceViewName = batch.Operation == CopyOperation.View ? batch.SourceView?.Name : null,
        destinationViewId = batch.DestinationView?.Id.Value,
        destinationViewName = batch.DestinationView?.Name,
        crossView = batch.CrossView,
        elementCount = batch.Items.Count,
        translationMm = Point(batch.TranslationMm),
        error = batch.Error
    };

    private static object DescribeItem(CopyItem item, bool includeCopyIds) => new
    {
        elementId = item.ElementId,
        elementName = item.Source?.Name,
        category = item.Source?.Category,
        elementClass = item.Source?.ClassName,
        ownerViewId = item.Source?.OwnerViewId,
        ownerViewName = item.OwnerViewName,
        hostId = item.Source?.HostId,
        groupId = item.Source?.GroupId,
        pinned = item.Source?.Pinned,
        batch = item.BatchIndex >= 0 ? item.BatchIndex : (int?)null,
        status = item.Status,
        reason = item.Reason,
        copyId = includeCopyIds ? item.Copy?.Id : null,
        mapping = includeCopyIds ? item.Mapping : null,
        copyHostId = includeCopyIds ? item.Copy?.HostId : null,
        copyPointMm = includeCopyIds ? Point(item.Copy?.Anchor) : null,
        notes = item.Notes.Count > 0 ? item.Notes : null
    };

    private static object DescribeSnapshot(ElementSnapshot snapshot) => new
    {
        elementId = snapshot.Id,
        elementName = snapshot.Name,
        category = snapshot.Category,
        elementClass = snapshot.ClassName,
        ownerViewId = snapshot.OwnerViewId
    };

    private static Dictionary<string, int> CountByCategory(IEnumerable<ElementSnapshot> snapshots) =>
        snapshots
            .GroupBy(snapshot => string.IsNullOrEmpty(snapshot.Category) ? $"({snapshot.ClassName})" : snapshot.Category)
            .OrderByDescending(group => group.Count())
            .ToDictionary(group => group.Key, group => group.Count());

    private static object? Point(PointMm? point) => point.HasValue
        ? new
        {
            x = MoveElementsMath.Round(point.Value.X),
            y = MoveElementsMath.Round(point.Value.Y),
            z = MoveElementsMath.Round(point.Value.Z)
        }
        : null;

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

public class MoveElementsTool : IRevitMcpTool
{
    public string Name => "revit_move_elements";

    public string Description =>
        "Moves existing elements onto exact model coordinates. Requires approval. Nothing is " +
        "deleted or recreated, so element ids, types, parameters, circuits and tags all survive. " +
        "Required: moves — a JSON array of {elementId, targetXmm, targetYmm, targetZmm, expectedXmm, " +
        "expectedYmm, expectedZmm}. An omitted target axis keeps its current value, so leaving out " +
        "targetZmm preserves the elevation. The expected coordinates are an optional concurrency " +
        "check: an element further than positionToleranceMm (default 1.0) from them is reported " +
        "stale and left alone. Optional: atomic (default true — any failure undoes the whole " +
        "batch), skipPinned (default true). Up to 2000 moves per call, in one transaction, " +
        "reversible with a single Revit undo. Run revit_preview_move_elements first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null)
            return Task.FromResult(Fail(request, "No active document."));

        var warnings = new List<string>();
        var options = MoveElementsService.ParseOptions(request.Arguments, warnings);

        request.Arguments.TryGetValue("moves", out var rawMoves);
        var moves = MoveElementsMath.ParseMoves(rawMoves, warnings, out var error);
        if (moves == null)
            return Task.FromResult(Fail(request, error!));

        // Every element is measured before the first one moves, and against the model the caller
        // described. A stale or unmovable element found here means the batch is already wrong.
        var plans = MoveElementsService.BuildPlans(doc, moves, options, cancellationToken);
        var blocked = MoveElementsMath.CountFailures(plans);

        foreach (var plan in plans)
        {
            if (plan.IsFailure && plan.Reason != null)
                warnings.Add($"Element {plan.ElementId}: {plan.Reason}");
        }

        if (MoveElementsMath.ShouldRollBack(options.Atomic, blocked))
        {
            // atomic=true and the batch cannot be completed. Rejecting it here rather than opening
            // a transaction and rolling it back leaves the undo stack untouched.
            foreach (var plan in plans.Where(plan => plan.CanMove))
                plan.Status = MoveStatus.NotAttempted;

            sw.Stop();
            return Task.FromResult(Result(
                request, plans, options, sw.ElapsedMilliseconds, warnings,
                success: false,
                message: $"Nothing was moved: atomic=true and {blocked} of {plans.Count} element(s) " +
                         "cannot be moved. Fix or drop them, or pass atomic=false to move the rest."));
        }

        var moved = 0;
        var runtimeFailures = 0;

        cancellationToken.ThrowIfCancellationRequested();
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Move Elements", () =>
        {
            foreach (var plan in plans)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!plan.CanMove)
                    continue;

                var element = doc.GetElement(MoveElementsService.ToElementId(plan.ElementId));
                if (element == null)
                {
                    plan.Status = MoveStatus.Missing;
                    plan.Reason = "The element disappeared between the preview and the move.";
                    runtimeFailures++;
                    if (options.Atomic)
                        throw new MoveBatchAbortedException(plan.ElementId, plan.Reason);
                    continue;
                }

                if (options.Atomic)
                {
                    // No per-element sub-transaction: a failure discards the whole batch anyway,
                    // and skipping them keeps 500 moves to one transaction's worth of overhead.
                    try
                    {
                        MoveElementsService.Move(doc, element, plan);
                    }
                    catch (Exception ex)
                    {
                        plan.Status = MoveStatus.Failed;
                        plan.Reason = ex.Message;
                        runtimeFailures++;
                        throw new MoveBatchAbortedException(plan.ElementId, ex.Message);
                    }

                    plan.Status = MoveStatus.Moved;
                    moved++;
                    continue;
                }

                // atomic=false: one sub-transaction per element, so a refusal by Revit costs that
                // element and nothing else.
                using var subTransaction = new SubTransaction(doc);
                subTransaction.Start();
                try
                {
                    MoveElementsService.Move(doc, element, plan);
                    subTransaction.Commit();
                    plan.Status = MoveStatus.Moved;
                    moved++;
                }
                catch (Exception ex)
                {
                    if (subTransaction.GetStatus() == TransactionStatus.Started)
                        subTransaction.RollBack();

                    plan.Status = MoveStatus.Failed;
                    plan.Reason = ex.Message;
                    runtimeFailures++;
                    warnings.Add($"Element {plan.ElementId} could not be moved: {ex.Message}");
                }
            }

            // Once, at the end. Regenerating per element would rebuild dependent geometry — tags,
            // circuits, hosted families — hundreds of times over for no benefit.
            if (moved > 0)
                doc.Regenerate();
        });

        if (!txSuccess)
        {
            foreach (var plan in plans.Where(plan => plan.Status == MoveStatus.Moved))
                plan.Status = MoveStatus.RolledBack;

            foreach (var plan in plans.Where(plan => plan.CanMove && plan.Status == MoveStatus.Ready))
                plan.Status = MoveStatus.NotAttempted;

            var rolledBack = moved;
            warnings.AddRange(diagnostics.ToErrorLines());

            sw.Stop();
            return Task.FromResult(Result(
                request, plans, options, sw.ElapsedMilliseconds, warnings,
                success: false,
                message: options.Atomic
                    ? $"Rolled back: atomic=true and a move failed, so all {rolledBack} element(s) " +
                      "already moved were returned to where they were. The model is unchanged."
                    : $"The transaction did not commit, so none of the {rolledBack} move(s) landed. " +
                      "The model is unchanged."));
        }

        sw.Stop();
        var summaryMessage =
            $"Moved {moved} of {plans.Count} element(s)" +
            $"{(blocked > 0 ? $", {blocked} blocked" : string.Empty)}" +
            $"{(runtimeFailures > 0 ? $", {runtimeFailures} failed" : string.Empty)}.";

        return Task.FromResult(Result(
            request, plans, options, sw.ElapsedMilliseconds, warnings,
            success: moved > 0 || (blocked == 0 && runtimeFailures == 0),
            message: summaryMessage));
    }

    private static McpToolResult Result(
        McpToolRequest request,
        List<MovePlan> plans,
        MoveElementsOptions options,
        long durationMs,
        List<string> warnings,
        bool success,
        string message)
    {
        var summary = MoveElementsMath.Summarise(plans);
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = success,
            Message = message,
            Data = new
            {
                total = plans.Count,
                moved = summary.Moved.Count,
                skipped = summary.Skipped.Count,
                failed = summary.Failed.Count,
                atomic = options.Atomic,
                skipPinned = options.SkipPinned,
                positionToleranceMm = options.PositionToleranceMm,
                elementIds = MoveElementsPayload.Summarise(summary),
                moves = plans.Select(MoveElementsPayload.Describe).ToList()
            },
            Warnings = warnings,
            DurationMs = durationMs
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };

    /// <summary>
    /// Thrown to abandon an atomic batch. Escaping the transaction body is what rolls the
    /// transaction back — see <see cref="SafeTransactionCore"/>.
    /// </summary>
    private sealed class MoveBatchAbortedException : Exception
    {
        public MoveBatchAbortedException(long elementId, string reason)
            : base($"atomic=true: element {elementId} could not be moved ({reason}), " +
                   "so the whole batch was rolled back.")
        {
        }
    }
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Approval;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>revit_preview_mirror_elements (#108): where mirrored elements would land, and what Revit refuses.</summary>
public sealed class PreviewMirrorElementsTool : IRevitMcpTool
{
    public string Name => "revit_preview_mirror_elements";

    public string Description =>
        "Previews mirroring elements across a plane, without changing the model. Same arguments as revit_mirror_elements. " +
        "Returns the resolved plane, per element whether Revit can mirror it (ElementTransformUtils.CanMirrorElement) and " +
        "where its insertion point lands, and — from a trial run that is rolled back (dryRun, default true) — how many " +
        "elements Revit would create.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(MirrorElementsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_mirror_elements (#108): ElementTransformUtils.MirrorElements across a plane, copy or move.</summary>
public sealed class MirrorElementsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_mirror_elements";

    public string Description =>
        "Mirrors elements across a plane, as Revit's Mirror – Draw Axis / Pick Axis. Requires approval. Required: elementIds " +
        "(up to 2000) and plane: {viewId?, fromMm {x,y}, toMm {x,y}} — a line drawn in a view (plan by default); the mirror " +
        "plane contains the line and the view direction, so in a plan it is the vertical plane through the line — or " +
        "{axis: x|y|z, throughMm {x,y,z}} (the plane perpendicular to that model axis through the point) or " +
        "{pointMm, normal}. copy (default true) creates mirrored copies and leaves the originals; copy=false mirrors the " +
        "elements in place. A plane that is not vertical needs allowHorizontalPlane=true. View-specific elements are " +
        "mirrored in their own view (the line must be drawn there). A group instance mirrors the whole group. Elements " +
        "Revit cannot mirror are listed (status mirror_not_allowed with atomic=true), never silently dropped. atomic " +
        "(default true), skipPinned (default true, copy=false only). One transaction, one undo. Run " +
        "revit_preview_mirror_elements first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(MirrorElementsExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var plan = MirrorService.Plan(target.Document, request.Arguments, out _);
        return plan == null ? null : TransformsAndGroupsApprovalSummaries.Mirror(request, plan.Plane.Description);
    }
}

internal static class MirrorElementsExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var plan = MirrorService.Plan(doc, request.Arguments, out var error);
        if (plan == null)
            return new McpToolResult { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = error! };

        var warnings = new List<string>();
        var blocked = plan.Items.Count(i => i.IsFailure);
        var wouldReject = plan.Options.Atomic && blocked > 0;

        if (!apply)
        {
            MirrorRun? trial = null;
            string? note;
            var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", true);
            if (!dryRun) note = "dryRun=false: only the static checks ran.";
            else if (wouldReject) note = "No trial run: atomic=true and some elements cannot be mirrored, so the request would be rejected.";
            else if (!plan.Items.Any(i => i.CanMirror)) note = "No trial run: nothing can be mirrored.";
            else trial = TrialRun(doc, plan, warnings, cancellationToken, out note);

            foreach (var item in plan.Items.Where(i => i.Status is MirrorStatus.Mirrored or MirrorStatus.Moved))
            {
                item.Status = MirrorStatus.Ready;
                item.Copy = null;
            }

            sw.Stop();
            var ready = plan.Items.Count(i => i.CanMirror);
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Status = wouldReject && plan.NotAllowed.Count > 0 ? MirrorMath.NotAllowedStatus : null,
                Message = $"Preview: {ready} of {plan.Items.Count} element(s) would be {(plan.Options.Copy ? "mirror-copied" : "mirrored in place")} " +
                          $"across {plan.Plane.Description}" +
                          (blocked > 0 ? $"; {blocked} cannot" : string.Empty) +
                          (trial != null && plan.Options.Copy ? $"; Revit would create {trial.Created.Count} element(s)" : string.Empty) + "." +
                          (wouldReject ? " With atomic=true the whole request would be rejected." : string.Empty),
                Data = Payload(plan, trial, includeCopies: false, extra: new { trialRun = trial != null, trialNote = note }),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        foreach (var item in plan.Items.Where(i => i.IsFailure && i.Reason != null))
            warnings.Add($"Element {item.ElementId}: {item.Reason}");

        if (wouldReject || !plan.Items.Any(i => i.CanMirror))
        {
            foreach (var item in plan.Items.Where(i => i.CanMirror)) item.Status = MirrorStatus.NotAttempted;
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = plan.NotAllowed.Count > 0 ? MirrorMath.NotAllowedStatus : null,
                Message = wouldReject
                    ? $"Nothing was mirrored: atomic=true and {blocked} of {plan.Items.Count} element(s) cannot be mirrored" +
                      (plan.NotAllowed.Count > 0 ? $" ({plan.NotAllowed.Count} refused by Revit's CanMirrorElement — see data.notAllowed)" : string.Empty) +
                      ". Fix or drop them, or pass atomic=false to mirror the rest."
                    : "Nothing was mirrored: none of the elements can be mirrored as requested.",
                Data = Payload(plan, null, includeCopies: false, extra: null),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        MirrorRun? run = null;
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Mirror Elements", () =>
        {
            run = MirrorService.Execute(doc, plan, cancellationToken);
        });

        if (!txSuccess)
        {
            foreach (var item in plan.Items.Where(i => i.Status is MirrorStatus.Mirrored or MirrorStatus.Moved))
            {
                item.Status = MirrorStatus.RolledBack;
                item.Copy = null;
            }
            foreach (var item in plan.Items.Where(i => i.CanMirror && i.Status == MirrorStatus.Ready)) item.Status = MirrorStatus.NotAttempted;
            warnings.AddRange(diagnostics.ToErrorLines());
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = "Rolled back: Revit refused the mirror, so nothing changed. See the per-element reasons.",
                Data = Payload(plan, null, includeCopies: false, extra: new { transaction = diagnostics }),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        warnings.AddRange(diagnostics.FailureMessages);
        foreach (var item in plan.Items.Where(i => i.Status == MirrorStatus.Failed && i.Reason != null))
            warnings.Add($"Element {item.ElementId} could not be mirrored: {item.Reason}");

        var done = run?.Done ?? 0;
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = done > 0,
            Message = plan.Options.Copy
                ? $"Mirror-copied {done} of {plan.Items.Count} element(s) across {plan.Plane.Description}; Revit created {run?.Created.Count ?? 0} element(s)."
                : $"Mirrored {done} of {plan.Items.Count} element(s) in place across {plan.Plane.Description}.",
            Data = Payload(plan, run, includeCopies: true, extra: new { transaction = diagnostics }),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    /// <summary>Asks Revit to mirror inside a transaction that is always rolled back: the model is left as it was.</summary>
    private static MirrorRun? TrialRun(Document doc, MirrorPlan plan, List<string> warnings, CancellationToken cancellationToken, out string note)
    {
        if (doc.IsReadOnly || doc.IsModifiable)
        {
            note = "No trial run: the document is read-only or has an open transaction. Only the static checks ran.";
            return null;
        }

        MirrorRun? run = null;
        note = "Trial run rolled back: the model is unchanged.";
        using var transaction = new Transaction(doc, "Revit MCP - Mirror Elements (preview, rolled back)");
        try
        {
            RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
            if (transaction.Start() != TransactionStatus.Started)
            {
                note = "No trial run: Revit did not start a transaction.";
                return null;
            }
            try
            {
                run = MirrorService.Execute(doc, plan, cancellationToken);
            }
            catch (MirrorBatchAbortedException ex)
            {
                warnings.Add(ex.Message);
                note = "The trial run failed: Revit refused the mirror. See the per-element reasons.";
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
            if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
        }
        return run;
    }

    private static object Payload(MirrorPlan plan, MirrorRun? run, bool includeCopies, object? extra)
    {
        var done = plan.Items.Count(i => i.Status is MirrorStatus.Mirrored or MirrorStatus.Moved);
        return new
        {
            total = plan.Items.Count,
            copy = plan.Options.Copy,
            mirrored = plan.Options.Copy ? done : 0,
            moved = plan.Options.Copy ? 0 : done,
            atomic = plan.Options.Atomic,
            plane = new
            {
                mode = plan.PlaneRequest.Mode,
                description = plan.Plane.Description,
                viewId = plan.PlaneView?.Id.Value,
                viewName = plan.PlaneView?.Name,
                originMm = Point(plan.Plane.Origin),
                normal = new { x = Math.Round(plan.Plane.Normal.X, 6), y = Math.Round(plan.Plane.Normal.Y, 6), z = Math.Round(plan.Plane.Normal.Z, 6) },
                vertical = plan.Plane.IsVertical
            },
            newIds = includeCopies ? run?.Created.Select(c => c.Id).ToList() ?? new List<long>() : null,
            wouldCreate = !includeCopies && run != null ? run.Created.Count : (int?)null,
            pairs = plan.Items.Where(i => i.CanMirror || i.Status is MirrorStatus.Mirrored or MirrorStatus.Moved)
                .Select(i => new
                {
                    sourceId = i.ElementId,
                    newId = includeCopies ? i.Copy?.Id : null,
                    mapping = includeCopies ? i.Mapping : null,
                    fromMm = Point(i.FromMm),
                    toMm = Point(i.ToMm)
                }).ToList(),
            skipped = plan.Items.Where(i => !i.CanMirror && i.Status is not (MirrorStatus.Mirrored or MirrorStatus.Moved))
                .Select(i => new { elementId = i.ElementId, status = i.Status, reason = i.Reason }).ToList(),
            notAllowed = plan.NotAllowed,
            dependents = includeCopies ? run?.Dependencies.Select(d => new { elementId = d.Id, category = d.Category, elementClass = d.ClassName }).ToList() : null,
            elements = plan.Items.Select(i => new
            {
                elementId = i.ElementId,
                elementName = i.Source?.Name,
                category = i.Source?.Category,
                ownerViewName = i.OwnerViewName,
                groupId = i.Source?.GroupId,
                isGroupInstance = i.IsGroupInstance,
                status = i.Status,
                reason = i.Reason,
                notes = i.Notes.Count > 0 ? i.Notes : null
            }).ToList(),
            details = extra
        };
    }

    private static object? Point(PointMm? point) => point.HasValue
        ? new { x = Math.Round(point.Value.X, 1), y = Math.Round(point.Value.Y, 1), z = Math.Round(point.Value.Z, 1) }
        : null;
}

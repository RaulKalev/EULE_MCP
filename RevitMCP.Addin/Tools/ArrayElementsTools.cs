using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>revit_preview_array_elements (#108): the steps of a linear or radial array and where the copies land.</summary>
public sealed class PreviewArrayElementsTool : IRevitMcpTool
{
    public string Name => "revit_preview_array_elements";

    public string Description =>
        "Previews a linear or radial array, without changing the model. Same arguments as revit_array_elements. Returns " +
        "the steps (translation or angle per copy), how many copies are made, per element whether it can be copied, and " +
        "where the first elements land in every step. Static checks only: no trial run.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ArrayElementsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_array_elements (#108): plain copies per step (default) or a Revit array group.</summary>
public sealed class ArrayElementsTool : IRevitMcpTool
{
    public string Name => "revit_array_elements";

    public string Description =>
        "Arrays elements. Requires approval. Required: elementIds, kind (linear | radial), count (2-200 members; with " +
        "includeOriginal=true, the default, the original is one of them). Linear: spacingMm {x,y,z} (member to member), " +
        "totalMm {x,y,z} (original to last copy) or deltaRightMm/deltaUpMm (member to member along a view's axes; viewId " +
        "or the elements' owner view). Radial: centerMm {x,y,z}, axis (x|y|z, default z), angleDeg per step or " +
        "totalAngleDeg (360 = evenly round a full circle), rotateElements (default true; false keeps orientation). " +
        "asGroup=false (default): independent copies, made with Revit's copy per step (model or view path as " +
        "revit_copy_elements); asGroup=true: a Revit array (LinearArray / RadialArray, radial needs count >= 3) whose " +
        "count parameter equals count. Collisions with existing elements are not checked. atomic (default true). One " +
        "transaction, one undo. Run revit_preview_array_elements first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ArrayElementsExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class ArrayElementsExecutor
{
    /// <summary>How many elements' positions the preview lists per step.</summary>
    private const int PreviewPositionsPerStep = 20;

    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var plan = ArrayService.Plan(doc, request.Arguments, out var error, out var status);
        if (plan == null)
            return new McpToolResult { RequestId = request.RequestId, Success = false, Status = status ?? "validation_failed", Message = error! };

        var warnings = new List<string> { "Collisions with existing elements are not checked: copies may overlap what is already there." };
        var blocked = plan.Items.Count(i => i.IsFailure);
        var readyCount = plan.Items.Count(i => i.CanCopy);
        var wouldReject = plan.Error != null || (plan.Request.Atomic && blocked > 0) || readyCount == 0;

        if (!apply)
        {
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = plan.Error ?? (
                    $"Preview: {readyCount} of {plan.Items.Count} element(s) × {plan.Request.Copies} cop{(plan.Request.Copies == 1 ? "y" : "ies")} " +
                    $"({plan.Request.Kind}{(plan.Request.AsGroup ? ", as a Revit array" : string.Empty)})" +
                    (blocked > 0 ? $"; {blocked} element(s) cannot be copied" : string.Empty) + "." +
                    (wouldReject ? " The request would be rejected as it stands." : string.Empty)),
                Data = Payload(plan, null, preview: true),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        foreach (var item in plan.Items.Where(i => i.IsFailure && i.Reason != null))
            warnings.Add($"Element {item.ElementId}: {item.Reason}");

        if (wouldReject)
        {
            foreach (var item in plan.Items.Where(i => i.CanCopy)) item.Status = CopyStatus.NotAttempted;
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "validation_failed",
                Message = plan.Error ?? (readyCount == 0
                    ? "Nothing was arrayed: none of the elements can be copied."
                    : $"Nothing was arrayed: atomic=true and {blocked} of {plan.Items.Count} element(s) cannot be copied. Fix or drop them, or pass atomic=false."),
                Data = Payload(plan, null, preview: false),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        ArrayRun? run = null;
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Array Elements", () =>
        {
            run = ArrayService.Execute(doc, plan, cancellationToken);
        });

        if (!txSuccess)
        {
            foreach (var item in plan.Items.Where(i => i.Status == CopyStatus.Copied)) item.Status = CopyStatus.RolledBack;
            foreach (var item in plan.Items.Where(i => i.CanCopy && i.Status == CopyStatus.Ready)) item.Status = CopyStatus.NotAttempted;
            warnings.AddRange(diagnostics.ToErrorLines());
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = "Rolled back: Revit refused a copy, so nothing was created. The model is unchanged.",
                Data = Payload(plan, null, preview: false),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        warnings.AddRange(diagnostics.FailureMessages);
        foreach (var step in run!.Steps.Where(s => s.Error != null))
            warnings.Add($"Step {step.Step.Index} failed: {step.Error}");

        sw.Stop();
        var created = run.NewIds.Count;
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created > 0,
            Message = plan.Request.AsGroup
                ? $"Created a Revit {plan.Request.Kind} array (id {run.ArrayId}) with {run.NumMembers?.ToString() ?? "?"} member(s); {created} new element(s)."
                : $"Arrayed {readyCount} element(s) in {run.Steps.Count(s => s.NewIds.Count > 0)} step(s); Revit created {created} new element(s)" +
                  (run.Failures > 0 ? $", {run.Failures} copy attempt(s) failed" : string.Empty) + ".",
            Data = Payload(plan, run, preview: false),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Payload(ArrayPlan plan, ArrayRun? run, bool preview)
    {
        var request = plan.Request;
        var ready = plan.Items.Where(i => i.CanCopy || i.Status == CopyStatus.Copied).ToList();
        return new
        {
            kind = request.Kind,
            count = request.Count,
            includeOriginal = request.IncludeOriginal,
            copiesPerElement = request.Copies,
            asGroup = request.AsGroup,
            atomic = request.Atomic,
            linearStepMm = Point(plan.LinearStepMm),
            stepAngleDeg = request.Kind == ArrayKind.Radial ? Math.Round(plan.StepAngleDeg, 6) : (double?)null,
            centerMm = Point(request.CenterMm),
            axis = request.Kind == ArrayKind.Radial ? request.Axis : null,
            rotateElements = request.Kind == ArrayKind.Radial ? request.RotateElements : (bool?)null,
            groupViewId = plan.GroupView?.Id.Value,
            groupViewName = plan.GroupView?.Name,
            arrayId = run?.ArrayId,
            numMembers = run?.NumMembers,
            newElementIds = run?.NewIds ?? new List<long>(),
            steps = plan.Steps.Select(step =>
            {
                var result = run?.Steps.FirstOrDefault(s => s.Step.Index == step.Index);
                return new
                {
                    step = step.Index,
                    translationMm = Point(step.TranslationMm),
                    rotationDeg = Math.Round(step.RotationDeg, 6),
                    landsMm = preview
                        ? ready.Where(i => i.Source?.Anchor != null).Take(PreviewPositionsPerStep)
                            .Select(i => new { elementId = i.ElementId, pointMm = Point(ArrayMath.PositionAfter(step, i.Source!.Anchor!.Value, request)) })
                            .ToList()
                        : null,
                    newIds = result?.NewIds,
                    copies = result?.Copies.Select(c => new { sourceId = c.SourceId, copyId = c.CopyId }).ToList(),
                    error = result?.Error
                };
            }).ToList(),
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
            error = plan.Error,
            collisionsChecked = false
        };
    }

    private static object? Point(PointMm? point) => point.HasValue
        ? new { x = Math.Round(point.Value.X, 1), y = Math.Round(point.Value.Y, 1), z = Math.Round(point.Value.Z, 1) }
        : null;
}

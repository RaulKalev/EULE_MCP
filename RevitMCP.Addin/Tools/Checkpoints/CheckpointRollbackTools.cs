using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Checkpoints;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Checkpoints;

/// <summary>Plans a rollback against the live model: what the planner proposes, minus what Revit will not allow.</summary>
internal static class RollbackSupport
{
    public const int MaxListed = 200;
    private const double MmPerFoot = 304.8;

    public sealed class LivePlan
    {
        public Checkpoint Checkpoint { get; set; } = null!;
        public RollbackPlan Plan { get; set; } = null!;
    }

    public static LivePlan? Prepare(UIApplication uiapp, McpToolRequest request, out Document? doc, out McpToolResult? failure)
    {
        doc = null;
        var target = CheckpointSupport.Resolve(uiapp, request, out var store, out failure);
        if (target == null) return null;
        doc = target.Document;
        var checkpoint = CheckpointSupport.Find(store!, doc, request, out failure);
        if (checkpoint == null) return null;

        var options = new RollbackOptions
        {
            Force = ToolArguments.GetBool(request.Arguments, "force"),
            ElementIds = ToolArguments.GetLongArray(request.Arguments, "elementIds") is { Length: > 0 } only ? new HashSet<long>(only) : null
        };
        if (!RollbackOptions.TryParseWhat(ToolArguments.GetStringArray(request.Arguments, "what"), options, out var unknown))
        {
            failure = CheckpointSupport.Fail(request, $"Unknown what value '{unknown}'. Use deleteAdded, restoreValues, restoreLocations.");
            return null;
        }

        var (diff, _, _) = CheckpointSupport.ComputeDiff(doc, checkpoint);
        var plan = RollbackPlanner.Plan(diff, checkpoint, options);
        ApplyRevitLimits(doc, plan);
        return new LivePlan { Checkpoint = checkpoint, Plan = plan };
    }

    /// <summary>Pinned elements are neither deleted nor moved; elements that vanished are skipped.</summary>
    private static void ApplyRevitLimits(Document doc, RollbackPlan plan)
    {
        plan.Delete.RemoveAll(id =>
        {
            var element = doc.GetElement(new ElementId(id));
            if (element == null) return true;   // already gone with its host: nothing to do
            if (!element.Pinned) return false;
            plan.Skipped.Add(new RollbackSkip { Id = id, Action = "deleteAdded", Reason = "pinned; unpin it in Revit to delete it" });
            return true;
        });
        plan.Locations.RemoveAll(location =>
        {
            var element = doc.GetElement(new ElementId(location.Id));
            if (element == null) return true;
            if (!element.Pinned) return false;
            plan.Skipped.Add(new RollbackSkip { Id = location.Id, Action = "restoreLocations", Reason = "pinned; unpin it in Revit to move it back" });
            return true;
        });
    }

    public static string Summary(LivePlan live)
    {
        var p = live.Plan;
        return $"DESTRUCTIVE: Rollback to checkpoint '{live.Checkpoint.Title}' ({live.Checkpoint.Id}): delete {p.Delete.Count} added element(s), " +
               $"restore {p.Values.Count} parameter value(s) on {p.ValueElementCount} element(s), move back {p.Locations.Count} element(s)" +
               (p.Skipped.Count > 0 ? $"; {p.Skipped.Count} skipped" : string.Empty) +
               ". Deleted elements are NOT restored. Reversible with one Revit undo.";
    }

    public static object Describe(RollbackPlan plan) => new
    {
        deleteAdded = new { count = plan.Delete.Count, ids = plan.Delete.Take(MaxListed).ToList() },
        restoreValues = new
        {
            count = plan.Values.Count,
            elements = plan.ValueElementCount,
            items = plan.Values.Take(MaxListed).Select(v => new { id = v.Id, parameter = v.Parameter, now = v.Current, restoreTo = v.Value }).ToList()
        },
        restoreLocations = new
        {
            count = plan.Locations.Count,
            items = plan.Locations.Take(MaxListed).Select(l => new
            {
                id = l.Id,
                moveMm = l.TranslateMm?.Select(v => Math.Round(v, 1)).ToArray(),
                rotateDeg = l.RotateRad is double r ? Math.Round(r * 180 / Math.PI, 2) : (double?)null
            }).ToList()
        },
        cannotRestore = new { deleted = plan.CannotRestoreDeleted, notSnapshotted = plan.CannotRestoreNotSnapshotted },
        skipped = plan.Skipped.Take(MaxListed).Select(s => new { id = s.Id, action = s.Action, reason = s.Reason }).ToList(),
        skippedCount = plan.Skipped.Count
    };

    /// <summary>Runs the plan in one transaction. Failures per element are added to <paramref name="skipped"/>.</summary>
    public static (int Deleted, int Restored, int Moved) Execute(Document doc, LivePlan live, List<RollbackSkip> skipped, out TransactionDiagnostics? failedDiagnostics)
    {
        var plan = live.Plan;
        int deleted = 0, restored = 0, moved = 0;
        var (success, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Rollback to " + live.Checkpoint.Title, () =>
        {
            foreach (var value in plan.Values)
            {
                var reason = RestoreValue(doc, value);
                if (reason == null) restored++;
                else skipped.Add(new RollbackSkip { Id = value.Id, Action = "restoreValues", Reason = $"'{value.Parameter}': {reason}" });
            }

            foreach (var location in plan.Locations)
            {
                try
                {
                    var id = new ElementId(location.Id);
                    if (location.TranslateMm is { } t)
                        ElementTransformUtils.MoveElement(doc, id, new XYZ(t[0] / MmPerFoot, t[1] / MmPerFoot, t[2] / MmPerFoot));
                    if (location.RotateRad is double angle && location.RotationOriginMm is { } o)
                    {
                        var origin = new XYZ(o[0] / MmPerFoot, o[1] / MmPerFoot, o[2] / MmPerFoot);
                        ElementTransformUtils.RotateElement(doc, id, Line.CreateBound(origin, origin + XYZ.BasisZ), angle);
                    }
                    moved++;
                }
                catch (Exception ex)
                {
                    skipped.Add(new RollbackSkip { Id = location.Id, Action = "restoreLocations", Reason = ex.Message });
                }
            }

            foreach (var id in plan.Delete)
            {
                try
                {
                    if (doc.GetElement(new ElementId(id)) == null) continue;   // removed with an element deleted before it
                    doc.Delete(new ElementId(id));
                    deleted++;
                }
                catch (Exception ex)
                {
                    skipped.Add(new RollbackSkip { Id = id, Action = "deleteAdded", Reason = ex.Message });
                }
            }
        });
        failedDiagnostics = success ? null : diagnostics;
        return success ? (deleted, restored, moved) : (0, 0, 0);
    }

    /// <summary>Sets a parameter back to its stored value; returns why not, or null on success.</summary>
    private static string? RestoreValue(Document doc, RollbackValue value)
    {
        var element = doc.GetElement(new ElementId(value.Id));
        if (element == null) return "the element no longer exists";
        if (TextNoteSnapshot.IsPseudoParameter(value.Parameter)) return RestoreTextNote(element, value);
        var parameter = element.GetOrderedParameters()
            .FirstOrDefault(p => string.Equals(p.Definition?.Name, value.Parameter, StringComparison.OrdinalIgnoreCase));
        if (parameter == null) return "the parameter no longer exists";
        if (parameter.IsReadOnly) return "the parameter is read-only now";
        if (!RawParameterValue.TryParse(value.Raw, out var kind, out var text)) return "no stored value";

        try
        {
            switch (kind)
            {
                case 'S' when parameter.StorageType == StorageType.String:
                    return parameter.Set(text) ? null : "Revit refused the value";
                case 'I' when parameter.StorageType == StorageType.Integer:
                    return parameter.Set(int.Parse(text, CultureInfo.InvariantCulture)) ? null : "Revit refused the value";
                case 'D' when parameter.StorageType == StorageType.Double:
                    return parameter.Set(double.Parse(text, CultureInfo.InvariantCulture)) ? null : "Revit refused the value";
                default:
                    return $"the parameter now stores {parameter.StorageType}, not the type it had at checkpoint time";
            }
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>Sets a text note's text or width back (the "(Text)" / "(Width)" pseudo-parameters); returns why not, or null.</summary>
    private static string? RestoreTextNote(Element element, RollbackValue value)
    {
        if (element is not TextNote note) return "the element is no text note now";
        if (!TextNoteSnapshot.TryParseRestore(value.Parameter, value.Raw, out var field, out var text, out var widthFeet, out var error))
            return error ?? "no stored value";
        try
        {
            if (field == TextNoteField.Text) note.Text = text;
            else note.Width = widthFeet;
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>revit_preview_checkpoint_rollback (#98): what revit_checkpoint_rollback would do, without doing it.</summary>
public sealed class PreviewCheckpointRollbackTool : IRevitMcpTool
{
    public string Name => "revit_preview_checkpoint_rollback";
    public string Description =>
        "Previews undoing the changes since a checkpoint: which added elements would be deleted, which parameter values " +
        "restored and which elements moved back, and what cannot be undone (deleted elements, elements outside the " +
        "snapshot, elements the user changed last unless force=true). Same arguments as revit_checkpoint_rollback. " +
        "Does not change the model.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var live = RollbackSupport.Prepare(uiapp, request, out _, out var failure);
        if (live == null) return Task.FromResult(failure!);

        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = RollbackSupport.Summary(live).Replace("DESTRUCTIVE: ", "Preview: ") +
                      (live.Plan.IsEmpty ? " Nothing to roll back." : " Run revit_checkpoint_rollback with the same arguments to apply (manual approval)."),
            Data = new
            {
                checkpoint = CheckpointDiffReport.Describe(live.Checkpoint, DateTimeOffset.Now),
                plan = RollbackSupport.Describe(live.Plan)
            },
            DurationMs = sw.ElapsedMilliseconds
        };
        CheckpointSupport.AddTrackingWarning(live.Checkpoint, result);
        return Task.FromResult(result);
    }
}

/// <summary>revit_checkpoint_rollback (#98): compensating transaction back to a checkpoint. Always manual approval.</summary>
public sealed class CheckpointRollbackTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_checkpoint_rollback";
    public string Description =>
        "Undoes the changes since a checkpoint in one transaction (one Revit undo): deletes elements added since, restores " +
        "snapshotted parameter values and moves elements back. Deleted elements are never recreated. Elements the user " +
        "changed last are skipped unless force=true. what=[deleteAdded, restoreValues, restoreLocations] (default all), " +
        "elementIds limits it. Always needs manual approval; preview it with revit_preview_checkpoint_rollback.";
    public ToolPermission Permission => ToolPermission.DestructiveRequiresManualApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var live = RollbackSupport.Prepare(uiapp, request, out _, out _);
        return live == null ? null : RollbackSupport.Summary(live);
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var live = RollbackSupport.Prepare(uiapp, request, out var doc, out var failure);
        if (live == null) return Task.FromResult(failure!);

        if (live.Plan.IsEmpty)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = $"Nothing to roll back to checkpoint {live.Checkpoint.Id}.",
                Data = new { plan = RollbackSupport.Describe(live.Plan) },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var skipped = new List<RollbackSkip>(live.Plan.Skipped);
        var (deleted, restored, moved) = RollbackSupport.Execute(doc!, live, skipped, out var diagnostics);
        if (diagnostics != null)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = $"The rollback was not applied: {diagnostics.OriginalError}",
                Errors = diagnostics.ToErrorLines(),
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        // The rollback's own transaction has been recorded by now; show where the model stands.
        var (diff, warnings, _) = CheckpointSupport.ComputeDiff(doc!, live.Checkpoint);
        var after = CheckpointDiffReport.Build(live.Checkpoint, diff, warnings, new DiffReportOptions(), DateTimeOffset.Now);

        var result = new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Rolled back to checkpoint '{live.Checkpoint.Title}': deleted {deleted}, restored {restored} value(s), moved back {moved} element(s); " +
                      $"{skipped.Count} skipped. Deleted elements were not restored. One Revit undo reverses this rollback.",
            Data = new
            {
                deleted,
                restoredValues = restored,
                movedBack = moved,
                cannotRestore = new { deleted = live.Plan.CannotRestoreDeleted, notSnapshotted = live.Plan.CannotRestoreNotSnapshotted },
                skipped = skipped.Take(RollbackSupport.MaxListed).Select(s => new { id = s.Id, action = s.Action, reason = s.Reason }).ToList(),
                skippedCount = skipped.Count,
                after = new { counts = after["counts"], warnings = after.TryGetValue("warnings", out var w) ? w : null }
            },
            DurationMs = sw.ElapsedMilliseconds
        };
        CheckpointSupport.AddTrackingWarning(live.Checkpoint, result);
        return Task.FromResult(result);
    }
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.FilledRegions;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.FilledRegions;

/// <summary>revit_preview_edit_filled_regions (#135): what an edit would change, and what a reshape would put at risk.</summary>
public sealed class PreviewEditFilledRegionsTool : IRevitMcpTool
{
    public string Name => "revit_preview_edit_filled_regions";

    public string Description =>
        "Previews revit_edit_filled_regions without changing the model. Same arguments. Per edit: willEdit | invalid with a status " +
        "code (loop_open, loop_self_intersecting, not_a_filled_region, type_not_found, parameter_not_settable, ...), the type and " +
        "parameter changes, whether the id is kept, and for a reshape the new boundary and the tags and dimensions that may be lost. " +
        "dryRun=true (default) also tries the edits in a rolled-back transaction.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EditFilledRegionsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_edit_filled_regions (#135): type, line style and parameters keep the id; a new boundary replaces the element.</summary>
public sealed class EditFilledRegionsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_edit_filled_regions";

    public string Description =>
        "Edits filled regions (max 500). Requires approval. Each edit: id and any of type, lineStyle, parameters (these keep the " +
        "element id) and boundary (one shape as in revit_create_filled_regions). The Revit API cannot edit a boundary, so a reshape " +
        "creates a new region and deletes the old one: it keeps type, view and parameters, returns idMap [{oldId, newId}] and warns " +
        "about tags and dimensions on the old region. One transaction, one undo. Run revit_preview_edit_filled_regions first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(EditFilledRegionsExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var parsed = FilledRegionEditParser.ParseEdits(request.Arguments, out _);
        if (parsed == null) return null;

        var annotations = 0;
        foreach (var edit in parsed.Edits.Where(e => e.Reshapes))
            if (target.Document.GetElement(new ElementId(edit.Id)) is FilledRegion region)
                annotations += FilledRegionSupport.AttachedAnnotations(target.Document, region).Count;
        return FilledRegionSummaries.Edit(parsed) + (annotations > 0 ? $" {annotations} tag(s) or dimension(s) are attached to the regions being replaced." : string.Empty);
    }
}

internal sealed class ResolvedEdit
{
    public RegionEditSpec Spec { get; init; } = null!;
    public FilledRegion? Region { get; set; }
    public View? View { get; set; }
    public ViewFrame? Frame { get; set; }
    public FilledRegionType? CurrentType { get; set; }
    public FilledRegionType? NewType { get; set; }
    public ElementId? LineStyleId { get; set; }
    public FinishedShape? Shape { get; set; }
    public List<AttachedAnnotation> Annotations { get; set; } = new();
    public bool? SketchEditingSupported { get; set; }

    public string Status { get; set; } = RegionStatus.WillEdit;
    public string? Code { get; set; }
    public string? Reason { get; set; }
    public long? NewId { get; set; }
    public List<long> LostAnnotations { get; } = new();
    public List<object> ParameterChanges { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool IsReady => Status == RegionStatus.WillEdit || Status == RegionStatus.Edited;

    public void Refuse(string code, string reason)
    {
        Status = RegionStatus.Invalid;
        Code = code;
        Reason = reason;
    }
}

internal static class EditFilledRegionsExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;

        var parsed = FilledRegionEditParser.ParseEdits(request.Arguments, out var parseError);
        if (parsed == null)
            return new McpToolResult { RequestId = request.RequestId, Success = false, Status = RegionCodes.InvalidSpec, Message = parseError! };

        var reader = new RegionShapeReader(doc);
        var shortCurveMm = FilledRegionSupport.ShortCurveMm(doc);
        var edits = new List<ResolvedEdit>();
        foreach (var spec in parsed.Edits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            edits.Add(Resolve(doc, spec, reader, shortCurveMm, probeSketch: !apply));
        }

        var warnings = new List<string>();
        return apply
            ? Apply(request, doc, parsed, edits, warnings, sw, cancellationToken)
            : Preview(request, doc, parsed, edits, warnings, sw, cancellationToken);
    }

    private static ResolvedEdit Resolve(Document doc, RegionEditSpec spec, RegionShapeReader reader, double shortCurveMm, bool probeSketch)
    {
        var edit = new ResolvedEdit { Spec = spec };
        if (spec.Errors.Count > 0)
        {
            edit.Refuse(RegionCodes.InvalidSpec, string.Join(" ", spec.Errors));
            return edit;
        }

        var element = doc.GetElement(new ElementId(spec.Id));
        if (element == null)
        {
            edit.Refuse(RegionCodes.RegionNotFound, $"Element {spec.Id} does not exist.");
            return edit;
        }
        if (element is not FilledRegion region)
        {
            edit.Refuse(RegionCodes.NotAFilledRegion, $"Element {spec.Id} is a {element.GetType().Name}, not a filled region.");
            return edit;
        }
        edit.Region = region;
        edit.CurrentType = doc.GetElement(region.GetTypeId()) as FilledRegionType;
        edit.View = doc.GetElement(region.OwnerViewId) as View;

        var owner = FilledRegionSupport.OwnedByOther(doc, region.Id);
        if (owner != null)
        {
            edit.Refuse(RegionCodes.RegionOwnedByOther, $"Region {spec.Id} is checked out by {owner}.");
            return edit;
        }

        if (!string.IsNullOrWhiteSpace(spec.Type))
        {
            edit.NewType = FilledRegionSupport.ResolveType(doc, spec.Type!);
            if (edit.NewType == null)
            {
                edit.Refuse(RegionCodes.TypeNotFound, $"No filled region type '{spec.Type}'. Types: {FilledRegionSupport.TypeNames(doc)}.");
                return edit;
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.LineStyle))
        {
            edit.LineStyleId = FilledRegionSupport.ResolveLineStyle(doc, spec.LineStyle!);
            if (edit.LineStyleId == null)
            {
                edit.Refuse(RegionCodes.LineStyleNotFound, $"'{spec.LineStyle}' is not a line style a filled region can use.");
                return edit;
            }
        }

        foreach (var pair in spec.Parameters)
        {
            var problem = FilledRegionSupport.ParameterProblem(doc, region, pair.Key);
            if (problem != null)
            {
                edit.Refuse(RegionCodes.ParameterNotSettable, problem);
                return edit;
            }
            var current = ParameterResolver.Selected(ParameterResolver.Resolve(doc, region, new Query.ParameterTarget { Name = pair.Key, ExactMatch = true }));
            edit.ParameterChanges.Add(new { name = pair.Key, from = current == null ? null : ParameterResolver.DisplayValue(current), to = pair.Value });
        }

        if (spec.Boundary != null) ResolveReshape(doc, edit, reader, shortCurveMm, probeSketch);
        return edit;
    }

    private static void ResolveReshape(Document doc, ResolvedEdit edit, RegionShapeReader reader, double shortCurveMm, bool probeSketch)
    {
        var region = edit.Region!;
        if (region.GroupId != null && region.GroupId != ElementId.InvalidElementId)
        {
            edit.Refuse(RegionCodes.RegionInGroup, $"Region {region.Id.Value} is in a group; ungroup it first, a reshape has to delete it.");
            return;
        }
        if (edit.View == null)
        {
            edit.Refuse(RegionCodes.ViewNotFound, $"The owner view of region {region.Id.Value} was not found.");
            return;
        }
        edit.Frame = FilledRegionSupport.FrameOf(edit.View);

        ShapeResult shape;
        try
        {
            shape = reader.Read(edit.Spec.Boundary!, edit.View, edit.Frame);
        }
        catch (Autodesk.Revit.Exceptions.ApplicationException ex)
        {
            shape = ShapeResult.Fail(RegionCodes.NoRoomBoundary, $"Revit could not read the shape: {ex.Message}");
        }
        edit.Shape = RegionShapeFinisher.Finish(shape, edit.Spec.Boundary!.PaddingMm, shortCurveMm);
        edit.Warnings.AddRange(edit.Shape.Warnings);
        if (!edit.Shape.IsValid)
        {
            edit.Refuse(edit.Shape.Validation.Code!, edit.Shape.Validation.Reason ?? string.Empty);
            return;
        }

        edit.Annotations = FilledRegionSupport.AttachedAnnotations(doc, region);
        if (edit.Annotations.Count > 0)
            edit.Warnings.Add($"Reshaping replaces region {region.Id.Value}: {edit.Annotations.Count} tag(s) or dimension(s) on it may be lost ({string.Join(", ", edit.Annotations.Select(a => $"{a.Kind} {a.Id}"))}).");
        if (probeSketch) edit.SketchEditingSupported = FilledRegionSupport.SketchEditingSupported(doc, region.Id);
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private static McpToolResult Preview(
        McpToolRequest request, Document doc, RegionEditRequest parsed, List<ResolvedEdit> edits, List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", true);
        string? trialNote = null;
        var trialRan = false;
        var described = new List<object>();

        if (!dryRun) trialNote = "dryRun=false: only the static checks ran; Revit was not asked to try the edits.";
        else if (!edits.Any(e => e.IsReady)) trialNote = "No trial run: no edit is ready.";
        else if (doc.IsReadOnly || doc.IsModifiable) trialNote = "No trial run: the document is read-only or has an open transaction. Only the static checks ran.";
        else
        {
            using var transaction = new Transaction(doc, "Revit MCP - Edit Filled Regions (preview, rolled back)");
            try
            {
                RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
                if (transaction.Start() == TransactionStatus.Started)
                {
                    trialRan = true;
                    RunAll(doc, edits, atomic: false, cancellationToken);
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
                foreach (var edit in edits.Where(e => e.Status == RegionStatus.Edited))
                {
                    edit.Status = RegionStatus.WillEdit;
                    edit.NewId = null;
                }
                if (transaction.GetStatus() == TransactionStatus.Started) transaction.RollBack();
            }
            trialNote ??= "Trial run rolled back: the model is unchanged.";
        }

        described.AddRange(edits.Select(e => Describe(e, applied: false)));
        var willEdit = edits.Count(e => e.Status == RegionStatus.WillEdit);
        var invalid = edits.Count(e => e.Status == RegionStatus.Invalid);
        var trialFailed = edits.Count(e => e.Status == RegionStatus.Failed);
        var reshapes = edits.Count(e => e.Status == RegionStatus.WillEdit && e.Spec.Reshapes);

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Status = willEdit == 0 ? FirstCode(edits) : null,
            Message = $"Preview: {willEdit} of {edits.Count} edit(s) would apply" +
                      (reshapes > 0 ? $", {reshapes} replacing the element" : string.Empty) +
                      (invalid > 0 ? $", {invalid} invalid" : string.Empty) +
                      (trialFailed > 0 ? $", {trialFailed} refused by Revit in the trial run" : string.Empty) + ".",
            Data = new
            {
                total = edits.Count,
                willEdit,
                invalid,
                trialFailed,
                reshapes,
                atomic = parsed.Atomic,
                trialRun = trialRan,
                trialNote,
                summary = FilledRegionSummaries.Edit(parsed),
                edits = described
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    // ── Apply ────────────────────────────────────────────────────────────────

    private static McpToolResult Apply(
        McpToolRequest request, Document doc, RegionEditRequest parsed, List<ResolvedEdit> edits, List<string> warnings, Stopwatch sw, CancellationToken cancellationToken)
    {
        var invalid = edits.Count(e => e.Status == RegionStatus.Invalid);
        if (parsed.Atomic && invalid > 0)
        {
            foreach (var edit in edits.Where(e => e.IsReady)) edit.Status = RegionStatus.NotAttempted;
            return Result(request, parsed, edits, warnings, sw, false, FirstCode(edits),
                $"Nothing was changed: atomic=true and {invalid} of {edits.Count} edit(s) are invalid.", null);
        }
        if (!edits.Any(e => e.IsReady))
            return Result(request, parsed, edits, warnings, sw, false, FirstCode(edits), "Nothing was changed: no edit is valid.", null);

        cancellationToken.ThrowIfCancellationRequested();
        var (committed, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Edit Filled Regions", () =>
            RunAll(doc, edits, parsed.Atomic, cancellationToken));

        if (!committed)
        {
            foreach (var edit in edits.Where(e => e.IsReady))
            {
                edit.NewId = null;
                edit.LostAnnotations.Clear();
                edit.Status = edit.Status == RegionStatus.Edited ? RegionStatus.RolledBack : RegionStatus.NotAttempted;
            }
            warnings.AddRange(diagnostics.ToErrorLines());
            return Result(request, parsed, edits, warnings, sw, false, RegionCodes.RevitRefused,
                "The transaction did not commit, so nothing was changed.", diagnostics);
        }

        warnings.AddRange(diagnostics.FailureMessages);
        var edited = edits.Count(e => e.Status == RegionStatus.Edited);
        var failed = edits.Count(e => e.Status == RegionStatus.Failed);
        var replaced = edits.Count(e => e.NewId != null);
        return Result(request, parsed, edits, warnings, sw, edited > 0, edited > 0 ? null : FirstCode(edits),
            $"Edited {edited} of {edits.Count} filled region(s)" +
            (replaced > 0 ? $", {replaced} replaced with a new id (see idMap)" : string.Empty) +
            (failed > 0 ? $"; Revit refused {failed}" : string.Empty) +
            (invalid > 0 ? $"; {invalid} invalid" : string.Empty) + ". One Undo reverts it.", diagnostics);
    }

    private static void RunAll(Document doc, List<ResolvedEdit> edits, bool atomic, CancellationToken cancellationToken)
    {
        foreach (var edit in edits.Where(e => e.IsReady))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                if (edit.Spec.Reshapes) Reshape(doc, edit);
                else ChangeInPlace(doc, edit);
                sub.Commit();
                edit.Status = RegionStatus.Edited;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                edit.Status = RegionStatus.Failed;
                edit.Code ??= RegionCodes.RevitRefused;
                edit.Reason = ex.Message;
                edit.NewId = null;
                edit.LostAnnotations.Clear();
                if (atomic) throw new EditBatchAbortedException($"atomic=true: the edit of region {edit.Spec.Id} failed ({ex.Message}), so the whole batch was undone.");
            }
        }
    }

    private static void ChangeInPlace(Document doc, ResolvedEdit edit)
    {
        var region = edit.Region!;
        if (edit.NewType != null && edit.NewType.Id != region.GetTypeId()) region.ChangeTypeId(edit.NewType.Id);
        if (edit.LineStyleId != null) region.SetLineStyleId(edit.LineStyleId);
        ApplyParameters(doc, edit, region);
    }

    private static void Reshape(Document doc, ResolvedEdit edit)
    {
        var old = edit.Region!;
        var typeId = edit.NewType?.Id ?? old.GetTypeId();
        var loops = FilledRegionSupport.ToCurveLoops(edit.Shape!.Validation.Loops, edit.Frame!);
        var replacement = FilledRegion.Create(doc, typeId, old.OwnerViewId, loops);

        foreach (var name in FilledRegionSupport.CopyParameters(old, replacement))
            edit.Warnings.Add($"Parameter '{name}' could not be copied to the new region.");

        var style = edit.LineStyleId ?? FilledRegionSupport.BoundaryLineStyle(doc, old, out var mixed);
        if (edit.LineStyleId == null && style == null)
            edit.Warnings.Add("The old boundary's line style could not be read as one style; the new region has the default.");
        if (style != null && FilledRegion.IsValidLineStyleIdForFilledRegion(doc, style)) replacement.SetLineStyleId(style);

        try
        {
            replacement.Pinned = old.Pinned;
            if (edit.View != null) edit.View.SetElementOverrides(replacement.Id, edit.View.GetElementOverrides(old.Id));
        }
        catch (Exception ex)
        {
            edit.Warnings.Add($"Pin state or graphic overrides were not carried over: {ex.Message}");
        }

        ApplyParameters(doc, edit, replacement);

        var deleted = new HashSet<long>(doc.Delete(old.Id).Select(id => id.Value));
        edit.LostAnnotations.AddRange(edit.Annotations.Where(a => deleted.Contains(a.Id)).Select(a => a.Id));
        edit.NewId = replacement.Id.Value;
        edit.Warnings.Add("The new region is on top of the view's draw order; the old region's place in it is not kept.");
    }

    private static void ApplyParameters(Document doc, ResolvedEdit edit, Element target)
    {
        foreach (var pair in edit.Spec.Parameters)
        {
            var problem = FilledRegionSupport.SetParameter(doc, target, pair.Key, pair.Value);
            if (problem == null) continue;
            edit.Code = RegionCodes.ParameterNotSettable;
            throw new InvalidOperationException(problem);
        }
    }

    // ── Shaping ──────────────────────────────────────────────────────────────

    private static McpToolResult Result(
        McpToolRequest request, RegionEditRequest parsed, List<ResolvedEdit> edits, List<string> warnings, Stopwatch sw,
        bool success, string? status, string message, TransactionDiagnostics? diagnostics)
    {
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = success,
            Status = status,
            Message = message,
            Data = new
            {
                total = edits.Count,
                edited = edits.Count(e => e.Status == RegionStatus.Edited),
                failed = edits.Count(e => e.Status == RegionStatus.Failed),
                invalid = edits.Count(e => e.Status == RegionStatus.Invalid),
                atomic = parsed.Atomic,
                idMap = edits.Where(e => e.NewId != null).Select(e => new { oldId = e.Spec.Id, newId = e.NewId!.Value }).ToList(),
                edits = edits.Select(e => Describe(e, applied: true)).ToList(),
                transaction = diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(ResolvedEdit edit, bool applied)
    {
        var validation = edit.Shape?.Validation;
        var reshape = edit.Spec.Reshapes && validation is { IsValid: true };
        return new
        {
            index = edit.Spec.Index,
            id = edit.Spec.Id,
            status = edit.Status,
            code = edit.Code,
            reason = edit.Reason,
            keepsId = !edit.Spec.Reshapes,
            newId = applied ? edit.NewId : null,
            type = edit.NewType == null ? null : new { fromId = edit.CurrentType?.Id.Value, from = edit.CurrentType?.Name, toId = edit.NewType.Id.Value, to = edit.NewType.Name },
            lineStyleId = edit.LineStyleId?.Value,
            parameters = edit.ParameterChanges.Count > 0 ? edit.ParameterChanges : null,
            reshape = !reshape ? null : new
            {
                replacesElement = true,
                outlines = validation!.OutlineCount,
                holes = validation.HoleCount,
                areaM2 = Math.Round(validation.AreaMm2 / 1e6, 3),
                extentUv = FilledRegionSupport.ExtentOf(validation.Loops, edit.Frame!),
                attachedAnnotations = edit.Annotations.Select(a => new { id = a.Id, kind = a.Kind }).ToList(),
                lostAnnotations = applied ? edit.LostAnnotations : null,
                sketchEditingSupported = applied ? null : edit.SketchEditingSupported
            },
            warnings = edit.Warnings.Count > 0 ? edit.Warnings : null
        };
    }

    private static string? FirstCode(IEnumerable<ResolvedEdit> edits) => edits.Select(e => e.Code).FirstOrDefault(c => c != null);

    /// <summary>Thrown to abandon an atomic batch: escaping the transaction body is what rolls it back.</summary>
    private sealed class EditBatchAbortedException : Exception
    {
        public EditBatchAbortedException(string message) : base(message)
        {
        }
    }
}

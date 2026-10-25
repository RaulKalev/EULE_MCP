using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documentation.Views;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Views;

/// <summary>revit_preview_create_views (#104): resolves every spec and, by default, tries the creation in a rolled-back transaction.</summary>
public sealed class PreviewCreateViewsTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_views";

    public string Description =>
        "Previews creating views from specs, without changing the model. Same arguments as revit_create_views. " +
        "Resolves every reference (level, view family type, template, scope box, parent view, source legend, area " +
        "scheme, sheet), fans plans out per level, computes the unique names and the section box (in mm), and reports " +
        "per view willCreate | skipped | invalid with a status code and reason. dryRun=true (default) also asks Revit " +
        "to create everything in a transaction that is rolled back, so refusals show up here.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateViewsExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_create_views (#104): plans, sections, callouts, 3D views, drafting views and legends from specs.</summary>
public sealed class CreateViewsTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_views";

    public string Description =>
        "Creates views from a list of specs (max 100 views per call). Requires approval. kind: floorPlan | ceilingPlan | " +
        "structuralPlan | areaPlan (level or levels[] / \"all\", one view per level with {level} and {n} in the name; " +
        "viewRange; cropTo) | section (from/to in mm, depthMm, heightMm/bottomMm, flip) | callout (parentView, from/to " +
        "corners or aroundElements/aroundRoom with paddingMm, referenceView for a reference callout) | threeD (box or " +
        "aroundElements/aroundRoom + paddingMm as section box, isometric, orientation) | drafting | legend (duplicates " +
        "sourceLegend: Revit cannot create legends). Common: name, viewFamilyType, template, scale, detailLevel, " +
        "discipline, phase, scopeBox, placeOnSheet {sheet, xMm, yMm}. ifNameExists: suffix (default) | skip | fail. " +
        "One transaction, one undo; atomic=true undoes everything on any failure. Run revit_preview_create_views first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Views;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(CreateViewsExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        var parsed = ViewCreateSpecParser.Parse(request.Arguments, out _);
        if (parsed == null) return null;
        var planned = ViewCreatePlanner.Plan(parsed, ViewReferenceResolver.Levels(target.Document),
            ViewReferenceResolver.ViewNames(target.Document), out var error);
        if (error != null) return null;
        foreach (var plan in planned) ViewReferenceResolver.Resolve(target.Document, plan);
        return ViewCreatePlanner.SummarizePlanned(planned, parsed);
    }
}

internal static class CreateViewsExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;
        var doc = target.Document;

        var parsed = ViewCreateSpecParser.Parse(request.Arguments, out var parseError);
        if (parsed == null) return Fail(request, parseError!, ViewCreateCodes.InvalidSpec);

        var planned = ViewCreatePlanner.Plan(parsed, ViewReferenceResolver.Levels(doc), ViewReferenceResolver.ViewNames(doc), out var planError);
        if (planError != null) return Fail(request, planError, ViewCreateCodes.InvalidSpec);

        var resolved = new List<ResolvedView>();
        foreach (var plan in planned)
        {
            cancellationToken.ThrowIfCancellationRequested();
            resolved.Add(ViewReferenceResolver.Resolve(doc, plan));
        }

        var warnings = new List<string>();
        return apply
            ? Create(request, doc, parsed, resolved, warnings, sw, cancellationToken)
            : Preview(request, doc, parsed, resolved, warnings, sw, cancellationToken);
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private static McpToolResult Preview(
        McpToolRequest request, Document doc, ViewCreateRequest parsed, List<ResolvedView> views, List<string> warnings,
        Stopwatch sw, CancellationToken cancellationToken)
    {
        var dryRun = ToolArguments.GetBool(request.Arguments, "dryRun", true);
        string? trialNote = null;
        var trialRan = false;
        var ready = views.Count(v => v.Plan.IsReady);

        if (!dryRun) trialNote = "dryRun=false: only the static checks ran; Revit was not asked to try the creation.";
        else if (ready == 0) trialNote = "No trial run: no view is ready to be created.";
        else if (doc.IsReadOnly || doc.IsModifiable) trialNote = "No trial run: the document is read-only or has an open transaction. Only the static checks ran.";
        else
        {
            using var transaction = new Transaction(doc, "Revit MCP - Create Views (preview, rolled back)");
            try
            {
                RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
                if (transaction.Start() == TransactionStatus.Started)
                {
                    trialRan = true;
                    RunAll(doc, views, atomic: false, cancellationToken);
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
            trialNote ??= "Trial run rolled back: the model is unchanged. View ids and names below are from the trial and are " +
                          "assigned again on the real run.";
        }

        var invalid = views.Count(v => v.Plan.Status == ViewCreateStatus.Invalid);
        var skipped = views.Count(v => v.Plan.Status == ViewCreateStatus.Skipped);
        var trialFailed = views.Count(v => v.Status == ViewCreateStatus.Failed);
        var willCreate = views.Count(v => v.Plan.IsReady && v.Status != ViewCreateStatus.Failed);
        var rejected = parsed.Atomic && (invalid > 0 || trialFailed > 0);

        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Status = willCreate == 0 ? FirstCode(views) : null,
            Message = $"Preview: {willCreate} of {views.Count} view(s) would be created" +
                      (invalid > 0 ? $", {invalid} invalid" : string.Empty) +
                      (skipped > 0 ? $", {skipped} skipped" : string.Empty) +
                      (trialFailed > 0 ? $", {trialFailed} refused by Revit in the trial run" : string.Empty) + "." +
                      (rejected ? " With atomic=true the request would be rejected as a whole." : string.Empty),
            Data = new
            {
                total = views.Count,
                willCreate,
                invalid,
                skipped,
                trialFailed,
                atomic = parsed.Atomic,
                ifNameExists = parsed.IfNameExists.ToString().ToLowerInvariant(),
                trialRun = trialRan,
                trialNote,
                summary = ViewCreatePlanner.SummarizePlanned(views.Select(v => v.Plan).ToList(), parsed),
                views = views.Select(v => Describe(v, applied: false)).ToList()
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    // ── Create ───────────────────────────────────────────────────────────────

    private static McpToolResult Create(
        McpToolRequest request, Document doc, ViewCreateRequest parsed, List<ResolvedView> views, List<string> warnings,
        Stopwatch sw, CancellationToken cancellationToken)
    {
        var invalid = views.Count(v => v.Plan.Status == ViewCreateStatus.Invalid);
        if (parsed.Atomic && invalid > 0)
        {
            foreach (var view in views.Where(v => v.Plan.IsReady)) view.Status = ViewCreateStatus.NotAttempted;
            return Result(request, parsed, views, warnings, sw, success: false, FirstCode(views),
                $"Nothing was created: atomic=true and {invalid} of {views.Count} view(s) are invalid. Fix them (see each view's " +
                "code and reason), or pass atomic=false to create the rest.", null);
        }

        if (!views.Any(v => v.Plan.IsReady))
        {
            var allSkipped = views.All(v => v.Plan.Status == ViewCreateStatus.Skipped);
            return Result(request, parsed, views, warnings, sw, success: allSkipped, allSkipped ? null : FirstCode(views),
                allSkipped ? "Nothing to create: every view already exists (ifNameExists=skip)." : "Nothing was created: no view spec is valid.", null);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Create Views", () =>
            RunAll(doc, views, parsed.Atomic, cancellationToken));

        if (!txSuccess)
        {
            foreach (var view in views.Where(v => v.Status == ViewCreateStatus.Created))
            {
                view.Status = ViewCreateStatus.RolledBack;
                view.CreatedId = null;
                view.ViewportId = null;
            }
            foreach (var view in views.Where(v => v.Plan.IsReady && v.Status == null)) view.Status = ViewCreateStatus.NotAttempted;
            warnings.AddRange(diagnostics.ToErrorLines());
            return Result(request, parsed, views, warnings, sw, success: false, ViewCreateCodes.RevitRefused,
                parsed.Atomic
                    ? "Rolled back: atomic=true and Revit refused a view, so nothing was created. The model is unchanged."
                    : "The transaction did not commit, so no view was created. The model is unchanged.", diagnostics);
        }

        warnings.AddRange(diagnostics.FailureMessages);
        foreach (var view in views.Where(v => v.Status == ViewCreateStatus.Created && v.Created != null))
        {
            // Names and ids after commit (Revit may have adjusted a name).
            try { view.CreatedName = view.Created!.Name; }
            catch { }
        }

        var created = views.Count(v => v.Status == ViewCreateStatus.Created);
        var failed = views.Count(v => v.Status == ViewCreateStatus.Failed);
        var placed = views.Count(v => v.ViewportId != null);
        var skipped = views.Count(v => v.Plan.Status == ViewCreateStatus.Skipped);
        return Result(request, parsed, views, warnings, sw, success: created > 0, created > 0 ? null : FirstCode(views),
            $"Created {created} of {views.Count} view(s)" +
            (placed > 0 ? $", placed {placed} on sheets" : string.Empty) +
            (failed > 0 ? $"; Revit refused {failed}" : string.Empty) +
            (invalid > 0 ? $"; {invalid} invalid" : string.Empty) +
            (skipped > 0 ? $"; {skipped} skipped" : string.Empty) + ". One Undo removes them.", diagnostics);
    }

    /// <summary>
    /// Creates every ready view inside the open transaction, each in its own SubTransaction so a refusal costs only
    /// that view. atomic=true abandons the whole batch on the first refusal by escaping the transaction body.
    /// </summary>
    private static void RunAll(Document doc, List<ResolvedView> views, bool atomic, CancellationToken cancellationToken)
    {
        foreach (var view in views.Where(v => v.Plan.IsReady))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var created = ViewCreator.Create(doc, view);
                if (created != null)
                {
                    view.CreatedId = created.Id.Value;
                    view.CreatedName = created.Name;
                    view.CreatedViewType = created.ViewType.ToString();
                }
                sub.Commit();
                view.Status = ViewCreateStatus.Created;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (sub.HasStarted() && !sub.HasEnded()) sub.RollBack();
                view.Status = ViewCreateStatus.Failed;
                view.Error = ex.Message;
                view.Created = null;
                view.CreatedId = null;
                view.ViewportId = null;
                if (atomic) throw new ViewBatchAbortedException(view.Plan.SpecIndex, ex.Message);
            }
        }
    }

    // ── Shaping ──────────────────────────────────────────────────────────────

    private static McpToolResult Result(
        McpToolRequest request, ViewCreateRequest parsed, List<ResolvedView> views, List<string> warnings, Stopwatch sw,
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
                total = views.Count,
                created = views.Count(v => v.Status == ViewCreateStatus.Created),
                failed = views.Count(v => v.Status == ViewCreateStatus.Failed),
                invalid = views.Count(v => v.Plan.Status == ViewCreateStatus.Invalid),
                skipped = views.Count(v => v.Plan.Status == ViewCreateStatus.Skipped),
                atomic = parsed.Atomic,
                newViewIds = views.Where(v => v.CreatedId != null).Select(v => v.CreatedId!.Value).ToList(),
                views = views.Select(v => Describe(v, applied: true)).ToList(),
                transaction = diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object Describe(ResolvedView view, bool applied)
    {
        var plan = view.Plan;
        var status = plan.Status != ViewCreateStatus.WillCreate
            ? plan.Status
            : view.Status == ViewCreateStatus.Failed ? ViewCreateStatus.Failed
            : applied ? view.Status ?? ViewCreateStatus.NotAttempted
            : ViewCreateStatus.WillCreate;
        var allWarnings = view.Warnings.Concat(plan.Notes).ToList();
        return new
        {
            index = plan.SpecIndex,
            fanOut = plan.FanOutIndex,
            kind = plan.Kind,
            status,
            code = plan.Code ?? (view.Status == ViewCreateStatus.Failed ? ViewCreateCodes.RevitRefused : null),
            reason = plan.Reason ?? view.Error,
            name = view.CreatedName ?? plan.Name,
            requestedName = plan.RequestedName != plan.Name ? plan.RequestedName : null,
            viewId = applied ? view.CreatedId : null,
            trialViewId = applied ? null : view.CreatedId,
            viewType = view.CreatedViewType,
            level = plan.Level == null ? null : new { id = plan.Level.Id, name = plan.Level.Name, elevationMm = Math.Round(plan.Level.ElevationMm, 1) },
            viewFamilyType = view.Type == null ? null : new { id = view.Type.Id.Value, name = view.Type.Name, family = view.Type.ViewFamily.ToString(), how = view.TypeHow },
            template = view.Template == null ? null : new { id = view.Template.Id.Value, name = view.Template.Name },
            templateApplied = view.Status == ViewCreateStatus.Created ? view.TemplateApplied : (bool?)null,
            areaScheme = view.AreaScheme?.Name,
            scopeBox = view.ScopeBox == null ? null : new { id = view.ScopeBox.Id.Value, name = view.ScopeBox.Name },
            parentView = view.Parent == null ? null : new { id = view.Parent.Id.Value, name = view.Parent.Name, viewType = view.Parent.ViewType.ToString() },
            referenceView = view.ReferenceView == null ? null : new { id = view.ReferenceView.Id.Value, name = view.ReferenceView.Name },
            sourceLegend = view.SourceLegend == null ? null : new { id = view.SourceLegend.Id.Value, name = view.SourceLegend.Name },
            sectionBox = plan.Section?.Describe(),
            extentBoxMm = view.ExtentBox?.ToMm(),
            calloutCornersMm = view.CalloutCorners == null ? null : new[] { view.CalloutCorners.Value.First.ToMm(), view.CalloutCorners.Value.Second.ToMm() },
            orientation = view.Orientation?.Describe(),
            actual = view.Actual,
            sheet = view.Sheet == null ? null : new { id = view.Sheet.Id.Value, number = view.Sheet.SheetNumber, name = view.Sheet.Name },
            sheetId = view.ViewportId != null ? view.Sheet?.Id.Value : null,
            viewportId = applied ? view.ViewportId : null,
            warnings = allWarnings.Count > 0 ? allWarnings : null
        };
    }

    private static string? FirstCode(IEnumerable<ResolvedView> views) =>
        views.Select(v => v.Plan.Code).FirstOrDefault(c => c != null && c != ViewCreateCodes.ViewNameExists)
        ?? views.Select(v => v.Plan.Code).FirstOrDefault(c => c != null);

    private static McpToolResult Fail(McpToolRequest request, string message, string status) =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };

    /// <summary>Thrown to abandon an atomic batch: escaping the transaction body is what rolls it back.</summary>
    private sealed class ViewBatchAbortedException : Exception
    {
        public ViewBatchAbortedException(int specIndex, string reason)
            : base($"atomic=true: view spec {specIndex} could not be created ({reason}), so the whole batch was undone.")
        {
        }
    }
}

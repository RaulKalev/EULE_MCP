using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>revit_preview_place_on_face: which face each placement would use, plus a rolled-back trial.</summary>
public class PreviewPlaceOnFaceTool : IRevitMcpTool
{
    public string Name => "revit_preview_place_on_face";

    public string Description =>
        "Previews placing a face-based family on wall, ceiling or floor faces, without changing the model. Same " +
        "arguments as revit_place_on_face. For each point the tool searches for the face by ray casting in a 3D view " +
        "— in the host model and in linked RVT/IFC models — and reports the face it would use (element, category, " +
        "host or link, surface kind, normal), where the family would land, how far that is from the point, the " +
        "family's orientation, and what else was in reach. With dryRun (default true) Revit actually tries the " +
        "placement in a transaction that is rolled back, so a face Revit refuses to host on is reported here.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PlaceOnFaceExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_place_on_face: hosts face-based families on faces found from points.</summary>
public class PlaceOnFaceTool : IRevitMcpTool
{
    public string Name => "revit_place_on_face";

    public string Description =>
        "Places instances of a face-based (or work-plane-based) family hosted on faces. Requires approval. " +
        "Required: the family type (typeId, or familyName and/or typeName) and placements — a JSON array of " +
        "{x, y, z, mountOn, rotationDegrees} in mm. For each point the face is found by ray casting in a 3D view, in " +
        "the host model and in linked RVT/IFC models: mountOn=wall searches horizontally all round, ceiling searches " +
        "up, floor searches down, nearest searches all of them; or give an exact direction with dx, dy, dz instead. " +
        "The family is hosted on the nearest matching face within maxDistanceMm (default 1500), at the foot of the " +
        "perpendicular from the point. On a wall it stands upright; rotationDegrees turns it about the face normal. " +
        "Optional: mountOn for the whole request, includeHost, includeLinks, linkInstanceIds, hostCategories, " +
        "offsetFromHostMm, angleToleranceDegrees, searchViewId, atomic (default true — any failure undoes the batch). " +
        "The response reports each new element id, the face and host it is on, and where it ended up. Level-based " +
        "and wall-hosted families are placed with revit_place_family_instances. Run revit_preview_place_on_face first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(PlaceOnFaceExecutor.Execute(uiapp, request, apply: true, cancellationToken));
}

internal static class PlaceOnFaceExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var uidoc = uiapp.ActiveUIDocument;
        var doc = uidoc?.Document;
        if (doc == null)
            return Fail(request, "No active document.");

        var arguments = request.Arguments;
        var (symbol, symbolError) = FamilyInstancePlacer.ResolveSymbol(
            doc,
            ToolArguments.GetLong(arguments, "typeId"),
            ToolArguments.GetString(arguments, "familyName"),
            ToolArguments.GetString(arguments, "typeName"));
        if (symbol == null)
            return Fail(request, symbolError!);

        var placementType = symbol.Family.FamilyPlacementType;
        if (placementType != FamilyPlacementType.WorkPlaneBased)
            return Fail(request, WrongFamilyKind(symbol, placementType));

        arguments.TryGetValue("placements", out var rawPlacements);
        var requests = FacePlacementMath.Parse(rawPlacements, ToolArguments.GetString(arguments, "mountOn"), out var parseError);
        if (requests == null)
            return Fail(request, parseError!);

        var warnings = new List<string>();
        var view = AlignmentRequest.ResolveSearchView(uidoc!, arguments, warnings, out var viewError);
        if (view == null)
            return Fail(request, viewError!);

        var requestedDistance = ToolArguments.GetDouble(arguments, "maxDistanceMm", FacePlacementMath.DefaultMaxDistanceMm);
        var maxDistanceMm = FacePlacementMath.ClampMaxDistance(requestedDistance);
        if (Math.Abs(maxDistanceMm - requestedDistance) > 1e-9)
            warnings.Add($"maxDistanceMm {requestedDistance} is outside {FacePlacementMath.MinMaxDistanceMm}-{FacePlacementMath.MaxMaxDistanceMm}; using {maxDistanceMm}.");

        var includeHost = ToolArguments.GetBool(arguments, "includeHost", true);
        var includeLinks = ToolArguments.GetBool(arguments, "includeLinks", true);
        if (!includeHost && !includeLinks)
            return Fail(request, "includeHost and includeLinks are both false — there is nowhere to look for a face.");

        var options = new FacePlacementOptions
        {
            MaxDistanceFt = AlignmentRequest.MmToFt(maxDistanceMm),
            IncludeHost = includeHost,
            IncludeLinks = includeLinks,
            LinkInstanceIds = new HashSet<long>(ToolArguments.GetLongArray(arguments, "linkInstanceIds")),
            HostCategories = new HashSet<string>(
                ToolArguments.GetStringArray(arguments, "hostCategories").Where(c => !string.IsNullOrWhiteSpace(c)),
                StringComparer.OrdinalIgnoreCase),
            AngleToleranceDegrees = AlignmentMath.ClampAngleTolerance(
                ToolArguments.GetDouble(arguments, "angleToleranceDegrees", 10.0)),
            HasOffsetFromHost = arguments.ContainsKey("offsetFromHostMm") && arguments["offsetFromHostMm"] != null,
            OffsetFromHostFt = AlignmentRequest.MmToFt(ToolArguments.GetDouble(arguments, "offsetFromHostMm", 0.0))
        };

        var atomic = ToolArguments.GetBool(arguments, "atomic", true);
        var service = new FacePlacementService(doc, view, options);
        var plans = service.BuildPlans(requests, cancellationToken);
        var blocked = plans.Count(plan => plan.IsFailure);

        var context = new RunContext(request, doc, symbol, view, service, plans, atomic, maxDistanceMm, warnings, sw);
        return apply ? Place(context, blocked, cancellationToken) : Preview(context, blocked, cancellationToken);
    }

    private sealed record RunContext(
        McpToolRequest Request, Document Doc, FamilySymbol Symbol, View3D View, FacePlacementService Service,
        List<FacePlacementPlan> Plans, bool Atomic, double MaxDistanceMm, List<string> Warnings, Stopwatch Stopwatch);

    private static string WrongFamilyKind(FamilySymbol symbol, FamilyPlacementType placementType)
    {
        var name = $"'{symbol.Family.Name} : {symbol.Name}'";
        return placementType switch
        {
            FamilyPlacementType.OneLevelBasedHosted =>
                $"{name} is hosted by a specific kind of element (a wall, ceiling, floor or roof), not by a face. Place it with " +
                "revit_place_family_instances and hostElementId. Such families can only be hosted on elements of this model, not of a link.",
            FamilyPlacementType.OneLevelBased or FamilyPlacementType.TwoLevelsBased =>
                $"{name} is level-based, not face-based — it is not attached to a face. Place it with revit_place_family_instances; " +
                "to put it against a wall or ceiling afterwards use revit_align_elements.",
            FamilyPlacementType.ViewBased =>
                $"{name} is a view-based family (a detail item or annotation). Place it with revit_place_family_instances and viewId.",
            _ => $"{name} has placement type '{placementType}', which is not hosted on a face. This tool places face-based " +
                 "and work-plane-based families."
        };
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private static McpToolResult Preview(RunContext context, int blocked, CancellationToken cancellationToken)
    {
        var dryRun = ToolArguments.GetBool(context.Request.Arguments, "dryRun", true);
        var wouldBeRejected = FacePlacementMath.ShouldRollBack(context.Atomic, blocked);
        string? trialNote;

        if (!dryRun)
            trialNote = "dryRun=false: the faces were found, but Revit was not asked to try the placement.";
        else if (wouldBeRejected)
            trialNote = "No trial run: atomic=true and some points have no usable face, so the request would be rejected as a whole.";
        else if (!context.Plans.Any(plan => plan.CanPlace))
            trialNote = "No trial run: no point has a usable face.";
        else
            trialNote = TrialRun(context, cancellationToken);

        var failures = context.Plans.Count(plan => plan.IsFailure);
        var ready = context.Plans.Count(plan => plan.Status is FacePlacementStatus.Ready or FacePlacementStatus.Placed);

        // The trial instances were rolled back: report that they could be placed, not ids that no longer exist.
        foreach (var plan in context.Plans.Where(plan => plan.Status == FacePlacementStatus.Placed))
        {
            plan.Status = FacePlacementStatus.Ready;
            plan.ElementId = null;
        }

        context.Stopwatch.Stop();
        return new McpToolResult
        {
            RequestId = context.Request.RequestId,
            Success = true,
            Message = $"Preview: {ready} of {context.Plans.Count} placement(s) have a face and would be placed" +
                      $"{(failures > 0 ? $", {failures} would not" : string.Empty)}." +
                      (failures > 0 && context.Atomic ? " With atomic=true the whole request would be rejected." : string.Empty),
            Data = Data(context, trialNote, includeIds: false),
            Warnings = context.Warnings,
            DurationMs = context.Stopwatch.ElapsedMilliseconds
        };
    }

    /// <summary>
    /// Has Revit perform the placements inside a transaction that is always rolled back — the only way
    /// to learn whether it accepts a given face as a host, which for linked and IFC geometry is not
    /// knowable from the face alone.
    /// </summary>
    private static string TrialRun(RunContext context, CancellationToken cancellationToken)
    {
        var doc = context.Doc;
        if (doc.IsReadOnly || doc.IsModifiable)
            return "No trial run: the document is read-only or has an open transaction. Only the face search ran.";

        using var transaction = new Transaction(doc, "Revit MCP - Place On Face (preview, rolled back)");
        try
        {
            RollBackOnErrorPreprocessor.Attach(transaction, new List<string>());
            if (transaction.Start() != TransactionStatus.Started)
                return "No trial run: Revit did not start a transaction. Only the face search ran.";

            try
            {
                Run(context, cancellationToken);
            }
            catch (PlacementBatchAbortedException ex)
            {
                context.Warnings.Add(ex.Message);
                return "The trial run failed: Revit refused a placement. See the per-placement reasons.";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"The trial run could not complete ({ex.GetType().Name}: {ex.Message}). Only the face search is reliable.";
        }
        finally
        {
            if (transaction.GetStatus() == TransactionStatus.Started)
                transaction.RollBack();
        }

        return "Trial run rolled back: the model is unchanged.";
    }

    // ── Place ────────────────────────────────────────────────────────────────

    private static McpToolResult Place(RunContext context, int blocked, CancellationToken cancellationToken)
    {
        var plans = context.Plans;
        foreach (var plan in plans.Where(plan => plan.IsFailure && plan.Reason != null))
            context.Warnings.Add($"Placement {plan.Request.Index + 1}: {plan.Reason}");

        if (FacePlacementMath.ShouldRollBack(context.Atomic, blocked))
        {
            // Rejected before a transaction opens, so the undo stack stays untouched.
            foreach (var plan in plans.Where(plan => plan.CanPlace))
                plan.Status = FacePlacementStatus.NotAttempted;
            return Result(context, success: false,
                $"Nothing was placed: atomic=true and {blocked} of {plans.Count} point(s) have no usable face. " +
                "Fix or drop them, or pass atomic=false to place the rest.", null);
        }

        if (!plans.Any(plan => plan.CanPlace))
            return Result(context, success: false, "Nothing was placed: no point has a usable face.", null);

        cancellationToken.ThrowIfCancellationRequested();
        var (txSuccess, diagnostics) = RevitTransactionRunner.Run(
            context.Doc, "Revit MCP - Place On Face", () => Run(context, cancellationToken));

        if (!txSuccess)
        {
            foreach (var plan in plans.Where(plan => plan.Status == FacePlacementStatus.Placed))
            {
                plan.Status = FacePlacementStatus.RolledBack;
                plan.ElementId = null;
                plan.PlacedHostId = null;
                plan.PlacedPoint = null;
            }
            foreach (var plan in plans.Where(plan => plan.CanPlace && plan.Status == FacePlacementStatus.Ready))
                plan.Status = FacePlacementStatus.NotAttempted;

            context.Warnings.AddRange(diagnostics.ToErrorLines());
            return Result(context, success: false,
                context.Atomic
                    ? "Rolled back: atomic=true and Revit refused a placement, so nothing was created. The model is unchanged."
                    : "The transaction did not commit, so nothing was placed. The model is unchanged.", diagnostics);
        }

        context.Warnings.AddRange(diagnostics.FailureMessages);
        foreach (var plan in plans.Where(plan => plan.Status == FacePlacementStatus.Failed && plan.Reason != null))
            context.Warnings.Add($"Placement {plan.Request.Index + 1} failed: {plan.Reason}");

        var placed = plans.Count(plan => plan.Status == FacePlacementStatus.Placed);
        var failed = plans.Count(plan => plan.IsFailure);
        return Result(context, success: placed > 0,
            $"Placed {placed} of {plans.Count} instance(s) of {context.Symbol.Family.Name} : {context.Symbol.Name}" +
            $"{(failed > 0 ? $"; {failed} could not be placed" : string.Empty)}.", diagnostics);
    }

    /// <summary>
    /// The placements themselves. Must run inside a transaction. atomic=true throws on the first
    /// refusal so the caller's transaction rolls back; atomic=false gives each placement its own
    /// sub-transaction and keeps what succeeds.
    /// </summary>
    private static void Run(RunContext context, CancellationToken cancellationToken)
    {
        if (!context.Symbol.IsActive)
            context.Symbol.Activate();

        var placed = 0;
        foreach (var plan in context.Plans.Where(plan => plan.CanPlace))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (context.Atomic)
            {
                try
                {
                    context.Service.Place(context.Symbol, plan);
                }
                catch (Exception ex)
                {
                    MarkFailed(plan, ex.Message);
                    throw new PlacementBatchAbortedException(plan.Request.Index + 1, ex.Message);
                }

                plan.Status = FacePlacementStatus.Placed;
                placed++;
                continue;
            }

            using var subTransaction = new SubTransaction(context.Doc);
            subTransaction.Start();
            try
            {
                context.Service.Place(context.Symbol, plan);
                subTransaction.Commit();
                plan.Status = FacePlacementStatus.Placed;
                placed++;
            }
            catch (Exception ex)
            {
                if (subTransaction.GetStatus() == TransactionStatus.Started)
                    subTransaction.RollBack();
                MarkFailed(plan, ex.Message);
            }
        }

        if (placed == 0) return;

        // Once, at the end: the host and insertion point are only settled after regeneration.
        context.Doc.Regenerate();
        foreach (var plan in context.Plans.Where(plan => plan.Status == FacePlacementStatus.Placed))
            context.Service.RecordResult(plan);
    }

    private static void MarkFailed(FacePlacementPlan plan, string reason)
    {
        plan.Status = FacePlacementStatus.Failed;
        plan.Reason = reason;
        plan.IsFailure = true;
        plan.ElementId = null;
    }

    // ── Shaping ──────────────────────────────────────────────────────────────

    private static McpToolResult Result(RunContext context, bool success, string message, TransactionDiagnostics? diagnostics)
    {
        context.Stopwatch.Stop();
        return new McpToolResult
        {
            RequestId = context.Request.RequestId,
            Success = success,
            Message = message,
            Data = Data(context, null, includeIds: true, diagnostics),
            Warnings = context.Warnings,
            DurationMs = context.Stopwatch.ElapsedMilliseconds
        };
    }

    private static object Data(RunContext context, string? trialNote, bool includeIds, TransactionDiagnostics? diagnostics = null) => new
    {
        familyName = context.Symbol.Family.Name,
        typeName = context.Symbol.Name,
        typeId = context.Symbol.Id.Value,
        total = context.Plans.Count,
        placed = context.Plans.Count(plan => plan.Status == FacePlacementStatus.Placed),
        canPlace = context.Plans.Count(plan => plan.CanPlace && !plan.IsFailure),
        failed = context.Plans.Count(plan => plan.IsFailure),
        atomic = context.Atomic,
        maxDistanceMm = context.MaxDistanceMm,
        searchViewId = context.View.Id.Value,
        searchViewName = context.View.Name,
        trialNote,
        createdElementIds = includeIds
            ? context.Plans.Where(plan => plan.ElementId.HasValue).Select(plan => plan.ElementId!.Value).ToList()
            : null,
        placements = context.Plans.Select(plan => DescribePlan(plan, includeIds)).ToList(),
        transaction = diagnostics
    };

    private static object DescribePlan(FacePlacementPlan plan, bool includeIds) => new
    {
        index = plan.Request.Index + 1,
        requestedPointMm = Point(plan.Request.PointMm),
        mountOn = plan.Request.Direction.HasValue ? null : plan.Request.MountOn,
        direction = plan.Request.Direction.HasValue ? Unit(plan.Request.Direction.Value) : null,
        rotationDegrees = plan.Request.RotationDegrees,
        status = plan.Status,
        reason = plan.Reason,
        face = plan.Target == null ? null : DescribeTarget(plan.Target),
        referenceDirection = plan.ReferenceDirection.HasValue ? Unit(plan.ReferenceDirection.Value) : null,
        elementId = includeIds ? plan.ElementId : null,
        placedPointMm = includeIds && plan.PlacedPoint != null ? PointFt(plan.PlacedPoint) : null,
        placedHostId = includeIds ? plan.PlacedHostId : null,
        placedVia = plan.PlacedVia,
        alternates = plan.Alternates.Count > 0 ? plan.Alternates.Select(DescribeTarget).ToList() : null,
        notes = plan.Notes.Count > 0 ? plan.Notes : null
    };

    private static object DescribeTarget(FaceTarget target) => new
    {
        model = target.Model,
        linkInstanceId = target.LinkInstanceId,
        hostElementId = target.HostElementId,
        hostCategory = target.HostCategory,
        hostName = target.HostName,
        surface = target.SurfaceKind,
        normal = Unit(target.Normal),
        normalSource = target.NormalSource,
        locationMm = PointFt(target.Location),
        distanceMm = Math.Round(AlignmentRequest.FtToMm(target.DistanceFt), 1),
        perpendicular = target.LocationIsPerpendicular
    };

    private static object Point(Vec3 point) => new
    {
        x = Math.Round(point.X, 2),
        y = Math.Round(point.Y, 2),
        z = Math.Round(point.Z, 2)
    };

    private static object PointFt(XYZ point) => new
    {
        x = Math.Round(AlignmentRequest.FtToMm(point.X), 2),
        y = Math.Round(AlignmentRequest.FtToMm(point.Y), 2),
        z = Math.Round(AlignmentRequest.FtToMm(point.Z), 2)
    };

    private static object Unit(Vec3 vector) => new
    {
        x = Math.Round(vector.X, 6),
        y = Math.Round(vector.Y, 6),
        z = Math.Round(vector.Z, 6)
    };

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };

    /// <summary>Thrown to abandon an atomic batch. Escaping the transaction body is what rolls it back.</summary>
    private sealed class PlacementBatchAbortedException : Exception
    {
        public PlacementBatchAbortedException(int placement, string reason)
            : base($"atomic=true: placement {placement} was refused ({reason}), so the whole batch was rolled back.")
        {
        }
    }
}

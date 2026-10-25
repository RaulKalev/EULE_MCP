using System.Diagnostics;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Mep;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Tools.Documents;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Mep;

/// <summary>revit_preview_create_mep_run (#106): what a tray or conduit run would be, without changing the model.</summary>
public sealed class PreviewCreateMepRunTool : IRevitMcpTool
{
    public string Name => "revit_preview_create_mep_run";

    public string Description =>
        "Previews a cable tray or conduit run along a polyline WITHOUT changing the model. Same arguments as revit_create_mep_run. " +
        "Returns the normalized polyline (collinear points and segments shorter than minSegmentMm merged, zero-length segments " +
        "dropped), the segments with lengths, the bends with angles and the elbows that will be attempted, the type's fitting " +
        "status (routing_preference_missing names the missing kinds), open connectors found within snapMm of each run end, the " +
        "level and the total length (mm and m).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(MepRunExecutor.Execute(uiapp, request, apply: false, cancellationToken));
}

/// <summary>revit_create_mep_run (#106): draws a cable tray or conduit run along a polyline, with elbows and end connections.</summary>
public sealed class CreateMepRunTool : IRevitMcpTool, IApprovalSummaryTool
{
    public string Name => "revit_create_mep_run";

    public string Description =>
        "Draws a cable tray or conduit run along a polyline (requires approval). One CableTray/Conduit per straight segment, an " +
        "elbow (NewElbowFitting) at every bend when fittings=auto, optional joins to existing open tray/conduit ends at the run " +
        "start and end. Required: kind (cableTray|conduit), type (name or id, see revit_list_mep_curve_types), points (JSON array " +
        "of >= 2 {x, y, z} in mm, model coordinates; z may be left out when level and offsetMm are given: z = level elevation + " +
        "offsetMm). Optional: level (default: nearest the first point), widthMm/heightMm (tray) or diameterMm (conduit, a standard " +
        "size of the type), fittings (auto|none), connectStart/connectEnd ('auto' = nearest open end of the same kind within " +
        "snapMm, default 50; or an element id), minSegmentMm (default 100), workset, atomic (default true: a refused segment undoes " +
        "the run). A failed elbow never removes the segments: that bend reports fitting_failed with Revit's reason. Every element " +
        "carries the run id (Extensible Storage RevitMCP_MepRun) for revit_get_mep_run_summary runId=. One transaction, one undo. " +
        "Run revit_preview_create_mep_run first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(MepRunExecutor.Execute(uiapp, request, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request) => MepRunExecutor.ApprovalSummary(uiapp, request);
}

/// <summary>A run request resolved against the live model.</summary>
internal sealed class MepRunContext
{
    public Document Doc { get; init; } = null!;
    public MepRunSpec Spec { get; init; } = null!;
    public MEPCurveType Type { get; init; } = null!;
    public Level Level { get; init; } = null!;
    public bool LevelDefaulted { get; init; }
    public MepRunPlan Plan { get; init; } = null!;
    public MepFittingAvailability Fittings { get; init; } = null!;
    public List<string> MissingFittings { get; init; } = new();
    public Workset? Workset { get; init; }
    public string? Standard { get; init; }
    public List<string> Warnings { get; } = new();
}

internal static class MepRunExecutor
{
    public static McpToolResult Execute(UIApplication uiapp, McpToolRequest request, bool apply, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return documentFailure!;

        var failure = Resolve(target.Document, request, out var context);
        if (failure != null) return failure;

        return apply
            ? Create(request, context!, sw, cancellationToken)
            : Preview(request, context!, sw);
    }

    /// <summary>The approval text, built from the live model: type name, segments, length, elbows and end connections.</summary>
    public static string? ApprovalSummary(UIApplication uiapp, McpToolRequest request)
    {
        var target = ToolDocument.Resolve(uiapp, request, out _);
        if (target == null) return null;
        if (Resolve(target.Document, request, out var context) != null || context == null) return null;
        return MepApprovalText.CreateRun(context.Spec.Kind, context.Type.Name, context.Plan.Segments.Count, context.Plan.TotalLengthMm,
            context.Plan.ElbowCount, context.Spec.ConnectStart, context.Spec.ConnectEnd, context.Spec.SnapMm, context.Level.Name);
    }

    // ── Resolution ───────────────────────────────────────────────────────────

    private static McpToolResult? Resolve(Document doc, McpToolRequest request, out MepRunContext? context)
    {
        context = null;
        var errors = new List<string>();
        var spec = MepRunSpec.Parse(request.Arguments, errors, out var status);
        if (spec == null)
            return Fail(request, status ?? MepStatus.ValidationFailed, errors[0], errors);

        var type = MepTypeCatalog.Resolve(doc, spec.Kind, spec.Type, out var typeError, out var available);
        if (type == null)
            return Fail(request, MepStatus.TypeNotFound, typeError!, null, new { kind = spec.Kind, availableTypes = available });

        // Level: named, or the one nearest the first point.
        var levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.ProjectElevation).ToList();
        if (levels.Count == 0)
            return Fail(request, MepStatus.ValidationFailed, "The document has no levels: a tray or conduit needs a reference level.");
        Level? level;
        var levelDefaulted = false;
        if (spec.Level.Length > 0)
        {
            level = long.TryParse(spec.Level, NumberStyles.Integer, CultureInfo.InvariantCulture, out var levelId)
                ? levels.FirstOrDefault(l => l.Id.Value == levelId)
                : levels.FirstOrDefault(l => string.Equals(l.Name, spec.Level, StringComparison.OrdinalIgnoreCase));
            if (level == null)
                return Fail(request, MepStatus.ValidationFailed,
                    $"No level '{spec.Level}'. Levels: {string.Join(", ", levels.Select(l => l.Name))}.");
        }
        else
        {
            var firstZFt = PlacementHelpers.MmToFt(spec.Points[0].Z ?? 0);
            level = levels.OrderBy(l => Math.Abs(l.ProjectElevation - firstZFt)).First();
            levelDefaulted = true;
        }

        var points = spec.ResolvePoints(MepTypeCatalog.FtToMm(level.ProjectElevation));
        var plan = MepRunPlanner.Plan(points, spec.MinSegmentMm, spec.AutoFittings);
        if (!plan.IsValid)
            return Fail(request, MepStatus.PolylineInvalid, plan.Errors.FirstOrDefault() ?? "The polyline has no segments.", plan.Errors,
                new { changes = plan.Changes });

        // A conduit diameter must be one of its standard's sizes.
        string? standard = null;
        if (type is ConduitType conduitType)
        {
            var (standardName, sizes) = MepTypeCatalog.ConduitSizesOf(doc, conduitType);
            standard = standardName;
            if (spec.DiameterMm.HasValue && sizes != null && !MepSizeMath.IsStandard(spec.DiameterMm.Value, sizes))
                return Fail(request, MepStatus.SizeNotStandard,
                    $"diameterMm {MepMath.FormatMm(spec.DiameterMm.Value)} is not a size of conduit standard '{standardName}'. Nearest: " +
                    $"{string.Join(", ", MepSizeMath.Nearest(spec.DiameterMm.Value, sizes).Select(MepMath.FormatMm))} mm.",
                    null, new { standard = standardName, nearestSizesMm = MepSizeMath.Nearest(spec.DiameterMm.Value, sizes), standardSizesMm = sizes });
        }

        Workset? workset = null;
        var warnings = new List<string>();
        if (spec.Workset.Length > 0)
        {
            if (!doc.IsWorkshared)
                warnings.Add($"workset '{spec.Workset}' is ignored: '{doc.Title}' is not workshared.");
            else
            {
                workset = WorksharingSupport.FindWorkset(doc, spec.Workset, out var worksetError);
                if (workset == null) return Fail(request, MepStatus.ValidationFailed, worksetError!);
            }
        }

        var fittings = MepTypeCatalog.Fittings(doc, type);
        var needed = new List<string>();
        if (spec.AutoFittings && plan.HasHorizontalBends) needed.Add("elbow");
        if (spec.AutoFittings && plan.HasVerticalBends) needed.Add("verticalElbow");
        var missing = fittings.Missing(needed);
        if (missing.Count > 0)
            warnings.Add($"{MepStatus.RoutingPreferenceMissing}: type '{type.Name}' has no {string.Join(" / ", missing)} fitting family, so the " +
                         "elbows will most likely fail (fitting_failed). The straight segments are created anyway; assign the bend " +
                         "families in the type properties and join the bends with revit_connect_mep_curves.");
        if (spec.Kind == MepKind.CableTray && !spec.WidthMm.HasValue)
            warnings.Add("No widthMm/heightMm: Revit's default cable tray size is used.");
        if (spec.Kind == MepKind.Conduit && !spec.DiameterMm.HasValue)
            warnings.Add("No diameterMm: Revit's default conduit size is used.");
        if (levelDefaulted)
            warnings.Add($"No level given: the run is hosted on '{level.Name}', the level nearest the first point.");
        warnings.AddRange(plan.Warnings);

        context = new MepRunContext
        {
            Doc = doc, Spec = spec, Type = type, Level = level, LevelDefaulted = levelDefaulted, Plan = plan, Fittings = fittings,
            MissingFittings = missing, Workset = workset, Standard = standard
        };
        context.Warnings.AddRange(warnings);
        return null;
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private static McpToolResult Preview(McpToolRequest request, MepRunContext context, Stopwatch sw)
    {
        var plan = context.Plan;
        var spec = context.Spec;
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Status = context.MissingFittings.Count > 0 ? MepStatus.RoutingPreferenceMissing : null,
            Message = $"Preview: a {MepKind.Label(spec.Kind)} run '{context.Type.Name}' of {plan.Segments.Count} segment(s), " +
                      $"{MepMath.ToMetres(plan.TotalLengthMm).ToString("0.###", CultureInfo.InvariantCulture)} m, {plan.ElbowCount} elbow(s) on " +
                      $"'{context.Level.Name}'." +
                      (context.MissingFittings.Count > 0 ? $" The type has no {string.Join("/", context.MissingFittings)} fitting: elbows will fail." : string.Empty),
            Data = new
            {
                kind = spec.Kind,
                type = DescribeType(context),
                level = new { id = context.Level.Id.Value, name = context.Level.Name, elevationMm = MepMath.Round(MepTypeCatalog.FtToMm(context.Level.ProjectElevation)), defaulted = context.LevelDefaulted },
                size = Size(spec),
                workset = context.Workset?.Name,
                normalizedPolyline = plan.Points.Select(Point).ToList(),
                changes = plan.Changes,
                segments = plan.Segments.Select(s => new
                {
                    index = s.Index, fromMm = Point(s.From), toMm = Point(s.To), lengthMm = MepMath.Round(s.LengthMm), orientation = s.Orientation
                }).ToList(),
                bends = plan.Bends.Select(b => new
                {
                    index = b.Index, atMm = Point(b.At), angleDeg = b.AngleDeg, plane = b.Plane, fitting = b.Fitting,
                    fittingAvailable = b.Fitting == null ? (bool?)null : context.Fittings.Has(b.Plane == "vertical" ? "verticalElbow" : "elbow")
                }).ToList(),
                fittingsToAttempt = plan.ElbowCount,
                routingPreference = new
                {
                    status = context.MissingFittings.Count > 0 ? MepStatus.RoutingPreferenceMissing : "ok",
                    missing = context.MissingFittings,
                    fittings = context.Fittings.Status()
                },
                connectStart = SnapPreview(context, spec.ConnectStart, plan.Points[0]),
                connectEnd = SnapPreview(context, spec.ConnectEnd, plan.Points[plan.Points.Count - 1]),
                totalLengthMm = MepMath.Round(plan.TotalLengthMm),
                totalLengthM = MepMath.ToMetres(plan.TotalLengthMm),
                atomic = spec.Atomic
            },
            Warnings = context.Warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static object? SnapPreview(MepRunContext context, MepEndConnection end, MepPoint pointMm)
    {
        if (end.Mode == MepEndMode.None) return null;
        var point = PlacementHelpers.PointFromMm(pointMm.X, pointMm.Y, pointMm.Z);
        if (end.Mode == MepEndMode.Auto)
        {
            var candidates = MepConnectorFinder.Candidates(context.Doc, context.Spec.Kind, point, context.Spec.SnapMm, Array.Empty<ElementId>());
            return new
            {
                mode = "auto",
                snapMm = context.Spec.SnapMm,
                candidates = candidates.Take(10).Select(c => new
                {
                    elementId = c.Element.Id.Value, distanceMm = MepMath.Round(c.DistanceMm), atMm = Point(MepConnectorFinder.ToMm(c.Connector.Origin))
                }).ToList(),
                willConnectTo = candidates.FirstOrDefault()?.Element.Id.Value,
                note = candidates.Count == 0 ? $"No open {MepKind.Label(context.Spec.Kind)} end within {MepMath.FormatMm(context.Spec.SnapMm)} mm: this end stays open." : null
            };
        }

        var element = context.Doc.GetElement(new ElementId(end.ElementId));
        var kindOk = MepTypeCatalog.IsKind(element, context.Spec.Kind);
        var connector = kindOk ? MepConnectorFinder.NearestEnd(element, point, openOnly: true) : null;
        return new
        {
            mode = "element",
            elementId = end.ElementId,
            exists = element != null,
            sameKind = kindOk,
            openEnd = connector == null ? null : Point(MepConnectorFinder.ToMm(connector.Origin)),
            distanceMm = connector == null ? (double?)null : MepMath.Round(MepTypeCatalog.FtToMm(connector.Origin.DistanceTo(point))),
            note = element == null ? "The element does not exist."
                : !kindOk ? $"The element is not a {MepKind.Label(context.Spec.Kind)}."
                : connector == null ? "The element has no open end." : null
        };
    }

    // ── Create ───────────────────────────────────────────────────────────────

    private static McpToolResult Create(McpToolRequest request, MepRunContext context, Stopwatch sw, CancellationToken cancellationToken)
    {
        var spec = context.Spec;
        var runId = Guid.NewGuid().ToString("D");
        MepBuildResult? build = null;
        string? aborted = null;
        var name = spec.Kind == MepKind.CableTray ? "Revit MCP - Create Cable Tray Run" : "Revit MCP - Create Conduit Run";
        cancellationToken.ThrowIfCancellationRequested();

        var (success, diagnostics) = RevitTransactionRunner.Run(context.Doc, name, () =>
        {
            try
            {
                build = MepRunBuilder.Build(new MepBuildRequest
                {
                    Doc = context.Doc,
                    Type = context.Type,
                    Kind = spec.Kind,
                    Level = context.Level,
                    Spec = spec,
                    Plan = context.Plan,
                    WorksetId = context.Workset?.Id,
                    RunId = runId
                }, cancellationToken);
            }
            catch (MepBuildAbortedException ex)
            {
                aborted = ex.Message;
                throw;
            }
        });

        var warnings = new List<string>(context.Warnings);
        if (!success || build == null)
        {
            warnings.AddRange(diagnostics.ToErrorLines());
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = aborted ?? $"The run could not be created and was rolled back: {diagnostics.OriginalError ?? "Revit refused the transaction"}. The model is unchanged.",
                Data = new { kind = spec.Kind, type = context.Type.Name, transaction = diagnostics },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        warnings.AddRange(diagnostics.FailureMessages);
        var segments = build.Segments;
        var created = segments.Count(s => s.Status == MepStatus.Created);
        var failedSegments = segments.Count(s => s.Status == MepStatus.Failed);
        var elbows = build.Fittings.Count(f => f.Status == MepStatus.Created);
        var failedFittings = build.Fittings.Where(f => f.Status == MepStatus.FittingFailed).ToList();
        foreach (var fitting in failedFittings)
            warnings.Add($"Bend {fitting.BendIndex} at {fitting.At} ({MepMath.FormatMm(fitting.AngleDeg)}°): {MepStatus.FittingFailed} — {fitting.Error}");
        foreach (var connection in build.Connections.Where(c => c.Status != MepStatus.Connected && c.Status != MepStatus.Skipped))
            warnings.Add($"Run {connection.End}: {connection.Status} — {connection.Reason}");
        foreach (var segment in segments.Where(s => s.Status == MepStatus.Failed))
            warnings.Add($"Segment {segment.Index} was not created: {segment.Error}");
        foreach (var segment in segments)
            warnings.AddRange(segment.Notes.Select(n => $"Segment {segment.Index}: {n}"));

        var totalMm = segments.Where(s => s.Status == MepStatus.Created).Sum(s => s.LengthMm);
        sw.Stop();
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = created > 0,
            Status = failedFittings.Count > 0 ? MepStatus.FittingFailed : null,
            Message = $"Created a {MepKind.Label(spec.Kind)} run '{context.Type.Name}': {created} segment(s), " +
                      $"{MepMath.ToMetres(totalMm).ToString("0.###", CultureInfo.InvariantCulture)} m, {elbows} elbow(s)" +
                      (failedFittings.Count > 0 ? $"; {failedFittings.Count} elbow(s) failed at bend(s) {string.Join(", ", failedFittings.Select(f => f.BendIndex))} (segments kept, unjoined)" : string.Empty) +
                      (failedSegments > 0 ? $"; {failedSegments} segment(s) refused" : string.Empty) +
                      $". {ConnectionsText(build.Connections)}Run id {runId}.",
            Data = new
            {
                runId,
                kind = spec.Kind,
                type = new { id = context.Type.Id.Value, name = context.Type.Name },
                level = new { id = context.Level.Id.Value, name = context.Level.Name },
                size = Size(spec),
                workset = context.Workset?.Name,
                segments = segments.Select(s => new
                {
                    index = s.Index, elementId = s.ElementId, fromMm = Point(s.From), toMm = Point(s.To),
                    lengthMm = s.Status == MepStatus.Created ? MepMath.Round(s.LengthMm) : (double?)null, status = s.Status, error = s.Error
                }).ToList(),
                fittings = build.Fittings.Select(f => new
                {
                    bendIndex = f.BendIndex, elementId = f.ElementId, atMm = Point(f.At), angleDeg = f.AngleDeg, plane = f.Plane, status = f.Status, error = f.Error
                }).ToList(),
                connections = build.Connections.Select(c => new
                {
                    end = c.End, requested = c.Requested, toElementId = c.ToElementId, status = c.Status, action = c.Action,
                    fittingId = c.FittingId, distanceMm = c.DistanceMm, relation = c.Relation, reason = c.Reason
                }).ToList(),
                totalLengthMm = MepMath.Round(totalMm),
                totalLengthM = MepMath.ToMetres(totalMm),
                changes = context.Plan.Changes,
                next = "revit_get_mep_run_summary runId=" + runId + " lists the run's open ends and totals.",
                transaction = diagnostics
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static string ConnectionsText(List<MepConnectionResult> connections)
    {
        var parts = connections.Where(c => c.Status != MepStatus.Skipped)
            .Select(c => c.Status == MepStatus.Connected
                ? $"{c.End} joined to {c.ToElementId} ({c.Action})"
                : $"{c.End} not joined ({c.Status})")
            .ToList();
        return parts.Count == 0 ? string.Empty : string.Join("; ", parts) + ". ";
    }

    // ── Shaping ──────────────────────────────────────────────────────────────

    private static object DescribeType(MepRunContext context) => new
    {
        id = context.Type.Id.Value,
        name = context.Type.Name,
        familyName = MepTypeCatalog.FamilyName(context.Type),
        withFittings = context.Fittings.WithFittings,
        standard = context.Standard
    };

    private static object? Size(MepRunSpec spec) => spec.Kind == MepKind.CableTray
        ? new { widthMm = spec.WidthMm, heightMm = spec.HeightMm, label = spec.SizeLabel }
        : new { diameterMm = spec.DiameterMm, label = spec.SizeLabel } as object;

    private static object Point(MepPoint point) => new { x = MepMath.Round(point.X), y = MepMath.Round(point.Y), z = MepMath.Round(point.Z) };

    private static McpToolResult Fail(McpToolRequest request, string status, string message, List<string>? errors = null, object? data = null)
    {
        var result = new McpToolResult { RequestId = request.RequestId, Success = false, Status = status, Message = message, Data = data };
        if (errors != null) result.Errors.AddRange(errors);
        return result;
    }
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Mep;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Mep;

/// <summary>revit_connect_mep_curves (#106): joins two open tray–tray or conduit–conduit ends.</summary>
public sealed class ConnectMepCurvesTool : IRevitMcpTool
{
    public string Name => "revit_connect_mep_curves";

    public string Description =>
        "Joins the open ends of two cable trays or two conduits (requires approval). Required: elementIdA, elementIdB. mode: " +
        "auto (default: the matching call for how the ends lie — touching in line: connected directly; in line with a gap: A is " +
        "extended to B; different sizes in line: a transition; crossing at an angle: an elbow), elbow, union, or extend (A's end " +
        "is moved onto B's end when the two are in line within 1°). maxGapMm (default 5000) is the furthest an end may travel. " +
        "The two open ends nearest each other are used. Reports connected, already_connected, skew_lines (the lines pass each " +
        "other, or are parallel side by side: no single fitting joins them), too_far or fitting_failed with Revit's reason, plus " +
        "the geometry (relation, gap, angle). One transaction, one undo.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Electrical;

    private static readonly string[] Modes = { "auto", "elbow", "union", "extend" };

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return Task.FromResult(failure!);
        var doc = target.Document;

        var idA = ToolArguments.GetLong(request.Arguments, "elementIdA");
        var idB = ToolArguments.GetLong(request.Arguments, "elementIdB");
        var mode = ToolArguments.GetString(request.Arguments, "mode", "auto").Trim().ToLowerInvariant();
        if (mode.Length == 0) mode = "auto";
        var maxGapMm = ToolArguments.GetDouble(request.Arguments, "maxGapMm", MepConnectMath.DefaultMaxGapMm);

        if (idA <= 0 || idB <= 0)
            return Done(Fail(request, MepStatus.ValidationFailed, "Give elementIdA and elementIdB: the two trays or conduits to join."), sw);
        if (idA == idB)
            return Done(Fail(request, MepStatus.ValidationFailed, "elementIdA and elementIdB are the same element."), sw);
        if (Array.IndexOf(Modes, mode) < 0)
            return Done(Fail(request, MepStatus.ValidationFailed, "mode must be auto, elbow, union or extend."), sw);
        if (maxGapMm <= 0)
            return Done(Fail(request, MepStatus.ValidationFailed, "maxGapMm must be above 0 (default 5000)."), sw);

        var a = doc.GetElement(new ElementId(idA)) as MEPCurve;
        var b = doc.GetElement(new ElementId(idB)) as MEPCurve;
        if (a == null || !(MepTypeCatalog.IsKind(a, MepKind.CableTray) || MepTypeCatalog.IsKind(a, MepKind.Conduit)))
            return Done(Fail(request, MepStatus.ValidationFailed, $"Element {idA} is not a cable tray or conduit."), sw);
        if (b == null || !MepTypeCatalog.IsKind(b, MepTypeCatalog.KindOf(a)))
            return Done(Fail(request, MepStatus.ValidationFailed,
                $"Element {idB} is not a {MepKind.Label(MepTypeCatalog.KindOf(a))}: trays join trays and conduits join conduits."), sw);
        var kind = MepTypeCatalog.KindOf(a);

        if (MepConnectorFinder.AreConnected(a, b))
            return Done(new McpToolResult
            {
                RequestId = request.RequestId, Success = true, Status = MepStatus.AlreadyConnected,
                Message = $"{MepKind.Label(kind)} {idA} and {idB} are already connected (directly or through one fitting). Nothing was changed.",
                Data = new { elementIdA = idA, elementIdB = idB, status = MepStatus.AlreadyConnected }
            }, sw);

        var openA = MepConnectorFinder.OpenEnds(a);
        var openB = MepConnectorFinder.OpenEnds(b);
        if (openA.Count == 0 || openB.Count == 0)
            return Done(Fail(request, MepStatus.NoOpenEnd,
                $"Element {(openA.Count == 0 ? idA : idB)} has no open end: both its ends are connected to something else."), sw);

        // The pair of open ends nearest each other.
        var (endA, endB) = openA.SelectMany(ca => openB.Select(cb => (ca, cb)))
            .OrderBy(p => p.ca.Origin.DistanceTo(p.cb.Origin)).First();

        MepJoinResult? join = null;
        var (success, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Connect MEP Curves", () =>
        {
            join = MepRunBuilder.Join(doc, a, endA, b, endB, mode, maxGapMm, string.Empty, kind);
            if (join.Status != MepStatus.Connected)
                throw new MepBuildAbortedException(join.Reason);
        });

        var geometry = join?.Geometry;
        var data = new
        {
            elementIdA = idA,
            elementIdB = idB,
            kind,
            mode,
            status = join?.Status ?? MepStatus.Failed,
            action = join?.Action,
            fittingId = success ? join?.FittingId : null,
            geometry = geometry == null ? null : new
            {
                relation = geometry.Relation,
                gapMm = MepMath.Round(geometry.GapMm),
                angleDeg = geometry.AngleDeg,
                lineDistanceMm = MepMath.Round(geometry.LineDistanceMm),
                intersectionMm = geometry.Intersection is { } p ? new { x = MepMath.Round(p.X), y = MepMath.Round(p.Y), z = MepMath.Round(p.Z) } : null,
                endAmm = Point(MepConnectorFinder.ToMm(endA.Origin)),
                endBmm = Point(MepConnectorFinder.ToMm(endB.Origin))
            },
            transaction = diagnostics
        };

        if (success && join?.Status == MepStatus.Connected)
            return Done(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Status = MepStatus.Connected,
                Message = $"Connected {MepKind.Label(kind)} {idA} and {idB}: {join.Action}" +
                          (join.FittingId.HasValue ? $" (fitting {join.FittingId})" : string.Empty) + $". {join.Reason}",
                Data = data,
                Warnings = diagnostics.FailureMessages.ToList()
            }, sw);

        var warnings = diagnostics.ToErrorLines().Where(l => join == null || !l.Contains(join.Reason)).ToList();
        return Done(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = false,
            Status = join?.Status is { } status && status != MepStatus.Connected ? status : "transaction_failed",
            Message = join == null
                ? $"Nothing was joined: {diagnostics.OriginalError ?? "the transaction failed"}. The model is unchanged."
                : $"Not joined ({join.Status}): {join.Reason} The model is unchanged.",
            Data = data,
            Warnings = warnings
        }, sw);
    }

    private static object Point(MepPoint p) => new { x = MepMath.Round(p.X), y = MepMath.Round(p.Y), z = MepMath.Round(p.Z) };

    private static McpToolResult Fail(McpToolRequest request, string status, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = status, Message = message };

    private static Task<McpToolResult> Done(McpToolResult result, Stopwatch sw)
    {
        sw.Stop();
        result.DurationMs = sw.ElapsedMilliseconds;
        return Task.FromResult(result);
    }
}

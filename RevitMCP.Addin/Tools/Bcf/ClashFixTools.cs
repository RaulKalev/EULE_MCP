using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Coordination.Clash.DTOs;
using RevitMCP.Addin.Coordination.Clash.Services;
using RevitMCP.Addin.Coordination.ClashFix;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Mep;
using RevitMCP.Addin.Transactions;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.Bcf;

internal static class ClashFixToolSupport
{
    public static ClashFixOptions ReadOptions(Dictionary<string, object?> args) => new()
    {
        ClearanceMm = Math.Max(0, ToolArguments.GetDouble(args, "clearanceMm", 25)),
        StepMm = Math.Max(0, ToolArguments.GetDouble(args, "stepMm", 10)),
        MaxShiftMm = Math.Max(1, ToolArguments.GetDouble(args, "maxShiftMm", 1000)),
        ToleranceMm = Math.Max(0, ToolArguments.GetDouble(args, "toleranceMm", 5)),
        Directions = ToolArguments.GetStringArray(args, "directions").Where(d => !string.IsNullOrWhiteSpace(d)).ToList(),
        MaxOptions = Math.Max(1, ToolArguments.GetInt(args, "maxOptions", 3))
    };

    public static object DescribeElement(PlacedElement placed) => new
    {
        elementId = placed.Element.Id.Value,
        linkInstanceId = placed.Link?.Id.Value,
        linkName = placed.Link?.Name,
        category = placed.Element.Category?.Name,
        name = placed.Element.Name
    };

    public static double Mm(double value) => Math.Round(value, 1);
}

/// <summary>Proposes moves that clear clashes of the document's clash run (detected or from revit_bcf_check).</summary>
public class ProposeClashFixesTool : IRevitMcpTool
{
    public string Name => "revit_propose_clash_fixes";

    public string Description =>
        "Proposes how to clear clashes of the document's clash run (from a detection or revit_bcf_check) by moving one " +
        "element of each pair, without changing the model. The element that moves is a host-model run segment (cable " +
        "tray, conduit, duct, pipe) or a family instance at a point; building and structural elements, linked, pinned and " +
        "grouped elements, fittings and flexible runs are never moved. Directions: up/down and sideways (square to a run), " +
        "X/Y for vertical runs and instances, within the host plane for hosted instances; a segment joined at its ends " +
        "only moves along the runs it is joined to (they stretch). For each direction the shortest move that clears the " +
        "solids is found, plus clearanceMm (default 25), rounded up to stepMm (default 10), up to maxShiftMm (default " +
        "1000), and the moved solid is checked against the host and links for new clashes. Returns per clash the options " +
        "(fewest new clashes, then shortest), the directions ruled out with the reason, status proposed | manual | " +
        "cleared | not_found, and a fixes array (the best option of each clash without new clashes) to pass to " +
        "revit_apply_clash_fixes. Optional: clashIds (default every Active/New clash, up to limit 20), moveElementId, " +
        "directions (up, down, side+, side-, +x, -x, +y, -y, vertical, horizontal), maxOptions (default 3), toleranceMm.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Coordination;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var args = request.Arguments;
        var options = ClashFixToolSupport.ReadOptions(args);
        var limit = Math.Max(1, ToolArguments.GetInt(args, "limit", 20));
        var moveElementId = ToolArguments.GetLong(args, "moveElementId");
        var clashIds = ToolArguments.GetStringArray(args, "clashIds").Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        var warnings = new List<string>();

        var run = ClashRunScope.Cache(uiapp, request).Load();
        if (run == null || run.Clashes.Count == 0)
            return Task.FromResult(Fail(request, "No clash run for this document. Run revit_bcf_check or a clash detection first."));

        List<ClashResultDto> clashes;
        if (clashIds.Count > 0)
        {
            clashes = clashIds
                .Select(id => run.Clashes.FirstOrDefault(c => c.ClashId.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase)))
                .Where(c => c != null)
                .Cast<ClashResultDto>()
                .ToList();
            var missing = clashIds.Where(id => run.Clashes.All(c => !c.ClashId.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0) warnings.Add($"Not in clash run {run.RunId}: {string.Join(", ", missing)}.");
        }
        else
        {
            clashes = run.Clashes.Where(c => c.Status is "New" or "Active").ToList();
        }
        if (clashes.Count > limit)
        {
            warnings.Add($"Proposed fixes for the first {limit} of {clashes.Count} clashes; pass clashIds or raise limit for the rest.");
            clashes = clashes.Take(limit).ToList();
        }
        if (moveElementId > 0 && clashes.Count > 1)
            warnings.Add("moveElementId applies to every listed clash; clashes without that element get no proposal.");

        var results = new List<object>();
        var fixes = new List<object>();
        var statuses = new List<string>();
        var tolerance = options.ToleranceMm3;

        foreach (var clash in clashes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var a = PlacedElement.Find(doc, clash.Source);
            var b = PlacedElement.Find(doc, clash.Target);
            if (a == null || b == null)
            {
                statuses.Add("not_found");
                results.Add(new { clashId = clash.ClashId, status = "not_found", reason = "An element of the clash no longer exists (or its link is not loaded)." });
                continue;
            }

            var current = ClashGeometry.IntersectionVolumeMm3(ClashGeometry.Solids(a), ClashGeometry.Solids(b), out _);
            if (current <= tolerance)
            {
                statuses.Add("cleared");
                results.Add(new { clashId = clash.ClashId, status = "cleared", reason = "The elements no longer intersect: nothing to fix." });
                continue;
            }

            var candidates = new[] { (Mover: a, Obstacle: b), (Mover: b, Obstacle: a) }
                .Select(p => (p.Mover, p.Obstacle, Info: ClashFixService.Classify(p.Mover)))
                .ToList();
            var notMoved = new List<object>();
            foreach (var c in candidates)
            {
                if (moveElementId > 0 && c.Mover.Element.Id.Value != moveElementId && c.Info.Blocked == null)
                    c.Info.Blocked = $"moveElementId is {moveElementId}";
                if (c.Info.Blocked != null)
                    notMoved.Add(new { elementId = c.Mover.Element.Id.Value, category = c.Mover.Element.Category?.Name, reason = c.Info.Blocked });
            }

            var ranked = ClashFixMath.RankMovers(candidates.Select(c => new MoverCandidate
            {
                ElementId = c.Mover.Element.Id.Value,
                Blocked = c.Info.Blocked,
                ConnectedEnds = c.Info.ConnectedEnds,
                Volume = BoxVolume(c.Mover.Element)
            }));

            object? chosen = null;
            List<object> blockedDirections = new();
            foreach (var pick in ranked)
            {
                var c = candidates.First(x => x.Mover.Element.Id.Value == pick.ElementId && x.Info.Blocked == null);
                var (proposals, blocked) = ClashFixService.Propose(doc, c.Mover, c.Info, c.Obstacle, options);
                blockedDirections.AddRange(blocked.Select(x => (object)new { elementId = pick.ElementId, direction = x.Direction, reason = x.Reason }));
                if (proposals.Count == 0) continue;

                var described = proposals.Select(o => DescribeOption(clash.ClashId, c.Mover, c.Obstacle, o)).ToList();
                chosen = new
                {
                    clashId = clash.ClashId,
                    status = "proposed",
                    title = clash.Message,
                    intersectionVolumeMm3 = Math.Round(current),
                    mover = new
                    {
                        elementId = c.Mover.Element.Id.Value,
                        category = c.Mover.Element.Category?.Name,
                        name = c.Mover.Element.Name,
                        kind = c.Info.Shape == MoverShape.LinearRun ? "run segment" : "instance",
                        joinedEnds = c.Info.ConnectedEnds,
                        followers = c.Info.Followers.Count > 0 ? c.Info.Followers : null
                    },
                    obstacle = ClashFixToolSupport.DescribeElement(c.Obstacle),
                    options = described.Select(d => d.Option).ToList(),
                    ruledOut = blockedDirections,
                    notMoved
                };

                var best = proposals[0];
                if (best.NewClashes.Count == 0) fixes.Add(described[0].Fix);
                break;
            }

            if (chosen == null)
            {
                statuses.Add("manual");
                results.Add(new
                {
                    clashId = clash.ClashId,
                    status = "manual",
                    title = clash.Message,
                    intersectionVolumeMm3 = Math.Round(current),
                    notMoved,
                    ruledOut = blockedDirections,
                    suggestion = Suggestion(candidates.Select(c => c.Info).ToList())
                });
            }
            else
            {
                statuses.Add("proposed");
                results.Add(chosen);
            }
        }

        var summary = statuses.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"{clashes.Count} clash(es): " + string.Join(", ", summary.Select(p => $"{p.Value} {p.Key}")) +
                      $". {fixes.Count} fix(es) without new clashes ready for revit_apply_clash_fixes.",
            Data = new
            {
                runId = run.RunId,
                summary,
                settings = new { options.ClearanceMm, options.StepMm, options.MaxShiftMm, options.ToleranceMm },
                clashes = results,
                fixes
            },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static (object Option, object Fix) DescribeOption(string clashId, PlacedElement mover, PlacedElement obstacle, FixOption option)
    {
        var delta = option.DeltaMm;
        var fix = new
        {
            clashId,
            elementId = mover.Element.Id.Value,
            deltaXmm = ClashFixToolSupport.Mm(delta.X),
            deltaYmm = ClashFixToolSupport.Mm(delta.Y),
            deltaZmm = ClashFixToolSupport.Mm(delta.Z),
            obstacleElementId = obstacle.Element.Id.Value,
            obstacleLinkInstanceId = obstacle.Link?.Id.Value,
            direction = option.Direction
        };
        var described = new
        {
            direction = option.Direction,
            travelMm = option.TravelMm,
            clearingTravelMm = option.ClearingTravelMm,
            deltaXmm = fix.deltaXmm,
            deltaYmm = fix.deltaYmm,
            deltaZmm = fix.deltaZmm,
            newClashes = option.NewClashes.Select(n => n.Describe()).ToList(),
            alsoClears = option.AlsoClears.Count > 0 ? option.AlsoClears.Select(n => n.Describe()).ToList() : null,
            fix
        };
        return (described, fix);
    }

    private static string Suggestion(List<MoverInfo> infos)
    {
        if (infos.All(i => i.Blocked != null && i.Blocked.StartsWith("in linked model", StringComparison.Ordinal)))
            return "Both elements are in linked models: fix it in the model that owns them.";
        if (infos.Any(i => i.Blocked == null && i.ConnectedEnds > 0))
            return "The run segment is joined to runs that do not lie along any clearing move: reroute it by hand " +
                   "(offset with fittings around the obstacle), or draw a new run with revit_create_mep_run.";
        if (infos.All(i => i.Blocked != null))
            return "Neither element can be moved automatically (see notMoved). Decide with the other discipline which one changes.";
        return "No direction clears the obstacle within maxShiftMm: raise maxShiftMm, allow other directions, or reroute by hand.";
    }

    private static double BoxVolume(Element element)
    {
        var box = element.get_BoundingBox(null);
        if (box == null) return double.MaxValue;
        var size = box.Max - box.Min;
        return Math.Abs(size.X * size.Y * size.Z);
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Message = message };
}

/// <summary>Moves elements to clear clashes and keeps each move only when it is verified.</summary>
public class ApplyClashFixesTool : IRevitMcpTool
{
    public string Name => "revit_apply_clash_fixes";

    public string Description =>
        "Applies clash fixes by moving elements, verifying each one in the model and undoing it when it does not work. " +
        "Requires approval. fixes: JSON array of {elementId, deltaXmm, deltaYmm, deltaZmm, obstacleElementId, " +
        "obstacleLinkInstanceId?, clashId?} — the fixes array of revit_propose_clash_fixes. Each fix runs in its own " +
        "sub-transaction: after the move the element is tested against the obstacle (still_clashing), its joined runs " +
        "must stay joined and keep their direction (connections_broken, always undone), and the element and the runs " +
        "that followed it are tested against the host and loaded links for clashes they did not have before " +
        "(new_clashes). A fix that fails a check is rolled back unless keepIfStillClashing / keepIfNewClashes is true; " +
        "the others stay. One transaction, one Revit undo for the whole batch. Fixed clashes are marked Fixed in the " +
        "clash run. Optional: toleranceMm (default 5). Run revit_bcf_check again afterwards to confirm.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Coordination;

    private sealed class FixRequest
    {
        public string? ClashId { get; set; }
        public long ElementId { get; set; }
        public double DeltaXmm { get; set; }
        public double DeltaYmm { get; set; }
        public double DeltaZmm { get; set; }
        public long ObstacleElementId { get; set; }
        public long? ObstacleLinkInstanceId { get; set; }
    }

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;

        var args = request.Arguments;
        var tolerance = ClashGeometry.ToleranceVolumeMm3(Math.Max(0, ToolArguments.GetDouble(args, "toleranceMm", 5)));
        var keepIfStillClashing = ToolArguments.GetBool(args, "keepIfStillClashing");
        var keepIfNewClashes = ToolArguments.GetBool(args, "keepIfNewClashes");

        var fixes = ParseFixes(args, out var parseError);
        if (fixes == null) return Task.FromResult(Fail(request, parseError!));

        var warnings = new List<string>();
        var results = new List<object>();
        var fixedClashIds = new List<string>();
        var kept = 0;

        var (success, diagnostics) = RevitTransactionRunner.Run(doc, "Revit MCP - Apply Clash Fixes", () =>
        {
            foreach (var fix in fixes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var outcome = ApplyOne(doc, fix, tolerance, keepIfStillClashing, keepIfNewClashes);
                results.Add(outcome.Result);
                if (outcome.Kept)
                {
                    kept++;
                    if (outcome.Resolved && fix.ClashId != null) fixedClashIds.Add(fix.ClashId);
                }
            }
        });

        if (!success)
        {
            warnings.AddRange(diagnostics.ToErrorLines());
            sw.Stop();
            return Task.FromResult(new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "transaction_failed",
                Message = "The transaction did not commit: no fix was applied. The model is unchanged.",
                Data = new { fixes = results },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        if (fixedClashIds.Count > 0)
        {
            try
            {
                var cache = ClashRunScope.Cache(uiapp, request);
                var run = cache.Load();
                if (run != null)
                {
                    foreach (var clash in run.Clashes.Where(c => fixedClashIds.Contains(c.ClashId, StringComparer.OrdinalIgnoreCase)))
                        clash.Status = "Fixed";
                    cache.Save(run);
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"The clash run could not be updated: {ex.Message}");
            }
        }

        sw.Stop();
        return Task.FromResult(new McpToolResult
        {
            RequestId = request.RequestId,
            Success = true,
            Message = $"Kept {kept} of {fixes.Count} fix(es); {fixes.Count - kept} rolled back or skipped. One undo step.",
            Data = new { kept, total = fixes.Count, fixedClashIds, fixes = results },
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static (object Result, bool Kept, bool Resolved) ApplyOne(
        Document doc, FixRequest fix, double tolerance, bool keepIfStillClashing, bool keepIfNewClashes)
    {
        object Skip(string status, string reason) => new { clashId = fix.ClashId, elementId = fix.ElementId, status, reason };

        var element = doc.GetElement(new ElementId(fix.ElementId));
        if (element == null) return (Skip("not_found", "The element does not exist in this document."), false, false);
        var mover = new PlacedElement(element, null, Transform.Identity);
        var obstacle = PlacedElement.Find(doc, new ClashElementRefDto
        {
            ElementId = fix.ObstacleElementId,
            LinkInstanceId = fix.ObstacleLinkInstanceId is > 0 ? fix.ObstacleLinkInstanceId : null
        });
        if (obstacle == null) return (Skip("not_found", "The obstacle does not exist (or its link is not loaded)."), false, false);

        var info = ClashFixService.Classify(mover);
        if (info.Blocked != null) return (Skip("not_movable", info.Blocked), false, false);

        var delta = new FixVector(fix.DeltaXmm, fix.DeltaYmm, fix.DeltaZmm);
        if (delta.Length < 0.1) return (Skip("invalid", "The displacement is zero."), false, false);
        if (info.ConnectedEnds > 0 &&
            ClashFixMath.ConnectedMoveBlocker(delta, info.NeighbourAxes, info.ConnectedToEquipment) is { } blocker)
            return (Skip("not_movable", blocker), false, false);

        // Everything that changes with the move: the element, the fittings that follow it and the runs that stretch.
        var changed = new List<PlacedElement> { mover };
        changed.AddRange(info.Followers
            .Select(id => doc.GetElement(new ElementId(id)))
            .Where(e => e != null)
            .Select(e => new PlacedElement(e!, null, Transform.Identity)));
        var exclude = new HashSet<string>(changed.Select(c => c.Key), StringComparer.Ordinal);
        exclude.UnionWith(info.Attached);
        exclude.Add(obstacle.Key);

        var before = new HashSet<string>(changed
            .SelectMany(c => ClashGeometry.FindIntersecting(doc, ClashGeometry.Solids(c), exclude, tolerance))
            .Select(i => i.Placed.Key));
        var joinedBefore = MepJoins(element);

        using var sub = new SubTransaction(doc);
        sub.Start();
        try
        {
            ElementTransformUtils.MoveElement(doc, element.Id, ClashGeometry.ToFeet(delta));
            doc.Regenerate();
        }
        catch (Exception ex)
        {
            sub.RollBack();
            return (Skip("failed", $"Revit refused the move: {ex.Message}"), false, false);
        }

        var obstacleSolids = ClashGeometry.Solids(obstacle);
        var remaining = ClashGeometry.IntersectionVolumeMm3(ClashGeometry.Solids(mover), obstacleSolids, out _);
        var stillClashing = remaining > tolerance;

        var broken = new List<string>();
        var joinedAfter = MepJoins(element);
        if (joinedAfter < joinedBefore) broken.Add($"{joinedBefore - joinedAfter} joined end(s) came apart");
        foreach (var neighbour in info.Neighbours)
        {
            if (doc.GetElement(new ElementId(neighbour.Key)) is MEPCurve { Location: LocationCurve { Curve: Line line } })
            {
                if (!ClashFixMath.IsParallel(ClashGeometry.ToVector(line.Direction), neighbour.Value, 0.99995))
                    broken.Add($"run {neighbour.Key} changed direction (it would be sloped)");
            }
            else
            {
                broken.Add($"run {neighbour.Key} is gone or no longer straight");
            }
        }

        var newClashes = changed
            .SelectMany(c => ClashGeometry.FindIntersecting(doc, ClashGeometry.Solids(c), exclude, tolerance))
            .Where(i => !before.Contains(i.Placed.Key))
            .GroupBy(i => i.Placed.Key)
            .Select(g => g.First())
            .ToList();

        string status;
        string? reason = null;
        var keep = true;
        if (broken.Count > 0)
        {
            status = "connections_broken";
            reason = string.Join("; ", broken);
            keep = false;
        }
        else if (stillClashing && !keepIfStillClashing)
        {
            status = "still_clashing";
            reason = $"{Math.Round(remaining)} mm³ still intersect the obstacle after the move.";
            keep = false;
        }
        else if (newClashes.Count > 0 && !keepIfNewClashes)
        {
            status = "new_clashes";
            reason = $"The move runs into {newClashes.Count} element(s) it did not touch before.";
            keep = false;
        }
        else
        {
            status = stillClashing ? "kept_still_clashing" : newClashes.Count > 0 ? "kept_with_new_clashes" : "fixed";
        }

        if (keep) sub.Commit();
        else sub.RollBack();

        return (new
        {
            clashId = fix.ClashId,
            elementId = fix.ElementId,
            status,
            rolledBack = !keep,
            reason,
            deltaMm = new { x = fix.DeltaXmm, y = fix.DeltaYmm, z = fix.DeltaZmm },
            followers = info.Followers.Count > 0 ? info.Followers : null,
            remainingIntersectionMm3 = Math.Round(remaining),
            newClashes = newClashes.Count > 0 ? newClashes.Select(n => n.Describe()).ToList() : null
        }, keep, keep && !stillClashing);
    }

    /// <summary>How many of the element's end connectors are joined.</summary>
    private static int MepJoins(Element element) =>
        MepConnectorFinder.EndConnectors(element).Count(MepConnectorFinder.IsConnected);

    private static List<FixRequest>? ParseFixes(Dictionary<string, object?> args, out string? error)
    {
        error = null;
        args.TryGetValue("fixes", out var raw);
        var array = raw switch
        {
            JArray a => a,
            string s => ToolArguments.TryParseJArray(s),
            _ => null
        };
        if (array == null || array.Count == 0)
        {
            error = "fixes is required: a JSON array of {elementId, deltaXmm, deltaYmm, deltaZmm, obstacleElementId, obstacleLinkInstanceId?, clashId?} (revit_propose_clash_fixes returns it).";
            return null;
        }
        if (array.Count > 200)
        {
            error = "At most 200 fixes per call.";
            return null;
        }

        var result = new List<FixRequest>();
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JObject o)
            {
                error = $"fixes[{i}] is not an object.";
                return null;
            }
            var elementId = o.Value<long?>("elementId") ?? 0;
            var obstacleId = o.Value<long?>("obstacleElementId") ?? 0;
            if (elementId <= 0 || obstacleId <= 0)
            {
                error = $"fixes[{i}] needs elementId and obstacleElementId.";
                return null;
            }
            result.Add(new FixRequest
            {
                ClashId = o.Value<string?>("clashId"),
                ElementId = elementId,
                DeltaXmm = o.Value<double?>("deltaXmm") ?? 0,
                DeltaYmm = o.Value<double?>("deltaYmm") ?? 0,
                DeltaZmm = o.Value<double?>("deltaZmm") ?? 0,
                ObstacleElementId = obstacleId,
                ObstacleLinkInstanceId = o.Value<long?>("obstacleLinkInstanceId")
            });
        }
        return result;
    }

    private static McpToolResult Fail(McpToolRequest request, string message) =>
        new() { RequestId = request.RequestId, Success = false, Status = "validation_failed", Message = message };
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.ElementEditing;

/// <summary>revit_preview_set_pin_state (#162): what pinning or unpinning would do, without changing anything.</summary>
public sealed class PreviewSetPinStateTool : IRevitMcpTool
{
    public const string ToolName = "revit_preview_set_pin_state";
    public string Name => ToolName;

    public string Description =>
        "Previews pinning or unpinning elements, without changing the model. Same arguments as revit_set_pin_state. " +
        "Per element: name, category, host, current pin state, intended action and the blocked reason (group member, " +
        "checked out by someone else, a type, not found). Elements already in the wanted state are reported as noOp.";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ElementOrientationExecutor.Run(uiapp, request, flip: false, apply: false, cancellationToken));
}

/// <summary>revit_set_pin_state (#162): pins or unpins elements in one transaction.</summary>
public sealed class SetPinStateTool : IRevitMcpTool, IApprovalSummaryTool
{
    public const string ToolName = "revit_set_pin_state";
    public string Name => ToolName;

    public string Description =>
        "Pins (pinned=true) or unpins (pinned=false) elements — model and view-specific ones such as detail items — in one " +
        "transaction and one approval. Target elementIds[] or useSelection=true. Elements already in the wanted state are " +
        "reported as noOp and left alone. Group members, elements checked out by someone else, types and ids that are not in " +
        "the document are reported per element and not touched. atomic=true writes nothing unless every non-noOp element " +
        "can change. The result is aggregated (counts and up to sampleSize examples). Run revit_preview_set_pin_state first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ElementOrientationExecutor.Run(uiapp, request, flip: false, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
        => ElementOrientationExecutor.ApprovalSummary(uiapp, request, flip: false);
}

/// <summary>revit_preview_flip_family_instances (#162): what flipping facing and/or hand would do.</summary>
public sealed class PreviewFlipFamilyInstancesTool : IRevitMcpTool
{
    public const string ToolName = "revit_preview_flip_family_instances";
    public string Name => ToolName;

    public string Description =>
        "Previews flipping the facing and/or hand of family instances, without changing the model. Same arguments as " +
        "revit_flip_family_instances. Per element: name, category, host, pin state, whether facing and hand can be flipped " +
        "(CanFlipFacing / CanFlipHand), the current FacingFlipped / HandFlipped, the intended action and the blocked reason " +
        "(pinned, group member, checked out by someone else, family cannot flip, not a family instance).";

    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ElementOrientationExecutor.Run(uiapp, request, flip: true, apply: false, cancellationToken));
}

/// <summary>revit_flip_family_instances (#162): FamilyInstance.flipFacing() / flipHand(), verified per instance.</summary>
public sealed class FlipFamilyInstancesTool : IRevitMcpTool, IApprovalSummaryTool
{
    public const string ToolName = "revit_flip_family_instances";
    public string Name => ToolName;

    public string Description =>
        "Flips the facing (flipFacing=true) and/or hand (flipHand=true) of family instances with Revit's own flip — not a " +
        "rotation or a mirror — keeping the host. Target elementIds[] or useSelection=true. Needs CanFlipFacing / CanFlipHand; " +
        "other elements get a precise reason. A pinned element is NOT silently unpinned: pass unpinToFlip=true to unpin, " +
        "flip and re-pin it (the approval shows this). Each flip is verified (FacingFlipped / HandFlipped changed, location " +
        "unchanged within 0.5 mm; a flip that moves the instance is undone and reported). atomic=true writes nothing unless " +
        "every flippable element succeeds; otherwise failures are reported per element. Run revit_preview_flip_family_instances first.";

    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
        => Task.FromResult(ElementOrientationExecutor.Run(uiapp, request, flip: true, apply: true, cancellationToken));

    public string? BuildApprovalSummary(UIApplication uiapp, McpToolRequest request)
        => ElementOrientationExecutor.ApprovalSummary(uiapp, request, flip: true);
}

internal static class ElementOrientationExecutor
{
    private sealed class Item
    {
        public long Id;
        public Element? Element;
        public OrientationFacts Facts = new();
        public OrientationDecision Decision = new();
        public string Name = string.Empty;
        public string Category = string.Empty;
        public long? HostId;
        public bool? FacingFlipped;
        public bool? HandFlipped;
        public string Status = OrientationStatus.Ready;
        public string? Reason;
        public double? MovedMm;
        public bool Applied;
    }

    private sealed class Plan
    {
        public List<Item> Items = new();
        public bool WantPinned;
        public bool Facing;
        public bool Hand;
        public bool UnpinToFlip;
        public bool Atomic;
        public bool Workshared;
        public string DocumentTitle = string.Empty;
    }

    public static McpToolResult Run(UIApplication uiapp, McpToolRequest request, bool flip, bool apply, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var failure);
        if (target == null) return failure!;
        var doc = target.Document;
        var args = request.Arguments;
        var sample = ElementOrientationMath.ClampSample(ToolArguments.GetInt(args, "sampleSize", ElementOrientationMath.DefaultSampleSize));

        var plan = BuildPlan(uiapp, target, request, flip, out var error, out var activeFailure);
        if (activeFailure != null) return activeFailure;
        if (plan == null) return Fail(request, error!);

        var ready = plan.Items.Where(i => i.Status == OrientationStatus.Ready).ToList();
        var blocked = plan.Items.Count(i => i.Status is not (OrientationStatus.Ready or OrientationStatus.NoOp));
        var verb = flip ? $"would have {ElementOrientationMath.DescribeFlip(plan.Facing, plan.Hand)} flipped"
                        : plan.WantPinned ? "would be pinned" : "would be unpinned";
        var warnings = new List<string>();
        if (plan.Atomic && blocked > 0 && ready.Count > 0)
            warnings.Add($"atomic=true: {blocked} element(s) cannot be changed, so nothing would be written.");
        if (flip && ready.Any(i => i.Decision.NeedsUnpin))
            warnings.Add($"{ready.Count(i => i.Decision.NeedsUnpin)} pinned element(s) will be unpinned, flipped and pinned again (unpinToFlip=true).");

        if (!apply)
        {
            sw.Stop();
            var counts = ElementOrientationMath.CountByStatus(plan.Items.Select(i => i.Status));
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = true,
                Message = "Preview: " + ElementOrientationMath.SummaryLine(verb, plan.Items.Count, counts),
                Data = Data(plan, flip, counts, sample, applied: -1),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        if (ready.Count == 0)
        {
            sw.Stop();
            var counts0 = ElementOrientationMath.CountByStatus(plan.Items.Select(i => i.Status));
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = plan.Items.Count > 0 && blocked == 0,
                Status = blocked > 0 ? "validation_failed" : null,
                Message = blocked > 0 ? "Nothing changed: no element can be changed. " + ElementOrientationMath.SummaryLine("could change", plan.Items.Count, counts0)
                                      : "Nothing to do: every element is already in the wanted state.",
                Data = Data(plan, flip, counts0, sample, applied: 0),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        if (plan.Atomic && blocked > 0)
        {
            sw.Stop();
            var counts1 = ElementOrientationMath.CountByStatus(plan.Items.Select(i => i.Status));
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Status = "validation_failed",
                Message = $"Nothing changed: atomic=true and {blocked} element(s) cannot be changed. " + ElementOrientationMath.SummaryLine("could change", plan.Items.Count, counts1),
                Data = Data(plan, flip, counts1, sample, applied: 0),
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        // ── Write ──
        var rolledBack = false;
        using (var tx = new Transaction(doc, flip ? "Revit MCP - Flip Family Instances" : "Revit MCP - Set Pin State"))
        {
            tx.Start();
            var n = 0;
            foreach (var item in ready)
            {
                if (++n % 200 == 0) ct.ThrowIfCancellationRequested();
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    if (flip) ApplyFlip(item, plan); else ApplyPin(item, plan);
                    sub.Commit();
                    item.Applied = true;
                    item.Status = "done";
                }
                catch (Exception ex)
                {
                    try { sub.RollBack(); } catch (Exception rb) { Swallowed.Note(rb); }
                    item.Status = OrientationStatus.Failed;
                    item.Reason = ex.Message;
                }
            }

            var failed = ready.Count(i => !i.Applied);
            if (plan.Atomic && failed > 0)
            {
                tx.RollBack();
                rolledBack = true;
                foreach (var item in ready.Where(i => i.Applied))
                {
                    item.Applied = false;
                    item.Status = "rolledBack";
                    item.Reason = "Undone: atomic=true and another element failed.";
                }
            }
            else
            {
                TransactionCommitGuard.CommitOrThrow(tx);
            }
        }

        sw.Stop();
        var appliedCount = ready.Count(i => i.Applied);
        var finalCounts = ElementOrientationMath.CountByStatus(plan.Items.Select(i => i.Status));
        var changed = flip ? $"flipped {ElementOrientationMath.DescribeFlip(plan.Facing, plan.Hand)} on" : plan.WantPinned ? "pinned" : "unpinned";
        return new McpToolResult
        {
            RequestId = request.RequestId,
            Success = appliedCount > 0,
            Status = appliedCount == 0 ? "transaction_failed" : null,
            Message = rolledBack
                ? $"Nothing changed: atomic=true and {ready.Count(i => i.Status == OrientationStatus.Failed)} element(s) failed, so all were undone."
                : $"{char.ToUpperInvariant(changed[0])}{changed.Substring(1)} {appliedCount} element(s); {plan.Items.Count - appliedCount} not changed.",
            Data = Data(plan, flip, finalCounts, sample, appliedCount),
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    public static string? ApprovalSummary(UIApplication uiapp, McpToolRequest request, bool flip)
    {
        try
        {
            var target = ToolDocument.Resolve(uiapp, request, out _);
            if (target == null) return null;
            var plan = BuildPlan(uiapp, target, request, flip, out var error, out var activeFailure);
            if (plan == null) return activeFailure != null ? activeFailure.Message : error;
            var counts = ElementOrientationMath.CountByStatus(plan.Items.Select(i => i.Status));
            var ready = counts.TryGetValue(OrientationStatus.Ready, out var r) ? r : 0;
            var what = flip ? $"Flip the {ElementOrientationMath.DescribeFlip(plan.Facing, plan.Hand)} of" : plan.WantPinned ? "Pin" : "Unpin";
            var lines = new List<string>
            {
                $"{what} {ready} element(s) in '{plan.DocumentTitle}' ({(plan.Atomic ? "all or nothing" : "element by element")}, one undo step).",
                ElementOrientationMath.SummaryLine(flip ? "can be flipped" : "can change", plan.Items.Count, counts)
            };
            var unpin = plan.Items.Count(i => i.Status == OrientationStatus.Ready && i.Decision.NeedsUnpin);
            if (unpin > 0) lines.Add($"{unpin} pinned element(s) will be unpinned and pinned again (unpinToFlip=true).");
            foreach (var i in plan.Items.Where(i => i.Status == OrientationStatus.Ready).Take(3))
                lines.Add($"e.g. {i.Id} {i.Category} '{i.Name}'");
            return string.Join("\n", lines);
        }
        catch (Exception ex)
        {
            Swallowed.Note(ex);
            return null;
        }
    }

    private static Plan? BuildPlan(UIApplication uiapp, RevitDocumentResolver.DocumentTarget target, McpToolRequest request,
        bool flip, out string? error, out McpToolResult? activeFailure)
    {
        error = null;
        activeFailure = null;
        var doc = target.Document;
        var args = request.Arguments;
        var plan = new Plan
        {
            DocumentTitle = doc.Title,
            Atomic = ToolArguments.GetBool(args, "atomic"),
            Workshared = doc.IsWorkshared
        };

        if (flip)
        {
            plan.Facing = ToolArguments.GetBool(args, "flipFacing");
            plan.Hand = ToolArguments.GetBool(args, "flipHand");
            plan.UnpinToFlip = ToolArguments.GetBool(args, "unpinToFlip");
            error = ElementOrientationMath.ValidateFlip(plan.Facing, plan.Hand);
            if (error != null) return null;
        }
        else
        {
            if (!args.ContainsKey("pinned") || args["pinned"] == null || string.IsNullOrWhiteSpace(args["pinned"]!.ToString()))
            {
                error = "Pass pinned=true to pin or pinned=false to unpin.";
                return null;
            }
            plan.WantPinned = ToolArguments.GetBool(args, "pinned");
        }

        var ids = ToolArguments.GetLongArray(args, "elementIds").Distinct().ToList();
        if (ids.Count == 0 && ToolArguments.GetBool(args, "useSelection"))
        {
            var uidoc = ToolDocument.UiDocumentOf(uiapp, target);
            if (uidoc == null)
            {
                activeFailure = ToolDocument.ActiveRequired(request, "useSelection", doc.Title);
                return null;
            }
            ids = uidoc.Selection.GetElementIds().Select(i => i.Value).ToList();
            if (ids.Count == 0)
            {
                error = "useSelection=true but nothing is selected in Revit.";
                return null;
            }
        }
        if (ids.Count == 0)
        {
            error = "Provide elementIds, or set useSelection=true to use the current selection.";
            return null;
        }

        var reader = new Func<ElementId, string?>(id => OwnerIfOther(doc, id, plan.Workshared));
        foreach (var id in ids)
        {
            var item = new Item { Id = id };
            var element = doc.GetElement(new ElementId(id));
            item.Element = element;
            item.Facts = ReadFacts(element, id, reader, item);
            item.Decision = flip
                ? ElementOrientationMath.DecideFlip(item.Facts, plan.Facing, plan.Hand, plan.UnpinToFlip)
                : ElementOrientationMath.DecidePin(item.Facts, plan.WantPinned);
            item.Status = item.Decision.Status;
            item.Reason = item.Decision.Reason;
            plan.Items.Add(item);
        }
        return plan;
    }

    private static OrientationFacts ReadFacts(Element? element, long id, Func<ElementId, string?> owner, Item item)
    {
        var facts = new OrientationFacts { ElementId = id, Found = element != null };
        if (element == null) return facts;
        facts.IsType = element is ElementType;
        try { facts.Pinned = element.Pinned; } catch (Exception ex) { Swallowed.Note(ex); }
        try { if (element.GroupId != ElementId.InvalidElementId) facts.GroupId = element.GroupId.Value; } catch (Exception ex) { Swallowed.Note(ex); }
        facts.OwnedByOther = owner(element.Id);
        try { item.Name = element.Name ?? string.Empty; } catch { item.Name = string.Empty; }
        try { item.Category = element.Category?.Name ?? string.Empty; } catch { item.Category = string.Empty; }
        if (element is FamilyInstance fi)
        {
            facts.IsFamilyInstance = true;
            try { facts.CanFlipFacing = fi.CanFlipFacing; } catch (Exception ex) { Swallowed.Note(ex); }
            try { facts.CanFlipHand = fi.CanFlipHand; } catch (Exception ex) { Swallowed.Note(ex); }
            try { item.FacingFlipped = fi.FacingFlipped; } catch (Exception ex) { Swallowed.Note(ex); }
            try { item.HandFlipped = fi.HandFlipped; } catch (Exception ex) { Swallowed.Note(ex); }
            try { item.HostId = fi.Host?.Id.Value; } catch (Exception ex) { Swallowed.Note(ex); }
        }
        return facts;
    }

    private static string? OwnerIfOther(Document doc, ElementId id, bool workshared)
    {
        if (!workshared) return null;
        var info = OwnershipReader.Read(doc, id);
        return info.OwnedByOther ? (info.Owner ?? "another user") : null;
    }

    private static void ApplyPin(Item item, Plan plan)
    {
        var element = item.Element!;
        element.Pinned = plan.WantPinned;
        if (element.Pinned != plan.WantPinned)
            throw new InvalidOperationException("Revit did not change the pin state of this element.");
    }

    private static void ApplyFlip(Item item, Plan plan)
    {
        var fi = (FamilyInstance)item.Element!;
        var hostBefore = fi.Host?.Id;
        var before = LocationOf(fi);
        var facingBefore = fi.FacingFlipped;
        var handBefore = fi.HandFlipped;
        var wasPinned = fi.Pinned;

        if (wasPinned) fi.Pinned = false;
        try
        {
            if (plan.Facing) fi.flipFacing();
            if (plan.Hand) fi.flipHand();
        }
        finally
        {
            if (wasPinned) fi.Pinned = true;
        }

        if (plan.Facing && fi.FacingFlipped == facingBefore)
            throw new InvalidOperationException("Revit did not flip the facing of this instance (FacingFlipped is unchanged).");
        if (plan.Hand && fi.HandFlipped == handBefore)
            throw new InvalidOperationException("Revit did not flip the hand of this instance (HandFlipped is unchanged).");
        if (hostBefore != null && fi.Host?.Id != hostBefore)
            throw new InvalidOperationException("The flip changed the host of this instance.");

        var after = LocationOf(fi);
        if (before != null && after != null)
        {
            var movedMm = before.DistanceTo(after) * 304.8;
            item.MovedMm = Math.Round(movedMm, 2);
            if (ElementOrientationMath.MovedBeyond(movedMm))
                throw new InvalidOperationException($"The flip moved the instance {movedMm:0.0} mm; undone.");
        }
        item.FacingFlipped = fi.FacingFlipped;
        item.HandFlipped = fi.HandFlipped;
    }

    private static XYZ? LocationOf(Element element)
    {
        switch (element.Location)
        {
            case LocationPoint lp: return lp.Point;
            case LocationCurve lc: return lc.Curve.Evaluate(0.5, true);
            default: return null;
        }
    }

    private static object Data(Plan plan, bool flip, Dictionary<string, int> counts, int sample, int applied)
    {
        // Blocked elements first: they are what the user must act on.
        var ordered = plan.Items
            .OrderBy(i => i.Status == OrientationStatus.Ready || i.Status == "done" ? 1 : 0)
            .ThenBy(i => i.Id)
            .Take(sample)
            .Select(i => new
            {
                elementId = i.Id,
                name = i.Name,
                category = i.Category,
                hostId = i.HostId,
                pinned = i.Facts.Found ? i.Facts.Pinned : (bool?)null,
                groupId = i.Facts.GroupId,
                ownedBy = i.Facts.OwnedByOther,
                canFlipFacing = flip && i.Facts.IsFamilyInstance ? i.Facts.CanFlipFacing : (bool?)null,
                canFlipHand = flip && i.Facts.IsFamilyInstance ? i.Facts.CanFlipHand : (bool?)null,
                facingFlipped = flip ? i.FacingFlipped : null,
                handFlipped = flip ? i.HandFlipped : null,
                status = i.Status,
                reason = i.Reason,
                movedMm = i.MovedMm
            }).ToList();
        return new
        {
            total = plan.Items.Count,
            action = flip ? "flip " + ElementOrientationMath.DescribeFlip(plan.Facing, plan.Hand) : plan.WantPinned ? "pin" : "unpin",
            atomic = plan.Atomic,
            unpinToFlip = flip ? plan.UnpinToFlip : (bool?)null,
            workshared = plan.Workshared,
            applied = applied < 0 ? (int?)null : applied,
            counts,
            sampled = ordered.Count,
            elements = ordered
        };
    }

    private static McpToolResult Fail(McpToolRequest request, string message) => new()
    {
        RequestId = request.RequestId,
        Success = false,
        Status = "validation_failed",
        Message = message
    };
}

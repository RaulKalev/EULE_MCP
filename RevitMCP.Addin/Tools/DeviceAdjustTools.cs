using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Documents;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Placement;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>One element the rotate / set-elevation / assign-room tools will change.</summary>
internal sealed class AdjustPlanEntry
{
    /// <summary>The family instance. Null only for a group-instance entry of the rotate tool (#108).</summary>
    public DeviceRecord Device { get; set; } = null!;

    /// <summary>A group instance the rotate tool turns as a whole about the group origin (#108).</summary>
    public Group? GroupInstance { get; set; }

    /// <summary>The group origin in mm, for a group-instance entry.</summary>
    public P2 GroupPoint { get; set; }
    public double GroupZMm { get; set; }

    /// <summary>The group a blocked member belongs to; pass this id to act on the whole group.</summary>
    public long? GroupId { get; set; }

    /// <summary>Preview: ready | unchanged | stale | blocked. After the write: changed or rolledBack (was ready), failed.</summary>
    public string Status { get; set; } = "ready";
    public string Reason { get; set; } = string.Empty;
    public double? CurrentValue { get; set; }
    public double? TargetValue { get; set; }
    public string? CurrentText { get; set; }
    public string? TargetText { get; set; }
    public bool WillChange => Status == "ready";

    /// <summary>The element that is changed: the group instance, or the device.</summary>
    public Element Target => GroupInstance != null ? GroupInstance : (Element)Device.Element;

    public long Id => GroupInstance?.Id.Value ?? Device.Id;

    public object Payload(string unit, string? error = null) => new
    {
        elementId = Id,
        deviceCode = GroupInstance != null ? null : Device.Code?.Code,
        point = GroupInstance != null
            ? new { x = Math.Round(GroupPoint.X, 1), y = Math.Round(GroupPoint.Y, 1), z = Math.Round(GroupZMm, 1) }
            : Device.PointPayload(),
        isGroupInstance = GroupInstance != null,
        groupId = GroupId,
        status = error != null ? "failed" : Status,
        reason = error ?? Reason,
        current = CurrentText ?? (CurrentValue is { } c ? Math.Round(c, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + unit : null),
        target = TargetText ?? (TargetValue is { } t ? Math.Round(t, 2).ToString(System.Globalization.CultureInfo.InvariantCulture) + unit : null)
    };
}

internal static class DeviceAdjustSupport
{
    /// <summary>
    /// Per-element requests: an array argument (<paramref name="arrayKey"/>) of objects with elementId,
    /// or elementIds + shared values at the top level. Returns (elementId, fields) pairs.
    /// </summary>
    public static List<(long Id, Dictionary<string, object?> Fields)> Requests(Dictionary<string, object?> args, string arrayKey)
    {
        var result = new List<(long, Dictionary<string, object?>)>();
        foreach (var item in DevicePlacementService.ParseArray(args, arrayKey))
        {
            var id = ToolArguments.GetLong(item, "elementId");
            if (id != 0) result.Add((id, item));
        }
        foreach (var id in ToolArguments.GetLongArray(args, "elementIds"))
            result.Add((id, args));
        return result;
    }

    public static bool Has(Dictionary<string, object?> fields, string key) =>
        fields.TryGetValue(key, out var v) && v != null && !(v is JValue { Type: JTokenType.Null });

    public static McpToolResult Preview(McpToolRequest request, Stopwatch sw, string what, List<AdjustPlanEntry> plan, List<string> warnings, string unit)
    {
        sw.Stop();
        var ready = plan.Count(p => p.WillChange);
        return RoomToolSupport.Ok(request, sw,
            $"Preview: {ready} of {plan.Count} element(s) will {what}.",
            new
            {
                total = plan.Count,
                willChange = ready,
                unchanged = plan.Count(p => p.Status == "unchanged"),
                stale = plan.Count(p => p.Status == "stale"),
                blocked = plan.Count(p => p.Status == "blocked"),
                elements = plan.Select(p => p.Payload(unit)).ToList()
            },
            warnings);
    }

    /// <summary>Runs <paramref name="apply"/> for every ready entry in one transaction; atomic rolls all back on any failure.</summary>
    public static McpToolResult Apply(
        Document doc, McpToolRequest request, Stopwatch sw, string transactionName, string what,
        List<AdjustPlanEntry> plan, List<string> warnings, string unit, Action<AdjustPlanEntry> apply, CancellationToken ct)
    {
        var atomic = ToolArguments.GetBool(request.Arguments, "atomic", true);
        var outcomes = new List<(AdjustPlanEntry Entry, string? Error)>();
        var changed = 0;
        var failed = 0;

        using var transaction = new Transaction(doc, transactionName);
        transaction.Start();
        foreach (var entry in plan)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.WillChange)
            {
                outcomes.Add((entry, null));
                continue;
            }

            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                apply(entry);
                sub.Commit();
                changed++;
                entry.Status = "changed";   // X8: the result said "ready" after the change was made
                outcomes.Add((entry, null));
            }
            catch (Exception ex)
            {
                if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                failed++;
                outcomes.Add((entry, ex.Message));
            }
        }

        if (atomic && failed > 0)
        {
            foreach (var (entry, _) in outcomes.Where(o => o.Entry.Status == "changed"))
            {
                entry.Status = "rolledBack";
                entry.Reason = "Rolled back: another element failed (atomic=true).";
            }
        }
        var results = outcomes.Select(o => o.Entry.Payload(unit, o.Error)).ToList();

        if (atomic && failed > 0)
        {
            transaction.RollBack();
            sw.Stop();
            return new McpToolResult
            {
                RequestId = request.RequestId,
                Success = false,
                Message = $"{failed} element(s) failed — nothing was changed (atomic=true).",
                Data = new { changed = 0, failed, elements = results },
                Warnings = warnings,
                DurationMs = sw.ElapsedMilliseconds
            };
        }

        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);
        sw.Stop();
        var result = RoomToolSupport.Ok(request, sw, $"{changed} element(s) {what}; {failed} failed.",
            new { changed, failed, elements = results }, warnings);
        result.Success = failed == 0 || changed > 0;
        return result;
    }

    public static void CheckPinned(AdjustPlanEntry entry, Dictionary<string, object?> args)
    {
        if (entry.Status == "ready" && entry.Target.Pinned && ToolArguments.GetBool(args, "skipPinned", true))
        {
            entry.Status = "blocked";
            entry.Reason = "Element is pinned (skipPinned=true).";
        }
    }
}

// ── Rotate ──────────────────────────────────────────────────────────────────────

internal static class RotatePlanner
{
    public static List<AdjustPlanEntry>? Build(Document doc, Dictionary<string, object?> args, List<string> warnings, out string? error)
    {
        error = null;
        var requests = DeviceAdjustSupport.Requests(args, "rotations");
        if (requests.Count == 0)
        {
            error = "Provide rotations=[{elementId, angleDeg | rotateByDeg | faceToward{x,y}, expectedAngleDeg?}] or elementIds + one of those.";
            return null;
        }

        var tolerance = ToolArguments.GetDouble(args, "angleToleranceDeg", 0.5);

        // Group instances (#108) are turned as a whole about the group origin; they are not family
        // instances, so they stay out of the device inventory.
        var groups = new Dictionary<long, Group>();
        foreach (var (id, _) in requests)
            if (doc.GetElement(new ElementId(id)) is Group group) groups[id] = group;

        var devices = DeviceAudit.Inventory(doc, null, [], requests.Select(r => r.Id).Where(id => !groups.ContainsKey(id)).ToList(), warnings)
            .ToDictionary(d => d.Id);
        var plan = new List<AdjustPlanEntry>();

        foreach (var (id, fields) in requests)
        {
            AdjustPlanEntry entry;
            P2 point;
            if (groups.TryGetValue(id, out var groupInstance))
            {
                entry = GroupEntry(groupInstance);
                plan.Add(entry);
                if (entry.Status == "blocked") continue;
                point = entry.GroupPoint;

                if (entry.CurrentValue == null)
                {
                    // Revit did not report the group's rotation: only a relative turn can be resolved.
                    if (!DeviceAdjustSupport.Has(fields, "rotateByDeg"))
                    {
                        entry.Status = "blocked";
                        entry.Reason = "Revit does not report this group's rotation, so angleDeg and faceToward cannot be resolved. Use rotateByDeg.";
                        continue;
                    }
                    entry.CurrentValue = 0;
                    entry.CurrentText = "unknown (relative turn)";
                }
            }
            else
            {
                if (!devices.TryGetValue(id, out var device)) continue;
                entry = new AdjustPlanEntry { Device = device, CurrentValue = device.FacingDeg };
                plan.Add(entry);
                point = device.Point;

                if (device.Element.GroupId != ElementId.InvalidElementId)
                {
                    // A member keeps InGroup, now with the group id and the way to turn the whole group.
                    var groupId = device.Element.GroupId.Value;
                    string? groupName = null;
                    try { groupName = doc.GetElement(device.Element.GroupId)?.Name; } catch { /* courtesy only */ }
                    entry.Status = "blocked";
                    entry.GroupId = groupId;
                    entry.Reason = "InGroup: " + MoveElementsMath.GroupMemberReason(groupId, groupName, "rotate");
                    continue;
                }
            }

            if (entry.CurrentValue is not { } current)
            {
                entry.Status = "blocked";
                entry.Reason = "The element has no plan facing direction.";
                continue;
            }

            double? target = null;
            if (DeviceAdjustSupport.Has(fields, "angleDeg"))
                target = ToolArguments.GetDouble(fields, "angleDeg");
            else if (DeviceAdjustSupport.Has(fields, "rotateByDeg"))
                target = current + ToolArguments.GetDouble(fields, "rotateByDeg");
            else if (fields.TryGetValue("faceToward", out var raw) && raw is JObject toward &&
                     toward["x"]?.Value<double?>() is { } x && toward["y"]?.Value<double?>() is { } y)
                target = RoomAuditMath.FaceTowardDeg(point, new P2(x, y));

            if (target == null)
            {
                entry.Status = "blocked";
                entry.Reason = "No target: give angleDeg, rotateByDeg or faceToward {x, y} (faceToward must differ from the element's point).";
                continue;
            }

            entry.TargetValue = RoomGeometryMath.NormalizeDeg(target.Value);

            if (DeviceAdjustSupport.Has(fields, "expectedAngleDeg") &&
                RoomAuditMath.AngleDifferenceDeg(current, ToolArguments.GetDouble(fields, "expectedAngleDeg")) > tolerance)
            {
                entry.Status = "stale";
                entry.Reason = $"Facing is {current:0.##}°, not the expected {ToolArguments.GetDouble(fields, "expectedAngleDeg"):0.##}° — left alone.";
                continue;
            }

            if (RoomAuditMath.AngleDifferenceDeg(current, entry.TargetValue.Value) <= 0.01)
            {
                entry.Status = "unchanged";
                entry.Reason = "Already facing that way.";
                continue;
            }

            entry.Reason = entry.GroupInstance != null
                ? $"Rotate the whole group {RoomAuditMath.DeltaDeg(current, entry.TargetValue.Value):0.##}° about its origin."
                : $"Rotate {RoomAuditMath.DeltaDeg(current, entry.TargetValue.Value):0.##}° about its insertion point.";
            DeviceAdjustSupport.CheckPinned(entry, args);
        }

        return plan;
    }

    /// <summary>
    /// A group instance's entry (#108): its origin and its rotation (LocationPoint.Rotation, CCW from +X). A nested
    /// group is a member of its parent and is blocked with the parent's id.
    /// </summary>
    private static AdjustPlanEntry GroupEntry(Group group)
    {
        var entry = new AdjustPlanEntry { GroupInstance = group };
        if (group.GroupId != ElementId.InvalidElementId)
        {
            string? parentName = null;
            try { parentName = group.Document.GetElement(group.GroupId)?.Name; } catch { /* courtesy only */ }
            entry.Status = "blocked";
            entry.GroupId = group.GroupId.Value;
            entry.Reason = "InGroup: " + MoveElementsMath.GroupMemberReason(group.GroupId.Value, parentName, "rotate");
            return entry;
        }

        if (group.Location is not LocationPoint lp)
        {
            entry.Status = "blocked";
            entry.Reason = "The group has no origin point to rotate about.";
            return entry;
        }

        entry.GroupPoint = new P2(RoomUnits.FtToMm(lp.Point.X), RoomUnits.FtToMm(lp.Point.Y));
        entry.GroupZMm = RoomUnits.FtToMm(lp.Point.Z);
        try { entry.CurrentValue = RoomGeometryMath.NormalizeDeg(lp.Rotation * 180.0 / Math.PI); }
        catch { entry.CurrentValue = null; }
        return entry;
    }

    public static void Apply(Document doc, AdjustPlanEntry entry)
    {
        var element = entry.Target;
        var lp = (LocationPoint)element.Location;
        var delta = RoomAuditMath.DeltaDeg(entry.CurrentValue!.Value, entry.TargetValue!.Value);
        var axis = Line.CreateBound(lp.Point, lp.Point + XYZ.BasisZ);
        ElementTransformUtils.RotateElement(doc, element.Id, axis, delta * Math.PI / 180.0);
    }
}

public sealed class PreviewRotateElementsTool : IRevitMcpTool
{
    public string Name => "revit_preview_rotate_elements";
    public string Description =>
        "Previews rotating elements about their own insertion point (vertical axis) without changes. " +
        "rotations=[{elementId, angleDeg (absolute facing, CCW from +X) | rotateByDeg | faceToward {x, y} (host mm), " +
        "expectedAngleDeg?}] or elementIds + one shared target. expectedAngleDeg is a staleness check within " +
        "angleToleranceDeg (default 0.5). skipPinned (default true). A group instance id rotates the whole group about its " +
        "origin; a group member is blocked as InGroup with its group id.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var warnings = new List<string>();
        var plan = RotatePlanner.Build(doc, request.Arguments, warnings, out var error);
        return Task.FromResult(plan == null
            ? RoomToolSupport.Fail(request, error!)
            : DeviceAdjustSupport.Preview(request, sw, "be rotated", plan, warnings, "°"));
    }
}

public sealed class RotateElementsTool : IRevitMcpTool
{
    public string Name => "revit_rotate_elements";
    public string Description =>
        "Rotates elements about their own insertion point (vertical axis). Requires approval; one undoable transaction. " +
        "Same arguments as revit_preview_rotate_elements (run it first) plus atomic (default true: any failure undoes all).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var warnings = new List<string>();
        var plan = RotatePlanner.Build(doc, request.Arguments, warnings, out var error);
        return Task.FromResult(plan == null
            ? RoomToolSupport.Fail(request, error!)
            : DeviceAdjustSupport.Apply(doc, request, sw, "Revit MCP - Rotate Elements", "rotated", plan, warnings, "°",
                e => RotatePlanner.Apply(doc, e), cancellationToken));
    }
}

// ── Set elevation ───────────────────────────────────────────────────────────────

internal static class ElevationPlanner
{
    public static List<AdjustPlanEntry>? Build(Document doc, Dictionary<string, object?> args, List<string> warnings, out string? error)
    {
        error = null;
        var requests = DeviceAdjustSupport.Requests(args, "elevations");
        if (requests.Count == 0)
        {
            error = "Provide elevations=[{elementId, elevationFromLevelMm, expectedElevationFromLevelMm?}] or elementIds + elevationFromLevelMm.";
            return null;
        }

        var tolerance = ToolArguments.GetDouble(args, "toleranceMm", 1.0);
        var devices = DeviceAudit.Inventory(doc, null, [], requests.Select(r => r.Id).ToList(), warnings).ToDictionary(d => d.Id);
        var plan = new List<AdjustPlanEntry>();

        foreach (var (id, fields) in requests)
        {
            if (!devices.TryGetValue(id, out var device)) continue;
            var current = device.ZMm - device.LevelElevationMm;
            var entry = new AdjustPlanEntry { Device = device, CurrentValue = current };
            plan.Add(entry);

            if (device.Level == null)
            {
                entry.Status = "blocked";
                entry.Reason = "No level found for the element.";
                continue;
            }
            if (!DeviceAdjustSupport.Has(fields, "elevationFromLevelMm"))
            {
                entry.Status = "blocked";
                entry.Reason = "elevationFromLevelMm is missing.";
                continue;
            }

            entry.TargetValue = ToolArguments.GetDouble(fields, "elevationFromLevelMm");
            if (DeviceAdjustSupport.Has(fields, "expectedElevationFromLevelMm"))
            {
                var expected = ToolArguments.GetDouble(fields, "expectedElevationFromLevelMm");
                if (Math.Abs(current - expected) > tolerance)
                {
                    entry.Status = "stale";
                    entry.Reason = $"Elevation is {current:0.#} mm, not the expected {expected:0.#} mm (tolerance {tolerance:0.#}) — left alone.";
                    continue;
                }
            }

            if (Math.Abs(current - entry.TargetValue.Value) < 0.5)
            {
                entry.Status = "unchanged";
                entry.Reason = "Already at that elevation.";
                continue;
            }

            entry.Reason = $"Elevation from level '{device.Level.Name}' {current:0.#} → {entry.TargetValue:0.#} mm.";
            DeviceAdjustSupport.CheckPinned(entry, args);
        }

        return plan;
    }

    public static void Apply(Document doc, AdjustPlanEntry entry, List<string> warnings)
    {
        var device = entry.Device;
        DevicePlacementService.FixElevation(doc, device.Element, device.Level!, device.LevelElevationMm + entry.TargetValue!.Value, warnings);
    }
}

public sealed class PreviewSetElevationTool : IRevitMcpTool
{
    public string Name => "revit_preview_set_elevation";
    public string Description =>
        "Previews setting elements' elevation above their level without changes. elevations=[{elementId, " +
        "elevationFromLevelMm, expectedElevationFromLevelMm?}] or elementIds + elevationFromLevelMm. The expected value " +
        "is a staleness check within toleranceMm (default 1), as in revit_move_elements. skipPinned (default true).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var warnings = new List<string>();
        var plan = ElevationPlanner.Build(doc, request.Arguments, warnings, out var error);
        return Task.FromResult(plan == null
            ? RoomToolSupport.Fail(request, error!)
            : DeviceAdjustSupport.Preview(request, sw, "change elevation", plan, warnings, " mm"));
    }
}

public sealed class SetElevationTool : IRevitMcpTool
{
    public string Name => "revit_set_elevation";
    public string Description =>
        "Sets elements' elevation above their level (Elevation from Level / Offset from Host, else a vertical move). " +
        "Requires approval; one undoable transaction. Same arguments as revit_preview_set_elevation (run it first) " +
        "plus atomic (default true).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var warnings = new List<string>();
        var plan = ElevationPlanner.Build(doc, request.Arguments, warnings, out var error);
        return Task.FromResult(plan == null
            ? RoomToolSupport.Fail(request, error!)
            : DeviceAdjustSupport.Apply(doc, request, sw, "Revit MCP - Set Elevation", "moved to the new elevation", plan, warnings, " mm",
                e => ElevationPlanner.Apply(doc, e, warnings), cancellationToken));
    }
}

// ── Assign room ─────────────────────────────────────────────────────────────────

internal static class AssignRoomPlanner
{
    public static List<AdjustPlanEntry>? Build(Document doc, Dictionary<string, object?> args, List<string> warnings, out string parameterName, out string? error)
    {
        error = null;
        parameterName = string.Empty;

        var elementIds = ToolArguments.GetLongArray(args, "elementIds");
        var onlyCodes = DeviceAudit.Codes(args);
        DeviceCodeContext? codes = null;
        if (elementIds.Length == 0)
        {
            codes = DeviceCodeContext.Load(doc, args, out error);
            if (codes == null)
            {
                error = "Pass elementIds, or set up device codes so devices can be found by code. " + error;
                return null;
            }
        }

        var settings = codes?.Settings ?? RoomToolSupport.Settings(doc, args);
        parameterName = ToolArguments.GetString(args, "roomParameter").Trim();
        if (parameterName.Length == 0) parameterName = settings.RoomParameter;
        if (parameterName.Length == 0)
        {
            error = "No room parameter: pass roomParameter, or set devicePlacement.roomParameter with revit_set_device_codes.";
            return null;
        }

        var service = RoomSourceService.FromArguments(doc, args, out error);
        if (service == null) return null;
        var index = RoomIndex.Build(service, new RoomFilter { LevelName = ToolArguments.GetString(args, "levelName").Trim() }, warnings);
        if (index.Rooms.Count == 0)
        {
            error = $"No rooms found in the {service.Source.Label}.";
            return null;
        }

        var onlyEmpty = ToolArguments.GetBool(args, "onlyEmpty");
        var plan = new List<AdjustPlanEntry>();
        foreach (var device in DeviceAudit.Inventory(doc, codes, onlyCodes, elementIds, warnings))
        {
            var entry = new AdjustPlanEntry { Device = device };
            plan.Add(entry);

            var p = device.Element.LookupParameter(parameterName);
            entry.CurrentText = p?.AsString() ?? string.Empty;
            if (p == null || p.IsReadOnly || p.StorageType != StorageType.String)
            {
                entry.Status = "blocked";
                entry.Reason = $"Parameter '{parameterName}' is missing, read-only or not text.";
                continue;
            }

            var room = index.Locate(device);
            if (room == null)
            {
                entry.Status = "blocked";
                entry.Reason = "The device is not inside any room.";
                continue;
            }

            entry.TargetText = room.Number;
            if (string.Equals(entry.CurrentText, room.Number, StringComparison.Ordinal))
            {
                entry.Status = "unchanged";
                entry.Reason = $"Already '{room.Number}'.";
                continue;
            }
            if (onlyEmpty && entry.CurrentText.Length > 0)
            {
                entry.Status = "unchanged";
                entry.Reason = $"Has '{entry.CurrentText}' and onlyEmpty=true; room found: {room.Number} {room.Name}.";
                continue;
            }

            entry.Reason = $"Room {room.Number} {room.Name}.";
        }

        return plan;
    }
}

public sealed class PreviewAssignRoomToElementsTool : IRevitMcpTool
{
    public string Name => "revit_preview_assign_room_to_elements";
    public string Description =>
        "Previews writing the room number into a text parameter of existing devices, without changes. The room is found " +
        "from each element's insertion point in the room source (source/linkInstanceId: linked Room or IfcSpace, or host " +
        "rooms; levelName). Elements: elementIds, or devices of the device code map (codes[] = only these codes). " +
        "roomParameter (default devicePlacement.roomParameter); onlyEmpty (default false) keeps existing values.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var warnings = new List<string>();
        var plan = AssignRoomPlanner.Build(doc, request.Arguments, warnings, out var parameter, out var error);
        if (plan == null) return Task.FromResult(RoomToolSupport.Fail(request, error!, warnings));
        warnings.Insert(0, $"Room parameter: '{parameter}'.");
        return Task.FromResult(DeviceAdjustSupport.Preview(request, sw, "get a room number", plan, warnings, string.Empty));
    }
}

public sealed class AssignRoomToElementsTool : IRevitMcpTool
{
    public string Name => "revit_assign_room_to_elements";
    public string Description =>
        "Writes the room number of the room each device stands in into its room parameter. Requires approval; one undoable " +
        "transaction. Same arguments as revit_preview_assign_room_to_elements (run it first) plus atomic (default true).";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Parameters;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var target = ToolDocument.Resolve(uiapp, request, out var documentFailure);
        if (target == null) return Task.FromResult(documentFailure!);
        var doc = target.Document;
        var warnings = new List<string>();
        var plan = AssignRoomPlanner.Build(doc, request.Arguments, warnings, out var parameter, out var error);
        if (plan == null) return Task.FromResult(RoomToolSupport.Fail(request, error!, warnings));
        return Task.FromResult(DeviceAdjustSupport.Apply(doc, request, sw, "Revit MCP - Assign Room Numbers", "updated", plan, warnings, string.Empty,
            e => e.Device.Element.LookupParameter(parameter)!.Set(e.TargetText ?? string.Empty), cancellationToken));
    }
}

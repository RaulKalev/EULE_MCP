using System.IO;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Configuration;
using RevitMCP.Addin.Families;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

// ── Device code map ─────────────────────────────────────────────────────────────

public sealed class GetDeviceCodesTool : IRevitMcpTool
{
    public string Name => "revit_get_device_codes";
    public string Description =>
        "Reads the device code map (project config section 'deviceCodes') and the 'devicePlacement' settings, and " +
        "checks each code against the model: ok | missingType (creatable from sourceType) | missingSource | missingFamily. " +
        "projectRoot is optional when a .rktools folder exists above the model file.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Configuration;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));

        var context = DeviceCodeContext.Load(doc, request.Arguments, out var error);
        if (context == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));

        var codes = context.Codes.Values.OrderBy(c => c.Code).Select(c => DeviceCodeContext.Validate(doc, c)).ToList();
        var warnings = context.ParseErrors.ToList();
        var settings = context.Settings;

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{codes.Count} device code(s){(warnings.Count > 0 ? $", {warnings.Count} invalid" : string.Empty)}.",
            new
            {
                configPath = context.ConfigPath,
                settings = new
                {
                    roomParameter = settings.RoomParameter,
                    handOrientationPointsTo = settings.HandPointsToLatch ? "latch" : "hinge",
                    swingTowardFacing = settings.SwingTowardFacing
                },
                codes,
                raw = context.Config?[DeviceCodeMap.SectionName]?.ToJsonString()
            },
            warnings));
    }
}

public sealed class SetDeviceCodesTool : IRevitMcpTool
{
    public string Name => "revit_set_device_codes";
    public string Description =>
        "Writes the device code map into the project config (section 'deviceCodes'). Requires approval; backs the file up first. " +
        "deviceCodes: {CODE: {family, type, mount: wall|ceiling|floor, heightMm, offsetFromWallMm, offsetFromCeilingMm, " +
        "doorSide: lock|hinge, doorOffsetMm, rotationOffsetDeg, avoidCategories[], clearanceMm, maxSpacingMm, " +
        "maxDistFromWallMm, sourceType ('Type' or 'Family : Type'), typeParameters{}}}. replace=false (default) merges " +
        "by code; removeCodes[] deletes. Optional settings: {roomParameter, handOrientationPointsTo: latch|hinge, " +
        "swingTowardFacing}. Every entry is validated; invalid input writes nothing.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Configuration;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;

        var root = ProjectConfigLocator.FindProjectRoot(doc, ToolArguments.GetString(args, "projectRoot"));
        if (root == null)
            return Task.FromResult(RoomToolSupport.Fail(request,
                "No project root: pass projectRoot (the folder that will hold .rktools\\mcp.project.config.json)."));
        var (path, pathError) = ConfigPathResolver.Resolve(ConfigPathResolver.ScopeProject, root);
        if (path == null) return Task.FromResult(RoomToolSupport.Fail(request, pathError!));

        var updates = DeviceCodeContext.InlineCodes(args) ?? new JsonObject();
        var remove = ToolArguments.GetStringArray(args, "removeCodes");
        var replace = ToolArguments.GetBool(args, "replace");
        var settingsNode = InlineObject(args, "settings");
        if (updates.Count == 0 && remove.Length == 0 && settingsNode == null)
            return Task.FromResult(RoomToolSupport.Fail(request, "Nothing to write: pass deviceCodes, removeCodes or settings."));

        var service = new JsonConfigService();
        var (existing, readError) = File.Exists(path) ? service.Read(path) : (new JsonObject(), null);
        if (existing == null) return Task.FromResult(RoomToolSupport.Fail(request, readError ?? "Could not read the project config."));

        var merged = DeviceCodeMap.Merge(existing[DeviceCodeMap.SectionName] as JsonObject, updates, remove, replace);
        var errors = new List<string>();
        var parsed = DeviceCodeMap.Parse(merged, errors);
        if (errors.Count > 0)
            return Task.FromResult(RoomToolSupport.Fail(request, $"{errors.Count} invalid device code(s) — nothing written.", errors));

        existing[DeviceCodeMap.SectionName] = merged;
        if (settingsNode != null)
        {
            if (existing[DeviceCodeMap.SettingsSectionName] is not JsonObject current)
            {
                current = new JsonObject();
                existing[DeviceCodeMap.SettingsSectionName] = current;
            }
            foreach (var pair in settingsNode)
                current[pair.Key] = pair.Value?.DeepClone();
        }

        var (ok, writeError, backup) = service.Write(path, existing.ToJsonString(), ToolArguments.GetBool(args, "backupBeforeOverwrite", true));
        if (!ok) return Task.FromResult(RoomToolSupport.Fail(request, writeError ?? "Write failed."));

        var status = parsed.Values.OrderBy(c => c.Code).Select(c => DeviceCodeContext.Validate(doc, c)).ToList();
        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"Saved {parsed.Count} device code(s) to {path}.",
            new { configPath = path, backupPath = backup, codes = status },
            []));
    }

    private static JsonObject? InlineObject(Dictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var raw) || raw == null) return null;
        try
        {
            var text = raw is string s ? s : Newtonsoft.Json.JsonConvert.SerializeObject(raw);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch { return null; }
    }
}

// ── Ensure device types ─────────────────────────────────────────────────────────

internal static class DeviceTypePlanner
{
    internal sealed class Entry
    {
        public DeviceCode Code { get; set; } = null!;
        public FamilySymbol? Existing { get; set; }
        public FamilySymbol? Source { get; set; }
        public string Action { get; set; } = "exists";
        public string Reason { get; set; } = string.Empty;
        public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static List<Entry>? Build(Document doc, Dictionary<string, object?> args, out DeviceCodeContext? context, out string? error)
    {
        context = DeviceCodeContext.Load(doc, args, out error);
        if (context == null) return null;

        var only = ToolArguments.GetStringArray(args, "codes");
        var setComments = ToolArguments.GetBool(args, "setTypeCommentsToCode");
        var entries = new List<Entry>();

        foreach (var code in context.Codes.Values.OrderBy(c => c.Code))
        {
            if (only.Length > 0 && !only.Contains(code.Code, StringComparer.OrdinalIgnoreCase)) continue;
            var entry = new Entry { Code = code, Existing = DeviceCodeContext.FindSymbol(doc, code.Family, code.Type) };
            foreach (var p in code.TypeParameters) entry.Parameters[p.Key] = p.Value;
            if (setComments && !entry.Parameters.ContainsKey("Type Comments")) entry.Parameters["Type Comments"] = code.Code;

            if (entry.Existing != null)
            {
                entry.Action = entry.Parameters.Count > 0 && ToolArguments.GetBool(args, "updateExisting") ? "update" : "exists";
                entry.Reason = entry.Action == "update" ? "Type exists; its parameters will be updated." : "Type already exists.";
            }
            else if (code.SourceType == null)
            {
                entry.Action = "blocked";
                entry.Reason = $"'{code.Family} : {code.Type}' is missing and the code has no sourceType.";
            }
            else
            {
                var (sf, st) = code.SourceFamilyAndType();
                entry.Source = DeviceCodeContext.FindSymbol(doc, sf, st);
                if (entry.Source == null)
                {
                    entry.Action = "blocked";
                    entry.Reason = $"sourceType '{sf} : {st}' is not loaded.";
                }
                else if (!string.Equals(sf, code.Family, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Action = "blocked";
                    entry.Reason = $"sourceType is in family '{sf}', but the code needs family '{code.Family}' — a duplicate stays in its own family.";
                }
                else if (!FamilyTypeNamePlanner.IsValidTypeName(code.Type, out var nameError))
                {
                    entry.Action = "blocked";
                    entry.Reason = nameError;
                }
                else
                {
                    entry.Action = "create";
                    entry.Reason = $"Will duplicate '{sf} : {st}' as '{code.Type}'.";
                }
            }

            entries.Add(entry);
        }

        if (entries.Count == 0)
            error = only.Length > 0 ? "None of the requested codes exist in the device code map." : "The device code map is empty.";
        return entries.Count == 0 ? null : entries;
    }

    public static object Payload(Entry e, IEnumerable<object>? parameters = null, long? typeId = null) => new
    {
        code = e.Code.Code,
        family = e.Code.Family,
        type = e.Code.Type,
        action = e.Action,
        reason = e.Reason,
        sourceTypeId = e.Source?.Id.Value,
        typeId = typeId ?? e.Existing?.Id.Value,
        parameters
    };
}

public sealed class PreviewEnsureDeviceTypesTool : IRevitMcpTool
{
    public string Name => "revit_preview_ensure_device_types";
    public string Description =>
        "Previews revit_ensure_device_types without changes: per device code, whether its family type exists, will be " +
        "created by duplicating sourceType (same family), or is blocked; plus a check of every typeParameters value. " +
        "Args: codes[] (default all), setTypeCommentsToCode, updateExisting, projectRoot, deviceCodes (inline override).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));

        var entries = DeviceTypePlanner.Build(doc, request.Arguments, out var context, out var error);
        if (entries == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));

        var warnings = context!.ParseErrors.ToList();
        var payload = entries.Select(e =>
        {
            var target = (Element?)e.Source ?? e.Existing;
            var checks = e.Action is "create" or "update" && target != null
                ? e.Parameters.Select(p => FamilyTypeSupport.CheckParameter(target, p.Key, p.Value)).ToList()
                : [];
            warnings.AddRange(checks.Where(c => !c.WillSucceed).Select(c => $"{e.Code.Code}: {c.Name} — {c.Message}"));
            return DeviceTypePlanner.Payload(e, checks.Select(c => c.ToPayload()));
        }).ToList();

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"Preview: {entries.Count(e => e.Action == "create")} to create, {entries.Count(e => e.Action == "exists")} exist, " +
            $"{entries.Count(e => e.Action == "update")} to update, {entries.Count(e => e.Action == "blocked")} blocked.",
            new { types = payload }, warnings));
    }
}

public sealed class EnsureDeviceTypesTool : IRevitMcpTool
{
    public string Name => "revit_ensure_device_types";
    public string Description =>
        "Creates the family types the device code map needs but the model lacks, by duplicating each code's sourceType " +
        "inside the same family and naming the copy after the code's type; then writes typeParameters. Requires approval; " +
        "one undoable transaction. Args as revit_preview_ensure_device_types — run that first.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));

        var entries = DeviceTypePlanner.Build(doc, request.Arguments, out var context, out var error);
        if (entries == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));

        var warnings = context!.ParseErrors.ToList();
        var results = new List<object>();
        int created = 0, updated = 0;

        using var transaction = new Transaction(doc, "Revit MCP - Ensure Device Types");
        transaction.Start();
        foreach (var e in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (e.Action is "exists" or "blocked")
            {
                results.Add(DeviceTypePlanner.Payload(e));
                continue;
            }

            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var type = e.Action == "create"
                    ? (e.Source!.Duplicate(e.Code.Type) as FamilySymbol ?? throw new InvalidOperationException("Revit did not return the duplicated type."))
                    : e.Existing!;
                var parameterResults = e.Parameters.Select(p => FamilyTypeSupport.SetParameter(doc, type, p.Key, p.Value)).ToList();
                sub.Commit();
                if (e.Action == "create") created++; else updated++;
                warnings.AddRange(parameterResults.Where(r => !r.Succeeded).Select(r => $"{e.Code.Code}: {r.Name} — {r.Message}"));
                results.Add(DeviceTypePlanner.Payload(e, parameterResults.Select(r => r.ToPayload()), type.Id.Value));
            }
            catch (Exception ex)
            {
                if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                warnings.Add($"{e.Code.Code}: {ex.Message}");
                e.Reason = ex.Message;
                e.Action = "failed";
                results.Add(DeviceTypePlanner.Payload(e));
            }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        var result = RoomToolSupport.Ok(request, sw, $"Created {created} type(s), updated {updated}.", new { created, updated, types = results }, warnings);
        result.Success = entries.All(e => e.Action != "failed");
        return Task.FromResult(result);
    }
}

// ── Placement ───────────────────────────────────────────────────────────────────

internal static class PlacementToolSupport
{
    public static McpToolResult Preview(McpToolRequest request, Stopwatch sw, PlacementPlan plan)
    {
        var placeable = plan.Devices.Count(d => d.CanPlace);
        var flagged = plan.Devices.Count(d => d.CanPlace && d.Warnings.Count > 0);
        sw.Stop();
        return RoomToolSupport.Ok(request, sw,
            $"Preview: {placeable} of {plan.Devices.Count} device(s) can be placed ({flagged} with warnings) from the {plan.Rooms.Source.Label}.",
            new
            {
                source = plan.Rooms.Source.Label,
                total = plan.Devices.Count,
                placeable,
                withWarnings = flagged,
                blocked = plan.Devices.Count - placeable,
                rooms = plan.RoomNotes,
                devices = plan.Devices.Select(d => d.ToPayload()).ToList()
            },
            plan.Warnings);
    }

    public static McpToolResult Apply(Document doc, McpToolRequest request, Stopwatch sw, PlacementPlan plan, string transactionName, CancellationToken ct)
    {
        var warnings = plan.Warnings;
        var skipWarned = ToolArguments.GetBool(request.Arguments, "skipDevicesWithWarnings");
        var results = new List<object>();
        var createdIds = new List<long>();

        using var transaction = new Transaction(doc, transactionName);
        transaction.Start();
        foreach (var device in plan.Devices)
        {
            ct.ThrowIfCancellationRequested();
            if (!device.CanPlace || (skipWarned && device.Warnings.Count > 0))
            {
                if (device.CanPlace) device.Blocked = "Skipped: has warnings (skipDevicesWithWarnings=true).";
                results.Add(device.ToPayload());
                continue;
            }

            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                var instance = DevicePlacementService.Create(doc, device, plan.Codes.Settings, warnings);
                sub.Commit();
                createdIds.Add(instance.Id.Value);
                results.Add(device.ToPayload(instance.Id.Value));
            }
            catch (Exception ex)
            {
                if (sub.GetStatus() == TransactionStatus.Started) sub.RollBack();
                results.Add(device.ToPayload(error: ex.Message));
            }
        }
        RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(transaction);

        sw.Stop();
        var result = RoomToolSupport.Ok(request, sw,
            $"Placed {createdIds.Count} of {plan.Devices.Count} device(s).",
            new { placed = createdIds.Count, total = plan.Devices.Count, createdElementIds = createdIds, rooms = plan.RoomNotes, devices = results },
            warnings);
        result.Success = createdIds.Count > 0;
        return result;
    }
}

public sealed class PreviewPlaceAtWallTool : IRevitMcpTool
{
    public string Name => "revit_preview_place_at_wall";
    public string Description =>
        "Previews unhosted wall-device placement against room walls (host or linked/IFC rooms) without changes. " +
        "placements=[{deviceCode, roomNumber, levelName?, wall: wallIndex | wallId | nearestToPoint{x,y} | nearDoorId, " +
        "position: alongMm | alongFraction | nearDoorId + doorSide (lock|hinge) + offsetMm, heightMm?, count?, spacingMm?}]. " +
        "Common: source/linkInstanceId, projectRoot, deviceCodes (inline). Point = inner face + normal × offsetFromWallMm; " +
        "the device faces into the room; z = level + heightMm. Warns about openings (±100 mm), door leaves, corners (<200 mm), " +
        "points outside the room and duplicates (<200 mm).";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var plan = DevicePlacementService.PlanAtWall(doc, request.Arguments, out var error);
        return Task.FromResult(plan == null ? RoomToolSupport.Fail(request, error!) : PlacementToolSupport.Preview(request, sw, plan));
    }
}

public sealed class PlaceAtWallTool : IRevitMcpTool
{
    public string Name => "revit_place_at_wall";
    public string Description =>
        "Places unhosted wall devices by device code against room walls. Requires approval; one undoable transaction, " +
        "each device in its own sub-transaction. Same arguments as revit_preview_place_at_wall (run it first), plus " +
        "skipDevicesWithWarnings (default false) and roomParameter (overrides devicePlacement.roomParameter). " +
        "Writes the room number to the room parameter when configured.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var plan = DevicePlacementService.PlanAtWall(doc, request.Arguments, out var error);
        return Task.FromResult(plan == null
            ? RoomToolSupport.Fail(request, error!)
            : PlacementToolSupport.Apply(doc, request, sw, plan, "Revit MCP - Place Devices at Walls", cancellationToken));
    }
}

public sealed class PreviewPlaceInRoomTool : IRevitMcpTool
{
    public string Name => "revit_preview_place_in_room";
    public string Description =>
        "Previews unhosted device placement inside rooms without changes. deviceCode; rooms: roomNumbers[] or roomFilter " +
        "(name or regex), levelName; strategy: center (interior point, safe for L-shapes) | grid (maxSpacingMm, " +
        "maxDistFromWallMm, coverageRadiusMm — defaults from the code; reports uncovered area) | nearDoor (side inside|outside, " +
        "doorSide lock|hinge, offsetMm) | points (points=[{u,v}] 0…1 in the room bounding box). Ceiling codes: z = ceiling − " +
        "offsetFromCeilingMm (room height if no ceiling, with a warning); others: level + heightMm. Warns about avoidCategories " +
        "within clearanceMm, points outside the room and duplicates. Common: source/linkInstanceId, projectRoot, deviceCodes, heightMm.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var plan = DevicePlacementService.PlanInRoom(doc, request.Arguments, out var error);
        return Task.FromResult(plan == null ? RoomToolSupport.Fail(request, error!) : PlacementToolSupport.Preview(request, sw, plan));
    }
}

public sealed class PlaceInRoomTool : IRevitMcpTool
{
    public string Name => "revit_place_in_room";
    public string Description =>
        "Places unhosted devices by device code inside rooms (center | grid | nearDoor | points). Requires approval; one " +
        "undoable transaction. Same arguments as revit_preview_place_in_room (run it first), plus skipDevicesWithWarnings " +
        "and roomParameter. Writes the room number to the room parameter when configured.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var plan = DevicePlacementService.PlanInRoom(doc, request.Arguments, out var error);
        return Task.FromResult(plan == null
            ? RoomToolSupport.Fail(request, error!)
            : PlacementToolSupport.Apply(doc, request, sw, plan, "Revit MCP - Place Devices in Rooms", cancellationToken));
    }
}

using System.Diagnostics;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.RoomDevices;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools;

/// <summary>Shared plumbing for the room/device tools (issue #52).</summary>
internal static class RoomToolSupport
{
    public static McpToolResult Fail(McpToolRequest request, string message, List<string>? warnings = null) =>
        new() { RequestId = request.RequestId, Success = false, Message = message, Warnings = warnings ?? [] };

    public static McpToolResult Ok(McpToolRequest request, Stopwatch sw, string message, object data, List<string> warnings) =>
        new()
        {
            RequestId = request.RequestId,
            Success = true,
            Message = message,
            Data = data,
            Warnings = warnings,
            DurationMs = sw.ElapsedMilliseconds
        };

    /// <summary>Settings come from the project config when one is found; defaults otherwise.</summary>
    public static DevicePlacementSettings Settings(Document doc, Dictionary<string, object?> args)
    {
        var (config, _, _) = ProjectConfigLocator.Read(doc, ToolArguments.GetString(args, "projectRoot"));
        return DevicePlacementSettings.From(config);
    }
}

public sealed class ListLevelsTool : IRevitMcpTool
{
    public string Name => "revit_list_levels";
    public string Description =>
        "Lists host model levels (id, name, elevationMm, projectElevationMm) and, with linkInstanceId, the link's " +
        "levels mapped to host levels by elevation (link levels transformed to host coordinates). " +
        "projectElevationMm is in host internal coordinates — the same frame as all room/placement coordinates.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var warnings = new List<string>();

        var host = new RoomSourceService(new RoomSource { HostDoc = doc, Doc = doc });
        var hostLevels = host.HostLevels.Select(l => new
        {
            id = l.Level.Id.Value,
            name = l.Level.Name,
            elevationMm = Math.Round(RoomUnits.FtToMm(l.Level.Elevation), 1),
            projectElevationMm = Math.Round(RoomUnits.FtToMm(l.Level.ProjectElevation), 1)
        }).ToList();

        object? linkLevels = null;
        var linkId = ToolArguments.GetLong(request.Arguments, "linkInstanceId");
        if (linkId != 0)
        {
            var source = RoomSourceService.ResolveSource(doc, "link", linkId, out var error);
            if (source == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));

            linkLevels = new FilteredElementCollector(source.Doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.ProjectElevation)
                .Select(l =>
                {
                    var hostZ = source.ToHostZMm(new XYZ(0, 0, l.ProjectElevation));
                    var match = host.HostLevelFor(hostZ);
                    var delta = match != null ? hostZ - RoomUnits.FtToMm(match.ProjectElevation) : (double?)null;
                    if (delta is { } d && Math.Abs(d) > 50)
                        warnings.Add($"Link level '{l.Name}' is {d:0} mm from host level '{match!.Name}'.");
                    return new
                    {
                        id = l.Id.Value,
                        name = l.Name,
                        hostElevationMm = Math.Round(hostZ, 1),
                        hostLevelId = match?.Id.Value,
                        hostLevelName = match?.Name,
                        offsetFromHostLevelMm = delta is { } dd ? Math.Round(dd, 1) : (double?)null
                    };
                })
                .ToList();
        }

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{hostLevels.Count} host level(s).", new { hostLevels, linkLevels }, warnings));
    }
}

public sealed class GetRoomGeometryTool : IRevitMcpTool
{
    public string Name => "revit_get_room_geometry";
    public string Description =>
        "Reads room geometry for device placement, in host internal coordinates (mm). source=host|link " +
        "(+linkInstanceId; IFC links without Room elements fall back to IfcSpace geometry). Filters: levelName, " +
        "roomNumbers[], nameFilter (name or regex). Per room: number, name, level, areaM2, heightMm, ceilingHeightMm " +
        "(lowest linked ceiling above the room, null if none), boundary (outer clockwise + holes), centroid, " +
        "interiorPoint, doors[] (location, width, wallIndex, along range, facingIntoRoom, swingIntoRoom, hingeSide, " +
        "lockSide) and windows[] (sill height). Options includeDoors/includeWindows/includeCeilingHeight (default true). " +
        "Read-only; values are live from the model.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        var service = RoomSourceService.FromArguments(doc, args, out var error);
        if (service == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));

        var limit = Math.Max(1, ToolArguments.GetInt(args, "limit", 200));
        var rooms = service.LoadRooms(RoomSourceService.ParseFilter(args), warnings, limit);
        var settings = RoomToolSupport.Settings(doc, args);
        var includeDoors = ToolArguments.GetBool(args, "includeDoors", true);
        var includeWindows = ToolArguments.GetBool(args, "includeWindows", true);
        var includeCeiling = ToolArguments.GetBool(args, "includeCeilingHeight", true);

        var payload = new List<object>();
        foreach (var room in rooms)
        {
            cancellationToken.ThrowIfCancellationRequested();
            payload.Add(RoomAnalysis.RoomPayload(service, room, settings, includeDoors, includeWindows, includeCeiling));
        }

        if (includeDoors && rooms.Count > 0)
            warnings.Add("Door hingeSide/lockSide/swingIntoRoom follow the devicePlacement conventions " +
                         $"(handOrientationPointsTo={(settings.HandPointsToLatch ? "latch" : "hinge")}, swingTowardFacing={settings.SwingTowardFacing}); " +
                         "verify on one door with revit_export_view_image.");

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"{rooms.Count} room(s) from the {service.Source.Label}.",
            new { source = service.Source.Label, rooms = payload, truncated = rooms.Count >= limit },
            warnings));
    }
}

public sealed class GetRoomWallsTool : IRevitMcpTool
{
    public string Name => "revit_get_room_walls";
    public string Description =>
        "Lists the room-side wall faces of one room (host internal mm): index (use as wallIndex in " +
        "revit_place_at_wall), wallId (bounding wall in the source model, null for room separation lines), " +
        "typeName, thicknessMm, axis, innerFace start/end, normalIntoRoom, lengthMm, isCurved and openings " +
        "(doors/windows as fromMm/toMm along the face). Args: roomNumber (or roomId), source/linkInstanceId, levelName.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory Category => ToolCategory.Elements;

    public Task<McpToolResult> ExecuteAsync(UIApplication uiapp, McpToolRequest request, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(RoomToolSupport.Fail(request, "No active document."));
        var args = request.Arguments;
        var warnings = new List<string>();

        var service = RoomSourceService.FromArguments(doc, args, out var error);
        if (service == null) return Task.FromResult(RoomToolSupport.Fail(request, error!));

        var roomId = ToolArguments.GetLong(args, "roomId");
        var filter = RoomSourceService.ParseFilter(args);
        if (roomId == 0 && filter.RoomNumbers.Length == 0)
            return Task.FromResult(RoomToolSupport.Fail(request, "Provide roomNumber or roomId."));

        var rooms = service.LoadRooms(roomId != 0 ? new RoomFilter { LevelName = filter.LevelName } : filter, warnings, roomId != 0 ? 5000 : 10);
        var room = roomId != 0 ? rooms.FirstOrDefault(r => r.SourceId == roomId) : rooms.FirstOrDefault();
        if (room == null)
            return Task.FromResult(RoomToolSupport.Fail(request, $"Room not found in the {service.Source.Label}.", warnings));
        if (roomId == 0 && rooms.Count > 1)
            warnings.Add($"{rooms.Count} rooms match; the first ({room.HostLevelName}) is used — pass levelName.");

        var walls = RoomAnalysis.WallsPayload(service, room, RoomToolSupport.Settings(doc, args), warnings);

        sw.Stop();
        return Task.FromResult(RoomToolSupport.Ok(request, sw,
            $"Room {room.Number}: {walls.Count} wall face(s).",
            new { room = RoomAnalysis.RoomSummary(room), walls },
            warnings));
    }
}

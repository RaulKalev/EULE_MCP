using System.ComponentModel;
using ModelContextProtocol.Server;

namespace RevitMCP.Bridge;

/// <summary>MEP routing: cable tray and conduit runs, fittings, connections and length summaries (docs/mep-routing.md, #106).</summary>
internal sealed partial class RevitMcpTools
{
    [McpServerTool(Name = "revit_list_mep_curve_types", ReadOnly = true),
     Description("Lists the cable tray and conduit types: id, name, kind, family, and per fitting kind (elbow, verticalElbow, tee, cross, transition, union) whether a fitting family is assigned (available|missing). A type with missing elbows is flagged: runs drawn with it get their straight segments but fitting_failed at the bends. Sizes: conduits list their standard's nominal diameters in mm (diameterMm must be one of them); cable tray width/height are free values, with the project's tray size table as the usual choices.")]
    public async Task<string> ListMepCurveTypes(
        [Description("cableTray or conduit; empty = both.")] string? kind = null,
        [Description("Include conduit standard sizes and the cable tray size table. Default true.")] bool includeSizes = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["kind"] = kind ?? string.Empty,
            ["includeSizes"] = includeSizes,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_list_mep_curve_types", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_preview_create_mep_run", ReadOnly = true),
     Description("Previews a cable tray or conduit run along a polyline without changing the model. Same arguments as revit_create_mep_run. Returns the normalized polyline (collinear points and segments shorter than minSegmentMm merged, zero-length segments dropped), segments with lengths, bends with angles and the elbows that will be attempted, the type's fitting status (routing_preference_missing names missing kinds), open connectors within snapMm of each run end, the level and the total length.")]
    public Task<string> PreviewCreateMepRun(
        [Description("cableTray or conduit.")] string kind,
        [Description("Type name or id (revit_list_mep_curve_types).")] string type,
        [Description("JSON array of at least 2 points {x, y, z} in mm, model coordinates. z may be left out when level and offsetMm are given (z = level elevation + offsetMm).")] string points,
        [Description("Level name or id. Default: the level nearest the first point.")] string? level = null,
        [Description("Height above the level in mm for points given without z.")] double? offsetMm = null,
        [Description("Cable tray width in mm.")] double? widthMm = null,
        [Description("Cable tray height in mm.")] double? heightMm = null,
        [Description("Conduit diameter in mm: a standard size of the type.")] double? diameterMm = null,
        [Description("auto (default: an elbow at every bend) or none.")] string fittings = "auto",
        [Description("Join the run start: 'auto' (nearest open end of the same kind within snapMm), an element id, or empty/none.")] string? connectStart = null,
        [Description("Join the run end: 'auto', an element id, or empty/none.")] string? connectEnd = null,
        [Description("Search radius for connect 'auto', mm. Default 50.")] double snapMm = 50,
        [Description("Segments shorter than this are merged, mm. Default 100.")] double minSegmentMm = 100,
        [Description("Workset name or id (workshared models). Default: the active workset.")] string? workset = null,
        [Description("true (default): a segment Revit refuses undoes the whole run; false: keep the segments that succeed.")] bool atomic = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendMepRun("revit_preview_create_mep_run", kind, type, points, level, offsetMm, widthMm, heightMm, diameterMm, fittings,
            connectStart, connectEnd, snapMm, minSegmentMm, workset, atomic, document, cancellationToken);

    [McpServerTool(Name = "revit_create_mep_run"),
     Description("Draws a cable tray or conduit run along a polyline (requires approval): one CableTray/Conduit per straight segment, an elbow at every bend when fittings=auto, and optional joins to existing open tray/conduit ends at the start and end (connectStart/connectEnd = 'auto' within snapMm, or an element id). Points are mm in model coordinates; z may come from level + offsetMm. Sizes: widthMm/heightMm (tray), diameterMm (conduit, standard size). A failed elbow never removes the straight segments: the bend reports fitting_failed with Revit's reason, and the run's open ends can be joined later with revit_connect_mep_curves. Returns runId (stored on every element; revit_get_mep_run_summary runId=... finds the run again), segments, fittings, connections and the total length. One transaction, one undo. Run revit_preview_create_mep_run first.")]
    public Task<string> CreateMepRun(
        [Description("cableTray or conduit.")] string kind,
        [Description("Type name or id (revit_list_mep_curve_types).")] string type,
        [Description("JSON array of at least 2 points {x, y, z} in mm, model coordinates. z may be left out when level and offsetMm are given (z = level elevation + offsetMm).")] string points,
        [Description("Level name or id. Default: the level nearest the first point.")] string? level = null,
        [Description("Height above the level in mm for points given without z.")] double? offsetMm = null,
        [Description("Cable tray width in mm.")] double? widthMm = null,
        [Description("Cable tray height in mm.")] double? heightMm = null,
        [Description("Conduit diameter in mm: a standard size of the type.")] double? diameterMm = null,
        [Description("auto (default: an elbow at every bend) or none.")] string fittings = "auto",
        [Description("Join the run start: 'auto' (nearest open end of the same kind within snapMm), an element id, or empty/none.")] string? connectStart = null,
        [Description("Join the run end: 'auto', an element id, or empty/none.")] string? connectEnd = null,
        [Description("Search radius for connect 'auto', mm. Default 50.")] double snapMm = 50,
        [Description("Segments shorter than this are merged, mm. Default 100.")] double minSegmentMm = 100,
        [Description("Workset name or id (workshared models). Default: the active workset.")] string? workset = null,
        [Description("true (default): a segment Revit refuses undoes the whole run; false: keep the segments that succeed.")] bool atomic = true,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default) =>
        SendMepRun("revit_create_mep_run", kind, type, points, level, offsetMm, widthMm, heightMm, diameterMm, fittings,
            connectStart, connectEnd, snapMm, minSegmentMm, workset, atomic, document, cancellationToken);

    private async Task<string> SendMepRun(
        string toolName, string kind, string type, string points, string? level, double? offsetMm, double? widthMm, double? heightMm,
        double? diameterMm, string fittings, string? connectStart, string? connectEnd, double snapMm, double minSegmentMm,
        string? workset, bool atomic, string? document, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(points))
            return FormatBridgeError("Provide 'points': a JSON array of at least 2 points {x, y, z} in mm.");
        if (!TryParseJsonArray(points, "points", out var parsedPoints, out var pointsError))
            return FormatBridgeError(pointsError!);

        var args = new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["type"] = type,
            ["points"] = parsedPoints,
            ["level"] = level ?? string.Empty,
            ["offsetMm"] = offsetMm,
            ["widthMm"] = widthMm,
            ["heightMm"] = heightMm,
            ["diameterMm"] = diameterMm,
            ["fittings"] = fittings,
            ["connectStart"] = connectStart ?? string.Empty,
            ["connectEnd"] = connectEnd ?? string.Empty,
            ["snapMm"] = snapMm,
            ["minSegmentMm"] = minSegmentMm,
            ["workset"] = workset ?? string.Empty,
            ["atomic"] = atomic,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync(toolName, args, cancellationToken));
    }

    [McpServerTool(Name = "revit_connect_mep_curves"),
     Description("Joins the open ends of two cable trays or two conduits (requires approval). mode: auto (default: touching in line = connected directly, in line with a gap = A extended to B, different sizes in line = transition, crossing at an angle = elbow), elbow, union, or extend (A's end moved onto B's end when in line within 1°). The two open ends nearest each other are used. Reports connected, already_connected, skew_lines (lines pass each other or run side by side: no single fitting joins them), too_far (beyond maxGapMm) or fitting_failed with Revit's reason, plus the geometry (relation, gap, angle).")]
    public async Task<string> ConnectMepCurves(
        [Description("Element id of the first tray or conduit (the one extended in extend/auto).")] long elementIdA,
        [Description("Element id of the second tray or conduit, same kind.")] long elementIdB,
        [Description("auto (default), elbow, union or extend.")] string mode = "auto",
        [Description("The furthest an end may travel to meet the other, mm. Default 5000.")] double maxGapMm = 5000,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["elementIdA"] = elementIdA,
            ["elementIdB"] = elementIdB,
            ["mode"] = mode,
            ["maxGapMm"] = maxGapMm,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_connect_mep_curves", args, cancellationToken));
    }

    [McpServerTool(Name = "revit_get_mep_run_summary", ReadOnly = true),
     Description("Summarises cable trays and conduits: count and length (mm and m) per type, size (300x60, Ø25), level, workset and type x size x level x workset (Revit's Length parameter, as a schedule sums it); fittings by family type; open ends (unconnected end connectors) with positions in mm, up to 500; disconnected segments (every end open), up to 500. Filters: kind, types, levels, viewId, runId (from revit_create_mep_run). Publish the open-ends list with revit_publish_page (one row per end, data-revit-id) so the user can jump to each one.")]
    public async Task<string> GetMepRunSummary(
        [Description("cableTray or conduit; empty = both.")] string? kind = null,
        [Description("Type names or ids to include (segments only).")] string[]? types = null,
        [Description("Level names or ids to include.")] string[]? levels = null,
        [Description("Only what this view shows.")] long viewId = 0,
        [Description("Only the elements of this run (data.runId of revit_create_mep_run).")] string? runId = null,
        [Description(DocumentArgDescription)] string? document = null,
        CancellationToken cancellationToken = default)
    {
        var args = new Dictionary<string, object?>
        {
            ["kind"] = kind ?? string.Empty,
            ["types"] = types ?? [],
            ["levels"] = levels ?? [],
            ["viewId"] = viewId,
            ["runId"] = runId ?? string.Empty,
            ["document"] = document ?? string.Empty
        };
        return FormatResult(await pipeClient.SendAsync("revit_get_mep_run_summary", args, cancellationToken));
    }
}

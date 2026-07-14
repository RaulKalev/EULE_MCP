using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;                                           // ToolArguments
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom;

/// <summary>
/// MCP tool: validate_ifc_space_room_conversion
///
/// Phase 4 — Read-only comparison of linked IFC Spaces against existing native Revit Rooms.
/// Reports match confidence, detects missing Rooms, possible renames, and geometry changes.
/// Uses built-in Room fields only (Level, Number, Name, Location, Area).
/// No shared parameters, IFC GUIDs, Comments, or Extensible Storage are read or written.
/// Model is never modified by this tool.
/// </summary>
public class ValidateIfcSpaceRoomConversionTool : IRevitMcpTool
{
    public string Name        => "validate_ifc_space_room_conversion";
    public string Description =>
        "Phase 4 — Compares linked IFC Space elements against existing native Revit Rooms using " +
        "built-in fields only (Level, Number, Name, Location, Area). " +
        "Returns per-space match confidence (High/Medium/Low/Ambiguous/None), " +
        "location and area deltas, detected renames, geometry changes, and missing Rooms. " +
        "Read-only — makes no model changes. " +
        "Recommended workflow: run this after convert_ifc_spaces_to_rooms to verify results, " +
        "or to identify Rooms that need a data sync after IFC model updates. " +
        "Follow up with sync_ifc_space_room_data to apply controlled built-in field updates.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Coordination;

    private readonly IfcRoomValidationService _service = new();

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active Revit document."));

        // ── Parse arguments ────────────────────────────────────────────────────
        var linkInstanceId = ToolArguments.GetLong(request.Arguments, "linkInstanceId", 0L);
        if (linkInstanceId == 0L)
        {
            return Task.FromResult(Fail(request,
                "Required argument 'linkInstanceId' is missing or zero. " +
                "Call ifc_list_links first to obtain a valid linkInstanceId."));
        }

        var linkedElementIds = ToolArguments.GetLongArray(request.Arguments, "linkedElementIds");

        var options = new IfcRoomValidationOptions
        {
            LinkInstanceId            = linkInstanceId,
            LinkedElementIds          = linkedElementIds.ToList(),
            LevelMatchToleranceMm     = ToolArguments.GetDouble(request.Arguments, "levelMatchToleranceMm",     300.0),
            LocationToleranceMm       = ToolArguments.GetDouble(request.Arguments, "locationToleranceMm",       1000.0),
            AreaTolerancePercent      = ToolArguments.GetDouble(request.Arguments, "areaTolerancePercent",      10.0),
            IncludeGeometryComparison = ToolArguments.GetBool(request.Arguments,   "includeGeometryComparison", true),
            EndpointSnapToleranceMm   = ToolArguments.GetDouble(request.Arguments, "endpointSnapToleranceMm",   3.0),
            TinySegmentToleranceMm    = ToolArguments.GetDouble(request.Arguments, "tinySegmentToleranceMm",    1.0),
            MaxResults                = ToolArguments.GetInt(request.Arguments,    "maxResults",                1000),
            RoomNameParameter         = ToolArguments.GetString(request.Arguments, "roomNameParameter", ""),
            RoomNumberParameter       = ToolArguments.GetString(request.Arguments, "roomNumberParameter", ""),
            StoreyParameter           = ToolArguments.GetString(request.Arguments, "storeyParameter", ""),
            AreaParameter             = ToolArguments.GetString(request.Arguments, "areaParameter", ""),
            EnableArRuumDefaults      = ToolArguments.GetBool(request.Arguments, "enableArRuumDefaults", true)
        };

        // ── Run validation ─────────────────────────────────────────────────────
        IfcRoomValidationResult validationResult;
        try
        {
            validationResult = _service.Run(doc, options);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request,
                $"Unexpected error during validation for link {linkInstanceId}: {ex.Message}"));
        }

        sw.Stop();

        if (!validationResult.Success)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = validationResult.Error ?? "Validation failed.",
                Data       = new { success = false, error = validationResult.Error },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var s = validationResult.Summary;
        var message =
            $"Validated {s.TotalIfcSpaces} IFC Space(s): " +
            $"{s.HighConfidenceMatches} high-confidence, " +
            $"{s.MediumConfidenceMatches} medium-confidence, " +
            $"{s.LowConfidenceMatches} low-confidence, " +
            $"{s.AmbiguousMatches} ambiguous, " +
            $"{s.MissingRooms} missing." +
            (s.PossibleGeometryChanges > 0 ? $" {s.PossibleGeometryChanges} possible geometry change(s)." : string.Empty) +
            (s.PossibleRenames         > 0 ? $" {s.PossibleRenames} possible rename(s) detected."         : string.Empty) +
            (s.Truncated ? " Results truncated." : string.Empty);

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = message,
            Data       = new
            {
                success        = true,
                linkInstanceId = validationResult.LinkInstanceId,
                summary        = validationResult.Summary,
                items          = validationResult.Items
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

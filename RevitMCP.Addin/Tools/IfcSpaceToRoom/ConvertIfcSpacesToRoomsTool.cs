using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;                                          // ToolArguments
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom;

/// <summary>
/// MCP tool: convert_ifc_spaces_to_rooms
///
/// Phase 3 — first model-writing tool in the IFC Space → Room pipeline.
/// Converts validated IFC Space footprints into native Revit Rooms:
///   1. Extracts geometry (solid → bottom face → closed loop → placement point)
///   2. Matches each space to a host Level by elevation
///   3. Checks for existing Rooms (no-overwrite rule)
///   4. Creates Room Separation Lines from the footprint boundary
///   5. Places a Room at the interior placement point
///   6. Writes Number and Name using built-in Room fields only
///
/// Rules:
///   - Never creates shared parameters.
///   - Never writes IFC GUIDs anywhere.
///   - Never overwrites or recreates existing Rooms.
///   - One bad space never aborts the whole batch.
///   - dryRun=true performs full validation with no model modifications.
/// </summary>
public class ConvertIfcSpacesToRoomsTool : IRevitMcpTool
{
    public string Name        => "convert_ifc_spaces_to_rooms";
    public string Description =>
        "Phase 3 — Converts linked IFC Space elements into native Revit Rooms. " +
        "For each IFC Space: extracts the floor-plan footprint, matches it to a host Level, " +
        "creates Room Separation Lines from the boundary, and places a Room at the interior " +
        "placement point. Number and Name are written from IFC metadata using built-in Room " +
        "fields only (no shared parameters, no IFC GUIDs, no Comments). " +
        "Existing Rooms are never overwritten. One failed space never aborts the batch. " +
        "Set dryRun=true to validate all spaces without modifying the model. " +
        "Call ifc_list_links to get linkInstanceId, " +
        "then ifc_preview_spaces + ifc_preview_space_geometry to identify GeometryReady candidates, " +
        "then call this tool with those linkedElementIds.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory   Category   => ToolCategory.Coordination;

    private readonly IfcSpaceConversionService _service = new();

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

        var linkedElementIdRaw = ToolArguments.GetLongArray(request.Arguments, "linkedElementIds");

        var options = new IfcSpaceToRoomOptions
        {
            LinkInstanceId           = linkInstanceId,
            LinkedElementIds         = linkedElementIdRaw.ToList(),
            DuplicateMode            = ToolArguments.GetString(request.Arguments, "duplicateMode",            "skip_existing"),
            LevelMatchToleranceMm    = ToolArguments.GetDouble(request.Arguments, "levelMatchToleranceMm",    300.0),
            EndpointSnapToleranceMm  = ToolArguments.GetDouble(request.Arguments, "endpointSnapToleranceMm",  3.0),
            TinySegmentToleranceMm   = ToolArguments.GetDouble(request.Arguments, "tinySegmentToleranceMm",   1.0),
            SetRoomNameAndNumber     = ToolArguments.GetBool(request.Arguments,   "setRoomNameAndNumber",     true),
            CreateRoomSeparationLines = ToolArguments.GetBool(request.Arguments,  "createRoomSeparationLines", true),
            AllowCreateWithoutName   = ToolArguments.GetBool(request.Arguments,   "allowCreateWithoutName",   false),
            AllowCreateWithoutNumber = ToolArguments.GetBool(request.Arguments,   "allowCreateWithoutNumber", false),
            DryRun                   = ToolArguments.GetBool(request.Arguments,   "dryRun",                   false)
        };

        // Validate duplicateMode value
        if (options.DuplicateMode != "skip_existing" && options.DuplicateMode != "skip_conflicts")
        {
            return Task.FromResult(Fail(request,
                $"Invalid duplicateMode '{options.DuplicateMode}'. " +
                "Allowed values: 'skip_existing' (default) or 'skip_conflicts'."));
        }

        // ── Run conversion ─────────────────────────────────────────────────────
        IfcSpaceToRoomResult conversionResult;
        try
        {
            conversionResult = _service.Run(doc, options);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request,
                $"Unexpected error during conversion for link {linkInstanceId}: {ex.Message}"));
        }

        sw.Stop();

        if (!conversionResult.Success)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = conversionResult.Error ?? "Conversion failed.",
                Data       = new { success = false, error = conversionResult.Error },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        // ── Build summary message ──────────────────────────────────────────────
        string dryRunPrefix = options.DryRun ? "[DRY RUN] " : string.Empty;
        string createdLabel  = options.DryRun ? "DryRunReady" : "created";

        var message =
            $"{dryRunPrefix}Processed {conversionResult.TotalProcessed} IFC Space candidate(s): " +
            $"{conversionResult.CreatedRooms} {createdLabel}, " +
            $"{conversionResult.SkippedExisting} skipped-existing, " +
            $"{conversionResult.SkippedWarnings} skipped-conflict, " +
            $"{conversionResult.Failed} failed/skipped-other." +
            (options.DryRun
                ? " No model modifications were made."
                : $" {conversionResult.CreatedBoundaryLoops} boundary loop(s) created.");

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = message,
            Data       = new
            {
                success        = true,
                dryRun         = conversionResult.DryRun,
                linkInstanceId = conversionResult.LinkInstanceId,
                summary        = new
                {
                    totalProcessed       = conversionResult.TotalProcessed,
                    createdRooms         = conversionResult.CreatedRooms,
                    createdBoundaryLoops = conversionResult.CreatedBoundaryLoops,
                    skippedExisting      = conversionResult.SkippedExisting,
                    skippedWarnings      = conversionResult.SkippedWarnings,
                    failed               = conversionResult.Failed
                },
                items = conversionResult.Items
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

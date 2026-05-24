using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;                                   // ToolArguments
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom;

/// <summary>
/// MCP tool: ifc_preview_space_geometry
/// Inspects linked IFC Space elements and returns detailed geometry extraction results:
/// solid detection, bottom face extraction, footprint loop, approximate area, and
/// a placement point for each candidate.
/// Phase 2: strictly read-only — no rooms, room separation lines, or any model elements
/// are created.
/// </summary>
public class PreviewIfcSpaceGeometryTool : IRevitMcpTool
{
    public string Name        => "ifc_preview_space_geometry";
    public string Description =>
        "Extracts geometry from linked IFC Space elements (Generic Models / DirectShapes) and " +
        "returns per-space geometry readiness: solid extraction, bottom face, footprint loop, " +
        "approximate area in m², and an interior placement point. " +
        "Call ifc_list_links to get linkInstanceId, then ifc_preview_spaces to get linkedElementIds. " +
        "Phase 2: read-only — does not create rooms, room separation lines, shared parameters, " +
        "or any other model elements.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Coordination;

    private readonly GeometryPreviewService _service = new();

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active Revit document."));

        // ── Parse arguments ───────────────────────────────────────────────────
        var linkInstanceId = ToolArguments.GetLong(request.Arguments, "linkInstanceId", 0L);
        if (linkInstanceId == 0L)
        {
            return Task.FromResult(Fail(request,
                "Required argument 'linkInstanceId' is missing or zero. " +
                "Call ifc_list_links first, then optionally ifc_preview_spaces, " +
                "to obtain a valid linkInstanceId."));
        }

        var linkedElementIdRaw = ToolArguments.GetLongArray(request.Arguments, "linkedElementIds");
        var linkedElementIds   = (IReadOnlyCollection<long>)linkedElementIdRaw;

        var options = new IfcGeometryExtractionOptions
        {
            EndpointSnapToleranceMm         = ToolArguments.GetDouble(request.Arguments, "endpointSnapToleranceMm",         3.0),
            TinySegmentToleranceMm          = ToolArguments.GetDouble(request.Arguments, "tinySegmentToleranceMm",          1.0),
            HorizontalFaceToleranceDegrees  = ToolArguments.GetDouble(request.Arguments, "horizontalFaceToleranceDegrees",  2.0),
            MinimumAreaM2                   = ToolArguments.GetDouble(request.Arguments, "minimumAreaM2",                    0.1),
            IncludeLoopCoordinates          = ToolArguments.GetBool(request.Arguments,   "includeLoopCoordinates",          false),
            MaxCoordinatePoints             = ToolArguments.GetInt(request.Arguments,    "maxCoordinatePoints",              250),
            LevelMatchToleranceMm           = ToolArguments.GetDouble(request.Arguments, "levelMatchToleranceMm",           300.0)
        };

        int maxResults = ToolArguments.GetInt(request.Arguments, "maxResults", 1000);

        // ── Run preview ────────────────────────────────────────────────────────
        IfcGeometryPreviewResult previewResult;
        try
        {
            previewResult = _service.Run(doc, linkInstanceId, linkedElementIds, options, maxResults);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request,
                $"Unexpected error during geometry preview for link {linkInstanceId}: {ex.Message}"));
        }

        sw.Stop();

        if (!previewResult.Success)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = previewResult.Error ?? "Geometry preview failed.",
                Data       = new { success = false, error = previewResult.Error },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var s = previewResult.Summary;
        var message =
            $"Processed {s.TotalCandidates} IFC Space candidate(s): " +
            $"{s.GeometryReady} geometry-ready, " +
            $"{s.NoSolidGeometry} no-solid, " +
            $"{s.NoUsableBottomFace} no-bottom-face, " +
            $"{s.InvalidLoop} invalid-loop, " +
            $"{s.Failed} failed." +
            (s.Truncated ? $" Results truncated at {maxResults}." : string.Empty);

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = message,
            Data       = new
            {
                success        = true,
                linkInstanceId = previewResult.LinkInstanceId,
                items          = previewResult.Items,
                summary        = previewResult.Summary
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

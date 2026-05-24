using System.Diagnostics;
using Autodesk.Revit.UI;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;                           // ToolArguments
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom;

/// <summary>
/// MCP tool: ifc_preview_spaces
/// Inspects one linked IFC model and returns all detected IfcSpace candidates with
/// metadata, host level match, existing-room check, and conversion readiness status.
/// Phase 1: read-only, no document modifications.
/// </summary>
public class PreviewIfcSpacesTool : IRevitMcpTool
{
    public string Name        => "ifc_preview_spaces";
    public string Description =>
        "Inspects a linked IFC model (identified by linkInstanceId from ifc_list_links) and returns " +
        "all detected IFC Space candidates with metadata (GUID, number, name, storey), the nearest " +
        "host level match, optional existing-room comparison, and per-space conversion readiness. " +
        "Phase 1: read-only — does not create rooms, shared parameters, or any model elements.";
    public ToolPermission Permission => ToolPermission.ReadOnly;
    public ToolCategory   Category   => ToolCategory.Coordination;

    private readonly IfcSpacePreviewService _service = new();

    public Task<McpToolResult> ExecuteAsync(
        UIApplication uiapp,
        McpToolRequest request,
        CancellationToken cancellationToken)
    {
        var sw  = Stopwatch.StartNew();
        var doc = uiapp.ActiveUIDocument?.Document;
        if (doc == null) return Task.FromResult(Fail(request, "No active Revit document."));

        // — Parse arguments ——————————————————————————————————————————————————
        var linkInstanceId = ToolArguments.GetLong(request.Arguments, "linkInstanceId", 0L);
        if (linkInstanceId == 0L)
        {
            return Task.FromResult(Fail(request,
                "Required argument 'linkInstanceId' is missing or zero. " +
                "Call ifc_list_links first to obtain a valid linkInstanceId."));
        }

        var options = new IfcSpacePreviewOptions
        {
            LinkInstanceId          = linkInstanceId,
            IncludeExistingRoomCheck = ToolArguments.GetBool(request.Arguments, "includeExistingRoomCheck", true),
            LevelMatchToleranceMm   = ToolArguments.GetDouble(request.Arguments, "levelMatchToleranceMm",    300.0),
            MaxResults              = ToolArguments.GetInt(request.Arguments,    "maxResults",                1000)
        };

        // — Run preview ———————————————————————————————————————————————————————
        IfcSpacePreviewResult previewResult;
        try
        {
            previewResult = _service.Preview(doc, options);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request,
                $"Unexpected error during IFC space preview for link {linkInstanceId}: {ex.Message}"));
        }

        sw.Stop();

        if (!previewResult.Success)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = previewResult.Error ?? "Preview failed.",
                Data       = new { success = false, error = previewResult.Error },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        var s = previewResult.Summary;
        var message = $"Previewed {s.TotalCandidates} IFC Space candidate(s): " +
                      $"{s.Ready} ready, {s.AlreadyExists} already exist, " +
                      $"{s.Warnings} warnings, {s.Failed} failed." +
                      (s.Truncated ? $" Results truncated at {options.MaxResults}." : string.Empty);

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = message,
            Data       = new
            {
                success        = true,
                linkInstanceId = previewResult.LinkInstanceId,
                spaces         = previewResult.Spaces,
                summary        = previewResult.Summary
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

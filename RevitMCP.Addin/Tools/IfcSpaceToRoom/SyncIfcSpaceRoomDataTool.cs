using System.Diagnostics;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCP.Addin.Interfaces;
using RevitMCP.Addin.Tools;                                           // ToolArguments
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;
using RevitMCP.Core.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom;

/// <summary>
/// MCP tool: sync_ifc_space_room_data
///
/// Phase 4 — Applies controlled built-in field updates to existing native Revit Rooms
/// based on current IFC Space metadata.
///
/// Allowed writes: Room.Number, Room.Name (built-in fields only).
/// Forbidden: shared parameters, IFC GUIDs, Comments, Extensible Storage,
///            Room boundaries, Room deletion.
///
/// Safety rules:
///   - Defaults to dry-run (dryRun = true).
///   - Ambiguous matches are always blocked.
///   - Medium/Low confidence updates require explicit opt-in.
///   - Each item is updated in its own Transaction.
///   - One failed item never aborts the batch.
/// </summary>
public class SyncIfcSpaceRoomDataTool : IRevitMcpTool
{
    public string Name        => "sync_ifc_space_room_data";
    public string Description =>
        "Phase 4 — Updates built-in Room Number and/or Name for selected Rooms to match " +
        "their corresponding IFC Space's current metadata. " +
        "Only Room.Number and Room.Name (built-in fields) are written — " +
        "no shared parameters, no IFC GUIDs, no Comments, no Extensible Storage. " +
        "Defaults to dryRun=true (reports planned changes without modifying the model). " +
        "Ambiguous matches are always blocked. " +
        "Medium/Low confidence updates require explicit opt-in via allowMediumConfidenceUpdates " +
        "or allowLowConfidenceUpdates. " +
        "Run validate_ifc_space_room_conversion first to identify items and their confidence levels, " +
        "then pass high-confidence items here with dryRun=true to preview, then dryRun=false to commit.";
    public ToolPermission Permission => ToolPermission.RequiresApproval;
    public ToolCategory   Category   => ToolCategory.Coordination;

    private readonly ControlledRoomSyncService _service = new();

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

        // Parse the items array: [{linkedElementId, roomId, updateName, updateNumber}]
        var syncItems = ParseSyncItems(request.Arguments);

        if (syncItems.Count == 0)
        {
            return Task.FromResult(Fail(request,
                "Required argument 'items' is missing or empty. " +
                "Run validate_ifc_space_room_conversion first and pass selected items here."));
        }

        var options = new RoomSyncOptions
        {
            LinkInstanceId               = linkInstanceId,
            Items                        = syncItems,
            AllowMediumConfidenceUpdates = ToolArguments.GetBool(request.Arguments, "allowMediumConfidenceUpdates", false),
            AllowLowConfidenceUpdates    = ToolArguments.GetBool(request.Arguments, "allowLowConfidenceUpdates",    false),
            DryRun                       = ToolArguments.GetBool(request.Arguments, "dryRun",                       true)
        };

        // ── Run sync ───────────────────────────────────────────────────────────
        RoomSyncResult syncResult;
        try
        {
            syncResult = _service.Run(doc, options);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(request,
                $"Unexpected error during sync for link {linkInstanceId}: {ex.Message}"));
        }

        sw.Stop();

        if (!syncResult.Success)
        {
            return Task.FromResult(new McpToolResult
            {
                RequestId  = request.RequestId,
                Success    = false,
                Message    = syncResult.Error ?? "Sync failed.",
                Data       = new { success = false, error = syncResult.Error },
                DurationMs = sw.ElapsedMilliseconds
            });
        }

        // ── Build summary message ──────────────────────────────────────────────
        string dryRunPrefix = options.DryRun ? "[DRY RUN] " : string.Empty;
        string actionLabel  = options.DryRun ? "would be updated" : "updated";

        var message =
            $"{dryRunPrefix}Processed {syncResult.TotalItems} sync item(s): " +
            $"{syncResult.Updated} {actionLabel}, " +
            $"{syncResult.NoChangeNeeded} no-change, " +
            $"{syncResult.Blocked} blocked, " +
            $"{syncResult.Failed} failed." +
            (options.DryRun ? " No model modifications were made." : string.Empty);

        return Task.FromResult(new McpToolResult
        {
            RequestId  = request.RequestId,
            Success    = true,
            Message    = message,
            Data       = new
            {
                success    = true,
                dryRun     = syncResult.DryRun,
                summary    = new
                {
                    totalItems    = syncResult.TotalItems,
                    updated       = syncResult.Updated,
                    noChangeNeeded = syncResult.NoChangeNeeded,
                    blocked       = syncResult.Blocked,
                    failed        = syncResult.Failed
                },
                items = syncResult.Items
            },
            DurationMs = sw.ElapsedMilliseconds
        });
    }

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses the "items" argument as an array of sync item objects.
    /// Each object should have: linkedElementId (long), roomId (long),
    /// updateName (bool), updateNumber (bool).
    /// </summary>
    private static List<RoomSyncItemRequest> ParseSyncItems(Dictionary<string, object?> args)
    {
        var result = new List<RoomSyncItemRequest>();

        if (!args.TryGetValue("items", out var raw) || raw == null)
            return result;

        JArray? ja = raw switch
        {
            JArray arr                  => arr,
            string s                    => ToolArguments.TryParseJArray(s),
            IEnumerable<object> enumerable => TryFromObject(enumerable),
            _                           => TryFromObject(raw)
        };

        if (ja == null) return result;

        foreach (var token in ja)
        {
            try
            {
                var item = new RoomSyncItemRequest
                {
                    LinkedElementId = token["linkedElementId"]?.Value<long>() ?? 0L,
                    RoomId          = token["roomId"]?.Value<long>()          ?? 0L,
                    UpdateName      = token["updateName"]?.Value<bool>()      ?? false,
                    UpdateNumber    = token["updateNumber"]?.Value<bool>()    ?? false
                };

                // Skip items without valid IDs
                if (item.LinkedElementId != 0L && item.RoomId != 0L)
                    result.Add(item);
            }
            catch { /* skip malformed items */ }
        }

        return result;
    }

    private static JArray? TryFromObject(object o)
    {
        try { return JArray.FromObject(o); }
        catch { return null; }
    }

    private static McpToolResult Fail(McpToolRequest r, string msg) =>
        new() { RequestId = r.RequestId, Success = false, Message = msg };
}

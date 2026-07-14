using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Applies controlled updates to existing Revit Rooms based on caller-supplied sync requests.
///
/// Allowed writes: Room.Number, Room.Name (built-in fields only).
///
/// Forbidden writes (always):
///   - Shared parameters
///   - IFC GUIDs
///   - Comments
///   - Extensible Storage
///   - Room boundaries
///   - Room deletion
///
/// Safety rules (enforced per item via <see cref="RoomUpdateSafetyService"/>):
///   - Ambiguous matches are always blocked.
///   - Medium/Low confidence updates require explicit allowance.
///   - Missing Rooms or Spaces are blocked.
///   - dry-run returns planned actions without model writes.
/// Phase 4.
/// </summary>
public class ControlledRoomSyncService
{
    private readonly IfcParameterReader       _reader     = new();
    private readonly LevelMatcher             _levelMatcher = new();
    private readonly RoomMatchConfidenceService _confidenceService = new();

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Runs the sync operation for all submitted items.</summary>
    public RoomSyncResult Run(
        Document hostDoc,
        RoomSyncOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options.Items.Count == 0)
        {
            return new RoomSyncResult
            {
                Success    = true,
                DryRun     = options.DryRun,
                TotalItems = 0,
                Items      = new()
            };
        }

        // ── Resolve link ───────────────────────────────────────────────────────
        var linkElement = hostDoc.GetElement(new ElementId(options.LinkInstanceId));
        if (linkElement is not RevitLinkInstance linkInstance)
        {
            return new RoomSyncResult
            {
                Success = false,
                DryRun  = options.DryRun,
                Error   = $"Element {options.LinkInstanceId} is not a RevitLinkInstance."
            };
        }

        var linkedDoc = linkInstance.GetLinkDocument();
        if (linkedDoc == null)
        {
            return new RoomSyncResult
            {
                Success = false,
                DryRun  = options.DryRun,
                Error   = $"Link {options.LinkInstanceId} ({linkInstance.Name}) is not loaded."
            };
        }

        Transform linkTransform;
        try   { linkTransform = linkInstance.GetTotalTransform(); }
        catch (Exception ex)
        {
            return new RoomSyncResult
            {
                Success = false,
                DryRun  = options.DryRun,
                Error   = $"Could not obtain link transform: {ex.Message}"
            };
        }

        // ── Pre-load host state ────────────────────────────────────────────────
        _levelMatcher.LoadLevels(hostDoc);
        _confidenceService.LoadRooms(hostDoc);

        // ── Process items ──────────────────────────────────────────────────────
        var itemResults = new List<RoomSyncItemResult>(options.Items.Count);

        foreach (var request in options.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemResult = ProcessItem(
                hostDoc, linkedDoc, linkTransform, request, options);
            itemResults.Add(itemResult);
        }

        // ── Aggregate ─────────────────────────────────────────────────────────
        int updated = itemResults.Count(i => i.Status is
            SyncStatus.UpdatedName or
            SyncStatus.UpdatedNumber or
            SyncStatus.UpdatedNameAndNumber or
            SyncStatus.DryRunWouldUpdateName or
            SyncStatus.DryRunWouldUpdateNumber or
            SyncStatus.DryRunWouldUpdateNameAndNumber);

        int noChange = itemResults.Count(i => i.Status == SyncStatus.NoChangeNeeded);

        int blocked = itemResults.Count(i => i.Status is
            SyncStatus.BlockedAmbiguousMatch or
            SyncStatus.BlockedMediumConfidence or
            SyncStatus.BlockedLowConfidence or
            SyncStatus.BlockedRoomNotFound or
            SyncStatus.BlockedSpaceNotFound or
            SyncStatus.BlockedMissingSelection or
            SyncStatus.BlockedEmptyValue);

        int failed = itemResults.Count(i => i.Status == SyncStatus.Error);

        return new RoomSyncResult
        {
            Success       = true,
            DryRun        = options.DryRun,
            TotalItems    = itemResults.Count,
            Updated       = updated,
            NoChangeNeeded = noChange,
            Blocked       = blocked,
            Failed        = failed,
            Items         = itemResults
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private RoomSyncItemResult ProcessItem(
        Document hostDoc,
        Document linkedDoc,
        Transform linkTransform,
        RoomSyncItemRequest request,
        RoomSyncOptions options)
    {
        var result = new RoomSyncItemResult
        {
            LinkedElementId = request.LinkedElementId,
            RoomId          = request.RoomId
        };

        try
        {
            // ── a. Look up Room ───────────────────────────────────────────────
            bool roomFound = false;
            Room? room     = null;
            try
            {
                var el = hostDoc.GetElement(new ElementId(request.RoomId));
                room      = el as Room;
                roomFound = room != null;
            }
            catch { /* roomFound stays false */ }

            // ── b. Look up IFC Space ─────────────────────────────────────────
            bool ifcSpaceFound = false;
            string? ifcName    = null;
            string? ifcNumber  = null;

            try
            {
                var ifcEl = linkedDoc.GetElement(new ElementId(request.LinkedElementId));
                if (ifcEl != null)
                {
                    ifcSpaceFound = true;
                    var meta = _reader.ReadMetadata(ifcEl, options);
                    ifcName   = meta.Name?.Trim();
                    ifcNumber = meta.Number?.Trim();

                    // ── c. Re-determine confidence ─────────────────────────
                    double? bboxZ = TryGetBboxBottomZ(ifcEl, linkTransform);
                    var levelMatch = _levelMatcher.Match(bboxZ, 300.0);

                    if (levelMatch.LevelId.HasValue)
                    {
                        var scoreResult = _confidenceService.Score(
                            levelMatch.LevelId.Value,
                            ifcNumber, ifcName,
                            null, null, null, // skip geometry in re-check for speed
                            1000.0, 10.0);    // use default tolerances for re-check
                        result.Confidence = scoreResult.Confidence;
                    }
                    else
                    {
                        result.Confidence = RoomMatchConfidence.None;
                    }
                }
            }
            catch { /* ifcSpaceFound stays false */ }

            // ── d. Safety check ───────────────────────────────────────────────
            string? targetName   = request.UpdateName   ? ifcName   : null;
            string? targetNumber = request.UpdateNumber ? ifcNumber : null;

            bool safe = RoomUpdateSafetyService.Check(
                request,
                result.Confidence,
                options.AllowMediumConfidenceUpdates,
                options.AllowLowConfidenceUpdates,
                ifcSpaceFound,
                roomFound,
                targetName,
                targetNumber,
                out string? blockStatus);

            if (!safe)
            {
                result.Status = blockStatus ?? SyncStatus.Error;
                return result;
            }

            // ── e. Determine current values ───────────────────────────────────
            string? currentName   = room!.Name?.Trim();
            string? currentNumber = room!.Number?.Trim();

            result.OldName   = request.UpdateName   ? currentName   : null;
            result.NewName   = request.UpdateName   ? targetName    : null;
            result.OldNumber = request.UpdateNumber ? currentNumber : null;
            result.NewNumber = request.UpdateNumber ? targetNumber  : null;

            // ── f. Check if anything actually needs changing ──────────────────
            bool nameChangeNeeded   = request.UpdateName &&
                !string.Equals(currentName, targetName, StringComparison.Ordinal);
            bool numberChangeNeeded = request.UpdateNumber &&
                !string.Equals(currentNumber, targetNumber, StringComparison.Ordinal);

            if (!nameChangeNeeded && !numberChangeNeeded)
            {
                result.Status = SyncStatus.NoChangeNeeded;
                return result;
            }

            // ── g. Dry-run short-circuit ──────────────────────────────────────
            if (options.DryRun)
            {
                result.Status = (nameChangeNeeded, numberChangeNeeded) switch
                {
                    (true,  true)  => SyncStatus.DryRunWouldUpdateNameAndNumber,
                    (true,  false) => SyncStatus.DryRunWouldUpdateName,
                    (false, true)  => SyncStatus.DryRunWouldUpdateNumber,
                    _              => SyncStatus.NoChangeNeeded
                };
                return result;
            }

            // ── h. Write inside a Transaction ─────────────────────────────────
            using var tx = new Transaction(hostDoc,
                $"MCP: Sync Room data '{currentNumber} {currentName}'");

            try
            {
                tx.Start();

                if (nameChangeNeeded)
                {
                    try   { room.Name = targetName!; }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Could not set Room Name: {ex.Message}");
                        nameChangeNeeded = false;
                    }
                }

                if (numberChangeNeeded)
                {
                    try   { room.Number = targetNumber!; }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Could not set Room Number: {ex.Message}");
                        numberChangeNeeded = false;
                    }
                }

                if (!nameChangeNeeded && !numberChangeNeeded)
                {
                    // Both writes failed
                    tx.RollBack();
                    result.Status = SyncStatus.Error;
                    return result;
                }

                RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);

                result.Status = (nameChangeNeeded, numberChangeNeeded) switch
                {
                    (true,  true)  => SyncStatus.UpdatedNameAndNumber,
                    (true,  false) => SyncStatus.UpdatedName,
                    (false, true)  => SyncStatus.UpdatedNumber,
                    _              => SyncStatus.NoChangeNeeded
                };
            }
            catch (Exception ex)
            {
                try { tx.RollBack(); } catch { /* ignore */ }
                result.Status = SyncStatus.Error;
                result.Warnings.Add($"Transaction exception: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            result.Status = SyncStatus.Error;
            result.Warnings.Add($"Unexpected exception: {ex.Message}");
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static double? TryGetBboxBottomZ(Element element, Transform transform)
    {
        try
        {
            var bbox = element.get_BoundingBox(null);
            if (bbox == null) return null;

            var corners = new[]
            {
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
            };

            return corners.Select(c => transform.OfPoint(c).Z).Min();
        }
        catch
        {
            return null;
        }
    }
}

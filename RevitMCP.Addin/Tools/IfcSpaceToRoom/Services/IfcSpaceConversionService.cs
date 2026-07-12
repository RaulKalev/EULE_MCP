using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using RevitMCP.Addin.Tools.IfcSpaceToRoom.Models;

namespace RevitMCP.Addin.Tools.IfcSpaceToRoom.Services;

/// <summary>
/// Orchestrates the Phase 3 IFC Space → Revit Room conversion pipeline.
///
/// Pipeline per space:
///   1. Read IFC metadata (Number, Name, StoreyName)
///   2. Resolve host Level via bbox elevation
///   3. Extract geometry (solid → bottom face → loop → clean/validate → placement point)
///   4. Check for existing Room (enforce no-overwrite rule)
///   5. Apply guards (dry-run, missing name/number, no view, probable-only detection)
///   6. [If writing] Open per-space Transaction:
///        a. Create SketchPlane at Level elevation
///        b. Create Room Separation Lines from outer loop
///        c. Place Room at placement point
///        d. Write Number and Name (built-in only)
///        e. Commit (or Rollback on any failure)
///
/// Non-negotiables:
///   - Never creates shared parameters.
///   - Never writes IFC GUIDs anywhere.
///   - Never uses Comments as storage.
///   - Never overwrites existing Rooms.
///   - One bad space must not abort the whole batch.
///   - dryRun is honoured strictly.
///   - Duplicate mode: skip_existing (default) and skip_conflicts both block Number+Level
///     conflicts; allow_conflicts is the explicit opt-in to permit them.
/// </summary>
public class IfcSpaceConversionService
{
    // ── Sub-services ──────────────────────────────────────────────────────────

    private readonly IfcSpaceCollector          _collector       = new();
    private readonly IfcParameterReader         _reader          = new();
    private readonly LevelMatcher               _levelMatcher    = new();
    private readonly IfcSpaceGeometryExtractor  _extractor       = new();
    private readonly RoomMatchService           _roomMatcher     = new();
    private readonly BoundaryViewResolver       _viewResolver    = new();
    private readonly RoomBoundaryCreator        _boundaryCreator = new();
    private readonly NativeRoomCreator          _roomCreator     = new();

    // ── Cached per-run state ──────────────────────────────────────────────────

    // Maps LevelId → ViewPlan (populated in setup step)
    private readonly Dictionary<long, ViewPlan?> _levelViews = new();

    // Maps LevelId → Level element (populated in setup step)
    private readonly Dictionary<long, Level> _levelElements = new();

    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs the full conversion for one linked IFC model.
    /// </summary>
    public IfcSpaceToRoomResult Run(
        Document hostDoc,
        IfcSpaceToRoomOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // ── 1. Resolve link ────────────────────────────────────────────────────
        var linkElement = hostDoc.GetElement(new ElementId(options.LinkInstanceId));
        if (linkElement is not RevitLinkInstance linkInstance)
        {
            return Fail(options,
                $"Element {options.LinkInstanceId} is not a RevitLinkInstance.");
        }

        var linkedDoc = linkInstance.GetLinkDocument();
        if (linkedDoc == null)
        {
            return Fail(options,
                $"Link {options.LinkInstanceId} ({linkInstance.Name}) is not loaded.");
        }

        Transform linkTransform;
        try   { linkTransform = linkInstance.GetTotalTransform(); }
        catch (Exception ex)
        {
            return Fail(options,
                $"Could not obtain link transform for {linkInstance.Name}: {ex.Message}");
        }

        // ── 2. Collect candidate elements ──────────────────────────────────────
        //
        // Explicit LinkedElementIds bypass the AllowProbableConversion guard — the caller
        // has explicitly selected those elements and takes responsibility.
        // Auto-collect respects AllowProbableConversion (default false → Confirmed-only).

        var candidateElements = new List<Element>();
        // Tracks which auto-collected elements were only probably IFC Spaces.
        // Used in ProcessElement to attach an advisory warning if they were allowed through.
        var probableElementIds = new HashSet<long>();

        if (options.LinkedElementIds.Count > 0)
        {
            foreach (var eid in options.LinkedElementIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var el = linkedDoc.GetElement(new ElementId(eid));
                if (el != null) candidateElements.Add(el);
            }
        }
        else
        {
            // Auto-collect: use includeProbable flag from options.
            // When AllowProbableConversion=false (default), only Confirmed elements come back.
            var detections = _collector.Collect(linkedDoc, options.AllowProbableConversion);
            foreach (var detection in detections)
            {
                cancellationToken.ThrowIfCancellationRequested();
                candidateElements.Add(detection.Element);
                if (detection.Confidence == IfcDetectionConfidence.Probable)
                    probableElementIds.Add(detection.Element.Id.Value);
            }
        }

        // ── 3. Pre-load host state (read-only) ─────────────────────────────────
        // Clear per-run caches so stale state from a previous call does not leak in.
        _levelViews.Clear();
        _levelElements.Clear();

        _levelMatcher.LoadLevels(hostDoc);
        _roomMatcher.LoadRooms(hostDoc);
        _viewResolver.LoadViewFamilyType(hostDoc);

        LoadLevelElements(hostDoc);

        // ── 4. Setup step: ensure ViewPlans exist for all required levels ──────
        //    This writes to the document (creates views) only when
        //    AllowCreateMissingBoundaryViews = true. Otherwise only existing views are used.
        //    Skip entirely if dry-run.
        if (!options.DryRun)
        {
            EnsureViewsForCandidates(hostDoc, candidateElements, linkTransform, options);
        }

        // ── 5. Process each candidate ─────────────────────────────────────────
        var items = new List<IfcSpaceConversionItemResult>(candidateElements.Count);

        foreach (var element in candidateElements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isProbable = probableElementIds.Contains(element.Id.Value);
            var item = ProcessElement(hostDoc, element, linkTransform, options, isProbable);
            items.Add(item);
        }

        // ── 6. Build result ───────────────────────────────────────────────────
        return BuildResult(options, items);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Per-element processing
    // ─────────────────────────────────────────────────────────────────────────

    private IfcSpaceConversionItemResult ProcessElement(
        Document hostDoc,
        Element element,
        Transform linkTransform,
        IfcSpaceToRoomOptions options,
        bool isProbable = false)
    {
        var item = new IfcSpaceConversionItemResult
        {
            LinkedElementId = element.Id.Value
        };

        try
        {
            // ── a. Read IFC metadata ─────────────────────────────────────────
            var meta = _reader.ReadMetadata(element);
            item.Number = meta.Number;
            item.Name   = meta.Name;

            // ── b. Probable-element advisory warning ─────────────────────────
            //    Explicit IDs always have isProbable=false (caller's intent).
            //    Auto-collected probable elements reach here only when
            //    AllowProbableConversion=true; add a warning so operators know.
            if (isProbable)
            {
                item.Warnings.Add(
                    $"Element {element.Id.Value} ({element.Name}) was auto-collected as a " +
                    "probable IFC Space (no explicit IfcSpace type parameter). " +
                    "Verify this element is actually an IfcSpace before accepting the result.");
            }

            // ── c. Guards: missing number / name ────────────────────────────
            if (!options.AllowCreateWithoutNumber &&
                string.IsNullOrWhiteSpace(item.Number))
            {
                item.Status = ConversionStatus.SkippedMissingNumber;
                item.Errors.Add("IFC Space has no Room Number and allowCreateWithoutNumber is false.");
                return item;
            }

            if (!options.AllowCreateWithoutName &&
                string.IsNullOrWhiteSpace(item.Name))
            {
                item.Status = ConversionStatus.SkippedMissingName;
                item.Errors.Add("IFC Space has no Room Name and allowCreateWithoutName is false.");
                return item;
            }

            // ── d. Level match ────────────────────────────────────────────
            double? bboxZ      = TryGetBboxBottomZ(element, linkTransform);
            var levelMatch     = _levelMatcher.Match(bboxZ, options.LevelMatchToleranceMm);

            item.TargetLevelName = levelMatch.LevelName;

            if (levelMatch.LevelId == null)
            {
                item.Status = ConversionStatus.SkippedNoLevelMatch;
                item.Errors.Add("No host Level could be matched to this element's elevation.");
                return item;
            }

            long   levelId       = levelMatch.LevelId.Value;
            double levelElevFeet = levelMatch.LevelElevationFeet;

            // ── e. Geometry extraction ──────────────────────────────────
            var geomOptions = new IfcGeometryExtractionOptions
            {
                EndpointSnapToleranceMm = options.EndpointSnapToleranceMm,
                TinySegmentToleranceMm  = options.TinySegmentToleranceMm,
                LevelMatchToleranceMm   = options.LevelMatchToleranceMm
            };

            var footprint = _extractor.Extract(element, linkTransform, levelElevFeet, geomOptions);

            if (!footprint.Success || footprint.OuterLoop == null || footprint.PlacementPoint == null)
            {
                item.Status = ConversionStatus.SkippedInvalidGeometry;
                item.Errors.Add($"Geometry extraction failed: {footprint.Status}. " +
                                string.Join("; ", footprint.Errors));
                item.Warnings.AddRange(footprint.Warnings);
                return item;
            }

            item.Warnings.AddRange(footprint.Warnings);

            // ── f. Room conflict check ──────────────────────────────────
            //    HasStrictMatch  = exact Number + Name + Level → always skip
            //    HasWarningMatch = Number + Level match but Name differs:
            //       skip_existing (default) → blocked (SkippedDuplicateWarning)
            //       skip_conflicts          → alias for skip_existing → blocked
            //       allow_conflicts         → warned but allowed through
            var roomMatch = _roomMatcher.Check(levelId, item.Number, item.Name);

            if (roomMatch.HasStrictMatch)
            {
                item.Status        = ConversionStatus.SkippedExisting;
                item.ExistingRoomId = roomMatch.ExistingRoomId;
                item.Warnings.Add($"Room #{item.Number} '{item.Name}' already exists on this level (id {roomMatch.ExistingRoomId}).");
                return item;
            }

            if (roomMatch.HasWarningMatch)
            {
                item.ExistingRoomId = roomMatch.ExistingRoomId;
                if (roomMatch.Warning != null)
                    item.Warnings.Add(roomMatch.Warning);

                // Default: skip_existing and skip_conflicts both block Number+Level conflicts.
                // Only allow_conflicts explicitly opts in to creation despite the conflict.
                if (options.DuplicateMode != "allow_conflicts")
                {
                    item.Status = ConversionStatus.SkippedDuplicateWarning;
                    item.Errors.Add(
                        "Skipped: a Room with the same Number and Level already exists with a different Name. " +
                        "Set duplicateMode='allow_conflicts' to allow creation despite the conflict.");
                    return item;
                }
                // allow_conflicts: warning already added above; continue to creation
            }

            // ── g. View check ─────────────────────────────────────────
            if (options.CreateRoomSeparationLines && !options.DryRun)
            {
                if (!_levelViews.TryGetValue(levelId, out var view) || view == null)
                {
                    item.Status = ConversionStatus.SkippedNoView;
                    item.Errors.Add(
                        $"No floor-plan view available for level '{item.TargetLevelName}' (id {levelId}). " +
                        "Set allowCreateMissingBoundaryViews=true to create views automatically.");
                    return item;
                }
            }

            // ── h. Dry-run short-circuit ──────────────────────────────
            if (options.DryRun)
            {
                item.Status = ConversionStatus.DryRunReady;
                return item;
            }

            // ── i. Write: one Transaction per space ───────────────────
            WriteSpace(hostDoc, item, levelId, levelElevFeet, footprint, options);
        }
        catch (Exception ex)
        {
            item.Status = ConversionStatus.Error;
            item.Errors.Add($"Unexpected exception: {ex.Message}");
        }

        return item;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Write step (inside individual Transaction)
    // ─────────────────────────────────────────────────────────────────────────

    private void WriteSpace(
        Document hostDoc,
        IfcSpaceConversionItemResult item,
        long levelId,
        double levelElevFeet,
        IfcSpaceFootprint footprint,
        IfcSpaceToRoomOptions options)
    {
        using var tx = new Transaction(hostDoc, $"MCP: Create Room '{item.Number} {item.Name}'");

        try
        {
            tx.Start();

            // ── i. SketchPlane ────────────────────────────────────────────
            SketchPlane sketchPlane;
            try
            {
                sketchPlane = SketchPlaneResolver.CreateAtElevation(hostDoc, levelElevFeet);
            }
            catch (Exception ex)
            {
                tx.RollBack();
                item.Status = ConversionStatus.FailedBoundaryCreation;
                item.Errors.Add($"Could not create SketchPlane: {ex.Message}");
                return;
            }

            // ── ii. Room Separation Lines ─────────────────────────────────
            if (options.CreateRoomSeparationLines)
            {
                var view = _levelViews[levelId]!;
                var boundaryResult = _boundaryCreator.Create(
                    hostDoc, footprint.OuterLoop!, sketchPlane, view, levelElevFeet);

                if (!boundaryResult.Success)
                {
                    tx.RollBack();
                    item.Status = ConversionStatus.FailedBoundaryCreation;
                    item.Errors.Add(boundaryResult.Error ?? "Room Separation Line creation failed.");
                    return;
                }

                item.CreatedBoundaryLineCount = boundaryResult.CreatedLineCount;
            }

            // ── iii. Place Room ───────────────────────────────────────────
            if (!_levelElements.TryGetValue(levelId, out var level) || level == null)
            {
                tx.RollBack();
                item.Status = ConversionStatus.FailedRoomCreation;
                item.Errors.Add($"Level element not found for id {levelId}.");
                return;
            }

            var roomResult = _roomCreator.Create(hostDoc, level, footprint.PlacementPoint!);

            if (!roomResult.Success || roomResult.RoomId == null)
            {
                tx.RollBack();
                item.Status = ConversionStatus.FailedRoomCreation;
                item.Errors.Add(roomResult.Error ?? "Room placement failed.");
                return;
            }

            item.CreatedRoomId = roomResult.RoomId;

            // ── iv. Write Number and Name ─────────────────────────────────
            if (options.SetRoomNameAndNumber)
            {
                var room = hostDoc.GetElement(new ElementId(roomResult.RoomId.Value)) as Room;
                if (room == null)
                {
                    tx.RollBack();
                    item.Status = ConversionStatus.FailedParameterWrite;
                    item.Errors.Add("Could not retrieve the newly created Room to write parameters.");
                    return;
                }

                bool writeOk = RoomParameterWriter.Write(
                    room, item.Number, item.Name, item.Warnings);

                if (!writeOk)
                {
                    // Parameter write failure is non-fatal: Room exists, just flag it
                    item.Status = ConversionStatus.FailedParameterWrite;
                    item.Errors.Add("Room was created but Number/Name could not be written.");
                    RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);
                    return;
                }
            }

            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);
            item.Status = ConversionStatus.Created;
        }
        catch (Exception ex)
        {
            try { tx.RollBack(); } catch { /* ignore rollback failure */ }
            item.Status = ConversionStatus.Error;
            item.Errors.Add($"Transaction exception: {ex.Message}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Setup helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Resolves ViewPlans for all Levels that at least one candidate will target.
    /// When <see cref="IfcSpaceToRoomOptions.AllowCreateMissingBoundaryViews"/> is false
    /// (the default), only existing ViewPlans are used — missing views are NOT created,
    /// and the per-space check will return <c>SkippedNoView</c> for affected spaces.
    /// When true, a minimal floor-plan view is created for each Level that has none.
    /// </summary>
    private void EnsureViewsForCandidates(
        Document hostDoc,
        List<Element> candidates,
        Transform linkTransform,
        IfcSpaceToRoomOptions options)
    {
        // Determine which level IDs will be needed
        var neededLevelIds = new HashSet<long>();
        foreach (var element in candidates)
        {
            double? bboxZ = TryGetBboxBottomZ(element, linkTransform);
            var lm = _levelMatcher.Match(bboxZ, options.LevelMatchToleranceMm);
            if (lm.LevelId != null)
                neededLevelIds.Add(lm.LevelId.Value);
        }

        if (neededLevelIds.Count == 0) return;

        // For each needed level, try to find an existing ViewPlan
        var levelsThatNeedNewViews = new List<Level>();

        foreach (var levelId in neededLevelIds)
        {
            if (!_levelElements.TryGetValue(levelId, out var level)) continue;

            var existingView = new FilteredElementCollector(hostDoc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(v =>
                    !v.IsTemplate &&
                    v.ViewType == ViewType.FloorPlan &&
                    v.GenLevel?.Id == level.Id);

            if (existingView != null)
            {
                _levelViews[levelId] = existingView;
            }
            else if (options.AllowCreateMissingBoundaryViews)
            {
                levelsThatNeedNewViews.Add(level);
            }
            // else: no existing view AND creation not allowed → _levelViews[levelId] stays absent.
            //       Per-space logic will return SkippedNoView.
        }

        if (levelsThatNeedNewViews.Count == 0) return;

        // Create missing views inside one Transaction (only when AllowCreateMissingBoundaryViews=true)
        using var tx = new Transaction(hostDoc, "MCP: Create IFC Room Boundary Views");
        try
        {
            tx.Start();

            foreach (var level in levelsThatNeedNewViews)
            {
                string? warning;
                var view = _viewResolver.Resolve(hostDoc, level, out warning);
                _levelViews[level.Id.Value] = view;
                // warnings from view resolver are informational; we don't surface them here
                // as they'll be picked up in per-space processing if the view is null
            }

            RevitMCP.Addin.TransactionCommitGuard.CommitOrThrow(tx);
        }
        catch
        {
            try { tx.RollBack(); } catch { /* ignore */ }
            // View creation failure is non-fatal: per-space checks will catch missing views
        }
    }

    private void LoadLevelElements(Document hostDoc)
    {
        var levels = new FilteredElementCollector(hostDoc)
            .OfClass(typeof(Level))
            .Cast<Level>();

        foreach (var level in levels)
            _levelElements[level.Id.Value] = level;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
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

    private static IfcSpaceToRoomResult BuildResult(
        IfcSpaceToRoomOptions options,
        List<IfcSpaceConversionItemResult> items)
    {
        int created         = items.Count(i => i.Status == ConversionStatus.Created
                                             || i.Status == ConversionStatus.DryRunReady);
        int createdBoundary = items.Where(i => i.Status == ConversionStatus.Created)
                                   .Sum(i => i.CreatedBoundaryLineCount > 0 ? 1 : 0);
        int skippedExisting = items.Count(i => i.Status == ConversionStatus.SkippedExisting);
        int skippedWarnings = items.Count(i => i.Status == ConversionStatus.SkippedDuplicateWarning);
        int failed          = items.Count(i => i.Status is ConversionStatus.FailedBoundaryCreation
                                                          or ConversionStatus.FailedRoomCreation
                                                          or ConversionStatus.FailedParameterWrite
                                                          or ConversionStatus.Error
                                                          or ConversionStatus.SkippedNoView
                                                          or ConversionStatus.SkippedNoLevelMatch
                                                          or ConversionStatus.SkippedInvalidGeometry
                                                          or ConversionStatus.SkippedMissingName
                                                          or ConversionStatus.SkippedMissingNumber);

        return new IfcSpaceToRoomResult
        {
            Success              = true,
            LinkInstanceId       = options.LinkInstanceId,
            DryRun               = options.DryRun,
            TotalProcessed       = items.Count,
            CreatedRooms         = created,
            CreatedBoundaryLoops = createdBoundary,
            SkippedExisting      = skippedExisting,
            SkippedWarnings      = skippedWarnings,
            Failed               = failed,
            Items                = items
        };
    }

    private static IfcSpaceToRoomResult Fail(IfcSpaceToRoomOptions options, string error) =>
        new()
        {
            Success        = false,
            Error          = error,
            LinkInstanceId = options.LinkInstanceId,
            DryRun         = options.DryRun
        };
}

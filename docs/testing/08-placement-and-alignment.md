# Smoke tests — Alignment, placing from DWG, moving elements

Part of the [smoke test checklist](../../TESTING.md).

## 47. Element Alignment Against Surfaces (issue #33)

Requires a model with a loaded link (an IFC converted to a Revit link is the target
case) containing walls, slabs, or ceilings, plus a few host-model devices to move.
A model where the linked walls land on Generic Models is the most valuable test case.

### 47.1 Preview

**Prompt:**
```
Preview moving the selected devices against the nearest wall.
```

**Verify:**
1. `revit_preview_align_elements` opens no transaction and leaves the undo stack unchanged.
2. Each proposal names the surface it would land on: `surfaceKind`, `targetCategory`, `targetName`, `model` (host or the link name), `distanceMm`, `currentGapMm`, `moveDistanceMm`.
3. `alternates[]` lists the other surfaces found in range, so a wrong pick is diagnosable.
4. `searchViewName` reports the 3D view the rays ran in.
5. An element already touching the surface reports a `moveDistanceMm` near 0 and counts toward `alreadyFlush`.
6. An element already inside the wall reports a negative `currentGapMm` and a negative `moveDistanceMm`.
7. The preview's target and distances match what the write tool then does.

### 47.2 Linked IFC With Unexpected Categories

**Prompt:**
```
Move these devices against the wall in the linked IFC.
```

**Verify:**
1. Walls that the IFC mapped onto Generic Models (or any other category) are still found — classification is by face orientation, not category.
2. `targetCategory` reports the category the linked element actually has, however odd.
3. `linkInstanceIds` restricts the search to the named links; other links stop being hit.
4. `targetCategories` restricts which categories are accepted, and a too-narrow list produces a "none of them is a wall/ceiling/floor" style reason rather than a silent wrong move.
5. `scope=host` ignores links entirely; `scope=links` ignores host geometry.
6. An unloaded link contributes nothing and the failure reason points at the link/view.

### 47.3 Surfaces and Tolerances

**Verify:**
1. `surface=ceiling` moves elements up to the underside of the slab above; `surface=floor` moves them down onto the floor top.
2. `surface=wall` searches horizontally only — it never picks a slab.
3. `surface=nearest` picks whichever of the three is closest and reports which kind it chose.
4. A sloped soffit is classified `other` at the default tolerance and is refused for `surface=ceiling`; raising `angleToleranceDegrees` accepts it.
5. `angleToleranceDegrees` above 44 is clamped with a warning (the bands would overlap).
6. `horizontalSamples` outside 4–32 falls back to 8 with a warning.
7. `surfaceNormalMeasured: false` appears when the face was too small or too irregular to measure, and the reason text says the orientation was inferred.

### 47.4 Moving

**Prompt:**
```
Align the selected devices to the wall with a 5 mm gap.
```

**Verify:**
1. `revit_align_elements` requires approval; the pending summary names the surface, the element count, and the gap. Rejecting it leaves the model unchanged.
2. Elements end up touching the surface, not centred on it — the move accounts for each element's own size along the search direction.
3. `gapMm` leaves the requested clearance; a negative `gapMm` embeds the element by that much.
4. A single Revit undo reverses every move from one call.
5. Pinned elements are reported as `outcome: "pinned"` and are not moved; the rest of the batch still moves.
6. Selecting several elements moves each to its own nearest surface — they never align to each other.
7. Running the tool twice in a row reports everything as `alreadyFlush` the second time.

### 47.5 Rotation and Search View

**Verify:**
1. `rotateToSurface=true` turns family instances square to the wall they land on; `rotationDegrees` in the response matches the applied rotation.
2. Rotation is skipped for ceiling and floor surfaces and for elements with no facing direction — reported, not guessed.
3. A 3D view with an active section box produces a warning, and geometry outside the box is not found.
4. `searchViewId` pointing at a non-3D view or a view template fails with a clear message.
5. A model with no 3D view fails with a message telling the user to create one or pass `searchViewId`.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Elements | `revit_preview_align_elements` | RO | "Preview moving the selection against the nearest wall." | `{total, canAlign, alreadyFlush, proposals[]}` with target and alternates | N/A | N/A | Reports the 3D view used; warns on an active section box |
| Elements | `revit_align_elements` | Approval | "Align the selection to the wall with a 5 mm gap." | `{aligned, alreadyFlush, rotated, results[]}` | Yes | Yes | Host-model elements only; plans resolved before the first move |

---

## 48. Placing Items From a DWG (issue #32)

Requires a model with an imported or linked DWG that marks device locations — blocks,
POINT entities, or circles on a dedicated layer — plus a loaded family to place.

### 48.1 Layer Inventory

**Prompt:**
```
Which layers in the imported DWG hold device locations?
```

**Verify:**
1. `revit_get_cad_placement_points` called WITHOUT `layers` returns every CAD layer with `blockCount`, `pointCount`, `circleCount`, `curveCount`, `textCount`, `otherCount`.
2. `placeable` is true only for layers carrying blocks, points, or circles; a curves-and-text layer reports false.
3. `minZmm` / `maxZmm` report the height range each layer sits at.
4. With no `importInstanceId` and exactly one CAD import in the model, that import is used; with several, the call fails listing the candidates and their ids.
5. A model with no CAD import fails with a clear message.

### 48.2 Reading Points

**Prompt:**
```
Show me the points on E-SOCKET-SYM.
```

**Verify:**
1. Passing `layers` returns points in millimetres with `layer`, `source` (block/point/circle), `rotationDegrees`, and `mergedCount`.
2. Coordinates land where the symbols visibly are in the model — spot-check two against Revit.
3. A symbol drawn as a block around a circle yields ONE point, not two, and the survivor reports `source: "block"` with the block's rotation.
4. `mergeToleranceMm=0` disables merging and the duplicate reappears.
5. `pointSources=["circle"]` returns only circle centres; `["block"]` only insertion points.
6. A layer name that does not exist produces a warning, not a failure.
7. `limit` caps the returned points and `pointCount` still reports the true total.
8. `elevation.isFlat` is true for a 2D drawing, and the note tells the caller to ask for a mounting height.

### 48.3 The Two Required Answers

**Verify:**
1. `revit_place_from_cad` without `layers` fails, and the message says to run the inventory and ask the user which layers hold the locations.
2. Without `elevationMode` it fails, and the message lists dwg / level / explicit and explains that a 2D drawing has no mounting height.
3. `elevationMode=level` without `levelName` fails; a `levelName` that does not exist in the model fails naming it.
4. The bridge rejects both omissions before the request reaches Revit.

### 48.4 Placing

**Prompt:**
```
Place the socket family at every location on E-SOCKET-SYM, 1100 above Level 1.
```

**Verify:**
1. `revit_preview_place_from_cad` opens no transaction and reports `locationsFound`, `willPlace`, `alreadyPlaced`, the elevation range, and a sample of points.
2. `revit_place_from_cad` requires approval; the pending summary names the family, the layers, the elevation, and the cap. Rejecting it leaves the model unchanged.
3. Instances land at the marked positions, at level elevation + `offsetMm`.
4. `elevationMode=explicit` places at the absolute `elevationMm`; `elevationMode=dwg` keeps the drawing's own heights on a 3D DWG.
5. `applyBlockRotation=true` orients instances the way the DWG blocks are rotated; `false` leaves them at 0; `rotationOffsetDegrees` shifts them all by a constant.
6. A single Revit undo removes every instance created by one call.
7. A family whose placement type needs a host or a view fails per location with an explanatory error, and the rest still place.

### 48.5 Re-running

**Verify:**
1. Running the same call twice places nothing the second time — every location reports `alreadyPlaced`.
2. `skipExisting=false` places a second instance at every location (then undo).
3. `duplicateToleranceMm` controls how close counts as the same location.
4. Existing instances are matched in plan only: sockets already at 1100 still match marks at 0.
5. Adding a second layer to the call places only the new locations.
6. `maxInstances` caps the run with a warning naming how many were dropped; the cap is enforced at 2000.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Query | `revit_get_cad_placement_points` | RO | "Which DWG layers hold device locations?" | `{layers[]}` with per-source counts; `{points[]}` when layers are passed | N/A | N/A | Omit layers for the inventory; `elevation.isFlat` flags a 2D drawing |
| Elements | `revit_preview_place_from_cad` | RO | "Preview placing sockets from E-SOCKET-SYM." | `{locationsFound, willPlace, alreadyPlaced, sample[]}` | N/A | N/A | Same plan the write tool executes |
| Elements | `revit_place_from_cad` | Approval | "Place sockets from E-SOCKET-SYM, 1100 above Level 1." | `{createdCount, alreadyPlaced, created[]}` | Yes | Yes | `layers` and `elevationMode` are required; `skipExisting` makes re-runs top up |

---

## 49. Lining Elements Up In a View (issue #38)

Requires a view with a handful of tags (some with leaders), text notes, detail lines,
and a few model elements, plus a sheet with two or more viewports.

### 49.1 Preview

**Prompt:**
```
Preview aligning the selected tags to the left.
```

**Verify:**
1. `revit_preview_align_in_view` opens no transaction and leaves the undo stack unchanged.
2. Each proposal reports `moveMm`, `direction` (left/right/up/down/none), `anchor`, and `viewSpecific`.
3. `misalignmentMm` reports how far out of line the set currently is; `axis` reports which way things move.
4. Elements already on the line report `direction: "none"` and count toward `alreadyInLine`.
5. A pinned element that would move is warned about in the preview, before approval is requested.
6. The preview's `moveMm` matches what the write tool then does.

### 49.2 Modes and Reference

**Verify:**
1. `left`, `right`, `top`, `bottom` line up the named edge; `centerVertical` and `centerHorizontal` line up centres.
2. `centerVertical` moves elements sideways and `centerHorizontal` moves them vertically — the mode is named for the line, not the motion.
3. `alignTo=extreme` (the default) never moves anything outside the set's current bounds.
4. `mode=right, alignTo=min` pulls every right edge back to the leftmost one — the opposite of `extreme`.
5. `alignTo=average` centres the set on its mean; `first` and `last` use the ends of `elementIds`.
6. `referenceElementId` keeps that element still and moves the rest to it; an ID outside the set warns and falls back.
7. `useSelection` with `alignTo=first` warns that a Revit selection has no picking order.
8. `mode="centre vertically"`, `"align-left"` and `"distribute horizontally"` are accepted; `mode="center"` is rejected with the valid list.

### 49.3 Distributing

**Prompt:**
```
Spread these tags out evenly down the view.
```

**Verify:**
1. `distributeVertical` leaves the top and bottom elements where they are and respaces the rest; `resultingSpacingMm` reports the step.
2. `spread=gaps` equalises the clear space between bounding boxes; `spread=centers` equalises centre-to-centre distance. The two differ visibly when the elements are different sizes.
3. Selecting the same elements bottom-to-top gives the same result as top-to-bottom.
4. `spacingMm` lays the elements out from the lowest one at that fixed step, ignoring the original span.
5. Two elements with no `spacingMm` fails with a message asking for a third element or a spacing.
6. `spread=gaps` on elements too wide for their span warns with the overlap and still produces a deterministic layout.
7. `alignTo` or `referenceElementId` on a distribute mode warns that it does not apply.

### 49.4 Anchors and Element Kinds

**Verify:**
1. A tag with a leader is measured from its head, not its bounding box: `anchor: "origin"` with an `anchorNote` naming the leader.
2. `anchor=boundingBox` forces the box even for those tags, and the leader visibly skews the result — this is the diagnostic case.
3. `anchor=origin` on ordinary elements lines up location points, so `left`, `right` and `centerVertical` all give the same answer.
4. Text notes, detail lines, dimensions, and model family instances all move; `viewSpecific` is `false` only for the model elements.
5. A model element aligned in a plan view also moves in every other view — it is a model edit, not a drawing edit.
6. An element with no graphics in the view falls back to its anchor point with a note rather than being dropped.

### 49.5 Views and Sheets

**Verify:**
1. Passing a sheet as `viewId` and viewport IDs as `elementIds` lines the viewports up on the sheet.
2. Passing a view ID instead of its viewport ID is dropped with a warning naming the viewport as the thing to pass.
3. Annotation owned by a different view is dropped with a warning naming that view; the rest still align.
4. In a rotated crop or a section, "left" follows the view, not model X.
5. A perspective 3D view is refused; an orthographic 3D view is allowed with a warning.
6. A view template as `viewId` is refused.

### 49.6 Moving

**Prompt:**
```
Align the selected tags to the left.
```

**Verify:**
1. `revit_align_in_view` requires approval; the pending summary names the mode, the element count, the reference, and the view. Rejecting it leaves the model unchanged.
2. A single Revit undo reverses every move from one call.
3. Pinned elements are reported as `outcome: "pinned"` and are not moved; the rest of the batch still moves.
4. Running the tool twice in a row reports everything as `alreadyAligned` the second time.
5. A tag Revit refuses to transform still moves — the head position fallback is used and the outcome is `aligned`.
6. Nothing moves perpendicular to the view: elevations are unchanged by a plan-view align, and plan positions are unchanged by a section-view align.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Elements | `revit_preview_align_in_view` | RO | "Preview aligning the selected tags to the left." | `{total, willMove, alreadyInLine, misalignmentMm, proposals[]}` | N/A | N/A | Reports the anchor used per element; same plan the write tool executes |
| Elements | `revit_align_in_view` | Approval | "Align the selected tags to the left." | `{aligned, alreadyAligned, failed, results[]}` | Yes | Yes | Moves in the view plane only; everything measured before the first move |

---

## 50. Moving Elements To Exact Coordinates

Requires a plan with a handful of point-based fixtures (family instances, sockets,
luminaires), at least one pinned, at least one wall or pipe, and a DWG whose fixture
positions differ from the model's.

### 50.1 Preview

**Prompt:**
```
Preview moving element 1756386 to X 76871.5, Y 71602.9.
```

**Verify:**
1. `revit_preview_move_elements` opens no transaction and leaves the undo stack unchanged.
2. Each entry reports `currentPointMm`, `targetPointMm`, `translationMm`, `distanceMm`, `pinned`, `canMove` and `status`.
3. `currentPointMm` matches what `revit_inspect_selected_elements` reports for the same element.
4. `distanceMm` equals the length of `translationMm`, and both are in millimetres.
5. The preview's numbers match what the write tool then does.

### 50.2 Omitted Axes

**Verify:**
1. Omitting `targetZmm` leaves `targetPointMm.z` equal to `currentPointMm.z` and `translationMm.z` at 0.
2. After the move, the fixture's elevation-from-level parameter is unchanged.
3. `"targetZmm": 0` is treated as a target, not an omission — the element moves to the project origin plane.
4. A move with only `targetXmm` leaves Y and Z alone.
5. A move with no target coordinate at all warns and reports `AlreadyThere`.

### 50.3 Staleness

**Verify:**
1. Correct `expectedXmm`/`expectedYmm`/`expectedZmm` let the move through; `staleDeviationMm` reports roughly 0.
2. Nudging the element by more than `positionToleranceMm` in Revit, then re-running the same request, reports `Stale` and moves nothing.
3. `staleDeviationMm` reports the worst axis, not the 3D distance.
4. Only the axes supplied are checked — an expected X alone does not catch a Z drift.
5. A stale element is reported `Stale` even when it is also pinned.
6. `positionToleranceMm` outside 0.001–10000 warns and is clamped.

### 50.4 Unsupported Elements and Missing Ids

**Verify:**
1. A wall, pipe, duct or cable tray is reported `UnsupportedLocation` naming the curve placement — never moved to a bounding-box centre.
2. A family type id (rather than an instance id) is reported `UnsupportedLocation`.
3. An id that does not exist is reported `Missing`.
4. Under `atomic=true` any of these rejects the whole batch before the transaction opens; the undo stack is untouched.

### 50.5 Pinned Elements

**Verify:**
1. With `skipPinned=true` (the default) a pinned element is reported `Pinned` and the rest of the batch still moves.
2. With `skipPinned=false` the pinned element is a failure, and under `atomic=true` the batch is rejected.
3. The element is still pinned afterwards — the tool never unpins anything.

### 50.6 Moving

**Prompt:**
```
Move element 1756386 to X 76871.5, Y 71602.9.
```

**Verify:**
1. `revit_move_elements` requires approval; the pending summary names the element count. Rejecting it leaves the model unchanged.
2. A single Revit undo reverses every move from one call.
3. The element keeps its id, type, parameters, circuit membership and tags; its tags follow it.
4. Running the same call twice reports everything as `AlreadyThere` the second time.
5. `elementIds` groups every element into exactly one of `moved`, `skipped`, `stale`, `missing`, `pinned`, `unsupportedLocation`, `failed`, `rolledBack`, `notAttempted`.
6. A 500-move batch completes in one transaction and one undo step.

### 50.7 Atomic and Non-Atomic

**Verify:**
1. `atomic=true` with one bad entry moves nothing; the good elements report `NotAttempted` and are still at their original coordinates.
2. `atomic=false` with the same batch moves the good elements and reports the rest individually.
3. `atomic=false` with a Revit refusal mid-batch keeps the earlier moves; only that element reports `Failed`.
4. Elements already at their target, and pinned elements under `skipPinned=true`, never trigger an atomic rollback.
5. An oversized batch (over 2000) is rejected with a message asking for it to be split, rather than truncated.
6. The same element id twice in one `moves` array is rejected naming both positions.

### 50.8 DWG Alignment End To End

**Prompt:**
```
Line the ceiling fixtures up with the DWG.
```

**Verify:**
1. `revit_get_cad_shapes` reconstructs the fixture outlines and centres.
2. The preview shows each fixture's travel; ambiguous matches are visible as outliers before anything moves.
3. After `revit_move_elements`, `revit_inspect_selected_elements` reports the target coordinates.
4. Elevations are unchanged because `targetZmm` was omitted.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Elements | `revit_preview_move_elements` | RO | "Preview moving element 1756386 to X 76871.5, Y 71602.9." | `{total, canMove, blocked, totalTravelMm, moves[]}` | N/A | N/A | Same plan the write tool executes; omitted axes keep their current value |
| Elements | `revit_move_elements` | Approval | "Move element 1756386 to X 76871.5, Y 71602.9." | `{moved, skipped, failed, elementIds{}, moves[]}` | Yes | Yes | Never deletes or recreates; `atomic=true` undoes the whole batch on any failure |

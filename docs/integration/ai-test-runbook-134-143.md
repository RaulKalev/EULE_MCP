# AI test runbook: issues #134-#143

For an agent verifying these issues against a live Revit. None of this has run in Revit yet: the build and unit tests pass,
and everything below is what they cannot show. Repeat in Revit 2026 and 2024.

## Rules

1. **Only a test copy.** Before each batch of writes, `revit_get_connection_status` must report the test copy's
   `localModelPath`. If it does not, stop and tell the operator.
2. Bridge and add-in must both be this build. Tools the session does not advertise run through `revit_tools_call`; read
   each schema with `revit_tools_describe` first.
3. Tests marked **(operator)** need a person to look at or click something in Revit. Ask, do not assume.
4. Report per test: pass, fail with the response, or not run with the reason. A failed assumption is a finding, not a
   reason to work around it.

## Setup

- Project A, active: walls with varied Comments and Mark, rooms with a Department parameter (some empty), a DWG with
  blocks (one rotated) and loose rectangles, sheets of one set with title blocks.
- Views in A: plan `P` (no template, cropped), plan `VB` whose template `TB` controls V/G filters and has 2+ views, plan
  `VC` whose template `TC` does not control them, section `S`, drafting view `D`, 3D view `V3`, schedule `SCH`, and a view
  with 200+ elements.
- Project B open as a second document. Link instance `L` with room `LR`. Filled region types `A` and `B`.
- For #137: a second Revit window and a second agent session.
- Name everything you create `AI-*`, and remove it afterwards.

## #137 Instance pin per session

1. Sessions 1 and 2 each `revit_select_instance` their own window, in both orders. `revit_get_connection_status` in each
   shows only its own model, whichever pinned last.
2. `revit_list_instances` shows `sessionProcessId`, `sharedProcessId`, and per instance `isSessionPin`, `isSharedDefault`,
   `isCurrentTarget`.
3. **(operator)** Click *Make This Project Active* in the other window. A pinned session does not move; `sharedProcessId`
   changes. `revit_select_instance processId=0`, and the session follows the click.
4. A new session that has pinned nothing follows the shared default. With two windows and no shared default, its calls fail
   and list both.
5. **(operator)** Close a session's pinned window. Its next call fails naming the pid and reaches no other window.

## #138 Delete approvals survive

1. `revit_delete target=elements elementIds=[<AI line>]`: `approval_required`, and the message says valid for 24 hours.
2. Before approving, run `revit_export_view_image`, then make another write. `revit_get_approval_status` is still pending.
3. **(operator)** Approve after 15 minutes or more (past the old 10-minute limit). The status is `succeeded`, and the result
   warns that the model changed.
4. **(operator)** Request, activate project B, approve: it runs on A, and B is untouched.
5. Request, delete the element by another route, approve: reported skipped, nothing else removed.
6. **(operator)** Request, close the project, approve: `approval_context_changed`.
7. Same as 1-3 with `target=views`, `target=sheets` and `revit_delete_view_filters`.
8. An ordinary write, such as `revit_create_lines` with Direct Edit off, then another model change, then approve: still
   `approval_context_changed`. Approve after an image export instead: it runs.

## #139 Image transform

1. `revit_export_view_image viewId=P bboxMm={...} pixelSize=2400`: `imageTransform` has `widthPx`, `heightPx`, `mmPerPx`,
   `originMm`, `rightDirection`, `upDirection`, `contentBoxPx`. `widthPx`/`heightPx` equal the PNG's real size.
2. **Highest risk:** draw a rectangle at known coordinates, also in a view whose grids reach past the crop, compute its
   pixels and look at the image: it must be there within a few pixels. `contentBoxPx` must be where the crop is, not always at
   the top-left. Nothing of the temporary line may remain, in the image or the model.
3. Same on a rotated plan and on section `S`.
4. A sheet: `coordinateSystem=sheet`; a viewport's location maps to its pixel.
5. An uncropped view: `imageTransform` absent, `imageTransformNote` present, the image still exported.
6. `highlightIds=[id]` outlines it in red, the same as `highlightElementIds`, and the view is unchanged afterwards.

## #140 Lines and rectangles

1. `revit_create_lines`, dimension one line, then `revit_set_line_geometry lines=[{elementId, x1, y1, x2, y2}]`: same id,
   dimension follows, no approval under Direct Edit.
2. With `lineStyle`: the style changes. An unknown style warns.
3. Skips with reasons: an arc, a pinned line, a missing id, a point off the plane (a section line with a wrong z). The other
   lines in the call still change.
4. `revit_create_rectangle viewId=P minX minY maxX maxY`: 4 lines in the right place. Undo removes it in one step.
5. `center={x,y} width height rotationDeg=30 text="AI" asGroup=true lineStyle=...`: returns `lineIds`, `textId`, `groupId`;
   text centred and rotated; moving the group moves all of it.
6. In section `S`: `coordinateFrame=viewRightUp`, and the rectangle lies where those coordinates say. On a sheet and in `D`:
   `modelXY`. In `V3`: refused.
7. Incomplete input (three of the min/max, both forms, max below min): refused, nothing drawn.

## #141 Text notes

1. A note with a bold heading above its body. `revit_set_text_notes` with `findReplace` in the body and `formatting=plain`:
   the replaced text is plain, the heading stays bold. **(operator)** confirm.
2. `formatting=matchFirstRun`: the replaced text is bold. Default `keep`: as before. `formatting=bold`: refused.
3. The preview takes the same argument and reports `formatting`.
4. `revit_get_text_notes`: `boundingBox`, `lineCount`, `paragraphCount`. The box encloses the note on a plan, on a sheet
   (sheet mm) and in a section. `lineCount` is plausible for a wrapped note.
5. `revit_check_annotation_overlaps` on a view with two overlapping notes, a note over a tag, a line through a note: one
   finding each, with both ids and sizes. A box of lines around a note is not reported.
6. On a sheet (`sheetNumber`): a note across a viewport edge is `viewportEdge`; one wholly inside is not reported.
7. Compare with an exported image: everything the picture shows as overlapping is found. Report misses.

## #142 Title blocks

1. `revit_get_titleblock_values sheetNumbers=[...]`: family, type, values, `widthMm`, `heightMm`, `isoSize` correct.
2. Set a wrong format on one sheet: `format_mismatch`. A different date on one: `dates_differ`. A different version:
   `versions_differ`. Restore them.
3. The project's own parameter names: if the roles are not found, pass `formatParameter`, `dateParameter`,
   `versionParameter`, and report which names the project uses.
4. A missing sheet number: listed in the warnings; the others still read.
5. `revit_set_parameters_bulk entity=sheets sheetNumbers=[...] parameters={<instance parameter>: "x"} includeTitleBlockInstance=true`:
   written to the instance, listed in `titleBlockParameters`. Without the flag: a warning, nothing written.

## #143 CAD sizes

1. `revit_tools_search` for each of "CAD geometry bounding box", "equipment outline in linked DWG", "seadme mõõdud DWG-st",
   "rectangle size from CAD": `revit_get_cad_shapes` or `revit_get_cad_placement_points` is in the top 3.
2. `revit_get_cad_placement_points layers=[<blocks layer>] includeExtents=true`: each block has `extents`. Width and depth
   match the DWG, also for the rotated block. Without the flag: no `extents`.
3. Draw `revit_create_rectangle` from a block's `extents` in the host view: it lines up with the block. Repeat for a linked
   DWG that has been moved.

## #136 Elements from a view

1. `revit_get_elements_info view=active pageSize=200 compact=true` on a plan: `data.view.matchedBy=active`, elements have
   `ElementId`, `Category`, `Location`, `BoundingBox`. Select the ids; **(operator)** they match what the plan shows.
2. Without `compact`: `Parameters` empty unless `includeInstanceParameters=true` or `parameterNames`.
3. Hide element X in plan A. `view=<A> elementIds=[X]`: 0, `excluded.hiddenInView` at least 1. `view=<B>` that shows it: X.
4. Category hide, then filter hide (rule, selection, and through a template), then temporary hide and isolate:
   the element is absent and `excluded.categoryHidden`, `filterHidden`, `temporarilyHidden` count it.
5. **Highest risk:** an element outside the crop is absent with `excluded.outsideCrop`, and everything inside stays. Repeat
   with an L-shaped and a split crop; compare the ids with the crop on and off.
6. Sheet: `revit_count_elements view=<sheet number>` gives `view.kind=sheet`, `sheetElements.count`, `viewports[]`;
   `totalCount` is the sum. Try the sheet id and name. `revit_get_elements_info view=<sheet> pageSize=20` pages to `hasMore=false`.
7. `revit_find_elements_by_parameter view=<id> filters=[{"parameterName":"Comments","operator":"isEmpty"}]`: only matches.
   With `view` also: `category`, `elementIds`, `useSelection`, `summaryOnly`.
8. `document=<B> view=<a view name in B>` works; `document=<B> view=active` is `active_document_required`.
9. Without `view`, the three reads match a pre-#136 build field for field.
10. A category of 200+ without `view` still gives `graph_first`; with `view` it does not.
11. The 200+ view: `status=view_large_read`, `read=false`, `categories`, `next`; each `next` call works. `revit_count_elements`
    on it returns plain counts. Check `durationMs` on a large 3D view against the 30 s timeout.
12. `scope=annotation` and `scope=model` add up to `all`; `scope=foo` is `validation_failed`.
13. Drafting view, `scope=annotation`: detail lines have `Location.Start`, `End`, `LineStyle`, `PartOfGroup`; text notes and
    filled regions have `BoundingBox` and `Type`; a filled region's boundary lines are not returned.
14. Fields: a hosted door has `Host`, a tag has `TaggedElementIds`, `includeRoom=true` gives `Room`.
15. References: an unknown name is `view_not_found`, a partial name adds `candidates`, a name shared by two views is
    `view_ambiguous`, a template is `view_is_template`, a schedule is `view_not_collectable`.
16. **Decision to check:** reference planes, reference lines and points are excluded even though they show. Say if that is wrong.

The full list of Revit assumptions is in [`docs/view-element-reads.md`](../view-element-reads.md).

## #134 View filters

1. `revit_list_filterable_parameters`, then with `categories=["Walls"]`: Comments has text operators, a Yes/No has only
   equals, notEquals, hasValue, hasNoValue.
2. Preview one bad item per code, and nothing changes: `category_not_found`, `category_not_filterable`,
   `parameter_not_filterable`, `operator_unknown`, `operator_not_valid_for_type`, `rule_value_missing`, `invalid_filter_name`,
   `filter_name_exists`, `element_not_found`, `duplicate_name_in_request`.
3. Create `AI-Nested` with `{"and":[{Comments contains x},{"or":[{Mark beginsWith y},{Mark hasNoValue}]}]}`. **(operator)**
   builds the same filter by hand; the same walls are overridden by both. This is the main acceptance criterion.
4. Numbers (`Length >= 3000`, in project units) and references (`Base Constraint equals <level name>`); a bad level name is
   `rule_value_not_found`. A selection filter: `memberCount`.
5. `revit_apply_view_filters views=["P"] add=["AI-Nested"]` with no graphics: `graphics:{}`. **(operator)** sets a colour in
   V/G, and `revit_get_view_filters` shows it.
6. With graphics (halftone, transparency, lines, surface and cut patterns): read back the same. A bad pattern is
   `pattern_not_found`, `transparency=101` is `graphics_invalid`, re-adding is `filter_already_applied`.
7. `order=[...]` is kept and settings survive. **(operator)** confirms the dialog's order and which override wins.
8. **Template:** preview `views=["VB"]`: target `TB`, `routedToTemplate:true`, `affectedViews` lists its views. After applying,
   `VB` shows `source:template`. `views=["VC"]`: goes on `VC`, `TC` unchanged. A sheet is `view_cannot_have_filters`.
9. Edit: rename, categories (`rule_parameter_not_applicable`), `clearRules`, selection members, `filter_kind_mismatch`.
10. Delete in use: the preview lists `usage.views`, `templates`, `viewsThroughTemplates`. The call needs manual approval even
    with Direct Edit on. Undo restores it.
11. Copy in one project: same graphics, order and state. `copySettings=false`: no graphics.
12. **Copy to B:** `unresolved` names each missing parameter and pattern, `selectionMembers` says members were not copied, and
    neither model changes on preview. The write creates what resolves. Try `ifExists` fail, skip, reuse, overwrite.
13. Non-active: list, create and apply on B while A is active. `atomic=true` with one refused item writes nothing. One undo per call.

## #135 Filled regions

1. `revit_list_filled_region_types`: patterns, colours, `lineWeight`, `isMasking`, `instanceCount`.
2. Preview, then create, a region with a hole in `P`: `outlines=1`, `holes=1`, area right. `revit_list_filled_regions` returns
   loops that give the same area when fed back.
3. In `S` with x, y, z (no z is `coordinate_z_required`) and with `space:"view"`. In `D`. Rectangles in all three; `paddingMm`.
4. Host room in `P`: matches the boundary, a column makes a hole, a curved wall gives arcs. Room in `S`: `boundary_not_in_view_plane`.
5. **Linked room:** `room:{id:LR,linkInstanceId:L}` lines up in the host view, also after moving the link.
6. Detail lines, shuffled: created. Three of four: `loop_open`.
7. Invalid, nothing written: `loop_open`, `loop_self_intersecting`, `loops_intersect`, `hole_outside_outer`. `V3` and `SCH`: `view_not_supported`.
8. Rule: rooms by level with `typeMap` on Department. The preview lists each region, `skippedSources`, `unmappedValues`; the
   write creates exactly those. Repeat with a linked source and an `elements` rule with `boundingBox`.
9. Type, parameter and line style edits keep the id.
10. Reshape a tagged region with Comments: the preview warns; the write returns `idMap`, `lostAnnotations`; same type, view and
    Comments. One undo restores it. In a group: `region_in_group`.
11. Types: the preview lists `affectedRegions`; colours and weight change; `duplicateFrom` makes a new one; the same name again
    is `type_name_exists`; a model pattern as background is `pattern_not_valid`; `isMasking` hides what is behind.
12. Non-active document; `atomic=true`; one undo per call.

## Afterwards

Run the full benchmark with Revit (`docs/benchmarks/README.md`) and commit the report: the committed ones are schema only.

# Smoke tests — Text notes, annotation checks, view reads, filled regions, view filters

Part of the [smoke test checklist](../../TESTING.md).

## 57. Editing Text Note Content (issue #85)

Requires a view with several text notes, at least one with a bold or underlined word, and ideally a
workshared model where another user has borrowed one note. Pure logic (modes, matchCase, wholeWord,
map lookup, width estimate) is covered by `TextNoteEditPlannerTests`.

**Verify:**
1. `revit_preview_set_text_notes mode=append suffix=" // Data network connections" textFilter="Arvutivõrgu"` opens no transaction and lists old text → new text with the view name for every matching note.
2. `revit_set_text_notes` with the same arguments requires approval; after approving, the element ids are unchanged and a single Revit undo restores every note.
3. `mode=findReplace` with `wholeWord: true` leaves partial-word matches alone; `matchCase: true` leaves different-case matches alone.
4. `mode=map` with a JSON object of 2–3 texts changes only notes whose whole (trimmed) text is a key; the rest are reported in `unchanged` with reason `text not in map`.
5. Bold/underline runs outside the edited range survive (`formattingPreserved: true`).
6. `autoWidth: true` widens notes whose text grew (never narrows); `widthMm: 60` sets the width; passing both is refused.
7. A note borrowed by another user appears in `skipped` with a reason; the rest of the batch is still applied.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Elements | `revit_preview_set_text_notes` | RO | "Preview adding ' // Power' to every 'Toide' text note" | `{changedCount, unchangedCount, skippedCount, changed[], unchanged[], skipped[]}` | N/A | N/A | Same plan the write tool executes |
| Elements | `revit_set_text_notes` | Approval | "Translate the rack labels with this map" | Per-note old → new text, `formattingPreserved` | Yes | One Undo | Element ids kept; worksharing-blocked notes skipped |

---

## 58. Instance Pin, Delete Approvals, Annotation and Sheet Checks (issues #137-#143)

Unit tests: `RevitInstanceRegistryTests`, `ApprovalOutcomeTests`, `AnnotationAndSheetChecksTests`, `ToolSearchIndexTests`.
Tools and fields are described in [`docs/annotation-and-sheets.md`](../annotation-and-sheets.md),
[`docs/worker-document.md`](../worker-document.md) and [`docs/execution-safety.md`](../execution-safety.md).

Manual, in Revit 2026 and 2024:

1. **#137** Two Revit windows, two agent sessions. Each runs `revit_select_instance` on its own window, then
   `revit_get_connection_status`: each sees only its own model, whichever pinned last. Click *Make This Project Active*
   in a window: a session with a pin does not move, a new session follows the click. Close a pinned window: its session's
   calls fail naming the pid and do not reach the other window.
2. **#138** `revit_delete target=elements` on a test element, then make other edits in the model and run
   `revit_export_view_image`. Approve later: the element is deleted, with a warning that the model changed. Repeat
   approving with another project active: it runs on the original project. Delete the element by hand first: the call
   reports it skipped. Close the project first: `approval_context_changed`.
3. **#139** Export a cropped plan and a sheet. Pick an element with a known location: its pixel by `imageTransform` is
   where it is on the image, and `contentBoxPx` ends where the empty area begins.
4. **#140** `revit_set_line_geometry` on a detail line with a dimension: same id, dimension follows. Sections and sloped
   model lines: a point off the plane is skipped. `revit_create_rectangle` in a plan, a section and a sheet, with text and
   `asGroup=true`: 4 lines and centred text, one group.
5. **#141** Edit a note under a bold heading with `formatting=plain`: the body is not bold, the heading stays.
   `revit_check_annotation_overlaps` on a sheet with overlapping notes and a note across a viewport edge.
6. **#142** `revit_get_titleblock_values` on sheets of one set: real size and ISO size correct; change a format value to a
   wrong size and a date on one sheet: the findings say so. Write an instance parameter with `includeTitleBlockInstance=true`.
7. **#143** `revit_get_cad_placement_points includeExtents=true` on a layer of blocks, including a rotated block: width
   and depth match the DWG; a box drawn from the extents lines up with the block in the host view.
## 59. View Element Reads (issue #136)

Needs a plan with a crop region, a hidden element, a view filter that hides something, tags and text; a drafting view with
detail lines, filled regions and a group; a sheet with two or more viewports; a schedule and a view template. Pure logic
(view references, scope, exclusion rules, guard, paging, crop maths) is covered by `ViewElementReadsTests`. What only Revit
can answer is listed in [docs/view-element-reads.md](../view-element-reads.md#to-be-confirmed-in-revit): write the results there.

**Verify:**
1. `revit_get_elements_info view=active pageSize=200` on a plan: select the returned ids in Revit (`revit_select_elements` or a model page) and compare with what the plan shows.
2. Hide an element in the view. It is gone from `view=active`, `excluded.hiddenInView` went up, and the same element is returned for another view that shows it.
3. Hide a category, turn on a filter that hides something, and use temporary hide: each shows up under its key in `excluded` (`categoryHidden`, `filterHidden`, `temporarilyHidden`).
4. An element outside the crop region is not returned (`excluded.outsideCrop`), including with a non-rectangular crop.
5. `revit_count_elements view=<sheet number>` gives `sheetElements` and one `viewports` entry per viewport, and the same read with `revit_get_elements_info` pages through them with `pageSize=20 page=0,1,...`.
6. `filters`, `category`, `elementIds`, `useSelection`, `compact=true`, `summaryOnly=true` and `document=<non-active title>` all work with `view`.
7. `scope=annotation` on the drafting view returns detail lines with `Location.Start`, `Location.End`, `LineStyle` and `PartOfGroup`; text notes and filled regions with `BoundingBox` and `Type`; no filled-region boundary lines.
8. `view=<unknown>` returns `view_not_found` with `candidates`; a name shared by two plans returns `view_ambiguous`; a schedule `view_not_collectable`; a template `view_is_template`; `view=active document=<non-active>` `active_document_required`.
9. A view with 200+ elements returns `status: view_large_read` with `categories` and `next`, and no elements; each `next` call then works. `revit_count_elements` on it just counts.
10. Without `view`, all three tools return the same payload as before: compare with a build from before #136.
## 60. Filled Regions (issue #135)

Requires a floor plan and a section through a room, a drafting view, a 3D view and a schedule, a host room, a loaded Revit
link with rooms, and at least two filled region types. Pure logic (loop validation, holes, padding, projection, rule
expansion, the type map) is covered by `FilledRegionGeometryTests` and `FilledRegionSpecTests`. Run this in Revit 2026 and 2024.
See `docs/filled-regions.md`.

**Verify:**
1. `revit_list_filled_region_types` lists patterns, colours, line weight, masking and `instanceCount`, with the fill patterns and line styles.
2. `revit_create_filled_regions` makes a region with a hole from `loops` in the plan, the section (with z) and the drafting view, and from a `rectangle` in each. `revit_list_filled_regions` returns the same loops and `revitAreaM2` matches `areaM2`. One Undo removes them.
3. A `room` spec in the plan follows the room boundary; a room with a column or island gets a hole; `paddingMm` grows it. In the section the preview returns `boundary_not_in_view_plane`.
4. A linked room's region lines up with the room in the host plan, also after the link is moved and the region is drawn again.
5. Four `detailLines` drawn in the section, given in any order, make a region in the section; the same ids in the plan return `boundary_not_in_view_plane`.
6. A rule with a `typeMap` per room: the preview lists every region and every `skippedSources` entry; the write creates exactly those. An unmapped value is skipped with `value_not_mapped`, never given a type.
7. The preview reports `loop_open` and `loop_self_intersecting` for bad loops and `view_not_supported` for the 3D view and the schedule, and nothing is written.
8. Changing `type` and `parameters` with `revit_edit_filled_regions` keeps the id. A `boundary` returns `idMap`, keeps type, view and Comments, and lists an attached tag or dimension as lost.
9. `revit_preview_set_filled_region_types` lists the regions of the type; the write changes all of them. `duplicateFrom` makes a new type; a taken name returns `type_name_exists`.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Elements | `revit_count_elements` `view` | RO | "How many tags are on this plan?" | `{totalCount, groups, view, scope, excluded}` | N/A | N/A | Per viewport for a sheet; never guarded |
| Elements | `revit_get_elements_info` `view` | RO | "List the fire alarm devices in this view with their locations" | `{totalMatched, elements[], view, scope, excluded}` | N/A | N/A | `view_large_read` from 200 unnarrowed elements |
| Parameters | `revit_find_elements_by_parameter` `view` | RO | "Which devices on sheet E-101 have no circuit?" | `{totalMatched, sheetElements, viewports[]}` | N/A | N/A | Filters per viewport |
## 61. View Filters (issue #134)

Reference: [`docs/view-filters.md`](../view-filters.md). Tools: `revit_list_view_filters`, `revit_list_filterable_parameters`,
`revit_get_view_filters`, `revit_preview_create_view_filters` / `revit_create_view_filters`,
`revit_preview_edit_view_filters` / `revit_edit_view_filters`, `revit_preview_delete_view_filters` / `revit_delete_view_filters`,
`revit_preview_apply_view_filters` / `revit_apply_view_filters`, `revit_preview_copy_view_filters` / `revit_copy_view_filters`.

Unit tests: `ViewFilterTests` (rule tree, operators per storage type, requests, matching, routing, order, approval text).

Manual test in Revit 2026 and 2024, on a project with walls, one view with a template that controls V/G filters and one
without; for the copy, a second open project.

1. `revit_list_filterable_parameters categories=["Walls"]` lists Comments, with `valueKind: Text` and text operators. Without
   categories it lists the filterable categories.
2. `revit_preview_create_view_filters` with a nested AND/OR filter on Walls, a taken name, a bad category
   (`"Sheets"`, `"Nope"`), a parameter that is not filterable, and `contains` on a number: each reports its own code
   (`filter_name_exists`, `category_not_filterable`, `category_not_found`, `parameter_not_filterable`,
   `operator_not_valid_for_type`). `revit_list_view_filters` shows nothing new.
3. `revit_create_view_filters` with the valid nested filter. Build the same filter by hand in Manage > Filters. In a view,
   override each with its own colour: both colour exactly the same walls.
4. `revit_apply_view_filters views=[<view without template>] add=[<filter>]`: Visibility/Graphics > Filters shows it with no
   overrides. Set a colour there by hand: it works, and `revit_get_view_filters` reports it.
5. `revit_preview_apply_view_filters views=[<view under template>] add=[<filter>]`: `targets` names the template with
   `routedToTemplate: true` and lists every view under it. Apply: the template has the filter, each of those views shows it.
6. `revit_apply_view_filters` with graphics (colour, weight, pattern, transparency, halftone) and `order`: the dialog shows them
   and the order. Settings of the other filters are unchanged.
7. `revit_preview_delete_view_filters` on a used filter lists its views and templates. `revit_delete_view_filters` returns
   `approval_required` even with Direct Edit on, and a `DESTRUCTIVE` summary. After manual approval it is gone everywhere; Undo
   restores it.
8. `revit_preview_copy_view_filters` to the second project with a project parameter it lacks: `unresolved` names it and the filter
   stays out. `revit_copy_view_filters`: the others arrive with their settings, the source is unchanged, a selection filter
   arrives empty with `membersNotCopied`.
9. Undo once after each write: everything of that call is reverted.
| Elements | `revit_list_filled_regions` | RO | "List the filled regions in this view" | Type, loops, `areaM2`, parameters | N/A | N/A | Loops in the format the create tool takes |
| Elements | `revit_list_filled_region_types` | RO | "What filled region types are there?" | Patterns, colours, `instanceCount` | N/A | N/A | Also patterns and line styles |
| Elements | `revit_preview_create_filled_regions` | RO | "Preview a region per room, type by department" | `regions[]`, `skippedSources`, `unmappedValues` | N/A | N/A | Rolled-back trial by default |
| Elements | `revit_create_filled_regions` | Approval | "Draw them" | `newRegionIds`, per region status | Yes | One Undo | Specs and rules share one path |
| Elements | `revit_preview_edit_filled_regions` | RO | "Preview making this region bigger" | `keepsId`, `reshape.attachedAnnotations` | N/A | N/A | Lists what may be lost |
| Elements | `revit_edit_filled_regions` | Approval | "Change its type" / "Reshape it" | Id kept / `idMap` | Yes | One Undo | A reshape replaces the element; plans refuse it |
| Elements | `revit_preview_set_filled_region_types` | RO | "Preview making this type red" | `changes`, `affectedRegions.byView` | N/A | N/A | Every region of the type |
| Elements | `revit_set_filled_region_types` | Approval | "Make a green copy of this type" | `newTypeIds`, changes | Yes | One Undo | Type edit changes all its regions |

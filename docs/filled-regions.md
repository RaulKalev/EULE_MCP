# Filled regions

The connector draws, reads, changes and types filled regions (#135): from coordinates with holes, rectangles, room
boundaries (host or linked), detail lines, or from rules such as "a region in each room on this level, with the type
chosen by the room's department". Moving, copying and deleting regions stays with the existing tools
(`docs/move-elements.md`, `docs/copy-elements.md`, `revit_delete`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_filled_regions` | read-only | Regions in a view, of a type or by id: type, boundary, Revit's Area value, parameters |
| `revit_list_filled_region_types` | read-only | Types: patterns, colours, line weight, masking, instance count; the fill patterns and valid line styles |
| `revit_preview_create_filled_regions` | read-only | Every region a call would make, skipped sources and status codes; a rolled-back trial by default |
| `revit_create_filled_regions` | approval | Creates them: one transaction, one Undo, a sub-transaction per region |
| `revit_preview_edit_filled_regions` | read-only | The changes, whether the id is kept, the tags and dimensions a reshape puts at risk |
| `revit_edit_filled_regions` | approval | Changes type, line style or parameters (id kept) or the boundary (replaces the element) |
| `revit_preview_set_filled_region_types` | read-only | From/to per field and every region of the type, by view |
| `revit_set_filled_region_types` | approval | Creates types by duplicating, edits patterns, colours, line weight, masking, name |

The tools are in the `tags` group. Each takes the standard `document` argument. A view, type or line style is named by
its **name** (exact, case-insensitive) or its **element id**. Filled regions inside family documents are out of scope.

## Workflow

1. `revit_list_filled_region_types` to pick types and line styles; `revit_list_views` for views.
2. `revit_preview_create_filled_regions` with specs and rules. Read `regions[].status` (`willCreate`, `invalid`,
   `skipped`), `code`, `reason`, `outlines`, `holes`, `areaM2`, `extentUv`, then `skippedSources` and `unmappedValues`.
3. `revit_create_filled_regions` with the same arguments (approval). It returns `newRegionIds` and per region `regionId`,
   `areaM2` (computed from the loops) and `revitAreaM2` (Revit's own Area value).

## Coordinates

Lengths are millimetres. A region is view-specific and lies in the view's drawing plane, so every shape is taken into
that plane first. The plane is the one through `View.Origin`, perpendicular to `View.ViewDirection`, and it has two axes:
`View.RightDirection` and `View.UpDirection`.

**Model coordinates** (`space: "model"`, the default): `{x, y, z}` in the host model's internal coordinates, the ones
the read tools return. A point is projected onto the drawing plane along the view direction.

- In a plan, a ceiling plan, a drafting view, a legend or a sheet, z is dropped and may be left out.
- In a section, an elevation or a detail view, x and y place the point along the section line and z is its height. z is
  required there: without it the result has `code: coordinate_z_required`.
- A set of points spread more than 1 mm along the view direction is still projected, with a warning. A loop seen edge-on,
  such as a horizontal rectangle in a section, has no area and gets `loop_degenerate`.

**View coordinates** (`space: "view"`): `{u, v}`. They are defined as the model point's dot product with the view's right
and up directions: `u = P · right`, `v = P · up`. They do not depend on the view's origin. In a plan that is not rotated
`u = x` and `v = y`; in a drafting view too; in a section `v` is the elevation and `u` runs along the section line, in the
direction of `right`. `revit_list_filled_regions` returns both forms for every point.

A rectangle runs along the view's own axes in both spaces, also in a rotated plan.

## Shapes

One region spec is `{view, type, lineStyle?, parameters? {name: value}, paddingMm?}` plus exactly one shape.

| Shape | Meaning |
|---|---|
| `loops` | `[[point, ...], ...]`: closed point lists. The first is the outline, the others are holes in it; a loop inside a hole is an island. A flat list of points is one loop. A loop has to end at its first point (`loop_open` otherwise), or pass `closeLoops: true`. A point may have `arcMid`: the segment to the next point is then an arc through it |
| `rectangle` | `{from, to}` (opposite corners) or `{center, widthMm, heightMm}` |
| `room` | `id`, `{id, linkInstanceId}`, or `{number, level, linkInstanceId}` for a room or space; `boundaryLocation`: `finish` (default), `center`, `coreCenter`, `coreBoundary`. A room's inner boundaries (around a column, for example) become holes; arcs stay arcs |
| `detailLines` | `[ids]` of detail or model lines, in any order and direction, chained into closed loops. Ends up to 0.5 mm apart are joined. Separate loops become separate outlines or holes by how they nest |
| `element` | `id` or `{id, linkInstanceId}`: the element's bounding box as seen in the view |

`paddingMm` moves the whole boundary outward (negative: inward) with mitred corners: outlines grow and holes shrink. A
hole that closes is left out with a warning; an outline that collapses gets `padding_collapsed`. Padding flattens arcs
into short straight lines, with a warning.

### Rooms in links, and views where a room cannot be drawn

A linked room is read in its link document and moved into host coordinates with the link instance's total transform
(`RoomSource`, as in `docs/room-devices.md`), so the region lines up with the room in the host view, also when the link is
moved or rotated. An IFC link without rooms falls back to the footprint of its IfcSpaces (Revit 2026 only).

A room boundary and a detail line have a position of their own and are never projected: they have to lie in a plane
parallel to the view, within 1 mm. A room boundary is horizontal, so it fits plans, ceiling plans and drafting views. In a
section or elevation the result is `code: boundary_not_in_view_plane`. Draw a section-shaped region there with `loops` or a
`rectangle` instead. A room more than 10 mm from the level of the plan it is drawn in gets a warning, not an error.

## Rules

A rule picks sources, a shape and a type, and expands into one ordinary region spec per source. The specs then go through
the same validation and creation code as direct specs.

```json
{
  "rules": [
    {
      "view": "EN 1. korrus",
      "source": { "kind": "rooms", "level": "1. korrus", "linkInstanceId": 556677,
                  "filters": [ { "parameterName": "Name", "operator": "notEquals", "value": "Koridor" } ] },
      "shape": { "kind": "roomBoundary", "paddingMm": -50 },
      "typeMap": { "parameter": "Department", "map": { "Müük": "Sinine", "Laohoone": "Punane" } }
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `view` | The view to draw in |
| `source.kind` | `rooms` or `elements` |
| `source.categories` | Category names (`elements`); the rule must narrow `elements` by categories, `elementIds` or `inView` |
| `source.filters` | `[{parameterName, operator, value, matchMode?, scope?}]`, as in `revit_find_elements_by_parameter`. Type and Family names work |
| `source.inView` | Only what Revit lists in this view (name or id), through the link when `linkInstanceId` is given. If a plan lists no rooms, the rooms on its level are taken, with a warning |
| `source.level` | `rooms`: the room's level, by the host level or the link's own level name |
| `source.linkInstanceId` | Read the sources from this link |
| `source.elementIds` | These sources, instead of a selection |
| `source.limit` | Stop after this many sources, with a warning (default and maximum 500) |
| `shape.kind` | `roomBoundary` (default for rooms) or `boundingBox` (default for elements) |
| `shape.paddingMm`, `shape.boundaryLocation` | As for direct specs |
| `type` | One type for every source, or |
| `typeMap` | `{parameter, map: {value: type}}`. Values are compared ignoring case and outer spaces |
| `lineStyle`, `parameters` | As for direct specs |

**A value with no mapped type is listed and skipped, never given a guessed type.** Sources left out appear in
`skippedSources` as `{rule, id, label, code, reason, value}`, with `value_not_mapped`, `value_empty` or
`parameter_not_found`, and `unmappedValues` gives each unmapped value and how many sources have it, so a map can be completed
in one go. A source whose shape fails (`room_not_bounded`, `no_bounding_box`, `loop_self_intersecting`,
`boundary_not_in_view_plane`, ...) is skipped the same way and the rest goes on. A problem with the rule itself (an unknown
view, a view that takes no regions) is one invalid entry in `regions`.

A rooms rule with no `level`, `inView` or `elementIds` draws the rooms of every level in that view, and warns.

## Views

Regions can be drawn in floor plans, ceiling plans, area and structural plans, sections, elevations, detail views, drafting
views, legends and sheets. Templates, schedules, 3D views, walkthroughs, renderings and reports get
`code: view_not_supported` in the preview. Anything further Revit refuses shows up as `revit_refused` in the preview's trial
run. `FilledRegion.IsRegionCreationEnabledInView` appears in the API documentation but is not public in either reference
assembly, so the static check is a list of view types.

## Changing a region

`revit_edit_filled_regions` takes `edits: [{id, type?, lineStyle?, parameters?, boundary?}]`. `boundary` holds one shape,
with its `paddingMm`, `space` and `closeLoops`.

- **Type, line style and parameters keep the element id**: `Element.ChangeTypeId`, `FilledRegion.SetLineStyleId`,
  `Parameter.Set`. A parameter value is written the way Revit shows it (a length in the project's units); a missing or
  read-only parameter gets `parameter_not_settable`.
- **A boundary replaces the element.**

**The finding on editing a boundary.** The public members of `FilledRegion` in the Revit 2024 and 2026 reference assemblies
are `Create`, `CreateMaskingRegion`, `GetBoundaries`, `SetLineStyleId`, `GetValidLineStyleIdsForFilledRegion`,
`IsValidLineStyleIdForFilledRegion`, `IsValidFilledRegionTypeId` and `IsMasking`. Nothing sets a boundary. The general
`SketchEditScope` (2022 and later) exists, but whether a filled region's sketch can be edited with it is only known at
runtime, and it cannot start inside a transaction, so it would not fit one Undo per call. The tool does not use it. The edit
preview reports Revit's own answer per region in `reshape.sketchEditingSupported`, for reference.

A reshape:

- creates a new region with the same type (or the new `type`), in the same view, and copies every writable instance
  parameter;
- keeps the boundary's line style when it is one style, the pin state and the per-element graphic overrides in the view;
- deletes the old region, and returns `idMap: [{oldId, newId}]`;
- lists `reshape.attachedAnnotations` (tags and dimensions found in the region's view) in the preview, with a warning, and
  `reshape.lostAnnotations` after the write: what Revit deleted with the region;
- puts the new region on top of the view's draw order.

A region in a group gets `region_in_group`; one checked out by someone else gets `region_owned_by_other`.

**Plans.** A plan never covers a delete (`docs/plans.md`), so a reshape inside a plan is rolled back with
`plan_envelope_violated`. Run it with its own approval. Changing a type or parameter is fine inside a plan. In a plan a
room's or element's `id` counts as a target like any other `id`; add it to the envelope or approve the call.

## Types

`revit_set_filled_region_types` takes `types: [spec]`. `{type, ...}` edits a type (`name` renames it). `{duplicateFrom, name, ...}`
creates one with `Element.Duplicate`. A field left out stays as it is.

| Field | Meaning |
|---|---|
| `foregroundPattern`, `backgroundPattern` | Pattern name or id, `"solid"`, or `"none"`. A name prefers the drafting pattern; a background must be a drafting pattern (`pattern_not_valid`) |
| `foregroundColor`, `backgroundColor` | `"#RRGGBB"`, `"r,g,b"` or `{r, g, b}` |
| `lineWeight` | 1 to 16 |
| `isMasking` | Whether the region hides what lies behind it |

A type edit changes all its regions. The preview lists them in `affectedRegions: {count, byView: [{viewId, viewName, regionIds}]}`
and `affectedRegionTotal`, and the approval prompt gives the count.

## Common arguments

- `atomic` (default false): `false` gives each region its own sub-transaction, so a refusal costs only that region and
  invalid specs are left out. `true` rejects the whole request on an invalid spec and rolls everything back on a refusal.
- `dryRun` (previews, default true): run the write in a transaction that is rolled back, so Revit's own refusals show.
- `includeLoops` (create and its preview, default false): return each region's boundary points.
- At most 500 regions per call after rule expansion (`too_many_regions`), 500 edits, 100 type specs.

## Status codes

Per item (`code`), and as the result's `status` when nothing can be done:

| Code | Meaning |
|---|---|
| `invalid_spec` | The spec itself is wrong (missing field, bad value) |
| `view_not_found`, `type_not_found`, `line_style_not_found`, `link_not_found`, `category_not_found` | A reference does not resolve |
| `view_not_supported` | The view takes no filled regions |
| `loop_empty` | No loop or line was given |
| `loop_open` | A loop does not close (the reason gives the gap) |
| `loop_branching` | More than two lines meet at a point, so the boundary is ambiguous |
| `loop_too_few_points` | Fewer than 3 distinct points |
| `loop_segment_too_short` | A segment is under Revit's short curve tolerance |
| `loop_self_intersecting` | A loop crosses, touches or folds back on itself |
| `loop_degenerate` | A loop has no area in the view's plane |
| `loops_intersect` | Two loops cross or touch |
| `hole_outside_outer` | `loops`: a loop after the first is not inside the first |
| `padding_collapsed` | The padding collapses the outline |
| `coordinate_z_required` | A model point without z, in a view that is not plan-like |
| `boundary_not_in_view_plane` | A room or line does not lie in a plane parallel to the view |
| `room_not_found`, `room_not_bounded`, `room_ambiguous`, `no_room_boundary` | A room cannot be read |
| `line_not_found`, `element_not_found`, `no_bounding_box` | A line or element cannot be read |
| `value_not_mapped`, `value_empty`, `parameter_not_found` | A rule source without a type: skipped |
| `region_not_found`, `not_a_filled_region`, `region_in_group`, `region_owned_by_other` | An edit's target |
| `parameter_not_settable` | A parameter is missing, read-only or refuses the value |
| `type_name_exists`, `pattern_not_found`, `pattern_not_valid`, `line_weight_not_valid`, `masking_not_valid` | A type spec |
| `too_many_regions` | The request expands past the limit |
| `revit_refused` | Revit refused in the (trial) run; `reason` holds its message |

## Code

Pure logic, with no Revit API and unit tested, is in `RevitMCP.Addin/FilledRegions`: `LoopValidator`, `LoopChainer`,
`PolygonOffset`, `ViewFrame`, `RegionShapeBuilder`, `RuleExpander`, `TypeMap`, the spec parsers. The tools are in
`RevitMCP.Addin/Tools/FilledRegions`. `RegionPlanner` turns rules into specs, and `RegionShapeReader` reads rooms, lines and
boxes from the model for specs, rules and reshapes alike.

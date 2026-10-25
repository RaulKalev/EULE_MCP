# Geometry Queries

Spatial questions about the live model, answered by Revit rather than estimated from level and offset
parameters: where exactly is an element and which way does it face, what is inside a box or within a
distance, which wall or ceiling is nearest, is a line between two points clear, and how much room is
there around a device. All six tools are read-only and run without approval.

| Tool | Answers |
|---|---|
| `revit_get_element_geometry` | Location point/curve, bounding box, facing and hand orientation, rotation, host, level, room, owner view, pinned, group — for up to 100 ids |
| `revit_find_elements_in_box` | Elements whose bounding box intersects (or lies inside) a box, a padded element box or a room's box |
| `revit_find_elements_near` | Elements within a radius of a point or an element, nearest first |
| `revit_find_nearest_surface` | Nearest wall / ceiling / floor / roof / any face from a point, per direction and per surface kind |
| `revit_check_line_of_sight` | Whether the straight line between two points is blocked, by what and where |
| `revit_measure_clearance` | Free distance around an element or a point per direction, and what blocks each |

They are in the `query` tool group: find them with `revit_tools_search` (or start the bridge with
`--tool-profile full`) and call them with `revit_tools_call`.

## Units and coordinates

- Every coordinate and distance is in **millimetres, host model internal coordinates**, rounded to
  0.1 mm — the same frame as [`room-devices.md`](room-devices.md) and the move, copy and placement tools.
  Directions and normals are unit vectors rounded to 4 decimals.
- Elements inside a link carry `linkInstanceId`; their `elementId` is the id **in the link document**.
  Their positions, boxes and directions are transformed to host coordinates with
  `RevitLinkInstance.GetTotalTransform()`.
- Shared payload shapes:
  - point `{x, y, z}`
  - box `{min: {x,y,z}, max: {x,y,z}, sizeMm: {x,y,z}, centerMm: {x,y,z}}`
  - direction `{x, y, z}` (unit)
  - hit `{elementId, category, name, documentTitle, linkInstanceId?, pointMm, normal?, distanceMm, surfaceKind?, normalSource?}` —
    `normal` points back towards the ray origin; `normalSource` is `geometry` (read from the face), `measured`
    (three rays) or `ray` (inferred from the ray direction).
- Arguments take points as `{x, y, z}` or `[x, y, z]`, boxes as `{min, max}` or `{minX … maxZ}`; corners may
  come in any order.

Bounding boxes are axis-aligned in host coordinates. For a rotated element (or a rotated link) the box is the
box around the rotated geometry, so it is larger than the element. Distances to elements in the box and radius
tools are distances to those boxes; solid-to-solid distances are out of scope.

## Scope and caps

Every tool refuses a call with no scope with status **`geometry_query_too_broad`** (the shape of
`QueryGuard.ValidateGeometryQuery`: `data.error = GeometryQueryTooBroad`, `message`, `suggestedActions`,
`limits`):

| Tool | Needs | Caps |
|---|---|---|
| `revit_get_element_geometry` | `elementIds` | 100 ids per call (`QueryLimits.MaxGeometryElements`) |
| `revit_find_elements_in_box` | `box`, `aroundElementId` or `roomId` | `maxResults` default 200, max 2000; above 200 `categories[]` is required |
| `revit_find_elements_near` | `point` or `elementId`, `radiusMm` ≤ 50 000 | as the box tool |
| ray tools | an origin (and an end for line of sight) | 32 directions per call, distances ≤ 50 000 mm |

The collectors also stop after 20 000 scanned candidates with a warning.

## Element geometry

`revit_get_element_geometry elementIds=[…]` returns per element:

- `location`: `{kind: "point", pointMm, rotationDeg}` or `{kind: "line" | "curve", startMm, endMm, lengthMm}`
  or `{kind: "none"}`
- `bbox` and `bboxSource` (`model`, or `ownerView` for view-specific elements, read through the owner view
  as the move tools do)
- `facing`, `hand`, `mirrored`, `facingFlipped`, `handFlipped` for family instances
- `host {id, category, name, linkInstanceId?}` — a family hosted on a face of a linked ceiling reports the
  linked ceiling, not the link instance
- `level {id, name, source}` — `source` is `LevelId` or the level parameter it came from
  (`FAMILY_LEVEL_PARAM`, `RBS_START_LEVEL_PARAM`, `SCHEDULE_LEVEL_PARAM`, `LEVEL_PARAM`)
- `room {id, number, name, method}` in the last phase: `FamilyInstance.Room`, else the room at the element's
  point (`GetRoomAtPoint`)
- `ownerViewId`, `isPinned`, `groupId`
- with `includeSolidsSummary=true`: `solids {volumeMm3, faceCount}` of the element's first solid

`linkInstanceId=<link>` reads ids inside that link (status `link_not_found` when it is not a loaded link).

## Boxes and radius

`revit_find_elements_in_box` takes the box from `box`, from `aroundElementId` grown by `paddingMm`
(negative shrinks it), or from `roomId` — the room's bounding box; a room in the architecture link needs
`roomLinkInstanceId`. `mode=intersects` (default) lists everything whose box touches the query box,
`mode=inside` only what lies wholly inside it. `levels[]` keeps elements on the named levels.

`includeLinks=true` (default false) searches every loaded link too, or only those whose name contains one of
`linkNameFilters[]`. The box is transformed into each link's coordinates for Revit's
`BoundingBoxIntersectsFilter`, and every element found is re-checked in host coordinates.

Rows are sorted by `distanceToBoxCenterMm`. Types, view-specific elements, link instances and the reference
element or room itself are never listed.

`revit_find_elements_near` takes `point` or `elementId` (its bounding box centre) and `radiusMm`. It runs the
box search on the cube of side 2·r around the point, then keeps elements whose bounding box comes within r:
`distanceMm` is to the nearest point of the box (0 when the point is inside it), `centerDistanceMm` to its centre.

## Ray casts

The three ray tools cast through Revit's `ReferenceIntersector` (faces) in a 3D view — the same code the
alignment and face placement tools use (`Geometry/RayCaster.cs`). Consequences:

- **The view.** `searchViewId`, else the active 3D view, else the first non-template 3D view. The result's
  `view {id, name, sectionBoxActive}` says which one was used. Only what is visible in it can be hit: hidden
  categories, hidden links and a section box hide geometry from the rays, and a section box is warned about.
  Without any 3D view the status is **`no_3d_view_for_raycast`** — create a 3D view first.
- **Links.** `includeLinks` defaults to true; linked hits are resolved to the element inside the link.
- **Filtering.** Categories (`categories[]`, `blockingCategories[]`) and `ignoreElementIds[]` are applied after
  the hit, because Revit applies a ray filter to the link instance rather than to the linked elements.
  `ignoreElementIds` names host elements.
- **Directions** (`directions`): `horizontal` (a compass ring of `horizontalSamples` directions), `axes`
  (+x, −x, +y, −y, up, down), `up`, `down`, `all` (ring plus up and down), `local` (the element's front =
  facing, back, left, right, up, down), single labels such as `"+x,up"` or `"front"`, or vectors
  `[{x,y,z}]`, which are normalised. At most 32.

### Nearest surface

`revit_find_nearest_surface` casts from `point` or from an element's bounding box centre (the element itself is
ignored) in the requested directions, default 16 horizontal ones, up to `maxDistanceMm` (default 10 000).

Each hit is classified by its face normal (as seen from the origin) — a face pointing down is a ceiling, up a
floor, sideways a wall — so walls a linked IFC put on Generic Models are still walls. Anything in the Roofs
category is a roof; a sloped face (outside `angleToleranceDegrees`, default 30) takes the kind of its
category or stays `other`. `surfaces[]` picks the kinds to report (`wall`, `ceiling`, `floor`, `roof`, `any`).

For the best candidates of each kind the tool measures the **perpendicular** distance: along the face normal,
confirmed with one ray straight at the face. `best.<kind>.perpendicular=true` means `hit.distanceMm` is that
perpendicular distance; otherwise the foot of the perpendicular is not on the face (it ends before it) and the
distance along the search ray is reported. `rayDistanceMm` is always the distance along the ray that found it.
`hits[]` lists the nearest hit per direction.

### Line of sight

`revit_check_line_of_sight` casts one ray from `from` to `to` (or `fromElementId` / `toElementId`, both bounding
box centres; both elements are ignored). A hit more than 1 mm before the end on a blocking category blocks the
line. Default blockers: walls, floors, ceilings, roofs, doors, structural columns, structural framing, curtain
panels, generic models; `blockingCategories[]` replaces the list (category names or `OST_` names). The result
is `{clear, distanceMm, blockedBy, hits[]}` with the first 10 hits, each flagged `blocking`.

### Clearance

`revit_measure_clearance` measures from an element's bounding box: for each direction the ray starts where a
ray from the box centre leaves the box (the face centre for the axis directions), so `freeMm` is the gap
between the element and what it faces. The element, the elements that depend on it (hosted children,
sub-components) and `ignoreElementIds[]` are ignored; `categories[]` restricts what blocks. A direction with
nothing within `maxDistanceMm` (default 5000) has `freeMm: null` and `clearBeyondMm`. The result also gives
`minFreeMm` and `minDirection`. With `point` the rays start at the point.

Each ray starts 15 mm behind its start point, so a face lying exactly on the box is still met; something
overlapping the start point gives `freeMm: 0`.

## Examples

### Is this detector at least 500 mm from every wall?

```json
revit_find_nearest_surface
{ "elementId": 4512873, "surfaces": ["wall"], "directions": "horizontal", "horizontalSamples": 16,
  "maxDistanceMm": 3000 }
```

Read `data.best.wall.hit.distanceMm`. With `perpendicular: true` it is the perpendicular distance from the
detector's centre to the nearest wall face, in the host model or the architecture link; ≥ 500 passes. To see
every wall around it, read `data.hits[]` — one entry per direction with the wall hit in that direction (for a
detector in a corner, check the two smallest). `best.wall` is null when no wall is within `maxDistanceMm`.

### Which luminaires are within 3 m of panel X?

```json
revit_find_elements_near
{ "elementId": 3011245, "radiusMm": 3000, "categories": ["Lighting Fixtures"], "includeLinks": true,
  "linkNameFilters": ["EL"] }
```

`data.elements[]` is sorted by `distanceMm` (bounding box to the panel's bounding box centre). Rows from a link
carry `linkInstanceId`; read their values with `revit_query_linked_elements`. For the luminaires in the
panel's room instead, use `revit_find_elements_in_box` with `roomId` (and `roomLinkInstanceId` when the
rooms are in the architecture link).

### Can camera C see door D?

```json
revit_check_line_of_sight
{ "fromElementId": 5120001, "toElementId": 3398720 }
```

`data.clear` answers it. When it is false, `data.blockedBy` names the wall (or other blocker) with its
`linkInstanceId` and the point where the line meets it, `distanceMm` from the camera. A door seen through its
own doorway is clear: the door itself is ignored as the target, and the wall has an opening there. To check
several points of the door, call it with `to` points instead of `toElementId` (pass the door in
`ignoreElementIds`).

### More

- Clearance in front of a panel (the 1000 mm rule): `revit_measure_clearance elementId=<panel>
  directions="front"` and compare `minFreeMm`.
- Elements in a room's box, any category, at most 200: `revit_find_elements_in_box roomId=<room>
  roomLinkInstanceId=<AR link> includeLinks=true`.

## Payload changes in other tools (one release of aliases)

The shared DTOs replace anonymous payloads elsewhere:

- `revit_inspect_selected_elements` adds `bbox {min, max, sizeMm, centerMm}`. The old
  `boundingBoxMm {min, max, size, center}` stays next to it while `legacy=true`, the default for one release.
  Locations keep their field names (`pointMm`, `curveStartMm`, `curveEndMm`, `lengthMm`); values are now
  rounded to 0.1 mm.
- `revit_get_linked_elements_in_room`: `location {x, y, z}` is unchanged; `bbox` becomes
  `{min, max, sizeMm, centerMm}` and keeps the flat `minX … maxZ` while `legacy=true` (default).
- The device audit tools' `point {x, y, z}` is the shared point DTO (same fields).

`legacy=false` returns only the new fields (the add-in reads it today, e.g. from a job step; the two bridge
methods expose it once they gain the parameter). The aliases will be removed in the release after next.

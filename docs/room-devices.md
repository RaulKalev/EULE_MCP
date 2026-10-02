# Room-based device placement

Tools for filling an EN model with devices room by room — smoke detectors, data outlets,
Wi-Fi APs, cameras, access-control readers, clocks — against the rooms, walls, doors and
ceilings of a linked architecture model (issue #52).

Every device is placed **unhosted** (level-based or work-plane families): the tools compute
the point, rotation and height. All coordinates are **host model internal coordinates in mm**
(the same frame as `revit_move_elements`); angles are degrees.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_levels` | Read-only | Host levels and, with `linkInstanceId`, the link's levels mapped to host levels |
| `revit_get_room_geometry` | Read-only | Room boundary, centroid, interior point, area, height, ceiling height, doors, windows |
| `revit_get_room_walls` | Read-only | One room's wall faces seen from inside: `index`, `wallId`, thickness, inner face, normal, openings |
| `revit_get_device_codes` | Read-only | The device code map and its status in the model |
| `revit_set_device_codes` | Requires approval | Write/merge device codes and placement settings into the project config |
| `revit_preview_ensure_device_types` / `revit_ensure_device_types` | Read-only / approval | Create missing family types by duplicating `sourceType` |
| `revit_preview_place_at_wall` / `revit_place_at_wall` | Read-only / approval | Wall devices by wall + position |
| `revit_preview_place_in_room` / `revit_place_in_room` | Read-only / approval | Devices inside rooms: `center`, `grid`, `nearDoor`, `points` |
| `revit_export_view_image` | Read-only | PNG of a view (cropped to a room, devices highlighted) returned to the agent |

## Room sources

`source=link` + `linkInstanceId` reads the AR link (the only loaded link is used when the id is
omitted); `source=host` reads host rooms (e.g. after `convert_ifc_spaces_to_rooms`).

- **Room elements** (host or Revit link): `Room.GetBoundarySegments` with
  `SpatialElementBoundaryLocation.Finish`, so boundary edges are already the finished wall faces.
  Each face keeps its bounding element (`wallId`); room separation lines report
  `isSeparationLine=true`.
- **IFC links without rooms**: IfcSpace footprints are read with the same extraction as the
  IFC-space-to-room tools. Wall faces are matched to the wall whose bounds contain a point 100 mm
  behind the face. This fallback needs Revit 2026 (the IFC space tools are not built for 2024).
- Link coordinates go through `RevitLinkInstance.GetTotalTransform()`.
- Rooms are mapped to the host level at or just below their floor elevation.

Outer boundaries are normalized clockwise and holes counter-clockwise, so the room interior is
on the right of every edge. "Is the point in the room" checks use this polygon (the same test
for Room and IfcSpace sources).

### Doors and windows

Doors belong to a room through Revit's From/To Room, or — for IFC doors — when their centre is
within 400 mm of the boundary. Per door:

- `wallIndex`, `alongFromMm` / `alongToMm`: the door's stretch on the room face.
- `facingIntoRoom`: whether `FacingOrientation` points into this room.
- `swingIntoRoom`, `hingeSide`, `lockSide`, `sideSource`.

Revit does not document which way `HandOrientation` points, so the door sides follow two
settings in `devicePlacement` (see below). **Verify them once** on a known door with
`revit_export_view_image`, and flip the setting if lock sides come out mirrored. IFC doors
without hand data use a heuristic: the hinge is next to the nearer corner, so the lock is on the
side with more wall (`sideSource=cornerHeuristic`).

### Ceiling height

`ceilingHeightMm` is the lowest Ceilings-category element of the source model above the room's
interior point, measured from the room floor; `null` when there is none. Ceiling devices then
fall back to the room height with a warning.

## Device code map

Stored in the project config (`<projectRoot>/.rktools/mcp.project.config.json`). `projectRoot`
is optional when a `.rktools` folder exists above the model file. The placement tools also
accept the map inline as `deviceCodes`, so codes can be tried before they are saved.

```json
{
  "deviceCodes": {
    "ATS_SA":  { "family": "Suitsuandur", "type": "Optiline", "mount": "ceiling", "offsetFromCeilingMm": 0,
                 "maxSpacingMm": 10600, "maxDistFromWallMm": 7500,
                 "avoidCategories": ["Lighting Fixtures", "Air Terminals"], "clearanceMm": 500 },
    "ANDM_2x": { "family": "Andmesidepesa", "type": "2xRJ45", "mount": "wall", "heightMm": 300 },
    "LPS_LUG": { "family": "LPS lugeja", "type": "Standard", "mount": "wall", "heightMm": 1100,
                 "doorSide": "lock", "doorOffsetMm": 150,
                 "sourceType": "LPS lugeja : Default", "typeParameters": { "Type Comments": "LPS_LUG" } }
  },
  "devicePlacement": {
    "roomParameter": "Ruum",
    "handOrientationPointsTo": "latch",
    "swingTowardFacing": false
  }
}
```

| Field | Meaning |
|---|---|
| `family`, `type` | Family type to place (exact names) |
| `mount` | `wall` (needs `heightMm`), `ceiling` or `floor` |
| `heightMm` | Insertion height above the level (wall/floor) |
| `offsetFromWallMm` | Distance from the finished wall face into the room |
| `offsetFromCeilingMm` | Distance below the ceiling (ceiling devices) |
| `doorSide`, `doorOffsetMm` | Default side (`lock`/`hinge`) and clearance for door-relative placement |
| `rotationOffsetDeg` | Added to the computed facing when the family's front is not its facing direction |
| `avoidCategories`, `clearanceMm` | Linked categories a device must not sit over (preview warning) |
| `maxSpacingMm`, `maxDistFromWallMm` | Grid defaults |
| `sourceType` | Type to duplicate when `type` is missing — `"Type"` or `"Family : Type"`, same family |
| `typeParameters` | Type parameters `revit_ensure_device_types` writes on created types |

`devicePlacement.roomParameter` names the instance parameter that receives the room number on
placement (`roomParameter` argument overrides it).

## Placement

### At walls

```json
{ "placements": [
  { "deviceCode": "ANDM_2x", "roomNumber": "1.12", "wallIndex": 2, "alongFraction": 0.5, "count": 2, "spacingMm": 600 },
  { "deviceCode": "LPS_LUG", "roomNumber": "1.12", "nearDoorId": 345678, "doorSide": "lock" }
], "linkInstanceId": 123456 }
```

- Wall: `wallIndex` (from `revit_get_room_walls`), `wallId`, `nearestToPoint {x, y}` or `nearDoorId`.
- Position: `alongMm` / `alongFraction` from the face start, or `nearDoorId` + `doorSide` + `offsetMm`.
- Point = inner face point + `normalIntoRoom × offsetFromWallMm`; the device is rotated so its
  `FacingOrientation` points into the room (+ `rotationOffsetDeg`); z = level + `heightMm`.
- Preview warnings: inside a door/window opening (±100 mm), behind the open leaf of a door that
  swings into the room, within 200 mm of a corner, outside the room, and a device of the same type
  within 200 mm (existing or planned).

### Inside rooms

- `center` — the centroid, or for L/U-shaped rooms the interior point farthest from the walls.
- `grid` — the largest regular grid with spacing ≤ `maxSpacingMm` and outer points ≤
  `maxDistFromWallMm` from the walls. Cells whose centre falls outside the room get the interior
  point of that cell closest to its centre. Coverage is verified on a 500 mm sample grid against
  `coverageRadiusMm` (default: half the cell diagonal), and uncovered m² are reported per room.
- `nearDoor` — beside every door of the room, `side` = `inside` | `outside` (outside uses the wall
  thickness, 200 mm when unknown).
- `points` — `[{u, v}]` in 0…1 of the room's bounding box.

Ceiling devices: z = floor + ceiling height − `offsetFromCeilingMm`. The preview warns when a
device is within `clearanceMm` of an `avoidCategories` element.

### Apply

The apply tools run the same planner as the preview. Everything goes in one transaction (one
Undo), with each device in its own sub-transaction, so a failing device does not stop the rest.
`skipDevicesWithWarnings=true` creates only clean devices. After creation, the height is fixed
through "Elevation from Level" / "Offset from Host" (or a vertical move), the device is rotated,
and the room number is written when `roomParameter` is set.

## Visual check

`revit_export_view_image` exports a plan view to PNG — `cropToRoom` (room number) or `bboxMm`,
`highlightElementIds` in red — and the MCP bridge returns the image itself. The crop and the
overrides are made inside a `TransactionGroup` that is rolled back after the export, so the
model is unchanged.

## Not yet implemented (issue #52 P2/P3)

`revit_get_linked_elements_in_room`, `revit_rotate_elements` / `revit_set_elevation`,
`revit_assign_room_to_elements`, `revit_check_devices_per_room`,
`revit_check_wall_devices_alignment` and `revit_check_coverage` are planned separately. The
avoid-category check of `place_in_room` already uses the same in-room element lookup as 1.4.

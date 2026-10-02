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
| `revit_get_linked_elements_in_room` | Read-only | Elements of given categories (lights, air terminals, ceilings, furniture) standing in a room, from any link |
| `revit_preview_rotate_elements` / `revit_rotate_elements` | Read-only / approval | Rotate elements about their own point: absolute angle, relative, or face a point |
| `revit_preview_set_elevation` / `revit_set_elevation` | Read-only / approval | Set elevation above level, with expected-value staleness check |
| `revit_preview_assign_room_to_elements` / `revit_assign_room_to_elements` | Read-only / approval | Write room numbers into existing devices |
| `revit_check_devices_per_room` | Read-only | Count rules per room, devices outside rooms, height deviations |
| `revit_check_device_alignment` | Read-only | Wall / ceiling / floor devices off their mount after a model update, with suggested moves |
| `revit_check_coverage` | Read-only | Smoke detector / AP radius coverage and camera sector coverage per room |
| `revit_check_fire_alarm` | Read-only | Fire alarm rules per room: Table 1 heights, detection gaps, corridors, sounder levels, single tone |

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
                 "detectorType": "pointSmoke",
                 "avoidCategories": ["Lighting Fixtures", "Air Terminals"], "clearanceMm": 500 },
    "ATS_SIR": { "family": "Sireen", "type": "Sein", "mount": "wall", "heightMm": 2400,
                 "detectorType": "sounder", "soundLevelDb": 97, "tone": "EN 54-3 slow whoop" },
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
| `coverageRadiusMm` | Coverage radius for `revit_check_coverage` (detectors, APs; default 7500) |
| `fovDeg`, `rangeM` | Camera field of view and range for `revit_check_coverage` |
| `detectorType` | Fire alarm device: `pointSmoke` (EN 54-7), `linearSmoke` (54-12), `aspirating` (ASD), `pointHeat` (54-5), `linearHeat` (54-22), `flame` (54-10), `co`, `sounder` |
| `detectorClass` | Detector class for the class-limited cells of Table 1 (`A1`, `A2`, `B`, `C`, …) |
| `soundLevelDb`, `tone` | Sounders: rated dB(A) at 1 m (required) and the alarm signal id |
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

## Linked elements in a room

`revit_get_linked_elements_in_room` lists elements of the given `categories` that stand in a room:
their point (or plan centre) is inside the footprint and their height range overlaps the room's
storey (floor − 100 mm … top + 1.5 m, so items above a suspended ceiling count but other floors do
not). Rooms come from the room source (`source` / `linkInstanceId`); elements from
`elementLinkInstanceIds` (e.g. the EK and KVJ links) and/or `includeHost`, defaulting to the room
source model. Each element has its id, model, category, type, location and bounding box in host mm.
The `avoidCategories` check of `place_in_room` uses the same lookup.

## Adjusting placed devices

- `revit_rotate_elements` turns elements about their own insertion point: `angleDeg` (absolute
  facing, counter-clockwise from +X), `rotateByDeg`, or `faceToward {x, y}`. Per element
  (`rotations=[{elementId, …, expectedAngleDeg?}]`) or shared (`elementIds` + one target).
- `revit_set_elevation` sets the elevation above the element's level
  (`elevations=[{elementId, elevationFromLevelMm, expectedElevationFromLevelMm?}]` or shared).
- Both work like `revit_move_elements`: the optional expected value is a staleness check
  (`angleToleranceDeg` 0.5°, `toleranceMm` 1 mm), pinned elements are skipped by default, and
  `atomic=true` (default) undoes the whole batch when any element fails.
- `revit_assign_room_to_elements` writes the room number into `roomParameter` (default
  `devicePlacement.roomParameter`) for `elementIds` or all devices of the code map (`codes[]`
  limits). The room is the one whose footprint contains the insertion point on the device's
  storey (linked Room or IfcSpace). `onlyEmpty=true` keeps existing values.

## Checks

All checks read the devices of the device code map (`codes[]` limits them) and locate each one in
a room of the room source.

### Devices per room

```json
{ "rules": [
  { "code": "ANDM_2x", "roomFilter": "klass", "min": 2 },
  { "code": "ANDM_4x", "roomFilter": "klass", "min": 1 },
  { "code": "ATS_SA", "min": 1, "excludeRoomFilter": "^WC", "minAreaM2": 0 },
  { "code": "ATS_SA", "roomFilter": "^WC", "minAreaM2": 4, "min": 1 }
] }
```

A rule applies to rooms matching `roomNumbers`, `roomFilter` (name regex), not
`excludeRoomFilter`, and within `minAreaM2` / `maxAreaM2`. The result lists every room below `min`
or above `max`, each rule's number of rooms checked, devices outside every room, and devices whose
height is off their code by more than `heightToleranceMm` (default 50).

### Device alignment

Unhosted devices do not move with the linked walls and ceilings, so check them after every AR
update. `revit_check_device_alignment` compares each device with its mount:

| Mount | Checked |
|---|---|
| `wall` | Signed distance from the nearest wall face of its room vs `offsetFromWallMm` (negative = inside or behind the wall), and height above the level vs `heightMm` |
| `ceiling` | Gap below the linked ceiling vs `offsetFromCeilingMm` (skipped when the room has no linked ceiling) |
| `floor` | Height above the level vs `heightMm` |

Devices that are not inside any room are listed separately (for wall devices this often means
the wall moved over them). Deviations beyond `toleranceMm` (default 20) come with
`suggestedMoves` — `{elementId, targetXmm, targetYmm, targetZmm, expectedXmm, expectedYmm,
expectedZmm}` entries that can go straight to `revit_preview_move_elements` /
`revit_move_elements`. `revit_align_elements` is the alternative for snapping to surfaces.

### Coverage

`revit_check_coverage` samples each room on a `stepMm` grid (default 500 mm) and reports covered %,
uncovered m² and the largest uncovered regions (area + centre point).

- Radius devices (smoke detectors, APs): `radiusMm`, else the code's `coverageRadiusMm`, else the
  fire rule radius of its `detectorType` (smoke/CO/ASD 6.2 m, heat 4.5 m, × the `ceilingSlopeDeg`
  factor), else 7500 mm.
- Cameras (codes with `fovDeg`): a view sector of `fovDeg` around the camera's facing direction,
  `rangeM` deep (15 m when missing, with a warning).
- `scope`: `room` counts only devices in the same room (default for detectors and cameras — walls
  block them); `level` counts every device on the storey (default for codes with
  `coverageRadiusMm` and no `fovDeg`, e.g. APs).

Uncovered regions are reported as numbers. To see them, export the room with
`revit_export_view_image`.

## Fire alarm rules

Device codes with a `detectorType` follow the fire alarm design rules (Table 1, 6.5.2.2, 6.5.2.3 and
the acoustic alarm requirements). The rules live in `FireAlarmRules` (pure, unit tested).

### Table 1 — detector type vs room height

Room height is the linked ceiling height, else the room height.

| Room height | Point smoke | Linear smoke | ASD | Point heat | Linear heat | Flame |
|---|---|---|---|---|---|---|
| ≤ 6 m | suitable | suitable | suitable | suitable (B–G object protection only) | suitable | suitable (c) |
| ≤ 7.5 m | suitable | suitable | suitable | conditional — class A1 only | suitable | suitable (c) |
| ≤ 9 m | suitable | suitable | suitable | unsuitable | conditional — class A1 only | suitable (c) |
| ≤ 12 m | suitable | suitable | suitable | unsuitable | unsuitable | suitable (c) |
| ≤ 16 m | conditional | suitable | conditional — ≥ 5 class C holes | unsuitable | unsuitable | suitable (c) |
| ≤ 25 m | unsuitable | conditional (d) | conditional — ≥ 15 class C holes | unsuitable | unsuitable | suitable (c) |
| ≤ 45 m | unsuitable | conditional (d, f) | conditional — ≥ 15 class B holes | unsuitable | unsuitable | suitable (c) |

"Conditional" means suitability depends on use and environment (fast fire growth, smoke spread).
With `detectorClass` the class-limited cells become a firm answer (point heat A1/A1R/A1S up to
7.5 m, linear heat A1 up to 9 m, ASD class B up to 45 m). CO detectors use the point smoke column.

- `revit_place_in_room` checks Table 1 for every room: **unsuitable** rooms are blocked (unless
  `allowUnsuitableHeight=true`), **conditional** rooms get a warning with the condition.
- `revit_check_fire_alarm` reports the same per placed detector code.

### Point detector spacing (6.5.2.2 / 6.5.2.3)

| | Radius | Grid spacing | From wall | Corridor ≤ 2 m: spacing | Corridor: from end wall |
|---|---|---|---|---|---|
| Smoke, CO, ASD sampling hole | 6.2 m | 8.8 m | 4.4 m | 12.4 m | 6.2 m |
| Heat | 4.5 m | 6.4 m | 3.2 m | 9.0 m | 4.5 m |

- `revit_place_in_room strategy=grid` uses these values for detector codes: rooms whose
  minimum-area bounding box is at most 2 m wide are treated as corridors and get an evenly spaced
  row on the centreline (`corridorMode=off` disables this); other rooms get the largest regular grid
  within the spacing and wall distance. Explicit `maxSpacingMm` / `maxDistFromWallMm` still win, with
  a warning when they exceed the rule.
- Sloped ceilings: `ceilingSlopeDeg` widens every horizontal distance by 1 % per degree, at most
  25 %. The standard allows this for detectors at or near the ridge; the tools apply it to the whole
  room, so use it only where the detectors are at the ridge.
- Linear, flame and sounder devices have no point spacing; Table 1 still applies to linear and flame.

### Acoustic alarm

`revit_check_fire_alarm` estimates the alarm level at every sample point of a room from the
sounders' rated `soundLevelDb` at 1 m (free field, −6 dB per doubling of distance, energetic sum):

- At least **65 dB(A)**, or **10 dB(A) above** ambient noise lasting over 30 s (`ambientNoiseDb`
  globally, `roomNoise=[{roomFilter, ambientNoiseDb}]` per room) — whichever is higher.
- **75 dB(A)** in rooms matching `sleepingRoomFilter` (alarm meant to wake sleeping people). The
  standard asks for this at the bedhead; the check applies it to the whole room.
- Never above **118 dB(A)** where people are likely to be.
- One signal everywhere: all sounder codes must share the same `tone`.
- `soundScope=room` (default) counts only sounders in the room; `level` counts every sounder on the
  storey (walls are not modelled, so this is optimistic).

The estimate ignores walls, doors and room acoustics — verify on site with a class 2 (EN 61672-1)
meter, slow (1 s) response, A-weighting. The rule that the fire alarm tone may be used for other
purposes only when the expected reaction is the same is a design decision the tools do not check.

### Fire alarm check

`revit_check_fire_alarm` combines the above per room: Table 1 status of each detector code present,
rooms without any detector (`excludeRoomFilter` skips e.g. WCs), point detector coverage gaps
(radius per type, corridor spacing and end distance), sound level gaps (`tooQuiet`), places above
118 dB(A) (`tooLoud`), and a global `differentTones` finding.

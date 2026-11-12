# Smoke tests — Cable types, room devices and fire alarm rules

Part of the [smoke test checklist](../../TESTING.md).

## 53. Cable Types (issue #51)

Requires a Revit 2026 model with at least one cable type and one electrical circuit.
Automated: `RevitMCP.Config\Tests\Test-RoomDevices.ps1` covers 53.1 (previews) and, with
`-IncludeWrites`, 53.2.

### 53.1 Preview

**Prompt:**
```
Preview creating cable types "ATS ahelakaabel 2x2x0,8" and "ATS tulekindel 1x2x1,5 FE180" from "Default".
```

**Verify:**
1. `revit_preview_create_cable_type` reports `kind: CableType`, the source type and one proposal per name.
2. A new name is `create`, an existing one is `skipExisting` (with `existingTypeId`), and a name containing `:` is `blocked`.
3. The same name twice in one request: the second is `blocked`.
4. `parameters` show `willSet` / `notFound` / `readOnly` per parameter.
5. The undo stack is unchanged.

### 53.2 Create and assign

**Verify:**
1. `revit_create_cable_type` asks for approval; after approval the result lists each new `id`.
2. The new type appears in Revit's cable type list and in `revit_get_available_cable_types`.
3. `revit_change_circuit_cable_or_wire_type` with the new name assigns it to a circuit.
4. One Undo removes every type the call created.
5. `ifExists: "error"` with an existing name creates nothing for that name.
6. Revit 2024: a warning says WireType is duplicated instead.

## 54. Room Geometry and Device Placement (issue #52)

Requires an EN model with an AR link: one IFC link without rooms (Tarvastu, IfcSpaces) and one
Revit link with rooms (e.g. NJK AR), a room with a door and a window, and loaded device families.
Automated: `Test-RoomDevices.ps1 -LinkInstanceId <id> -RoomNumber <n> [-CorridorNumber <n>]` runs
54.1–54.14; repeat once per link type.

### 54.1 Levels and room geometry

**Verify:**
1. `revit_list_levels linkInstanceId=<AR>` maps every link level to a host level; offsets over 50 mm are warned.
2. `revit_get_room_geometry source=link` returns `kind: Room` for the Revit link and `kind: IfcSpace` for the IFC link (Revit 2026).
3. The outer boundary is clockwise, `interiorPoint` is inside even for an L-shaped room, `areaM2` matches the room's area.
4. `ceilingHeightMm` matches the linked ceiling (measure in a section); `null` in a room without a ceiling.
5. Doors list `wallIndex`, `alongFromMm`/`alongToMm` and `widthMm` matching the door; windows list `sillHeightMm`.

### 54.2 Door sides (convention check — do once per family set)

1. Pick a door whose latch side you know. Run `revit_get_room_geometry` and note `lockSide`.
2. Preview `revit_place_at_wall` with `nearDoorId` + `doorSide: lock`, and export the room with `revit_export_view_image highlightElementIds=[door]`.

**Verify:** the device is on the latch side. If it is on the hinge side, set
`devicePlacement.handOrientationPointsTo` to `hinge` with `revit_set_device_codes`. Likewise check
`swingIntoRoom` against the door swing and flip `swingTowardFacing` if needed.

### 54.3 Wall faces

**Verify:**
1. `revit_get_room_walls` returns one face per boundary edge; `normalIntoRoom` points into the room.
2. Revit link: `wallId`, `typeName`, `thicknessMm` match the wall; room separation lines show `isSeparationLine: true`.
3. IFC link: `wallId` is the IFC wall behind the face (DirectShape walls included).
4. Openings appear on the face that holds the door/window, with correct `fromMm`/`toMm`.
5. Curved walls appear as several faces with `isCurved: true`.

### 54.4 Device codes and types

**Verify:**
1. `revit_set_device_codes` (approval) writes `deviceCodes` into `.rktools/mcp.project.config.json` with a backup; an invalid entry (wall code without `heightMm`) writes nothing.
2. `revit_get_device_codes` shows `ok` / `missingType` / `missingSource` / `missingFamily` per code.
3. `revit_ensure_device_types` creates the missing type in the source family and writes `typeParameters`; Undo removes it.

### 54.5 Placement

**Verify:**
1. `revit_preview_place_at_wall` with `wallIndex` + `alongFraction: 0.5`: the device is at the face midpoint, `elevationFromLevelMm` = code `heightMm`, `facingDeg` = the face normal angle.
2. `count: 3, spacingMm: 600` gives three devices 600 mm apart.
3. Warnings appear for a point inside a door opening, behind an open door leaf, within 200 mm of a corner, and within 200 mm of an existing device of the same type.
4. After `revit_place_at_wall` (approval): the device stands on the wall face, faces into the room, has the right height, and the room parameter holds the room number. One Undo removes all devices.
5. `revit_preview_place_in_room`: `center` lies inside an L-shaped room; `grid` keeps every point inside and reports `uncoveredAreaM2`; `nearDoor side=outside` puts the device on the corridor side; `points` maps u/v to the room box.
6. Ceiling devices sit at the linked ceiling minus `offsetFromCeilingMm`; a room without a ceiling uses the room height with a warning.
7. `avoidCategories`: a device under a linked luminaire gets a warning; a luminaire on the floor above does not.

### 54.6 Image export

**Verify:**
1. `revit_export_view_image cropToRoom=<n> highlightElementIds=[...]` returns a PNG cropped to the room with the elements in red.
2. The view's crop and overrides are unchanged afterwards; the undo stack is unchanged.
3. In an MCP client the image is shown inline.

## 55. Adjusting and Auditing Devices (issue #52)

Requires placed devices of the code map (run 54.5 first). With `-IncludeWrites -RoomParameter <name>`,
`Test-RoomDevices.ps1` covers 55.2–55.3 on a device it places itself.

### 55.1 Linked elements in a room

**Verify:** `revit_get_linked_elements_in_room categories=[Lighting Fixtures, Air Terminals] elementLinkInstanceIds=[EK, KVJ]`
returns the luminaires and diffusers of that room only — not those of the floor above or the next room.

### 55.2 Rotate and elevation

**Verify:**
1. `revit_preview_rotate_elements` with `angleDeg`, `rotateByDeg` and `faceToward` reports the right target; the element turns about its own point.
2. `revit_preview_set_elevation elevationFromLevelMm=1200` reports `ready`; a wrong `expectedElevationFromLevelMm` reports `stale`; pinned elements are `blocked`.
3. Apply (approval), then Undo restores everything; with `atomic: true` a failing element undoes the batch.

### 55.3 Room numbers

**Verify:** `revit_preview_assign_room_to_elements roomParameter=<name>` finds the room each device stands in
(Room and IfcSpace sources), `onlyEmpty` keeps existing values, and the write fills the parameter.

### 55.4 Audits

**Verify:**
1. `revit_check_devices_per_room` with `{code: ANDM_2x, roomFilter: klass, min: 2}` lists every classroom with fewer than 2; a device moved outside all rooms is listed under `devicesOutsideRooms`.
2. `revit_check_device_alignment`: move a linked AR wall 100 mm (or reload a changed IFC) — wall devices behind it report a negative `wallOffset`; a ceiling device 150 mm under the ceiling reports `ceilingGap`; a floor device at the wrong height reports `height`. Passing `suggestedMoves` to `revit_preview_move_elements` puts them back.
3. `revit_check_coverage codes=[ATS_SA]` reports covered % and the uncovered region of a large room with too few detectors; cameras (`fovDeg`, `rangeM`) cover only their view sector.

## 56. Fire Alarm Rules

Requires device codes with `detectorType` (`pointSmoke`, `pointHeat`, `sounder` with `soundLevelDb` and `tone`),
a normal room, a corridor up to 2 m wide, and ideally a room over 6 m high.
Automated: `Test-RoomDevices.ps1` runs 56.1 (grid), 56.2 (with `-CorridorNumber`), the Table 1
check for the room's height, and `revit_check_fire_alarm`.

### 56.1 Spacing

**Verify:**
1. `revit_preview_place_in_room strategy=grid` for a `pointSmoke` code: room note `rule: 6.5.2.3`, steps ≤ 8.8 m, outer points ≤ 4.4 m from walls, `coverageRadiusMm` 6200. `pointHeat`: 6.5.2.2, 6.4 m / 3.2 m / 4500.
2. Corridor ≤ 2 m wide: `layout: corridor`, detectors on the centreline, ⌈length / 12.4 m⌉ smoke detectors (9.0 m for heat), first and last ≤ 6.2 m (4.5 m) from the end walls. `corridorMode: off` gives a grid instead.
3. `ceilingSlopeDeg: 20` enlarges the spacing by 20 %; 40 is capped at 25 %.
4. An explicit `maxSpacingMm` above the rule warns.

### 56.2 Table 1

**Verify:**
1. A `pointHeat` code with `detectorClass: A2` in a room 6–7.5 m high is blocked; `A1` is allowed. Over 7.5 m every point heat detector is blocked.
2. `pointSmoke` in a 12–16 m room warns "conditional"; over 16 m it is blocked; `allowUnsuitableHeight: true` places it with a warning.
3. `linearHeat` class `A2` in a 7.5–9 m room is blocked, `A1` allowed.
4. Cross-check the Table 1 cells read from the standard (see `docs/room-devices.md`) against the printed table.

### 56.3 Fire alarm check

**Verify:**
1. `revit_check_fire_alarm` lists `noDetection` for a room without detectors, and nothing for rooms matching `excludeRoomFilter`.
2. A large room with one smoke detector reports `detectionGap` with the uncovered region's centre.
3. Sounders: a room far from any sounder reports `tooQuiet`; `ambientNoiseDb: 60` raises the requirement to 70 dB(A); a `sleepingRoomFilter` room needs 75 dB(A); a 120 dB(A) sounder reports `tooLoud`.
4. Two sounder codes with different `tone` give a global `differentTones` finding.
5. Spot-check the estimated levels with a class 2 meter on site; the tool says the estimate ignores walls.

### Matrix rows (Section 30)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Electrical | `revit_preview_create_cable_type` | RO | "Preview cable type X from Default" | create / skipExisting / blocked per name | N/A | N/A | Revit 2024: WireType |
| Electrical | `revit_create_cable_type` | Approval | "Create cable type X from Default" | New type id returned | Yes | One Undo | |
| Elements | `revit_get_room_geometry` | RO | "Read room 1.12 from the AR link" | Boundary, doors, windows, ceiling height | N/A | N/A | IfcSpace on 2026 only |
| Elements | `revit_get_room_walls` | RO | "List the walls of room 1.12" | Faces with normals and openings | N/A | N/A | |
| Elements | `revit_place_at_wall` | Approval | "Place ANDM_2x on wall 2 of 1.12" | Device on the wall face, facing in | Yes | One Undo | Preview first |
| Elements | `revit_place_in_room` | Approval | "Place ATS_SA in 1.12 with grid" | Fire-rule grid / corridor row | Yes | One Undo | Table 1 can block |
| Elements | `revit_rotate_elements` / `revit_set_elevation` | Approval | "Turn device X 90°" | Rotated about its own point | Yes | One Undo | Staleness check |
| Parameters | `revit_assign_room_to_elements` | Approval | "Write room numbers to all devices" | Room parameter filled | Yes | One Undo | |
| Elements | `revit_check_device_alignment` | RO | "Check device alignment after the AR update" | Off-mount devices + suggestedMoves | N/A | N/A | Wall, ceiling, floor |
| Electrical | `revit_check_fire_alarm` | RO | "Check the fire alarm on level 1" | Table 1, gaps, sound levels, tone | N/A | N/A | Sound is an estimate |
| Views | `revit_export_view_image` | RO | "Show me room 1.12" | PNG returned inline | N/A | N/A | Changes rolled back |

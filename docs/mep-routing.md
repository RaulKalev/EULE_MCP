# MEP Routing: Cable Tray and Conduit Runs

Draw containment: cable tray and conduit runs along a polyline, with an elbow at every bend, joined
to existing trays or conduits at their ends, and summarised by type, size, level and workset with
every open end listed. The circuits, wires and cable types already have their own tools
(`revit_create_electrical_circuit`, `revit_draw_circuit_arcs`, `revit_create_cable_type`); these
tools draw what the cables run in.

Out of scope on purpose: routing around obstacles (clash-aware pathfinding), ducts and pipes, cable
fill, editing routing preferences or fitting assignments, and slope.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_list_mep_curve_types` | Read-only | Cable tray and conduit types, which fittings each can place, standard sizes |
| `revit_preview_create_mep_run` | Read-only | The normalized polyline, segments, bends, elbows to attempt, snap candidates, total length |
| `revit_create_mep_run` | Requires approval | Draw the run: segments, elbows, end connections, in one transaction |
| `revit_connect_mep_curves` | Requires approval | Join two open ends (tray–tray, conduit–conduit) with the right fitting or by extending |
| `revit_get_mep_run_summary` | Read-only | Lengths and counts per type, size, level and workset; fittings; open ends; disconnected segments |

All tools take `document` (title or path; empty = the active document), like the other model tools.

## Coordinates and units

- Every length is **millimetres**, in and out. Results are rounded to 0.1 mm; totals are also given
  in metres (rounded to 1 mm).
- Points are **model coordinates** (Revit's internal origin), the same frame the read tools
  (`revit_get_room_geometry`, `revit_get_elements_info`) report. A point is the curve's centreline:
  for a tray that is the middle of its height, for a conduit its axis.
- `z` may be left out of a point when `level` and `offsetMm` are given: that point gets
  `z = level elevation + offsetMm`. The level elevation is Revit's `ProjectElevation` (independent of the
  level type's Elevation Base). Points that carry their own `z` keep it, so a riser is drawn by giving its
  top and bottom points explicit heights.
- `level` is the reference level of every segment (name or id). Without it the run is hosted on the
  level whose elevation is nearest the first point's `z`.
- A run longer than 500 m warns: that is almost always metres or feet read as millimetres.

## Types, sizes and fittings

```json
{ "kind": "cableTray" }
```

`revit_list_mep_curve_types` returns per type:

```json
{
  "id": 401233, "name": "KL-300x60", "kind": "cableTray", "familyName": "Cable Tray with Fittings",
  "withFittings": true,
  "routingPreference": { "elbow": "available", "verticalElbow": "available", "tee": "available",
                         "cross": "missing", "transition": "available", "union": "available" },
  "fittingFamilies": { "elbow": "Cable Tray Horizontal Bend: Standard", "verticalElbow": "Cable Tray Vertical Inside Bend: Standard" },
  "missingElbows": false
}
```

**Where fittings come from.** Pipe and duct types choose fittings through routing preferences. Cable tray
and conduit types do not: Revit keeps their default bend, tee, cross, transition and union families as
type parameters (*Horizontal Bend*, *Vertical Inside/Outside Bend*, *Bend*, *Tee*, …; the API names them
`RBS_CURVETYPE_DEFAULT_*`). The tool reads both: a fitting kind is `available` when a type parameter
names a family **or** the type's `RoutingPreferenceManager` lists rules for it
(`GetNumberOfRules(RoutingPreferenceRuleGroupType.Elbows…) > 0`).

- `elbow` is the bend between two level segments (a tray's *Horizontal Bend*, a conduit's *Bend*).
- `verticalElbow` is the bend into or out of a riser (a tray's *Vertical Inside/Outside Bend*; a conduit's
  one *Bend* serves both).
- A type with a missing elbow is flagged (`missingElbows`): `NewElbowFitting` throws for it, so a run drawn
  with it gets its straight segments and `fitting_failed` at every bend. Assign the bend families in the
  type properties first, or join the bends afterwards with `revit_connect_mep_curves`.

**Sizes.**

- Conduits: the type's *Standard* (Electrical Settings → Conduit Settings → Size) lists its nominal
  diameters. `diameterMm` must be one of them; another value is refused with `size_not_standard` and the
  nearest standard sizes.
- Cable trays: width and height are **free values** in the API. The project's cable tray size table
  (`CableTraySizes`, one list used for width and height) is returned as `cableTraySizesMm`, the usual
  choices, not a limit.
- Without a size, Revit's default size for the type is used, and the result says so.

## Drawing a run

```json
{
  "kind": "cableTray",
  "type": "KL-300x60",
  "level": "1. korrus",
  "offsetMm": 2800,
  "widthMm": 300,
  "heightMm": 60,
  "points": [ { "x": 2000, "y": 5000 }, { "x": 26000, "y": 5000 }, { "x": 26000, "y": 14000 } ],
  "fittings": "auto",
  "connectStart": "auto",
  "connectEnd": "none"
}
```

| Argument | Meaning |
|---|---|
| `kind` | `cableTray` or `conduit` (required) |
| `type` | Type name or id (required). An exact name, a case-insensitive name, or a unique part of a name |
| `points` | JSON array of at least 2 points, `{x, y, z}` or `[x, y, z]`, mm, model coordinates; at most 500 |
| `level` | Reference level, name or id. Default: nearest the first point |
| `offsetMm` | Height above `level` for points without `z` |
| `widthMm`, `heightMm` | Tray size (both or neither) |
| `diameterMm` | Conduit size: a standard size of the type |
| `fittings` | `auto` (default): an elbow at every bend. `none`: the segments meet without fittings (their ends stay open) |
| `connectStart`, `connectEnd` | `auto`: join to the nearest **open** tray (or conduit) end within `snapMm`. An element id: join to that element's open end nearest the run end. Empty or `none`: leave open |
| `snapMm` | Search radius for `auto`, default 50 |
| `minSegmentMm` | Segments shorter than this are merged, default 100 (0 keeps everything) |
| `workset` | Workset name or id (workshared models); ignored with a warning otherwise |
| `atomic` | `true` (default): a segment Revit refuses undoes the whole run. `false`: the segments that succeed are kept |

### How a polyline becomes segments and bends

The planner (pure, unit tested) normalizes the polyline before anything is drawn, and the preview shows
every change it made (`changes`):

1. Repeated points (closer than 0.5 mm) are dropped: no zero-length segments.
2. Points where the run goes straight on are merged, including bends below 1°, which count as straight.
3. A segment shorter than `minSegmentMm` leaves no room for elbows and is merged: an inner one collapses to
   its midpoint; the first and the last keep the run end and lose their inner point. Steps 2 and 3 repeat
   until nothing changes (a collapsed jog can leave its neighbours in line).
4. A polyline that doubles back on itself is refused (`polyline_invalid`).

Each remaining segment becomes one `CableTray.Create` / `Conduit.Create` with the size set through
`RBS_CABLETRAY_WIDTH_PARAM` / `RBS_CABLETRAY_HEIGHT_PARAM` or `RBS_CONDUIT_DIAMETER_PARAM`. Each bend between
two segments becomes one `NewElbowFitting` on the two end connectors nearest the bend point. The plan warns
for bends over 90° (most elbow families stop there), for vertical segments (risers are fine; their bends need
vertical bend fittings) and for sloped segments (slope is not supported: the segment is drawn, its elbows may
fail).

**A failed fitting never loses the straight segments.** Every elbow and every end connection runs in its own
sub-transaction. When Revit refuses one (no bend family, an angle the family cannot make, segments too short
for the fitting), that bend reports `status: "fitting_failed"` with Revit's message, the two segments stay
where they are, unjoined, and the run is still created. The result says exactly which bend failed and why.

### End connections

`auto` collects the trays (or conduits) whose bounding box comes within `snapMm` of the run end, takes their
**open** end connectors (never a connected one) within `snapMm`, and joins to the nearest. How it joins depends
on how the two ends lie (the same decision as `revit_connect_mep_curves` with `mode=auto`):

| The ends | Joined by |
|---|---|
| Touching and in line, same size | `Connector.ConnectTo` |
| In line with a gap, same size | The run's end is extended onto the other end, then `ConnectTo` |
| In line, different sizes | `NewTransitionFitting` |
| Meeting at an angle | `NewElbowFitting` |
| Passing each other, or parallel side by side | Not joined: `skew_lines` |

No open end within `snapMm` → `no_candidate`; the end stays open. A join Revit refuses → `fitting_failed`
with the reason; the run itself stays.

### The result

```json
{
  "runId": "5c0e…",
  "segments": [ { "index": 0, "elementId": 1203344, "fromMm": {"x": 2000, "y": 5000, "z": 2800}, "toMm": {…}, "lengthMm": 23849.9, "status": "created" } ],
  "fittings": [ { "bendIndex": 0, "elementId": 1203350, "atMm": {…}, "angleDeg": 90, "plane": "horizontal", "status": "created" } ],
  "connections": [ { "end": "start", "requested": "auto", "toElementId": 1188002, "status": "connected", "action": "connect_to" } ],
  "totalLengthMm": 32700.2,
  "totalLengthM": 32.7
}
```

Segment lengths are read back after Revit has trimmed them for the elbows. The whole run is one transaction
("Revit MCP - Create Cable Tray Run" / "… Conduit Run"): one Undo removes it.

**Run id.** Every segment and fitting of the run carries the run id in the Extensible Storage schema
`RevitMCP_MepRun` (`b8f0c3a2-6d4e-4c71-9e25-3f1a7d6b9c04`; fields `RunId`, `Kind`, `Role`, `CreatedUtc`;
public read and write, like the circuit-arcs marker). Revit schema names allow letters, digits and underscores
only, so the name is `RevitMCP_MepRun`, not `RevitMCP.MepRun`. `revit_get_mep_run_summary runId=…` finds the
run again; `revit_read_extensible_storage` decodes the marker like any public schema.

## Connecting two curves

```json
{ "elementIdA": 1203344, "elementIdB": 1188002, "mode": "auto", "maxGapMm": 5000 }
```

`revit_connect_mep_curves` takes the two open ends nearest each other, measures them (pure geometry:
coincident, collinear, angled, parallel offset or skew; the gap; the angle; where the lines cross) and makes
the matching call:

| `mode` | Does |
|---|---|
| `auto` | As in the table above |
| `elbow` | `NewElbowFitting`; only for ends that meet at an angle |
| `union` | `NewUnionFitting` for ends in line (A is extended first when there is a gap); a transition when sizes differ |
| `extend` | Moves A's end onto B's end (`LocationCurve.Curve = Line.CreateBound`) when the two are in line within 1°, then connects |

It reports `connected` (with the fitting id), `already_connected` (directly or through one fitting; nothing
changes), `skew_lines`, `too_far` (an end would travel more than `maxGapMm`), `no_open_end` or `fitting_failed`
with Revit's reason, plus the geometry. Trays join trays and conduits join conduits.

## The summary

```json
{ "kind": "cableTray", "levels": ["1. korrus"] }
```

`revit_get_mep_run_summary` collects trays and conduits (and their fittings, `OST_CableTrayFitting` /
`OST_ConduitFitting`), optionally only of some `types`, `levels`, what `viewId` shows, or one `runId`, and
returns:

- `totals`: segments, fittings, length in mm and m, metres per kind;
- `byType`, `bySize` (`300x60`, `Ø25`), `byLevel`, `byWorkset` and `groups` (type × size × level × workset,
  the rows a schedule shows), each with count, `lengthMm` and `lengthM`. Lengths are Revit's own *Length*
  parameter, so the totals equal a schedule's sums;
- `fittingsByType`: fittings counted by "Family: Type";
- `openEnds`: every end connector that is not connected, with element, type, level and position (`xMm`,
  `yMm`, `zMm`), at most 500 listed (`truncated` says when there are more);
- `disconnectedSegments`: segments with every end open, at most 500.

The scan stops at the connector's scan limit (20 000 elements) and says so.

**Publish the open ends.** The open-ends list is the review the user works through. Publish it with
`revit_publish_page`: one table, one row per open end with `data-revit-id` set to the element id, columns for
kind, type, level and the position. The served page then selects the element in Revit from each row.

## Example: a corridor tray and conduits to three sockets

1. **Find the corridor.** `revit_get_room_geometry roomNumbers=["1.05"]` returns the corridor's boundary and
   `ceilingHeightMm` (say 3000). Take its centreline: along the corridor from (2000, 5000) to (26000, 5000), then
   round the corner to (26000, 14000).
2. **Pick the type.** `revit_list_mep_curve_types kind=cableTray` → `KL-300x60` with `elbow: available`.
3. **Preview.**

   ```json
   {
     "kind": "cableTray", "type": "KL-300x60", "level": "1. korrus", "offsetMm": 2800,
     "widthMm": 300, "heightMm": 60,
     "points": [ {"x": 2000, "y": 5000}, {"x": 14000, "y": 5000}, {"x": 26000, "y": 5000}, {"x": 26000, "y": 14000} ],
     "connectStart": "auto"
   }
   ```

   The preview merges the collinear point (14000, 5000), shows 2 segments (24.0 m and 9.0 m), 1 bend of 90°
   with an elbow, 33.0 m, and the open tray end within 50 mm of the start if the riser tray from the electrical room is there.
4. **Create** with the same arguments (approval: "Create a cable tray run 'KL-300x60' of 2 segments, 33.0 m, 1
   elbow on '1. korrus', connect start to the nearest open tray end within 50 mm."). Keep the returned `runId`.
5. **Conduits from the tray to three sockets.** For each socket on the corridor wall at y = 3650 (say at
   x = 6000, 12000 and 18000, box at 400 mm), draw a conduit from the tray's side down to the box:

   ```json
   {
     "kind": "conduit", "type": "EMT", "diameterMm": 20, "level": "1. korrus",
     "points": [ {"x": 6000, "y": 4850, "z": 2800}, {"x": 6000, "y": 3700, "z": 2800}, {"x": 6000, "y": 3700, "z": 450} ]
   }
   ```

   Two segments, one vertical elbow. A conduit that leaves a tray's side has no connector to join (a tray only
   has connectors at its ends), and a socket box is not a conduit, so both ends stay open on purpose.
6. **Check.** `revit_get_mep_run_summary runId=<the tray's run id>`: no open ends except the free tray end and,
   where `connectStart` found nothing, the start. `revit_get_mep_run_summary kind=conduit levels=["1. korrus"]`:
   three conduits, six open ends (tray side and box at each), their total length. Publish the open ends with
   `revit_publish_page` for the user to review.

## Status codes

| Status | When |
|---|---|
| `validation_failed` | An argument cannot be read (kind, type missing, sizes for the wrong kind, bad snap, workset, level) |
| `polyline_invalid` | Fewer than 2 points, a point without x/y, missing z without level + offsetMm, no length, doubling back |
| `mep_type_not_found` | No tray/conduit type with that name or id; `data.availableTypes` lists them |
| `size_not_standard` | `diameterMm` is not a size of the conduit's standard; `data.nearestSizesMm` |
| `routing_preference_missing` | Preview: the type has no fitting family for the bends of this run (`data.routingPreference.missing`); create warns and goes on |
| `fitting_failed` | Create: at least one elbow failed (per bend in `fittings`); connect: Revit refused the join |
| `skew_lines`, `too_far`, `no_open_end`, `already_connected` | `revit_connect_mep_curves` and end connections |

## Revit versions

The same calls on Revit 2024 and 2026: `CableTray.Create`, `Conduit.Create`,
`Document.Create.NewElbowFitting / NewUnionFitting / NewTransitionFitting`, `ConnectorManager`,
`MEPCurveType.RoutingPreferenceManager`, `CableTraySizes.GetCableTraySizes`,
`ConduitSizeSettings.GetConduitSizeSettings` and the size parameters exist in both API versions.

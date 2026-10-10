# Placing Face-Based Families

Put a face-based family on a wall, ceiling or floor by giving a point: the tool finds the face
and hosts the family on it. The face can be in this model or in a linked RVT or IFC model, which
is where the walls and ceilings usually are in a services model.

For families that are not face-based — level-based components, families hosted by a wall or
ceiling element, detail items — use `revit_place_family_instances`
(see [What this tool does not place](#what-this-tool-does-not-place)).

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_place_on_face` | Read-only | Which face each point would use, where the family would land, and a rolled-back trial |
| `revit_place_on_face` | Requires approval | Place the families |

## The request

```json
{
  "familyName": "EN_SIDE_Pistik",
  "typeName": "2xRJ45",
  "mountOn": "wall",
  "placements": [
    { "x": 12450, "y": 8300, "z": 300 },
    { "x": 15100, "y": 8300, "z": 300 },
    { "x": 14000, "y": 6000, "z": 2600, "mountOn": "ceiling", "rotationDegrees": 90 }
  ],
  "maxDistanceMm": 1500
}
```

| Argument | Meaning |
|---|---|
| `typeId`, or `familyName` and/or `typeName` | The family type to place. Names match partially; an ambiguous name returns the candidates |
| `placements` | Up to 500 entries. Coordinates in mm, project coordinates |
| `mountOn` | For the whole request, or per placement: `wall`, `ceiling`, `floor`, `nearest` |
| `maxDistanceMm` | How far from each point a face may be (default 1500) |
| `includeHost`, `includeLinks` | Where to look: this model, linked models (both default true) |
| `linkInstanceIds` | Restrict the links to these instances |
| `hostCategories` | Only faces of elements in these categories, e.g. `["Walls"]` |
| `offsetFromHostMm` | Sets the family's *Offset from Host* after placing |
| `angleToleranceDegrees` | How far a face may lean and still count as a wall, ceiling or floor (default 10) |
| `searchViewId` | The 3D view the search runs in |
| `atomic` | `true` (default) — any failure undoes the batch. `false` — place what can be placed |
| `dryRun` | Preview only. `true` (default) lets Revit try the placement and rolls it back |

Each placement:

| Field | Meaning |
|---|---|
| `x`, `y`, `z` | A point at or near the face. All three are required — a missing `z` is not taken as 0 |
| `mountOn` | Overrides the request's `mountOn` for this placement |
| `dx`, `dy`, `dz` | An exact search direction instead of `mountOn` (not both) |
| `rotationDegrees` | Turns the family about the face normal |

## How the face is found

From each point the tool casts rays in a 3D view and takes the nearest face of the kind asked for:

| `mountOn` | Rays | Accepts |
|---|---|---|
| `wall` | 16 horizontal directions, all round | A vertical face |
| `ceiling` | Straight up | A face whose normal points down |
| `floor` | Straight down | A face whose normal points up |
| `nearest` | All of the above | Any of the three |
| `dx`, `dy`, `dz` | That one direction | Whatever it meets first |

A face is classified by its **orientation**, not by its element's category. A linked IFC model
often delivers walls and slabs as generic elements with no useful category, and they are found
all the same. `hostCategories` narrows the search when the categories are meaningful.

"Nearest" means the smallest perpendicular distance from the point to the face's plane — a wall
that a ray happened to meet at a shallow angle is measured by how far away it actually is.

The response names the face: the element, its category and name, whether it is in this model or
which link, the surface kind, and the measured normal. When nothing matches, it lists what *was*
in reach, so "there is a wall 2100 mm away" is distinguishable from "there is nothing".

### The 3D view

Ray casting only sees what is visible in the 3D view it runs in. By default that is the active
view if it is a 3D view, otherwise the first 3D view in the model; `searchViewId` names one. A
face goes unfound when its link is unloaded or hidden in that view, when the view has a section
box that cuts it off (the response warns), or when its category or workset is turned off there.

## Where the family lands

At the foot of the perpendicular from the point to the face — the point's projection onto the
wall or ceiling. So a point taken from a room's centre line, 300 mm off the wall, puts the device
on the wall directly opposite it, at the same height.

If that foot is not on the face (the face ends before it), the family lands where the search ray
met the face instead, and the placement carries a note. The response reports `locationMm` and
`distanceMm` for every placement, and after placing, `placedPointMm` read back from the model.

## Orientation

Revit hosts a face-based family with its local Z along the face normal and asks for the
direction of its local X. With `rotationDegrees` 0:

- on a **wall**, local X runs horizontally along the face, so the family stands upright — its
  local Y is model up;
- on a **ceiling or floor**, local X is model X.

`rotationDegrees` turns the family about the face normal, counter-clockwise as seen from in
front of the face (from inside the room for a wall, from below for a ceiling, from above for a
floor). The resulting `referenceDirection` is reported per placement.

How a particular family looks at rotation 0 depends on how it was built. Place one, look at it,
and adjust `rotationDegrees` for the rest.

## Linked models

A family can be hosted on a face of a linked model. The response's `placedHostId` is then what
Revit reports as the host — normally the link instance. The tool passes Revit the reference to the linked face that the
ray returned, and if Revit refuses that, a second reference to the same face built from the
linked element's geometry. `placedVia` says which one worked.

Whether Revit accepts a linked face as a host depends on the link and the geometry — it is not
knowable from the face alone, which is what the preview's trial run is for. When Revit refuses,
the placement is `Failed` with both of Revit's messages. If a link's faces cannot host, the
alternatives are a level-based family placed with `revit_place_family_instances` and pushed
against the surface with `revit_align_elements`.

If the linked model is later reloaded with changed geometry, families hosted on its faces may
lose their host, as they do when placed by hand.

## The preview

`revit_preview_place_on_face` runs the same face search and reports, per point, the face it
would use, where the family would land, its orientation, and the alternatives in reach. Use it to
confirm the *right* wall or ceiling was found — in a room corner the nearest wall may not be the
intended one.

Unless `dryRun=false`, it then has Revit perform the placements inside a transaction that is
always rolled back. That is how a face Revit will not host on shows up before approval. The trial
changes nothing, reaches no undo stack, and reports no element ids, since they no longer exist.
It is skipped, with the reason in `trialNote`, when the request would be rejected anyway, when
the document is read-only, or when a transaction is already open.

## Outcomes

| `status` | Meaning | Counts as a failure |
|---|---|---|
| `Ready` | A face was found; the family would be placed (preview) | No |
| `Placed` | Placed | No |
| `NoFace` | No face within `maxDistanceMm` in the searched directions | Yes |
| `WrongSurface` | Faces in reach, but none of the kind asked for; the reason names the nearest | Yes |
| `Failed` | Revit refused to host the family on the face | Yes |
| `RolledBack` | Placed, then undone because the atomic batch failed | Yes |
| `NotAttempted` | Placeable, but the atomic batch was rejected before it started | Yes |

`atomic=true` (the default) undoes everything on any failure; when the failure is known before
the transaction opens — a point with no face — the transaction is never started. `atomic=false`
gives each placement its own sub-transaction and keeps what succeeds. Either way it is one Revit
transaction named `Revit MCP - Place On Face`, and a single undo removes every instance.

## What this tool does not place

| Family | Use |
|---|---|
| Level-based component (most equipment, furniture, unhosted devices) | `revit_place_family_instances` |
| Hosted by a wall, ceiling, floor or roof *element* (doors, windows, wall-hosted fixtures) | `revit_place_family_instances` with `hostElementId`. These cannot be hosted on a linked element |
| Detail item, annotation symbol | `revit_place_family_instances` with `viewId` |

Asking this tool for one of them fails before anything is searched, with the tool to use.

Face-based and work-plane-based families share one placement type in Revit, so both are accepted
here; either ends up hosted on the face.

## A typical run

1. Get the points: from room geometry (`revit_get_room_walls`), a DWG
   (`revit_get_cad_placement_points`), or existing elements.
2. `revit_preview_place_on_face` with the family and the points. Check each `face` is the
   intended wall or ceiling and `distanceMm` is what you expect.
3. `revit_place_on_face` with the same arguments.
4. The response lists `createdElementIds`; each placement has its `elementId`, `placedHostId`
   and `placedPointMm`.

## Safety

- The preview changes nothing; its trial run is always rolled back.
- A missing coordinate rejects the request rather than defaulting to zero.
- A point with no face of the requested kind is reported, never placed on something else.
- The family type is checked first: a family that cannot be hosted on a face is turned away with
  the tool that places it.
- Requests over 500 placements are rejected rather than truncated.

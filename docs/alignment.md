# Aligning Elements Against Surfaces

Moves elements until they sit against the nearest wall, ceiling, floor — or whatever
is closest. Built for the case where the surfaces live in a **linked IFC**: the search
never trusts categories, because an IFC import routinely lands walls and slabs on
Generic Models, Mass, or whatever the exporter felt like.

For the drafting-tool kind of aligning — putting tags, text, or elements on a common
line within one view, or spreading them out evenly — see
[`view-alignment.md`](view-alignment.md). For putting elements on coordinates you
already know, see [`move-elements.md`](move-elements.md).

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_align_elements` | Read-only | Show where each element would land and what it would land on |
| `revit_align_elements` | Requires approval | Perform the moves |

Only host-model elements can be moved. Elements inside a link are read-only in Revit —
open the link's own model to move those.

## How a surface is found

1. Rays are cast from each element's centre through `ReferenceIntersector`, which sees
   the host model **and** every loaded link, so linked IFC geometry is found the same
   way native geometry is.
2. The directions depend on what was asked for: straight up for a `ceiling`, straight
   down for a `floor`, a ring of horizontal directions for a `wall`, and all of them for
   `nearest`. For a family instance the ring starts behind its facing direction, so a
   wall-mounted device usually resolves on the first ray.
3. Each hit face is then **measured**: two more rays are cast alongside the first, and
   the three hit points define the face's plane. The resulting normal decides whether
   the hit is a wall, a ceiling, a floor, or none of those.
4. The nearest hit whose surface matches the request wins. Everything else is returned
   as `alternates` so a wrong answer is easy to diagnose.

Classification is by orientation, never by category:

| Surface | Face normal (as seen from the element) |
|---|---|
| `floor` | points up, within `angleToleranceDegrees` of +Z |
| `ceiling` | points down, within the tolerance of −Z |
| `wall` | horizontal, within the tolerance of the XY plane |
| `other` | anything in between — a ramp, a sloped soffit |

`angleToleranceDegrees` defaults to 30 and is capped at 44: above 45 the three bands
would overlap.

When the probe rays disagree — a curved face, a fragmented mesh, a face narrower than
the ~76 mm probe offset — the orientation falls back to an inference from the ray
direction. That case is flagged as `surfaceNormalMeasured: false` in the response and
called out in the reason text.

## How far it moves

```
move = distance to the surface − how far the element reaches that way − gapMm
```

The reach is the element's bounding box measured along the search direction, so an
element ends up **touching** the surface rather than centred on it. `gapMm` leaves a
clearance; a negative `gapMm` embeds the element.

A negative move means the element already overshot the surface and has to come back.
The preview reports `currentGapMm` for exactly this reason — a negative value means the
element is currently inside the wall or slab.

Moves under 0.5 mm are reported as `alreadyFlush` and skipped.

## Arguments

| Argument | Meaning |
|---|---|
| `elementIds` / `useSelection` | What to move (host model only, max 500) |
| `surface` | `wall`, `ceiling`, `floor`, or `nearest` — required |
| `searchRadiusMm` | How far to look (default 3000, max 50000) |
| `gapMm` | Clearance to leave; negative embeds |
| `scope` | `both` (default), `links`, or `host` |
| `targetCategories` | Only accept surfaces on these categories — the escape hatch when a link is messy |
| `linkInstanceIds` | Only search these links |
| `rotateToSurface` | Also turn each element square to the wall it lands on (default false) |
| `angleToleranceDegrees` | How far a face may tilt and still count (1–44, default 30) |
| `horizontalSamples` | Horizontal directions sampled for a wall (4–32, default 8) |
| `searchViewId` | The 3D view the rays run in |

`targetCategories` is applied **after** the hit, against the linked element's own
category. It is deliberately not passed to `ReferenceIntersector`, whose element filter
is applied to the `RevitLinkInstance` rather than to the elements inside the link — that
would discard every linked hit.

## The search view matters

Ray casting only sees geometry visible in a 3D view. The tool uses the active 3D view,
falls back to the first non-template one, and reports which it used as `searchViewName`.

If targets go missing, the view is the first thing to check:

- an active **section box** clips geometry out of the search (this is warned about);
- hidden categories, worksets, or a view template that hides the link will hide it from
  the rays too;
- an unloaded link contains no geometry at all.

## Rotation

`rotateToSurface` turns the element about its vertical axis until it faces away from the
wall it landed on, using the measured face normal. It applies only to family instances
against `wall` surfaces — spinning about Z cannot square anything to a ceiling or floor,
and those cases are skipped rather than guessed at.

The rotation is applied **before** the move, about the element's own centre, and the
travel distance is then re-measured — turning an element changes how far it reaches
toward the surface. The preview's `moveDistanceMm` is therefore a pre-rotation estimate
whenever `rotateToSurface` is on; the write tool reports what it actually did.

## Safety

- The preview opens no transaction and changes nothing.
- All moves run inside one Revit transaction, one sub-transaction per element, so one
  failure does not abandon the rest. A single undo reverses the whole call.
- Every plan is resolved **before** the first move: moving an element changes the
  geometry the rays see, so resolving mid-transaction would make later elements chase
  the ones already moved.
- The elements being moved are excluded from their own search, so they cannot align to
  each other.
- Pinned elements are reported rather than force-moved.

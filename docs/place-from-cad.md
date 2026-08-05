# Placing Items From a DWG

Places a family at every location an imported DWG marks — the sockets, detectors, or
luminaires an electrical drawing already carries as blocks, points, or circles.

Two things are never guessed, because both differ per project and getting them wrong is
expensive:

- **which layers hold the locations** — layer naming is a per-office convention;
- **what height to place at** — a 2D drawing carries no mounting height.

Both are required arguments on the write tool. The read tool exists to give the agent
what it needs to ask.

> Drawing never made its symbols into blocks — the luminaires are bare lines on one
> layer? See [When the DWG Never Blocked Its Symbols](#when-the-dwg-never-blocked-its-symbols)
> at the end of this document.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_get_cad_placement_points` | Read-only | Layer inventory, then the actual points |
| `revit_preview_place_from_cad` | Read-only | What would be created, where, at what height |
| `revit_place_from_cad` | Requires approval | Creates the instances |

## Why tools and not a skill

The repository's skill system runs C# tasks inside Revit; it cannot stop halfway to ask
a question. The conversation this workflow needs — *"which layer?"*, *"what mounting
height?"* — belongs to the agent, and client-side skill files are not tracked in this
repository (`.claude/` is gitignored). So the requirement is met by the tools instead:
the write tool **refuses to run** without `layers` and `elevationMode`, and its error
text says what to ask. There is nothing for the agent to remember and nothing to
install.

## The workflow

**1. Inventory the layers.** Call `revit_get_cad_placement_points` with no `layers`:

```json
{ "importInstanceId": 123456 }
```

Every layer comes back with what it holds:

```json
{ "layerName": "E-SOCKET-SYM", "placeable": true, "blockCount": 42,
  "pointCount": 0, "circleCount": 42, "curveCount": 168, "textCount": 42,
  "minZmm": 0, "maxZmm": 0 }
```

`placeable` marks layers with block inserts, points, or circles. A layer that is all
curves and text is a drawing layer, not a locations layer. Show this to the user and ask
which layers to take.

**2. Read the points and check the heights.**

```json
{ "layers": ["E-SOCKET-SYM"] }
```

The response includes an `elevation` block. When every point sits at the same height:

> Every point sits at the same height, so the drawing carries no mounting height.

That is the cue to ask the user for one.

**3. Preview, then place.** `revit_preview_place_from_cad` reports how many instances
would be created, how many locations already have one, and the elevation range they
would land at. `revit_place_from_cad` then does it, behind the approval gate.

## What counts as a location

| `pointSource` | Geometry | Point |
|---|---|---|
| `block` | A block reference in the DWG | Its insertion point, plus its plan rotation |
| `point` | An AutoCAD POINT entity | The point itself |
| `circle` | A full circle | Its centre |

All three are read by default. Marks closer together than `mergeToleranceMm` (default
1 mm) collapse into one location — a symbol drawn as a block around a circle is one
socket, not two — and the surviving point keeps the block's rotation.

Geometry is walked with `GetSymbolGeometry` and transforms composed explicitly, rather
than relying on `GetInstanceGeometry` to pre-apply them, so nested blocks land in host
coordinates unambiguously. If block insertion points ever look wrong for a particular
export, set `pointSources` to `["circle"]` or `["point"]` — the preview shows the
coordinates before anything is created.

## Elevation

`elevationMode` is required. There is no default on purpose.

| Mode | Meaning |
|---|---|
| `dwg` | Keep the height the drawing carries — for a genuinely 3D DWG |
| `level` | `levelName` + `offsetMm`, the usual answer: *"1100 above finished floor"* |
| `explicit` | An absolute `elevationMm` |

`levelName` also picks the level a level-based family is hosted on.

## Rotation

Block references carry a rotation, and `applyBlockRotation` (default true) puts it on
the placed instance, so a directional symbol ends up pointing the way the drawing does.
`rotationOffsetDegrees` adds a constant on top, for families whose own origin faces a
different way than the DWG block. Points and circles carry no rotation.

## Re-running

`skipExisting` is true by default: a location that already has an instance of the same
type within `duplicateToleranceMm` (default 50 mm) is skipped. Adjusting a layer choice
and running again tops the model up instead of stacking a second socket on every point.

The check compares **plan position only** — the DWG marks sit at 0 while the placed
sockets sit at 1100, and they are still the same location.

## Limits and safety

- `maxInstances` defaults to 500 and is capped at 2000. Exceeding it is reported, not
  silently truncated.
- All instances are created in one transaction, so a single undo reverses the whole run.
- A failure at one location is reported and the rest still go in.
- The preview opens no transaction.

---

# When the DWG Never Blocked Its Symbols

Everything above assumes the drawing marks its locations with block inserts, points or
circles. Plenty of drawings do not: the luminaires are **loose lines** — four segments
that happen to form a rectangle — all on one layer, with nothing tying them together.
`revit_get_cad_placement_points` reports such a layer as pure `curveCount` and finds
nothing to place.

A second set of tools handles that case by reconstructing the fixtures from the line work.

| Tool | Permission | Purpose |
|---|---|---|
| `revit_get_cad_shapes` | Read-only | Layer inventory, then the reconstructed fixtures and their signatures |
| `revit_preview_place_from_cad_shapes` | Read-only | What would be created, with which type, where |
| `revit_place_from_cad_shapes` | Requires approval | Creates the instances |

## What the Revit API cannot do

**DWG text is not readable.** A drawing that labels its luminaires `V11.1`, `V09.2` and
so on next to each symbol is carrying exactly the information needed to pick a family
type — and none of it reaches Revit:

- no `GeometryObject` subclass carries text, so an `ImportInstance` yields only curves,
  points, meshes and solids;
- there is no `Explode` in the public API, so the text cannot be turned into `TextNote`
  elements and read back either.

So the type marks in the drawing **cannot** drive the placement. Fixtures are identified
by **size** instead. Say so plainly when reporting to a user rather than implying the
labels were read.

## How a fixture is reconstructed

1. **Collect.** Every curve on the named layers is tessellated into straight segments in
   host coordinates. Arcs, splines and circles all become short pieces, flagged as having
   come from a curve.
2. **Group.** Segments whose endpoints land within `joinToleranceMm` (default 2 mm) of
   each other are one fixture — the sides of a drawn rectangle touch, and touch nothing
   else. Grouping never crosses a layer.
3. **Measure.** The smallest box that contains the group, *at any angle*, gives the
   centre (the insertion point) and the direction of its long side (the rotation). This
   is the minimum-area box found by rotating calipers, not an axis-aligned bounding box:
   a luminaire drawn at 37° still measures 1200 × 200, where its axis-aligned box would
   read 1080 × 880.
4. **Classify.** How much of that box the outline fills says what the symbol is — a
   rectangle fills all of it, a circle fills π/4 of it, a bare line has no second
   dimension.

## Signatures

Each fixture gets a **signature**: its kind plus its size, bucketed to
`signatureBucketMm` (default 10 mm) because no two drawn symbols measure exactly alike.

```
rectangle 1200x200   x28
rectangle 600x600    x14
circle d200          x51
```

That table is what `revit_get_cad_shapes` returns, and it is what to show the user. The
signature is the key a family type is mapped to.

## Choosing the family type

`typeMap` pins a type to a signature and always wins:

```json
[
  { "signature": "rectangle 1200x200", "familyName": "POS-11-1" },
  { "signature": "circle d200", "typeId": 987654 }
]
```

Signatures the map leaves out fall back to **footprint matching** (`autoMatchTypes`,
default true): the drawn size is compared against each candidate family's own plan
footprint, and the closest within `autoMatchToleranceMm` (default 50 mm) wins. The
candidates must be narrowed with `autoMatchFamilyName` or `autoMatchCategory` — matching
a 1200 × 200 rectangle against every symbol in the model would happily return a door.

The fallback **refuses to choose** when two families fit equally well, and when nothing
fits at all. Those fixtures are reported unplaced with the reason. A silently wrong
luminaire type is worse than a missing one.

## Rotation

`applyShapeRotation` (default true) puts the drawn angle on the instance.

The angle is reported in **[0, 180)**. A drawn rectangle is symmetric, so which end is
its "front" is simply not in the geometry — 190° and 10° are the same picture. Where a
family's own origin faces the other way, `rotationOffsetDegrees` adds a constant on top.
Circles report no rotation at all; a round downlight has no orientation, and reporting
the tessellation's angle would set every one of them spinning.

## When a drawing line touches a symbol

This is the failure mode worth knowing about. If a wall line happens to end on a
luminaire's corner, step 2 pulls both into the same group, and half the plan can arrive
as one "fixture".

`maxShapeSizeMm` (default 3000) catches it: a cluster longer than that is flagged
`oversize`, reported, and skipped rather than placed. If it fires, either narrow the
layers or lower `joinToleranceMm`.

## Re-running

`skipExisting` works as it does above, with one difference: the check is made **per
family type**, so two signatures placing different families never shadow each other.

## Worked example

```
1. revit_get_cad_shapes { "importInstanceId": 123456 }
   -> layers, with the loose-geometry ones marked worthReconstructing

2. revit_get_cad_shapes { "layers": ["New_Valgustid_SA..."] }
   -> 93 fixtures; signatures rectangle 1200x200 x28, circle d200 x51, ...
   -> elevation.isFlat = true, so ask the user for a mounting height

3. revit_preview_place_from_cad_shapes {
     "layers": ["New_Valgustid_SA..."],
     "elevationMode": "explicit", "elevationMm": 2500,
     "typeMap": "[{\"signature\": \"rectangle 1200x200\", \"familyName\": \"POS-11-1\"}]",
     "autoMatchCategory": "Lighting Fixtures"
   }
   -> per signature: the type it resolved to, how it was resolved, how many would be placed

4. revit_place_from_cad_shapes { ...same... }
```

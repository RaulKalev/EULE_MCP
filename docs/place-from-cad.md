# Placing Items From a DWG

Places a family at every location an imported DWG marks — the sockets, detectors, or
luminaires an electrical drawing already carries as blocks, points, or circles.

Two things are never guessed, because both differ per project and getting them wrong is
expensive:

- **which layers hold the locations** — layer naming is a per-office convention;
- **what height to place at** — a 2D drawing carries no mounting height.

Both are required arguments on the write tool. The read tool exists to give the agent
what it needs to ask.

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

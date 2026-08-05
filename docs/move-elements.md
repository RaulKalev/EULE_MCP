# Moving Elements To Exact Coordinates

Put existing elements on precise model coordinates without recreating them. The elements
keep their ids, types, parameters, circuits, tags and hosts — this is the same edit as
dragging them, done in bulk and to the millimetre.

For the other kinds of moving: pushing an element until it sits against a wall, ceiling or
floor is [`alignment.md`](alignment.md); lining elements up with each other inside one view
is [`view-alignment.md`](view-alignment.md).

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_move_elements` | Read-only | Show where each element is, where it would go, and how far |
| `revit_move_elements` | Requires approval | Perform the moves |

## The request

Both tools take the same arguments.

```json
{
  "moves": [
    {
      "elementId": 1756386,
      "targetXmm": 76871.5,
      "targetYmm": 71602.9,
      "expectedXmm": 75388.79,
      "expectedYmm": 71229.96,
      "expectedZmm": 59275.0
    }
  ],
  "atomic": true,
  "positionToleranceMm": 1.0,
  "skipPinned": true
}
```

| Argument | Meaning |
|---|---|
| `moves` | Required. Up to 2000 entries, one per element |
| `atomic` | `true` (default) — any failure undoes the whole batch. `false` — move what can be moved |
| `positionToleranceMm` | How far an element may sit from its expected point before it counts as stale (default 1.0) |
| `skipPinned` | `true` (default) skips pinned elements; `false` reports them as failures |

Coordinates are millimetres in project coordinates — the same numbers
`revit_inspect_selected_elements` and `revit_get_cad_shapes` return.

## An omitted axis is not zero

`targetXmm`, `targetYmm` and `targetZmm` are each optional, and an omitted axis keeps its
current value. Leaving out `targetZmm` is how a fixture keeps the elevation its family and
level gave it while its plan position is corrected — which is almost always what you want
when matching a 2D drawing. `"targetZmm": 0` is a different request: it moves the element
to the project origin plane.

The same applies to the other two axes, so a single-axis nudge is
`{"elementId": 1756386, "targetXmm": 76871.5}`.

## Which point moves

The element's `LocationPoint` — its insertion point, the thing Revit itself measures from.
The translation is the vector from that point to the target, and it is applied with
`ElementTransformUtils.MoveElement`, so hosted geometry, tags and circuits follow the way
they do when you drag the element by hand.

An element with no `LocationPoint` is reported as `UnsupportedLocation` and left alone.
That covers everything placed on a curve — walls, pipes, ducts, conduit, cable tray — and
anything with no location at all. The bounding-box centre is deliberately **not** used as a
fallback: the box covers the whole symbol including its leader, flip handles and 3D body,
so its centre is not the insertion point, and moving to it would silently land the element
somewhere else.

## The staleness check

`expectedXmm`, `expectedYmm` and `expectedZmm` are optional and say "this is where I
measured the element before I worked out the target". If the element is further than
`positionToleranceMm` from that point on any axis, the target was calculated against a model
that no longer exists: the element is reported `Stale` and is not moved.

Only the axes you supply are checked, and the response reports `staleDeviationMm` — the
worst axis — so a near miss can be told apart from a wholesale rearrangement. Without any
expected coordinates the check is skipped entirely.

Staleness is checked before anything else, including pinning: if the model has moved on,
nothing else about that entry can be trusted.

## Outcomes

Every element comes back with a `status`, and appears in exactly one list under
`elementIds`.

| `status` | Meaning | Counts as a failure |
|---|---|---|
| `Ready` | Will move (preview only) | No |
| `Moved` | Moved | No |
| `AlreadyThere` | Within 0.1 mm of the target already | No |
| `Pinned` | Pinned; skipped under `skipPinned=true`, a failure under `skipPinned=false` | Only when `skipPinned=false` |
| `Stale` | Disagrees with the supplied expected point | Yes |
| `Missing` | No element with that id | Yes |
| `UnsupportedLocation` | No `LocationPoint` to move | Yes |
| `Failed` | Revit refused the move | Yes |
| `RolledBack` | Moved, then undone because the atomic batch failed | Yes |
| `NotAttempted` | Movable, but the atomic batch was rejected before it started | Yes |

The 0.1 mm no-op threshold is fixed and independent of `positionToleranceMm`, so a loose
staleness tolerance never starts swallowing real moves. It does mean re-running the same
call is a no-op.

Pinned elements are never unpinned. With `skipPinned=false` the response says to unpin them
in Revit and run again.

## Atomic and non-atomic

`atomic=true` (the default) means the request only makes sense whole. If any element is
stale, missing, unsupported, or refused by Revit, the entire batch is undone and the model
is left exactly as it was. When the problem is visible before the transaction opens, the
transaction is never started at all — the undo stack stays untouched — and everything
movable is reported `NotAttempted`.

`atomic=false` moves everything it can and reports the rest. Each element gets its own
sub-transaction, so one refusal costs that element and nothing else.

Either way, one call is one Revit transaction named `Revit MCP - Move Elements`, and a
single undo reverses it.

## The DWG-alignment workflow

The reason these tools exist: a drawing whose fixture positions are right and a model whose
fixtures have drifted.

1. `revit_get_cad_shapes` — reconstruct the fixture outlines and their centres from the DWG.
2. Match each fixture's insertion point (or a corner) to one DWG shape. This is the step
   that needs judgement; anything ambiguous should not get a move entry.
3. `revit_preview_move_elements` with the calculated centres as targets and the fixtures'
   current positions as `expected` values. Omit `targetZmm` — the drawing has nothing to say
   about elevation.
4. Read the preview. Anything `Stale`, `UnsupportedLocation` or unexpectedly far to travel
   is a bad match, not a bad model.
5. `revit_move_elements` with the same arguments.
6. `revit_inspect_selected_elements` to confirm the resulting positions.

## Safety

- The preview opens no transaction and changes nothing.
- Every element is measured **before** the first one moves, against the model the caller
  described.
- Nothing is deleted or recreated, so element ids, types, parameters, circuits, tags and
  hosts all survive.
- The document is regenerated once, after the last move, rather than after every element.
- An element that appears twice in one `moves` array is rejected: two destinations for one
  element is a contradiction, not a preference for the last one.
- Batches over 2000 moves are rejected rather than truncated — silently dropping moves would
  leave the model half-aligned.

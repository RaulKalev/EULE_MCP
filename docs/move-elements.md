# Moving Elements

Put existing elements on precise model coordinates, or shift them by a known distance, without
recreating them. The elements keep their ids, types, parameters, circuits, tags and hosts — this
is the same edit as dragging them, done in bulk and to the millimetre.

There are two ways to say where an element goes, chosen per entry:

| Entry | Meaning | Needs |
|---|---|---|
| `targetXmm`, `targetYmm`, `targetZmm` | **Absolute** — the insertion point lands on that model coordinate | A `LocationPoint` |
| `deltaXmm`, `deltaYmm`, `deltaZmm` | **Displacement** along the model axes | Nothing — any element Revit can move |
| `deltaRightMm`, `deltaUpMm` | **Displacement** along a view's right and up | The element's owner view, or `viewId` |

Displacements are how Detail Items, detail lines, text notes and other view-specific elements are
moved, and how elements without an insertion point (walls, pipes, lines) are moved at all.

To make a second element instead of moving the first, see [`copy-elements.md`](copy-elements.md).

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

A displacement entry instead of a target:

```json
{
  "moves": [
    { "elementId": 2104417, "deltaRightMm": 500, "deltaUpMm": -120 },
    { "elementId": 1988230, "deltaXmm": 250 }
  ]
}
```

| Argument | Meaning |
|---|---|
| `moves` | Required. Up to 2000 entries, one per element |
| `atomic` | `true` (default) — any failure undoes the whole batch. `false` — move what can be moved |
| `positionToleranceMm` | How far an element may sit from its expected point before it counts as stale (default 1.0) |
| `skipPinned` | `true` (default) skips pinned elements; `false` reports them as failures |
| `viewId` | Only for `deltaRightMm`/`deltaUpMm` on model elements: the view whose axes to follow |

One entry is either absolute or a displacement — giving both `target*` and `delta*`, or mixing
model-axis and view-axis deltas, rejects the request.

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

## Displacements and view axes

`deltaXmm`, `deltaYmm` and `deltaZmm` are a translation along the model axes. An omitted axis is
zero: unlike a target, a displacement has nothing to "keep".

`deltaRightMm` and `deltaUpMm` are a translation along a view's own axes — right and up as you
see them on screen and on paper. How they relate to model coordinates depends on the view:

| View | Right | Up | Normal (out of the view) |
|---|---|---|---|
| Floor or ceiling plan, unrotated | model X | model Y | model Z |
| Plan with a rotated crop / scope box | rotated in the XY plane | perpendicular to it, in XY | model Z |
| Section or elevation | along the cut, horizontal | model Z | horizontal, towards the viewer |
| Drafting view | its own X | its own Y | its own Z |

Every entry involving a view reports that view's `viewAxes` (`right`, `up`, `normal` as unit
vectors in model coordinates), so "500 mm right in this section" can be converted exactly.

Which view supplies the axes:

- An element **owned by a view** (`ownerViewId` in the response: Detail Items, detail lines, text
  notes, filled regions, most annotations) always uses its owner view. `viewId` is ignored for it.
- A **model element** has no owner view, so `deltaRightMm`/`deltaUpMm` need `viewId`. Without it
  the entry fails with that explanation rather than assuming the active view.

### View-specific elements stay on their view's plane

An element owned by a view lives on that view's plane and Revit will not take it off. A move
whose translation has a component along the view normal larger than 0.01 mm is reported
`OutOfViewPlane` and nothing is attempted. This applies to both modes: in a north-facing section
`deltaYmm` is off-plane, and so is an absolute `targetYmm` that differs from the current Y.
`deltaRightMm`/`deltaUpMm` can never be off-plane, which is why they are the natural choice.

### Elements without an insertion point

A displacement does not need a `LocationPoint`. Detail lines, model lines, walls, pipes, ducts,
conduit, cable tray, text notes and filled regions all move by a delta. For them the response
has no `currentPointMm`/`targetPointMm` — there is no insertion point to report — but it does
report the requested `translationMm` and, after the move, the `actualTranslationMm` measured on
the element itself.

Absolute targets still require a `LocationPoint`, for the reason below.

## Which point moves

The element's `LocationPoint` — its insertion point, the thing Revit itself measures from.
The translation is the vector from that point to the target, and it is applied with
`ElementTransformUtils.MoveElement`, so hosted geometry, tags and circuits follow the way
they do when you drag the element by hand.

An absolute target on an element with no `LocationPoint` is reported as `UnsupportedLocation`
and left alone. That covers everything placed on a curve — walls, pipes, ducts, conduit, cable
tray, detail and model lines — and anything with no location at all. The bounding-box centre is
deliberately **not** used as a fallback: the box covers the whole symbol including its leader,
flip handles and 3D body, so its centre is not the insertion point, and moving to it would
silently land the element somewhere else. Use a displacement for these elements instead.

## Where the element ended up

After a move the response reports, per element:

- `resultPointMm` — the insertion point read back from the model (point-located elements);
- `actualTranslationMm` — how far the element really travelled, measured from the insertion
  point, or from the element's bounding box when it has none.

Revit can hold an element back without raising an error: a hosted family slides along its host,
a work-plane-based family stays on its plane, a constraint pulls it back. When
`actualTranslationMm` differs from the requested `translationMm` by more than 0.1 mm the response
carries a warning naming the element. The bounding box is used only to measure that distance —
nothing is ever moved *to* it.

## The staleness check

`expectedXmm`, `expectedYmm` and `expectedZmm` are optional and say "this is where I
measured the element before I worked out the target" (or the displacement). They compare
against the insertion point, so on an element without a `LocationPoint` they are reported
`UnsupportedLocation` — leave them out there. If the element is further than
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
| `UnsupportedLocation` | An absolute target (or expected coordinates) on an element with no `LocationPoint` | Yes |
| `InGroup` | Member of a group; Revit only moves group members in group edit mode | Yes |
| `OutOfViewPlane` | Owned by a view, and the move would leave that view's plane | Yes |
| `Failed` | Revit refused the move | Yes |
| `RolledBack` | Moved, then undone because the atomic batch failed | Yes |
| `NotAttempted` | Movable, but the atomic batch was rejected before it started | Yes |

The 0.1 mm no-op threshold is fixed and independent of `positionToleranceMm`, so a loose
staleness tolerance never starts swallowing real moves. It does mean re-running the same
call is a no-op.

Pinned elements are never unpinned. With `skipPinned=false` the response says to unpin them
in Revit and run again.

Group members are never moved: outside group edit mode Revit either refuses or offers to
ungroup, neither of which a batch should decide. Move the group instance itself (it has its own
id and a `LocationPoint`), or ungroup first. The response names the group.

`InGroup` and `OutOfViewPlane` elements are listed under `elementIds.constrained`.

## What is supported

| Element | View | Absolute target | Displacement |
|---|---|---|---|
| Model family instance (fixture, device, equipment) | — | Yes | `deltaX/Y/Zmm`; `deltaRight/UpMm` with `viewId` |
| Hosted / face-based / work-plane-based instance | — | Yes, within what the host allows | Yes, within what the host allows |
| Detail Item (point-based) | Plan, section, elevation, drafting | Yes, when the target is on the view plane | Yes — `deltaRight/UpMm`, or in-plane `deltaX/Y/Zmm` |
| Detail Item (line-based), detail line, filled region | Plan, section, elevation, drafting | No (`UnsupportedLocation`) | Yes |
| Text note, generic annotation, tag | Their owner view | Where it has a `LocationPoint` | Yes |
| Wall, pipe, duct, conduit, cable tray, model line | — | No (`UnsupportedLocation`) | Yes |
| Group instance | — / owner view | Yes | Yes |
| Group member, pinned element | any | No | No (`InGroup` / `Pinned`) |
| Family type, view, sheet | — | No — they have no position | No |

"Within what the host allows" is Revit's decision, not this tool's: the move is attempted and
the response reports where the element actually ended up. Dimensions are positioned by their
references rather than by a translation; Revit refuses to move most of them and the entry comes
back `Failed` with Revit's message.

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

## Moving a Detail Item

1. `revit_get_selected_elements` or a query to get the Detail Item's id.
2. `revit_preview_move_elements` with `{"elementId": <id>, "deltaRightMm": 500}`. The preview
   shows the owner view, its axes, and the resulting translation in model coordinates.
3. `revit_move_elements` with the same arguments.
4. The response's `resultPointMm` and `actualTranslationMm` confirm the new position; the element
   id is unchanged.

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
- A view-specific element is never taken off its view's plane, and a group member is never moved
  behind the group's back.
- Where the element ended up is read back from the model and compared with what was asked.
- Every element is measured **before** the first one moves, against the model the caller
  described.
- Nothing is deleted or recreated, so element ids, types, parameters, circuits, tags and
  hosts all survive.
- The document is regenerated once, after the last move, rather than after every element.
- An element that appears twice in one `moves` array is rejected: two destinations for one
  element is a contradiction, not a preference for the last one.
- Batches over 2000 moves are rejected rather than truncated — silently dropping moves would
  leave the model half-aligned.

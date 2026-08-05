# Lining Things Up In a View

The drafting-tool Align: pick some tags, text notes, detail lines, dimensions, viewports
or model elements and put them on a common line, or spread them out evenly. Everything
happens in the plane of one view — this never moves anything toward or away from you.

For the other kind of aligning — pushing an element until it sits against a wall,
ceiling or floor in 3D — see [`alignment.md`](alignment.md). For putting elements on
coordinates you already know, see [`move-elements.md`](move-elements.md).

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_align_in_view` | Read-only | Show the slide each element would make |
| `revit_align_in_view` | Requires approval | Perform the moves |

## Modes

| `mode` | What lines up | Which way things move |
|---|---|---|
| `left` | left edges | horizontally |
| `right` | right edges | horizontally |
| `top` | top edges | vertically |
| `bottom` | bottom edges | vertically |
| `centerVertical` | centres, onto one vertical line | horizontally |
| `centerHorizontal` | centres, onto one horizontal line | vertically |
| `distributeHorizontal` | even spacing left to right | horizontally |
| `distributeVertical` | even spacing bottom to top | vertically |

The centre modes are named after the line the centres end up on, so `centerVertical`
moves elements sideways. Spellings are forgiving: `"align left"`, `"centre vertically"`,
`"distribute-horizontally"` and `"verticalCenter"` all resolve. A bare `"center"` does
not — it never says which axis — and is rejected with the list of valid modes.

"Left" and "up" mean left and up **in the view**, from `RightDirection` and
`UpDirection`. In a rotated crop or a section they are not model X and Y, and the moves
follow the view.

## Which line everything lands on

`alignTo` picks the coordinate:

| `alignTo` | Meaning |
|---|---|
| `extreme` (default) | The outermost element already in that direction — the leftmost for `left`, the topmost for `top`. Centre modes have no outermost element, so they use the average. |
| `first` / `last` | The first or last entry in `elementIds` |
| `min` / `max` | The lowest or highest value of the aligned edge, whichever direction the mode points |
| `average` | The mean of the aligned edge |

`referenceElementId` overrides all of it: that element sets the line and does not move.
It has to be one of the elements being aligned, because the tool measures only those.

`extreme` is what a drafting Align does, and it is the safe default — nothing moves
outward past the set's current bounds. `min` and `max` are the escape hatch when you
want the opposite: `mode=right, alignTo=min` pulls every right edge back to the
leftmost one.

A Revit selection is a set, not a picking order, so `useSelection` with
`alignTo=first` has no defined answer. That combination warns and asks for
`referenceElementId` or an ordered `elementIds` instead.

## Distributing

The two outermost elements define the span and never move; everything between them is
respaced. `spread` chooses what is made equal:

- `centers` (default) — equal centre-to-centre distance. Predictable, and what you want
  for a column of same-size tags.
- `gaps` — equal clear space between bounding boxes. This is what the eye reads as
  evenly spaced when the elements are different widths.

`spacingMm` replaces the span with a fixed step: the lowest element stays put and the
rest are laid out from it. That is the only way to distribute exactly two elements —
without it, both are extremes and nothing moves.

Distributing sorts by current position along the axis, not by the order the elements
were passed in, so selecting bottom-to-top gives the same layout as top-to-bottom.

With `spread=gaps`, elements that together are wider than the span produce a negative
gap — they overlap. That is reported as a warning with the overlap, rather than
silently overflowing.

## What gets measured

`anchor` decides what "the element" means:

| `anchor` | Measured |
|---|---|
| `auto` (default) | The bounding box, except for tags and text notes with a leader — those use their anchor point |
| `boundingBox` | Always the element's bounding box in the view |
| `origin` | Always a single point: tag head, text insertion point, viewport centre, location point, or the midpoint of a location curve |

The exception matters. A leader is part of an annotation's bounding box, so a tag with a
leader running two metres back to its host measures as two metres wide, and `left` would
line up leader tails instead of tag heads. `auto` measures those from the head, which
collapses every edge mode to the same answer — exactly what "line the tags up" means.

The response reports `anchor` per element and an `anchorNote` whenever it differs from
what was asked for, so a surprising result is traceable.

An element with no graphics in the view falls back to its anchor point with a note.
Only an element with neither is reported as unmeasurable and left alone.

## Arguments

| Argument | Meaning |
|---|---|
| `elementIds` / `useSelection` | What to align (at least 2, max 500) |
| `mode` | Required — see the mode table |
| `alignTo` | `extreme` (default), `first`, `last`, `min`, `max`, `average` |
| `referenceElementId` | Align everything to this element; it stays put |
| `spread` | `centers` (default) or `gaps` — distribute modes only |
| `spacingMm` | Fixed distribute step; 0 fills the current span |
| `anchor` | `auto` (default), `boundingBox`, `origin` |
| `viewId` | The view or sheet to work in; defaults to the active view |

## Which view

Everything is measured and moved in one view. Annotation owned by a different view is
dropped with a warning naming that view — a slide measured here means nothing there.

Model elements are fair game and move in the model, not just in this view: aligning
sockets in a plan moves them in the world XY plane. That is usually the point, but it is
a real edit to the model rather than to the drawing, and the response flags each element
with `viewSpecific: false`.

Sheets work the same way as any other view — pass a sheet as `viewId`, pass viewport IDs
as `elementIds`, and viewports line up on the sheet. Passing a view ID rather than its
viewport ID is caught and explained.

A perspective 3D view is refused: screen position there is not a plane the model can be
moved in. An orthographic 3D view is allowed with a warning, because the screen plane
rarely lines up with anything meaningful.

## Safety

- The preview opens no transaction and changes nothing.
- Every element is measured **before** the first move. A move changes what the next
  measurement would see, and a distribute is only stable if the span is read once.
- All moves run inside one Revit transaction, one sub-transaction per element, so one
  failure does not abandon the rest. A single undo reverses the whole call.
- Slides under 0.1 mm are reported as `alreadyAligned` and skipped, so re-running a call
  is a no-op.
- Pinned elements are reported rather than force-moved.
- Tags and text notes that Revit refuses to transform — a tag hosted in a link, for one —
  fall back to setting the head or insertion point directly.

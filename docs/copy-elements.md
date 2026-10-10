# Copying Elements

Make a copy of any element Revit can copy, shifted by a translation, with Revit's own copy
operations. The originals are not touched.

There is no category whitelist and no `LocationPoint` requirement. Family instances, Detail
Items, detail and model lines, filled regions, text notes, tags, dimensions, walls, pipes,
ducts and other curve-based elements, hosted and face-based instances are all handed to Revit;
what Revit refuses comes back with Revit's reason.

To move the element itself instead, see [`move-elements.md`](move-elements.md). Views, sheets
and family types are not "copied" by a translation — they are duplicated with `revit_duplicate`.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_copy_elements` | Read-only | What would be copied, how, and what Revit would create |
| `revit_copy_elements` | Requires approval | Perform the copy |

## The request

```json
{
  "elementIds": [2104417, 2104422],
  "deltaRightMm": 1500,
  "deltaUpMm": 0,
  "atomic": true
}
```

| Argument | Meaning |
|---|---|
| `elementIds` | The elements to copy, up to 2000. Each id once |
| `useSelection` | `true` copies the current Revit selection instead of `elementIds` (not both) |
| `deltaXmm`, `deltaYmm`, `deltaZmm` | Translation along the model axes, in mm. An omitted axis is zero |
| `deltaRightMm`, `deltaUpMm` | Translation along a view's right and up, in mm. Not mixed with the model-axis deltas |
| `targetViewId` | Destination view. Omitted: view-specific elements stay in their owner view, model elements stay in the model |
| `sourceViewId` | For model elements: the view they are copied from when `targetViewId` is given, and the view whose axes `deltaRightMm`/`deltaUpMm` follow |
| `atomic` | `true` (default) — sets are copied together and any refusal undoes everything. `false` — element by element |
| `dryRun` | Preview only. `true` (default) lets Revit try the copy in a transaction that is rolled back |

A translation of zero is allowed: the copies land on their originals, and Revit warns about
identical instances in the same place. Across views it is the normal case.

## Which Revit operation is used

Elements are sorted into sets, and each set goes to Revit in one call:

| Elements | `targetViewId` | Revit call | Result |
|---|---|---|---|
| Model elements | not given | `CopyElements(document, ids, document, transform, options)` | Copies in the model, shifted; hosted elements are rehosted where Revit can |
| Model elements | given, with `sourceViewId` | `CopyElements(sourceView, ids, destinationView, transform, options)` | Copies repositioned for the destination view — a different level, for example |
| View-specific elements | not given | the same view-to-view call with their owner view as both ends | Copies in the same view |
| View-specific elements | given | view-to-view, owner view → `targetViewId` | Copies in the other view |

Each view owns its own set, so a request can mix elements from several views and model elements;
the response lists the sets under `batches` with the call that was made.

The document-to-document form is used rather than the simple translation overload because it
rehosts: a copied door looks for a wall at its new position instead of failing or staying
attached to the old one.

## Views

Both ends of a view-to-view copy must be 2D views that can hold detail elements: floor and
ceiling plans, sections, elevations, drafting views. This is Revit's rule, checked with Revit's
own compatibility test before anything is attempted, and a refusal quotes Revit's message. In
particular:

- a 3D view, a schedule, a sheet or a legend cannot be the source or the destination;
- a view template cannot be either;
- a drafting view cannot receive model elements;
- an element owned by one view cannot be copied "from" another.

The translation of a view-to-view copy must lie in the destination view's plane. One with a
component along the view normal larger than 0.01 mm is rejected with the plane's normal in the
message. `deltaRightMm`/`deltaUpMm` follow the destination view's axes and are always in plane;
how those axes map to model X/Y/Z is described in
[`move-elements.md`](move-elements.md#displacements-and-view-axes).

For model elements copied in the model, `deltaRightMm`/`deltaUpMm` need `sourceViewId` to say
whose axes are meant. Without it the request is rejected rather than assuming the active view.

## What comes back

```json
{
  "copied": 2,
  "copies": [
    { "sourceId": 2104417, "copyId": 2110031, "mapping": "Position" },
    { "sourceId": 2104422, "copyId": 2110032, "mapping": "Position" }
  ],
  "newElementIds": [2110031, 2110032, 2110033],
  "dependents": [
    { "elementId": 2110033, "category": "Detail Item Tags", "elementClass": "IndependentTag" }
  ],
  "batches": [ { "operation": "View", "sourceViewName": "Kilbiskeem", "elementCount": 2 } ],
  "elements": [ { "elementId": 2104417, "status": "Copied", "copyId": 2110031, "copyPointMm": { } } ]
}
```

- `copies` — the source→copy pairs.
- `newElementIds` — every element Revit created, including dependents.
- `dependents` — what Revit copied along that no source asked for: tags' leaders, sketch lines,
  nested parts. Described, never silently dropped.
- `elements` — one entry per requested element with its status, reason, host, group and notes.

### How sources are paired with copies

Revit's copy calls return a flat list of new ids, not pairs. The pairing is worked out from what
the elements are and where they ended up, and `mapping` says how sure it is:

| `mapping` | Meaning |
|---|---|
| `Position` | A new element of the same class, category and type sits exactly at source + translation |
| `Order` | Sources and new elements of the same kind were equal in number and paired in id order (used across views, where Revit repositions the copies) |
| `Single` | The element was copied on its own |

When neither works — for example three new elements of a type for two sources — no pair is
invented: the element is `Copied` with a note, and its copy is among `newElementIds`.

### Hosts, groups and pins

- **Hosted, face-based and work-plane-based instances** are copied with rehosting. Each entry
  reports the source `hostId` and the copy's `copyHostId`, and a note when they differ. If there
  is nothing to host the copy at the new position, Revit refuses and the entry is `Failed` with
  Revit's message.
- **Group members** are handed to Revit like any other element, and the entry carries a note
  that only that member — not the group — was asked for. To copy the whole group, copy the group
  instance; it has its own id.
- **Pinned elements** can be copied — copying does not move the original. The entry notes that
  the source is pinned.
- **Tags and other annotations that reference an element** are best copied in the same request
  as that element, with `atomic=true`, so Revit copies them as one set. What a tag copied on its
  own refers to is Revit's decision; the entry reports the copy's `copyHostId`.

## Outcomes

| `status` | Meaning | Counts as a failure |
|---|---|---|
| `Ready` | Would be copied (preview) | No |
| `Copied` | Copied | No |
| `Missing` | No element with that id | Yes |
| `UseOtherTool` | A view, sheet or family type — the reason names `revit_duplicate` and the entity | Yes |
| `Unsupported` | The combination cannot work: incompatible views, a translation off the view plane, no view to take axes from | Yes |
| `Failed` | Revit refused the copy; the reason is Revit's message | Yes |
| `RolledBack` | Copied, then undone because the atomic request failed | Yes |
| `NotAttempted` | Copyable, but the atomic request was rejected before it started | Yes |

Nothing is ever left out without an entry: an element that is not copied always says why.

## Atomic and non-atomic

`atomic=true` (the default): each set is copied **together**, which is what keeps the
relationships between the copies — a tag with its element, a hosted family with its host. If any
element cannot be copied, or Revit refuses any set, the whole request is undone. When the problem
is visible before the transaction opens, the transaction is never started and everything
copyable is `NotAttempted`. A set Revit refuses is refused as a whole, so every element in it is
`Failed` with the same message; run with `atomic=false` to find the one responsible.

`atomic=false`: elements are copied **one at a time**, each in its own sub-transaction, and
whatever succeeds is kept. Because each element is copied alone, the copies are not made as a
set, so relationships between the requested elements are not carried over to their copies.

Either way it is one Revit transaction named `Revit MCP - Copy Elements`, and a single undo
removes every copy.

## The preview

`revit_preview_copy_elements` does the same resolution and checks, and then — unless
`dryRun=false` — asks Revit to perform the copy inside a transaction that is always rolled back.
That trial run is the only way to know what Revit will accept and how many dependents it brings
along. It reports `wouldCreate` (element and dependent counts by category) and per-element
failures with Revit's reasons.

The trial changes nothing: the transaction never commits, nothing reaches the undo stack, and no
ids from it are reported, since they no longer exist. Warnings Revit raises only on commit
(identical instances in the same place) appear when the copy is really made.

The trial is skipped, and says so in `trialNote`, when the request would be rejected anyway
(`atomic=true` with an uncopyable element), when the document is read-only, or when a transaction
is already open.

## Copying a Detail Item

1. Get the Detail Item's id (`revit_get_selected_elements`, or a query).
2. `revit_preview_copy_elements` with `{"elementIds": [<id>], "deltaRightMm": 1500}`.
3. `revit_copy_elements` with the same arguments.
4. `copies[0].copyId` is the new Detail Item; `elements[0].copyPointMm` is where it is.

Into another view: add `"targetViewId": <view id>`. With no delta the copy lands at the matching
position in that view.

## Safety

- The originals are never modified, moved or unpinned.
- Views, sheets and types are turned away with the name of the tool that handles them, not
  half-copied.
- A copy is never shifted off its destination view's plane.
- The pairing of sources and copies is reported with its basis, and is left out rather than
  guessed.
- Requests over 2000 elements are rejected rather than truncated.

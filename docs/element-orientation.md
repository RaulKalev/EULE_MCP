# Pin, unpin and flip family instances (#162)

Group `edit`. Every write has a read-only preview with the same arguments; the write needs approval in Revit, runs in one
transaction (one undo step) and returns counts plus up to `sampleSize` elements (blocked ones first).

| Tool | Permission | What it does |
|---|---|---|
| `revit_preview_set_pin_state` / `revit_set_pin_state` | ReadOnly / RequiresApproval | `pinned=true\|false` on `elementIds[]` or `useSelection` |
| `revit_preview_flip_family_instances` / `revit_flip_family_instances` | ReadOnly / RequiresApproval | `flipFacing` and/or `flipHand` on family instances |

Common arguments: `elementIds`, `useSelection`, `atomic` (default false), `sampleSize`, `document`.

## Statuses

`ready`, `noOp` (already in the wanted pin state), `notFound` (also linked-model ids), `notAnInstance` (a type),
`grouped` (member of a group; pin/flip the group or edit it), `ownedByOther` (workshared, checked out by someone else),
`notFamilyInstance`, `cannotFlip` (`CanFlipFacing` / `CanFlipHand` false for the requested axis), `pinned` (flip only),
`failed`, `rolledBack` (atomic).

## Flip

- Uses `FamilyInstance.flipFacing()` / `flipHand()` — not a rotation or mirror (use `revit_rotate_elements` /
  `revit_mirror_elements` for those). The host is kept.
- A pinned instance is **never** unpinned silently. `unpinToFlip=true` unpins, flips and pins it again; the approval text
  says how many elements this applies to.
- Each flip is verified: `FacingFlipped` / `HandFlipped` changed, host unchanged, location moved by at most 0.5 mm.
  Otherwise that element is undone (its own sub-transaction) and reported with the reason.
- `atomic=true`: if any flip fails, the whole transaction is rolled back; otherwise failures are per element.

## Pinned elements and the other write tools

Move (`revit_move_elements`) and mirror (`revit_mirror_elements`) report pinned elements; `skipPinned` decides whether they
are skipped or refused. They never unpin. Copy and group tools refuse pinned members. Use `revit_set_pin_state` first
when you really want to move a pinned element, and pin it again afterwards.

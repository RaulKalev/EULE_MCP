# Model datums: levels, grids, reference planes (#164)

Group `edit`. Millimetres everywhere, host internal coordinates. Writes go to the host document only; datums of linked
models are never edited (their ids are reported as `notFound`).

| Tool | Permission | Purpose |
| --- | --- | --- |
| `revit_list_datums` | ReadOnly | Levels (`elevationMm`, `projectElevationMm`), grids (`startMm`, `endMm`, `lengthMm`), reference planes (`bubbleEndMm`, `freeEndMm`, `normal`), ids, pinned flags. Filters `kinds`, `nameFilter`, `limit`. `revit_list_levels` is unchanged and still maps a link's levels to host levels. |
| `revit_preview_create_datums` / `revit_create_datums` | ReadOnly / RequiresApproval | Create `levels [{name, elevationMm}]`, straight horizontal `grids [{name?, startMm, endMm}]`, `referencePlanes [{name?, viewId, bubbleEndMm, freeEndMm}]`. |
| `revit_preview_edit_datums` / `revit_edit_datums` | ReadOnly / DestructiveRequiresManualApproval | `edits [{id, newName?, elevationMm? (levels), moveByMm? (grids, reference planes)}]`. |

## Safety rules (pure code in `RevitMCP.Addin/Datums/DatumPlanner.cs`)

Create refuses: invalid names (empty, `\ : { } [ ] | ; < > ? \` ~`, leading/trailing space), names already in use or
repeated in the call (case-insensitive), a level within `elevationToleranceMm` (default 1) of an existing or just-planned
level (`allowEquivalentLevels=true` overrides), values beyond ±1 000 000 mm (a unit mistake), grids or reference planes
shorter than 5 mm, non-horizontal grids, reference-plane lines parallel to the view direction, views that are missing,
templates or of a type that cannot hold a reference plane (3D, sheet, schedule, legend, drafting), and a grid or
reference plane identical to an existing one (`allowDuplicateGeometry=true` overrides).

Edit refuses: unknown ids and non-datums, pinned datums being moved, datums checked out by another user, name
conflicts (with the model or another edit), a level moved onto another level's final elevation (so swapping two levels
is fine), `elevationMm` on a grid or `moveByMm` on a level, and two edits of one datum.

## Impact

The edit preview reads what depends on each datum: elements with that level (`ElementLevelFilter`), plan views generated
from it, and every other dependent element (dimensions, tags, ...), with the top categories. Impact is `none` (nothing
changes), `low` (rename; move with no dependents under 1 m) or `high` (a move with dependents, or of 1 m or more). A high
impact change is refused unless `confirmHighImpact=true`. A move that passes another level reports a level-order warning.
A rename warns when views are associated with the datum (view names and schedule/filter rules that use the name are not
renamed).

## Modes and results

`mode=allOrNothing` (default): any refused item stops the call and nothing is written. `mode=perItem`: items that pass
are written, each in a sub-transaction so a Revit failure undoes only that item. One transaction ("Revit MCP - Create
Datums" / "Edit Datums"), one undo step. Results list every item with `status`, `impact`, `problems[{code, message}]`,
`warnings` and, after a write, the new or changed element ids (`created` / `changed`).

## Not included

No plan views are created for a new level: use `revit_create_views`. Curved grids, multi-segment grids, editing grid
endpoints (only translation), reference plane rotation and 3D extents/scope boxes are follow-up scope. Use
`revit_checkpoint_create` before an edit to be able to roll back.

# BCF Clashes: Read, Match, Re-check and Fix

Clash reports from Navisworks, Solibri, BIMcollab, Revizto or ACC arrive as BCF files. These tools read
the file, find each topic's elements in the open model and its links, test again whether they still
clash, and propose moves that clear the clashes. A proposed move is applied only after a check in the
model. Writing an updated BCF back is out of scope for now.

## Tools

| Tool | Permission | Purpose |
|---|---|---|
| `revit_bcf_read` | Read-only | Topics of a BCF file (2.0, 2.1, 3.0), or one topic in full; optional snapshot extraction. No model needed |
| `revit_bcf_check` | Read-only | Match topics to elements (host and loaded links), re-test each clash with the live solids, save them as the clash run `BCF-0001 …` |
| `revit_propose_clash_fixes` | Read-only | For clashes of the clash run: which element moves, in which direction, how far, and whether that creates new clashes |
| `revit_apply_clash_fixes` | Requires approval | Move the elements and verify each move; a move that fails a check is undone |

All of them are in the `coordination` group. Find them with `revit_tools_search "bcf"` (they are not in
the `core` profile).

## Workflow

```text
revit_bcf_read path=C:\...\clashes.bcfzip                    → topics, statuses, priorities
revit_bcf_check path=C:\...\clashes.bcfzip                   → still_clashing / cleared / not_found per topic
revit_focus_clash clashId=BCF-0003                           → look at one (approval: it sets up a review view)
revit_propose_clash_fixes                                    → options per clash + a ready "fixes" array
revit_apply_clash_fixes fixes=[...]                          → approval, then move + verify each fix
revit_bcf_check path=...                                     → confirm
```

For a list the user should go through, publish the `revit_bcf_check` result with `revit_publish_page`
(one row per topic, `data-revit-id` on the host element).

## Matching BCF elements to Revit

A BCF viewpoint lists components by **IFC GlobalId** (`IfcGuid`), often with the authoring tool's own id
(`AuthoringToolId`, the Revit ElementId when the IFC came from Revit).

1. **AuthoringToolId confirmed by the GlobalId** (`ifcGuid+authoringToolId`, High): cheap, tried first in
   the host and every loaded link.
2. **GlobalId** (`ifcGuid`, High): the host and each loaded link are indexed once per call. An element's
   GlobalId is its stored `IfcGUID` parameter when the IFC export stored one, else the compressed
   `ExportUtils.GetExportId`, which is the value Revit's IFC exporter writes by default.
3. **AuthoringToolId only** (`authoringToolId`, Low): only when the id exists in exactly one model. Ids
   repeat between models, so check these matches by eye.

What is matched as "the clash elements": the viewpoint's **selected** components. When nothing is
selected, it uses the components shown against a hidden model (`DefaultVisibility=false`, at most 10),
then the coloured ones (at most 10).

Unloaded links are named in the warnings. Their elements show as `not_found`.

## Re-check results (`revit_bcf_check`)

| check | Meaning |
|---|---|
| `still_clashing` | Both elements found; their solids still intersect by more than a cube of `toleranceMm` (default 5 mm). The volume is reported |
| `cleared` | Both found, no longer intersecting: someone already fixed it |
| `unknown` | Both found, Revit could not intersect the solids |
| `partial` / `*_partial` | Only some of the topic's elements were found |
| `not_found` | None found |
| `single_element` / `no_components` | The topic is not about a pair of elements |

Topics with two matched elements are saved as the document's **clash run**. They are written with
the host element first, as `Source`, and get the status `Active`, `Resolved` or `Unknown`. The save
replaces the last detection run, and a warning says so. After that, the existing review tools work
on them: `revit_focus_clash`, `revit_select_clash_elements`, `revit_get_adjacent_clash` and
`revit_export_clash_report_to_excel`. The focus and section box now also handle a clash where both
elements are in links.

## Proposing fixes (`revit_propose_clash_fixes`)

**Which element moves.** It must be in the host model. It must be a straight run segment (cable
tray, conduit, duct or pipe) or a family instance placed at a point. These never move:
- walls, floors, roofs, ceilings, doors, windows, stairs and structure
- linked, pinned and grouped elements
- fittings and flexible ducts and pipes

When both elements qualify, the one with fewer joined ends moves, and on a tie the smaller one. Pass
`moveElementId` to choose it yourself.

**Directions.**

| Mover | Directions |
|---|---|
| Horizontal run | `up`, `down`, `side+`, `side-` (square to the run) |
| Vertical run | `+x`, `-x`, `+y`, `-y` |
| Instance | `up`, `down`, `+x`, `-x`, `+y`, `-y` |
| Wall- or face-hosted instance | Only those within the host's plane |

Restrict them with `directions`, for example `["vertical"]`.

**Joined runs.** A segment joined at its ends moves only along the runs it is joined to through its
fittings. Those runs then stretch or shorten. For example, a horizontal tray between two vertical
drops may move up or down. Any other move would slope the neighbours, so the direction is ruled out
with the reason.

If no direction is left, the status is `manual` with a reroute suggestion: offset the run with
fittings around the obstacle by hand, or draw a new run with `revit_create_mep_run`. Instances joined
to runs are not moved.

**Distance.** For each direction, the shortest travel that clears the obstacle is found by
intersecting the real solids, not bounding boxes. It searches by bisection between 0 and the travel
that passes the obstacle completely. Then:
- `clearanceMm` (default 25) is added
- the result is rounded up to `stepMm` (default 10)
- anything over `maxShiftMm` (default 1000) is ruled out

**New clashes.** The moved solid is searched against the host and every loaded link. Elements it
already touched before the move are not counted. Neither are its host, its fittings and the joined
runs. The result has `newClashes`, and `alsoClears` for other clashes the move happens to remove.

Options are ranked by fewest new clashes, then shortest travel. The `fixes` array holds the best
option of every clash that creates no new clash, ready for `revit_apply_clash_fixes`. Pick other
options from `options[].fix`.

## Applying fixes (`revit_apply_clash_fixes`)

Each fix is `{elementId, deltaXmm, deltaYmm, deltaZmm, obstacleElementId, obstacleLinkInstanceId?, clashId?}`.
All fixes run in one transaction, which is one Revit undo, and each fix runs in its own
sub-transaction. After the move and a regenerate, each fix is checked:

| Status | Rule | Default |
|---|---|---|
| `still_clashing` | The element still intersects the obstacle | Undone (`keepIfStillClashing=true` keeps it) |
| `connections_broken` | A joined end came apart, or a joined run changed direction (it would be sloped) | Always undone |
| `new_clashes` | The element or the runs that followed it run into something they did not touch before | Undone (`keepIfNewClashes=true` keeps it) |
| `fixed` | All checks passed | Kept |

Before the move, it also refuses elements that cannot move (`not_movable`), elements that are gone
(`not_found`) and zero moves (`invalid`).

Clashes that were fixed are marked `Fixed` in the clash run. To review the changes, wrap the call in
a checkpoint (`revit_checkpoint_create` / `revit_checkpoint_diff`). For many fixes, use a plan
(`revit_plan_submit`).

## Coordinates and limits

- BCF cameras and clipping planes are in metres, in the IFC file's world coordinates. Those depend
  on how the IFC was exported, so they are reported but not used. Elements are located through the
  matched elements themselves.
- Bounding-box volumes only decide which element moves. All distances and checks use solids.
- One proposal call handles at most 20 clashes by default (`limit`). Each one costs a few dozen
  solid booleans per direction.
- Not done:
  - offsets with new fittings (a "dodge" around a beam)
  - moving a run together with its joined neighbours sideways
  - moving equipment that runs are joined to
  - writing BCF comments or statuses back

## Code

- `RevitMCP.Addin/Coordination/Bcf/`: the reader, models, IFC GlobalId conversion (pure, unit-tested)
  and the element resolver (Revit).
- `RevitMCP.Addin/Coordination/ClashFix/`: the direction, travel and ranking math (pure,
  unit-tested), solid geometry and the fix service (Revit).
- `RevitMCP.Addin/Tools/Bcf/`: the four tools. `RevitMCP.Bridge/RevitMcpTools.Bcf.cs`: the bridge methods.
- Tests: `RevitMCP.Tests/BcfReaderTests.cs`, `ClashFixMathTests.cs`.

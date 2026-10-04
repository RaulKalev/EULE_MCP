# Selected tag template manual validation

Use a disposable Revit model containing one point-based device family, several
instances, and a compatible loaded tag family. Run the read-only analysis before
each write and verify the Pending approval summary before approving.

## Baseline rotation matrix

1. Place source instances at 0°, then targets at 0°, 90°, 180°, 270°, and 37°.
2. Place the source tag 500 mm in the source host's front direction.
3. Select only the source tag and run `revit_analyze_selected_tag_template` with
   `scope=sameFamily`.
4. Confirm `localFrontOffsetMm` is approximately 500 mm and
   `localRightOffsetMm` is approximately zero.
5. Run `revit_apply_selected_tag_template` and approve it.
6. Verify every target tag is 500 mm in that target's own front direction.
7. Undo once and verify every generated tag is removed together.

Repeat with:

- front, back, left, right, diagonal, and uneven right/front offsets;
- `rotationMode=KeepViewAligned`, `FollowHost`, and `RelativeToHost`;
- normal, facing-flipped, hand-flipped, and mirrored family instances;
- same-family instances using different family types.

## Tag and leader state

Repeat the baseline for:

- leaderless horizontal and vertical tags;
- attached leaders, with and without an elbow;
- free leaders, including a moved endpoint and elbow;
- a source tag with a custom relative rotation.

Confirm free endpoint and elbow positions follow the target's local axes.
Attached leaders must attach to each target rather than copying the source
endpoint's global XYZ.

## Scope and safety

Validate `sameFamily`, `sameFamilyAndType`, `sameCategory`, `selection`, and
`explicitElementIds`. Confirm:

- source host is excluded by default;
- an existing matching tag is returned in `existingTagIds` and skipped;
- invisible, invalid, wrong-category, and unsupported elements have reasons;
- page limits truncate target details but not total counts;
- a selected linked-host tag, multi-host tag, unlocked 3D view, or ambiguous
  selection fails without model changes;
- cancellation rolls back the active write;
- a fatal transaction failure reports `retainedChanges=false`;
- a per-element creation failure does not remove successful sibling tags.

## Target view (`targetViewId`)

Both tools accept an optional `targetViewId`. The rule is still learned from the
example tag in its own view (tag type, host-local offsets, orientation, rotation,
leader); targets, existing-tag checks, visibility and the new tags then use the
target view. Validate:

- tagging a second floor plan from an example tag on the first floor creates tags
  only in the target view, with the same host-local offsets and leader state;
- the source host is an ordinary target in a separate target view;
- `targetViewId` of a missing element, a view template, a sheet/schedule/legend/
  drafting view, or an unlocked 3D view fails before any change;
- a target view looking in a different direction (e.g. a section) adds a warning;
- the response's `targetView` block names the view and `isSourceView=false`, and
  the approval summary names the target view ID.

## Invisible-target reasons

For `selection` / `explicitElementIds` targets that are not in the view, `reason`
names the cause when it can be determined, after the generic text, e.g.
`Element is not visible or taggable in the target view 'Level 2' (ID:123):
outside view range (Z=7900 mm > Top 7500 mm).` Checked causes: not a family
instance, view-specific to another view, hidden category, hidden element, later
phase, outside the plan view range (Top / View Depth, resolved from level +
offset; ceiling plans use the outermost planes), outside the active crop region.
Otherwise only the generic sentence is returned.

## Collision option

With `enableCollisionDetection=false`, positions must first reproduce the learned
rule exactly. With it enabled, overlap several targets and confirm adjusted items
report `collisionAdjusted=true`, remain on a reasonable offset from their host,
and are still included in the same one-step Undo operation.

# Transforms and groups: mirror, array, groups

Symmetric rooms, rows of luminaires, sockets per workstation and a classroom's EN layout reused
in every classroom: these tools do them in one call each instead of dozens of absolute
coordinates (#108). All coordinates are model millimetres, all angles degrees. Every write tool
has a preview, needs approval, runs in one transaction and is undone with one Revit undo.

| Tool | Permission | Does |
|---|---|---|
| `revit_preview_mirror_elements` / `revit_mirror_elements` | read-only / approval | Mirror across a plane, as copies (default) or in place |
| `revit_preview_array_elements` / `revit_array_elements` | read-only / approval | Linear or radial array: independent copies (default) or a Revit array |
| `revit_list_groups` | read-only | Model, detail and attached detail group types, instances, members |
| `revit_preview_create_group` / `revit_create_group` | read-only / approval | `doc.Create.NewGroup` with a unique name; model or detail detected |
| `revit_ungroup` | approval | `Group.UngroupMembers` for instances or every instance of a type |
| `revit_ungroup_and_delete_type` | **manual approval (destructive)** | Ungroup, then delete the group types left without instances |
| `revit_preview_place_group_instances` / `revit_place_group_instances` | read-only / approval | Place a group type at points, with rotation |

The existing `revit_move_elements`, `revit_rotate_elements` and `revit_copy_elements` act on the
**whole group** when given a group instance id (see [move-elements.md](move-elements.md) and
[copy-elements.md](copy-elements.md)); a group member is reported `InGroup` with its `groupId`.

## Mirror

```json
{
  "elementIds": [1756386, 1756390, 1756402],
  "plane": { "viewId": 312, "fromMm": { "x": 42000, "y": 18000 }, "toMm": { "x": 42000, "y": 36000 } },
  "copy": true
}
```

### The plane

Give exactly one form:

| Form | Plane |
|---|---|
| `{viewId?, fromMm {x,y,z?}, toMm {x,y,z?}}` | A line drawn in a view. The plane contains the line and the view direction. In a plan (the default when `viewId` is omitted) that is the **vertical plane through the line** — Revit's Mirror – Draw Axis. In a section a vertical line gives a vertical plane, a horizontal line a horizontal one. The line is projected onto the view plane first, so the points' z does not matter in a plan. |
| `{axis: x\|y\|z, throughMm {x,y,z}}` | The plane perpendicular to that model axis through the point: `axis: "x"` flips X about `x = throughMm.x` (a vertical plane parallel to model Y); `axis: "z"` is a horizontal plane. |
| `{pointMm {x,y,z}, normal {x,y,z}}` | An explicit plane. |

`viewId` may also be given at the top level. A plane that is **not vertical** turns elements
upside down, which for a device is almost always a mistake: it is refused unless
`allowHorizontalPlane=true`.

### Rules

- `copy=true` (default): Revit's mirror-copy; the originals stay. `copy=false` flips the elements
  in place (pinned elements are skipped with `skipPinned=true`, group members are `InGroup`).
- Revit's own check comes first: `ElementTransformUtils.CanMirrorElement` per element and
  `CanMirrorElements` per set. Refused elements are listed in `data.notAllowed` and `skipped[]`,
  never silently dropped. With `atomic=true` (default) the request is refused with status
  `mirror_not_allowed`; `atomic=false` mirrors the rest element by element.
- View-specific elements (tags, detail items, text) are mirrored in their own view, so the line
  must be drawn in that view (`plane.viewId`), and the plane must be perpendicular to the view.
- Model elements and each view's elements go to Revit in separate `MirrorElements` calls (Revit
  mirrors view-specific elements only together with elements of the same view).
- A group instance mirrors the whole group.
- Mirrored family instances get Revit's `Mirrored` state; check facing afterwards.

### Result

`{copy, mirrored | moved, plane {mode, description, originMm, normal, vertical}, newIds[],
pairs[] {sourceId, newId, mapping, fromMm, toMm}, skipped[] {elementId, status, reason},
notAllowed[], dependents[], elements[]}`. `fromMm`/`toMm` is the element's anchor (insertion
point, curve midpoint or box centre) before and after; the preview gives the reflected point and,
from a trial run that is rolled back (`dryRun`, default true), how many elements Revit would create.

## Array

```json
{ "elementIds": [2210001], "kind": "linear", "count": 6, "spacingMm": { "x": 1200, "y": 0, "z": 0 } }
```

```json
{ "elementIds": [2210500], "kind": "radial", "count": 8, "centerMm": { "x": 15000, "y": 9000, "z": 0 },
  "totalAngleDeg": 360 }
```

### Counting

`count` is the number of **members**, 2–200. With `includeOriginal=true` (default) the original is
one of them, so `count: 6` makes 5 copies; with `includeOriginal=false` it makes 6 copies. A count
outside the range is refused with status `array_count_out_of_range`.

### Linear

Exactly one of:

- `spacingMm {x,y,z}` — from one member to the next, model axes;
- `totalMm {x,y,z}` — from the original to the last copy (divided by the number of copies);
- `deltaRightMm` / `deltaUpMm` — from one member to the next along a view's right and up: the
  elements' owner view when they all belong to one, otherwise `viewId`.

View-specific elements must stay in their view's plane.

### Radial

`centerMm` (the point the axis passes through), `axis` (`x`, `y` or `z`, default `z` — a vertical
axis) and exactly one of `angleDeg` (per step, counter-clockwise) or `totalAngleDeg` (from the
original to the last copy). A full circle (`±360`) spreads all members evenly, so the last copy
does not land on the original: 8 members over 360° are 45° apart. `rotateElements=true` (default)
turns each copy with the array; `false` keeps each copy's orientation and moves the set round the
axis by the centre of its elements. For view-specific elements the axis must be perpendicular to
the view (`z` in a plan).

### Copies or a Revit array

- `asGroup=false` (default): every copy is an independent element — the agent's normal need. Each
  step goes through the copy tool's `CopyElements` (model path, or the view path for view-specific
  elements), then a radial copy is rotated about the axis with `RotateElements`. The result lists
  the new ids and the source → copy pairs of every step.
- `asGroup=true`: a Revit array (`LinearArray.Create` / `RadialArray.Create`, anchor = second
  member) whose count parameter equals `count`. It needs `includeOriginal=true`, the elements of
  one view or the model only, and, for model elements, a view (`viewId`, else the active view).
  A Revit radial array needs at least 3 members and always turns its members. Elements Revit
  cannot array (`LinearArray.IsElementArrayable`) are listed.

Collisions with existing elements are **not checked**: copies may overlap what is already there.

## Groups

### Kinds

| Kind | Category | Created by `revit_create_group` | Placed by `revit_place_group_instances` |
|---|---|---|---|
| `model` | Model Groups | All members are model elements | `doc.Create.PlaceGroup` at the point |
| `detail` | Detail Groups | All members are view-specific, in one view | By copying an existing instance (see below) |
| `attachedDetail` | Attached Detail Groups | No — Revit's UI only | No — shown with its model group |

### revit_list_groups

Per group type: `groupTypeId`, `name`, `kind`, `instanceCount`, `instanceIds` (first 50),
`memberCount` and `memberCategories` (from the first instance), `hasMixedInstances` (instances whose
member counts differ — some were edited in place), `ownerViewIds` for detail groups. Filters:
`nameContains`, `kind`. `includeMembers` (a group type name or id) also lists the member ids and
categories of its first instance.

### revit_create_group

```json
{ "elementIds": [1756386, 1756390, 1756402, 1756410], "name": "Classroom EN" }
```

The kind is detected from the elements. Mixed model and view-specific elements, or view-specific
elements of several views, are refused with status `group_members_mixed` and `offenders` (the
smaller side). Missing, pinned and already grouped elements block, each with its reason. The name
is made unique against existing group type names, case-insensitively: `Classroom EN 2`,
`Classroom EN 3`, … (default `MCP Group`); `nameChanged` says when that happened.

### revit_ungroup and revit_ungroup_and_delete_type

Give `groupIds` (instance ids) or `groupType` (a name or id: every instance). The members stay in
the model as ordinary elements; the result lists the member ids per instance. `revit_ungroup`
keeps the group types. `revit_ungroup_and_delete_type` is the destructive sibling: it then deletes
every group type of those instances that has no instance left, needs manual approval in Revit
(never Direct Edit, never covered by a plan) and reports `deletedTypes` and `keptTypes`.

### revit_place_group_instances

```json
{
  "groupType": "Classroom EN",
  "placements": [
    { "pointMm": { "x": 12000, "y": 4000 } },
    { "pointMm": { "x": 24000, "y": 4000 }, "rotationDeg": 180 },
    { "pointMm": { "x": 36000, "y": 4000, "z": 3600 }, "levelOverride": "2. korrus" }
  ]
}
```

Up to 500 placements. The group origin goes on the point and the group turns about the vertical
axis there by `rotationDeg`.

- **Model groups**: `doc.Create.PlaceGroup(point, groupType)`. A point without `z` sits on the
  nearest level's elevation (or on `levelOverride`, a level name or id); the preview reports that
  level per point, the result the group's reference level as Revit set it.
- **Detail groups**: `PlaceGroup` places model groups only, and the API thread cannot switch the
  active view, so an existing instance is **copied** with the copy tool's view path (into
  `viewId`, or in its own view) and moved onto the point within the view's plane, then rotated
  about the view normal. Each placement says `path: "copiedFromInstance"` and `sourceInstanceId`;
  a detail group with no placed instance is refused (place one in Revit first).

## Limits

- Not handled: editing group definitions (adding members to an existing type), creating attached
  detail groups, nested group analysis, assemblies, links as groups.
- Mirror pairs and array pairs are found by kind and position (or id order); a copy that cannot
  be told apart is listed among the new ids with a note, never invented.
- Mirror and array do not check collisions, and a mirrored device's facing is Revit's mirror of
  the original: check it.

# Model families: reference planes, dimensions and constraints

Part of [model-families.md](model-families.md) (#168), issue #170. Two tools with one body:

| Tool | Permission | What it does |
|---|---|---|
| `revit_preview_edit_model_family_references` | read-only | Runs the whole edit in the family and rolls it back. Reports `ready`, `problems`, what would be made, and constraints Revit refuses. |
| `revit_edit_model_family_references` | approval | Creates and edits reference planes, reference lines, dimensions and alignments, then saves or loads per `target`. |

Both take `target` (see [model-families.md](model-families.md)), `document`, and the operation lists below. Lengths are
millimetres, points `[x, y, z]` from the family origin, directions unit-less.

## Order of operations

The lists run in this order, each as one step (one transaction):

1. `remove` – element ids of reference planes, reference lines, dimensions or alignments.
2. `referencePlanes` – create, offset, edit (items run in list order, so a later item can use a plane an earlier one made).
3. `referenceLines`
4. `dimensions`
5. `alignments`

A later list can use what an earlier one created (a dimension between two new planes). When a step has a problem the
later steps are not checked (a warning says so): fix it and preview again.

## References (`ref`)

Dimensions and alignments name geometry with one shared selector format (other model-family tools accept it too):

| Form | Meaning |
|---|---|
| `"Center (Left/Right)"` | A reference plane by name (case-insensitive). A level name works too. |
| `1234` | Element id of a reference plane, a straight reference line or a level. |
| `"level"` | The family's reference level. Use it to locate geometry relative to the level. |
| `{"element": 1234, "face": "left"}` | The planar face of a form whose outward normal is closest to −X (`left`), +X (`right`), −Y (`front`), +Y (`back`), +Z (`top`), −Z (`bottom`). Must be within 1°; several such faces: the outermost. |
| `{"element": 1234, "face": [0, 0.7071, 0.7071]}` | The planar face facing that direction. |
| `{"element": 1234, "instanceReference": "centerLeftRight"}` | A reference of a nested family instance: `left`, `centerLeftRight`, `right`, `front`, `centerFrontBack`, `back`, `bottom`, `centerElevation`, `top`, `strong`, `weak`, or the name of a reference plane in the nested family. |
| `{"reference": "<stable representation>"}` | Revit's stable reference string, as returned in every dimension and alignment row. |

Every created dimension and alignment returns its references as `{description, elementId, reference}`; pass
`reference` back as `{"reference": …}` to reuse it exactly.

## `referencePlanes`

| Form | Keys |
|---|---|
| Create | `name` (required, unique), `origin` [x,y,z], `direction` (the plane's drawing direction, in the plane), `normal` or `cutVector`, `length` (default 1200, centred on origin), `view`, `isReference`, `definesOrigin` |
| Offset | `name`, `offset: {from: "<plane name or id>", distance: <mm>}` (a parallel plane `distance` along the from plane's normal, which `revit_get_model_family` lists; negative: the other side), `length`, `view`, `isReference`, `definesOrigin` |
| Edit | `plane` (name or id), `rename`, `isReference`, `definesOrigin`, `move` (mm along the plane's normal) |

- The plane contains `direction`. `normal` must be at right angles to it; `cutVector` is a second in-plane direction.
  With neither, a direction that is not vertical gives a **vertical** plane (cut vector +Z); a vertical direction needs
  `normal`. A new plane keeps the normal you asked for (it is flipped if Revit made it the other way), so `move` and
  `offset` go the expected way. Each row returns the plane's real `normal` and `offsetMm` (its distance from the origin
  along the normal).
- `view`: a view name or id, or `plan`, `front`, `back`, `left`, `right`. Default: the plan for a vertical plane, the
  Front (then Left) elevation for a horizontal one.
- Names are unique among the family's planes, case-insensitive: a clash is a problem naming the plane that has it.
- `isReference`: how a placed instance exposes the plane for snapping and dimensioning (the plane's *Is Reference*):

  | Value | Family Editor | Stored (`ELEM_REFERENCE_NAME`) |
  |---|---|---|
  | `notAReference` | Not a Reference | 12 |
  | `strong` | Strong Reference | 13 |
  | `weak` | Weak Reference | 14 |
  | `left`, `centerLeftRight`, `right` | Left, Center (Left/Right), Right | 0, 1, 2 |
  | `front`, `centerFrontBack`, `back` | Front, Center (Front/Back), Back | 3, 4, 5 |
  | `bottom`, `centerElevation`, `top` | Bottom, Center (Elevation), Top | 6, 7, 8 |

  The value is read back after the write; a mismatch is a problem quoting what Revit shows.
- `definesOrigin: true` makes the plane fix the insertion point in its direction. Revit keeps one per direction: the
  previous origin plane in that direction is cleared, and the result's `origin.changes` names it (`previous`, `now`).
  `origin.insertionPoint` describes the final insertion point.

```json
{
  "target": { "path": "C:\\fam\\ELV_Device.rfa", "overwrite": true },
  "referencePlanes": [
    { "name": "Left", "origin": [-100, 0, 0], "direction": [0, 1, 0], "normal": [-1, 0, 0], "isReference": "left" },
    { "name": "Left inner", "offset": { "from": "Left", "distance": -10 }, "isReference": "weak" },
    { "plane": "Center (Front/Back)", "rename": "Rear", "isReference": "back", "definesOrigin": true }
  ]
}
```

## `referenceLines`

| Form | Keys |
|---|---|
| Create | `start`, `end` (mm, on the work plane), `workPlane` (reference plane name/id, `"level"` (default) or `{origin, normal}`), `isReference` |
| Edit | `line` (id), `isReference` |

The line is drawn as a model line and changed to a reference line; both points must lie on the work plane.

## `dimensions`

| Form | Keys |
|---|---|
| Create | `references` (2 or more refs), `kind` (`linear` default, or `angular`), `view`, `linePoint`, `radius` (angular), `label`, `lock`, `equal` |
| Edit | `dimension` (id), `label` (a parameter name; `null` removes the label), `lock`, `equal` |

- **Linear**: the references must be parallel (planes, faces, levels) or lines at right angles to the dimension. The
  dimension runs along their common normal through `linePoint`; default: 300 mm beside the references' centre in the
  view. `view` default: a view containing that direction (horizontal normals: the plan; vertical normals: the Front
  elevation, else another elevation). The view used is in the row.
- **Angular**: exactly two planes or planar faces at an angle; the view must look along their intersection (default
  chosen). The arc (radius default 500 mm) spans the acute angle between them.
- `equal: true` (three or more references) is the EQ constraint: every segment the same length.
- `label`: a family parameter (Length for linear, Angle for angular). The parameter then drives the dimension. A
  reporting parameter can only label a single-segment dimension, and only one. A parameter with a formula is accepted
  with a warning. Revit's refusal is reported.
- `lock: true` locks the dimension at its current value (every segment of a multi-segment one). A labelled dimension
  cannot be locked.
- There is no `value`: a dimension never sets values. Label it, then set the parameter per type with
  `revit_edit_model_family_parameters`.

```json
{
  "target": { "familyDocument": "active" },
  "dimensions": [
    { "references": ["Left", "Center (Left/Right)", "Right"], "equal": true },
    { "references": ["Left", "Right"], "label": "Width", "linePoint": [0, -300, 0] }
  ]
}
```

## `alignments`

`{reference: ref (stays), element: ref (aligned to it), view?, lock? (default true)}` creates a locked alignment
(`NewAlignment`). The two must **already coincide**: a face 3 mm from its plane is a problem that says how far apart
they are. Creating geometry on the right planes is the forms tool's job; this tool then locks it.

```json
{
  "target": { "familyDocument": "active" },
  "alignments": [
    { "reference": "Rear", "element": { "element": 2345, "face": "back" } },
    { "reference": "level", "element": { "element": 2345, "face": "bottom" } }
  ]
}
```

## Conflicts and rollback

A constraint Revit cannot satisfy (two parameters with different values labelling the same pair of planes, a lock that contradicts a label) makes Revit refuse
the step. The whole edit is rolled back, nothing is saved or loaded, and the message names the step, Revit's failures
(`revitFailures` with element ids) and the request items whose elements Revit blamed:

```
Nothing was changed: … Dimensions: Constraints are not satisfied. Caused by dimensions[1] (element 2410): change or leave out that item.
```

A preview reports the same without changing anything. Warnings Revit raises in a successful edit are in
`revitFailures`; `result.failureSources` maps their element ids to request items.

## Inspection

`revit_get_model_family` gains these sections:

| Section | Rows |
|---|---|
| `referencePlanes` | id, name, isReference (`name`, stored `value`, Revit's `label`), definesOrigin, bubbleEnd, freeEnd, normal, offsetMm; `unnamed: true` for planes without a name |
| `referenceLines` | id, start, end, isReference, workPlane |
| `dimensions` | id, kind, view, references (description, elementId, stable reference), value or segments (mm or degrees), label, locked, equal |
| `alignments` | locked alignments: id, view, the two references, locked |
| `origin` | the origin-defining planes (axis, coordinate) and `insertionPoint` in words |

## Recipe: an enclosure that stays centred and keeps its mounting face

Width driven by an EQ dimension plus a labelled overall dimension (the box stays centred on the insertion point), depth
measured from the rear plane that defines the origin (the rear mounting face stays put), height from the reference
level. It assumes the parameters `Width`, `Depth`, `Height` exist (the parameters tool, #172).

1. Planes and dimensions:

```json
{
  "target": { "path": "C:\\fam\\ELV_Device.rfa", "overwrite": true },
  "referencePlanes": [
    { "name": "Left",  "origin": [-100, 0, 0], "direction": [0, 1, 0], "normal": [-1, 0, 0], "isReference": "left" },
    { "name": "Right", "origin": [100, 0, 0],  "direction": [0, 1, 0], "normal": [1, 0, 0],  "isReference": "right" },
    { "name": "Front", "origin": [0, -80, 0],  "direction": [1, 0, 0], "normal": [0, -1, 0], "isReference": "front" },
    { "name": "Top",   "origin": [0, 0, 150],  "direction": [1, 0, 0], "normal": [0, 0, 1],  "isReference": "top" },
    { "plane": "Center (Front/Back)", "rename": "Rear", "isReference": "back", "definesOrigin": true },
    { "plane": "Center (Left/Right)", "isReference": "centerLeftRight", "definesOrigin": true }
  ],
  "dimensions": [
    { "references": ["Left", "Center (Left/Right)", "Right"], "equal": true },
    { "references": ["Left", "Right"], "label": "Width", "linePoint": [0, -300, 0] },
    { "references": ["Rear", "Front"], "label": "Depth", "linePoint": [300, 0, 0] },
    { "references": ["level", "Top"], "label": "Height" }
  ]
}
```

2. The forms tool (#171) creates the extrusion: a rectangle sketched on the level between Left, Right, Rear and Front,
   extruded to Top. Say its id is 2345.
3. Lock its faces to the planes (skip the ones the forms tool already locked):

```json
{
  "target": { "path": "C:\\fam\\ELV_Device.rfa", "overwrite": true },
  "alignments": [
    { "reference": "Left",  "element": { "element": 2345, "face": "left" } },
    { "reference": "Right", "element": { "element": 2345, "face": "right" } },
    { "reference": "Rear",  "element": { "element": 2345, "face": "back" } },
    { "reference": "Front", "element": { "element": 2345, "face": "front" } },
    { "reference": "Top",   "element": { "element": 2345, "face": "top" } },
    { "reference": "level", "element": { "element": 2345, "face": "bottom" } }
  ]
}
```

4. Flex `Width`, `Depth`, `Height` (the flex tool, #176): Left and Right move symmetrically about Center (Left/Right);
   Front moves while Rear stays at y = 0; Top moves while the bottom stays on the level.

## Limitations

- Dimensions are linear or angular. Radial, diameter and arc-length dimensions are not offered (they need arc
  references this tool does not create).
- An alignment never moves geometry; the references must already coincide (0.1 mm).
- A labelled dimension cannot be locked; a reporting parameter labels one single-segment dimension.
- `isReference` integers 12–14 (Not a Reference, Strong, Weak) are Revit's stored values as known from Revit's
  behaviour, not from the API documentation; every write is read back, and the result shows Revit's own label.
- Faces of nested instances are taken from their instance geometry; prefer `instanceReference` for nested families.
- A nested-instance reference whose geometry Revit does not expose is located from the instance's axes, so the 0.1 mm
  alignment check is skipped for it (a warning says so) and Revit decides.
- Views are found by kind: the plan of the reference level, elevations by name (Front, Back, Left, Right) and else by
  the way they face, so translated templates work. A family without a plan or elevation view needs `view`.
- The same calls work in Revit 2024 and 2026; there is no API difference for these features.

## Live test

Run in Revit 2024 and in Revit 2026, on a family made from Metric Generic Model.rft (not hosted).

1. `revit_get_model_family`, sections `referencePlanes` and `origin`: the two centre planes define
   the origin; `insertionPoint` names them.
2. Preview the recipe's step 1 (`revit_preview_edit_model_family_references`): `ready: true`; then
   `revit_get_model_family` shows the family unchanged.
3. Apply step 1. Check: the planes exist with the names and `isReference` labels asked for (Revit's label in the
   row matches the table above — record any mismatch); `origin.changes` names Center (Front/Back)→Rear; the Width
   dimension is labelled and the EQ dimension shows `equal: true`.
4. Create the extrusion with the forms tool, then apply step 3 (alignments). The six alignments are listed in the
   `alignments` section with `locked: true`.
5. Alignment refused: align the extrusion's front face to `Left`. The problem says the face is not parallel; align
   `Front` to a face 5 mm off and see the distance reported.
6. Conflict rollback: add `{"references": ["Left", "Right"], "label": "Depth"}` to `dimensions` (Left–Right is already driven by Width, and Depth has another value). The edit fails, names
   `dimensions[0]`, and `revit_get_model_family` shows nothing changed (and the file is unchanged).
7. Flex Width 200 → 400 and Depth 80 → 120 (flex tool or Family Types): the box stays centred on Center (Left/Right);
   Rear stays at y = 0; Height keeps the bottom on the level.
8. Load the family, place it (not hosted), change Width and Depth of the instance's type: the enclosure stays centred
   at the insertion point, the rear mounting face stays fixed; snap a project dimension to the Left/Right references
   (strength `left`/`right`) and to Center (Left/Right).
9. Remove: remove the Rear plane with `remove`; the warnings say it defines the origin and carries dimensions. Preview
   only, then discard.
10. Reference line: create one on `"level"` with `isReference: "weak"`; it appears in `referenceLines` with that value.
11. Angular: create two vertical planes at 30° and an angular dimension between them; the value reads 30°.

# Model families: nested components, symbolic graphics, visibility

Part of [model-families.md](model-families.md) (#175). Loads and places nested families, draws symbolic and model
lines, manages subcategories and controls what shows in which view and detail level. The `target` argument, the
preview, rollback and the result envelope are described there.

| Tool | Permission | Use |
|---|---|---|
| `revit_preview_edit_model_family_components` | read only | Runs the same edit and rolls it back; `ready` and `problems` say whether it would work. |
| `revit_edit_model_family_components` | approval | Applies the edit. A failed edit changes nothing. |

`revit_get_model_family` gains the sections `subcategories`, `nestedFamilies`, `nestedInstances`, `symbolicLines`,
`modelLines` and `visibility`.

## Arguments

Lengths are millimetres, angles degrees, points `[x, y, z]` from the family origin (`[x, y]` means z = 0).

The lists run in this order: **`remove`, `subcategories`, `nestedFamilies`, `instances`, `symbolicLines`, `modelLines`,
`setSubcategory`, `visibility`**. A list that fails stops the lists after it; all problems are reported together.
A later list can name what an earlier one created, as `{"created":"instances[0]"}` (also `symbolicLines[n]`,
`modelLines[n]`), in `setSubcategory.elements` and `visibility.elements`. A reference to an item the call does not
have is a problem before anything runs.

| List | Item |
|---|---|
| `remove` | `[id, …]`: nested instances, symbolic lines or model lines. Forms, reference planes and reference lines are refused with the tool that owns them. |
| `subcategories` | `{name, lineWeightProjection?: 1-16, lineWeightCut?: 1-16, color?: [r,g,b], linePattern?: "Solid" or a pattern in the family, material?: name}`. Creates the subcategory under the family's category, or edits it when it exists. The result has `before` and `after`. |
| `nestedFamilies` | `{path: "C:\\X.rfa"}` or `{fromProject: "<family loaded in the project>"}`, plus `ifExists`: `skip` (default), `overwrite`, `overwriteParameters`. Result: id, category, placement type, `shared`, types, `status` (`loaded`, `reloaded`, `skipped`, or `unchanged` when the file equals the family already loaded). |
| `instances` | see below |
| `symbolicLines` | `{view?, curves, subcategory?, visibility?, visibleParameter?}` |
| `modelLines` | `{workPlane?, curves, subcategory?, visibility?, visibleParameter?}` |
| `setSubcategory` | `{elements: [ids], subcategory: "<name>" \| null}` for forms, symbolic lines and model lines. `null` puts them back on the family category. |
| `visibility` | `{elements: [ids], visible?, visibleParameter?: "<yes/no parameter>" \| null, detailLevels?: {coarse?, medium?, fine?}, views?: {planRcp?, frontBack?, leftRight?, whenCut?}}` |

**Curves** (`curves` of both line lists): `{"line":{"start":[x,y],"end":[x,y]}}`,
`{"arc":{"start":…,"mid":…,"end":…}}` or `{"circle":{"center":…,"radius":…}}`. Points are projected onto the plane (the
view's plane for symbolic lines, the work plane for model lines) before drawing; a line that collapses to a point,
an arc whose points are collinear and a circle under 0.1 mm are problems naming the item. `subcategory` is a
subcategory of the family's category (or the category itself, or Invisible lines); it may be one this call creates
in `subcategories`.

**Symbolic lines** are 2D and appear only in views parallel to the plane they are drawn on: `view` is `plan`
(default; the reference level's plan), `front`, `back`, `left`, `right`, a view name or id. A 3D view is refused.
**Model lines** are 3D on `workPlane`: a reference plane (name or id), `"level"` (default) or
`{"origin":[x,y,z],"normal":[x,y,z]}`.

### Instances

`{family, type?, position?, rotation?, view? | workPlane?, parameters?, associate?, typeParameter?, typeParameterInstance?, visibleParameter?, visibility?, lockTo?, name?}`

| Key | Meaning |
|---|---|
| `family`, `type` | A nested family loaded in the family (name or id; load it with `nestedFamilies` first, even in the same call). `type` defaults to the first type. The type is activated. |
| `position`, `rotation` | Origin and degrees about the placement normal (the view's, the plane's, or Z). |
| `view` | For view-based families (Generic Annotation, Detail Item, symbols): where it is placed. Default the plan. |
| `workPlane` | For work-plane-based model families: the reference plane it sits on. Level-based families need neither. Other placement types (two-level, hosted, curve-based, adaptive) are reported as a problem naming the type. |
| `parameters` | `{"<instance parameter name or BuiltInParameter>": value}`. Lengths in mm, angles in degrees, yes/no as true/false. |
| `associate` | `{"<nested instance parameter>": "<family parameter>"}`. Read back. A nested **type** parameter cannot be associated per instance; the problem says so. |
| `typeParameter` | Drives the nested type from a family-type parameter: the instance's type property is associated with it. The parameter is created for the nested family's category when missing (`typeParameterInstance`: instance instead of type, default false). Where Revit refuses (the category, or the type property), the problem says so and nothing is changed. |
| `visibleParameter`, `visibility` | As in the visibility list, applied to the instance. |
| `lockTo` | `[{reference: "<reference plane>", instanceReference: "centerLeftRight"}]`: a locked alignment between the plane and a named reference of the instance (`left`, `right`, `centerLeftRight`, `front`, `back`, `centerFrontBack`, `bottom`, `top`, `centerElevation`). Revit aligns only references that already coincide: if they do not, the problem gives the distance in mm. |
| `name` | A label echoed back in the result. |

Every instance row lists the named `references` its family offers, so a caller can also lock later with
`revit_edit_model_family_references` (`{"element": <id>, "instanceReference": "centerLeftRight"}`).

### Visibility

| Request | Applies to |
|---|---|
| `visible` | Any form, line or instance (the Visible property). Not applied while a family parameter drives it. |
| `visibleParameter` | Any of them: associates Visible with a Yes/No family parameter; `null` dissociates. Create the parameter first (parameters tool). |
| `detailLevels` | Forms, model lines, symbolic lines. At least one level must stay on. |
| `views.planRcp`, `frontBack`, `leftRight` | Model elements (forms, model lines): shown in plan/RCP, front/back, left/right views. |
| `views.whenCut` | Model elements: shown when cut in plan (`IsShownInPlanRCPCut`). Symbolic lines: shown only when cut (`IsShownOnlyWhenCut`). |

A flag that has no meaning for an element (`planRcp` on a symbolic line) is skipped with a warning. Every setting is
read back; a mismatch is a warning. A nested **instance** takes only `visible` and `visibleParameter`: the Revit API
has no detail-level or view settings for one, so a request for them is refused (set them on the nested family's own
geometry, or on the forms and lines of this family).

## Example: 3D device with a nested plan symbol

A device whose 3D forms must not show in plan, where a nested Generic Annotation symbol stands in, switchable by a
Yes/No parameter. First find the form ids with `revit_get_model_family` (sections `visibility`).

```json
{
  "target": {"family": "ELV_Device", "load": true},
  "nestedFamilies": [{"path": "C:\\Library\\Plan symbol.rfa", "ifExists": "overwrite"}],
  "subcategories": [{"name": "Plan symbol", "lineWeightProjection": 2, "color": [0, 0, 0]}],
  "instances": [{
    "family": "Plan symbol", "position": [0, 0, 0], "view": "plan",
    "visibleParameter": "Show plan symbol", "name": "symbol"
  }],
  "symbolicLines": [{
    "view": "plan", "subcategory": "Plan symbol",
    "curves": [{"circle": {"center": [0, 0], "radius": 60}}]
  }],
  "visibility": [{"elements": [1234, 1235], "views": {"planRcp": false}}]
}
```

Run `revit_preview_edit_model_family_components` with the same arguments first. The forms (ids 1234, 1235) keep
their front/back, left/right display and detail levels, so they still show in elevations and 3D views.

## Limitations

- **Nested instance visibility** is limited to `visible` and `visibleParameter` (no API for detail levels and view
  flags of a nested instance). The raw `GEOM_VISIBILITY_PARAM` bitmask is not documented and is not used.
- **Nested type selection** (`typeParameter`) depends on Revit allowing the nested instance's type property to be
  associated, which varies by category; Revit's refusal is reported, not worked around.
- **Locked alignments** to non-centre references (`left`, `front`, …) cannot be checked for distance in advance
  (only centre references are measured, from the instance origin); Revit's own refusal is reported for those.
- `fromProject` copies the project's family to a temporary `.rfa` (and deletes it); a family open in the Family Editor
  is refused. Loading a family whose category cannot be nested in this family's category is refused by Revit.
- Symbolic lines take no per-curve subcategory (one per group), and only line, arc and circle curves.
- Revit 2024 and 2026: same calls on both; `FamilyManager.AddParameter(name, group, category, isInstance)` is used
  with `GroupTypeId.General`.

## Live test

1. Open a Generic Model family (or use `target.familyDocument: "active"`) and a Generic Annotation family to nest
   (a circle and a tag-like symbol). Create a Yes/No family parameter `Show plan symbol` (parameters tool).
2. `revit_get_model_family` for the form ids (sections `visibility`, `nestedInstances`).
3. Run the example above as a preview: `ready: true`, instance and form rows present, nothing changed in the family.
4. Run `revit_edit_model_family_components` with `load: true`. Check the result: `nestedFamilies[0].status`,
   `instances[0].visibleParameter` and `references`, `symbolicLines` geometry, `visibility` rows with `planRcp: false`.
5. `revit_get_model_family` sections `nestedInstances`, `visibility`, `subcategories`: values match.
6. In the project, place the loaded family. With `revit_export_view_image` of a plan view: only the symbol shows. Of a
   3D view and an elevation: the forms show, the plan symbol does not.
7. Toggle `Show plan symbol` on the instance: the symbol appears and disappears in plan.
8. Reload the family (edit `ifExists`/`load` again) and repeat steps 6-7: the same representation after reloading.
9. `typeParameter`: nest a family with two types, `{"family": "...", "typeParameter": "Nested Type"}`, then set that
   parameter per type in the family and check the nested type follows; try a category where Revit refuses and read
   the problem.
10. `lockTo`: nest a family that has a "Center (Left/Right)" reference, place it on the origin, lock to the family's
    `Center (Left/Right)` plane; then with the instance 10 mm off, read the distance in the problem.
11. `remove` the instance and the symbolic lines by id; confirm they are gone from the inspection sections.

# Model family materials

Create and edit the materials of a loadable family, set the Material property of a form directly, or drive it from a
material family parameter, and give that parameter a default per family type (#173, part of #168). Read
[model-families.md](model-families.md) first: it defines `target`, the preview, rollback and the envelope every
model-family tool shares.

The tools are in the `familyeditor` group (`--tool-groups familyeditor`, or find them with `revit_tools_search`).

| Tool | Permission | Purpose |
|---|---|---|
| `revit_preview_edit_model_family_materials` | Read-only | Runs the edit in the family and rolls it back: `ready`, `problems`, and what would be created, assigned, associated and defaulted |
| `revit_edit_model_family_materials` | Requires approval | The same edit, kept; then the `target` decides whether it is saved and loaded |

Inspect the result with `revit_get_model_family` (sections `materials` and `materialAssignments`, below).

## Arguments

`target` and `document` are described in [model-families.md](model-families.md). The four lists run **in this
order**, so a later list can name what an earlier one made (a material from `materials`, a family parameter from
`associate`):

1. `materials`
2. `assign`
3. `associate`
4. `defaults`

If a step reports a problem, the steps after it are skipped (they would only repeat the missing name), and the whole
edit is rolled back. Unknown keys are problems.

### `materials`: create or edit materials

| Key | Meaning |
|---|---|
| `name` | Required. Matched against the family's materials by exact name. An existing material is edited; a new name creates one. Revit does not allow `{ } [ ] \| ; < > ? \` ~`. |
| `color` | `[r, g, b]`, whole numbers 0-255. |
| `transparency` | Whole number 0-100. |
| `shininess` | Whole number 0-128. |
| `smoothness` | Whole number 0-100. |
| `materialClass` | Text, for example `"Plastic"`. |
| `duplicateFrom` | Name of a material in the family to duplicate; the new material then gets the other properties. |
| `copyFromProject` | `true`: copy the material called `name` from the project (`document`, default the active one) into the family. Not together with `duplicateFrom`. |

If the family already has the exact name, `duplicateFrom` and `copyFromProject` are ignored with a warning and only the
given properties are applied. A name that differs from an existing material only in capitalisation (`steel` next to
`Steel`) is created, with a warning: Revit allows both, but they are easily confused.

### `assign`: set a form's material directly

`[{elements: [ids], material: "<name>" | <element id> | null, replaceAssociation?: bool}]`

Sets the form's Material property (`MATERIAL_ID_PARAM`). `null` is By Category. A form whose Material is currently
driven by a family parameter is a problem that names the parameter; `replaceAssociation: true` disconnects it first.
The elements must have a Material property: extrusions, blends, revolutions, sweeps and swept blends do. Any other
element is a problem that names its kind.

### `associate`: drive a form's material from a family parameter

`[{elements: [ids], parameter: "<name>" | null, create?: {instance?: bool, group?: text}}]`

Associates each element's Material property with a material family parameter (read back to confirm). `parameter: null`
dissociates; the material the element has at that moment stays. When the parameter does not exist, `create` adds it as
a Material-type parameter (`instance` default `false`, `group` default `"Materials and Finishes"`); without `create` a
missing name is a problem with close names. A parameter that exists but is not a Material parameter is a problem.

A **type** parameter gives every instance of a type the same material. An **instance** parameter lets one placed
instance be overridden after the family is loaded.

### `defaults`: a material parameter's value per family type

`[{parameter: "<name>", material: "<name>" | <element id> | null, type?: "<type name>"}]`

Sets the parameter's value in the named type, or in every type when `type` is left out (`null` empties it). The tool
switches the family's current type to set each value and restores it afterwards. A type that does not exist is a
problem listing the family's types; **creating types is the family parameters tool (#172)**. A parameter driven by a
formula cannot be set. For an instance parameter, the per-type value is the material a newly placed instance starts
with.

### How a material name is resolved

Everywhere a material is named (`assign`, `defaults`, `duplicateFrom`, `copyFromProject`):

| The name | Result |
|---|---|
| matches one material exactly (case-sensitive) | used |
| differs from exactly one material only by capitalisation | used, with a warning |
| differs from several only by capitalisation | problem: ambiguous, with each candidate and its id (pass the id) |
| matches none | problem: missing, with close names and the hint to create it in `materials` |

A number is an element id and must be a material of the family.

## Result

```json
{
  "materials": [{"id": 4101, "name": "Plastic White", "status": "created", "changed": true,
                 "after": {"color": [250, 250, 245], "transparency": 0, "shininess": 64, "smoothness": 50, "materialClass": "Plastic"}}],
  "assigned": [{"element": 3320, "description": "Extrusion 3320",
                "before": {"materialSource": "byCategory", "material": null, "parameter": null},
                "after":  {"materialSource": "direct", "material": "Plastic White", "parameter": null}}],
  "createdParameters": [{"name": "Housing Material", "instance": true, "group": "Materials and Finishes"}],
  "associated": [{"element": 3345, "description": "Extrusion 3345", "parameterBefore": null,
                  "parameterAfter": "Housing Material", "materialAfter": "Steel"}],
  "defaults": [{"type": "Type A", "parameter": "Housing Material", "instanceParameter": true, "before": null, "after": "Steel"}],
  "elements": [{"id": 3320, "description": "Extrusion 3320", "materialSource": "direct", "materialId": 4101,
                "material": "Plastic White", "parameter": null},
               {"id": 3345, "description": "Extrusion 3345", "materialSource": "parameter", "materialId": 4102,
                "material": "Steel", "parameter": "Housing Material", "parameterIsInstance": true}]
}
```

`materials[].status` is `created`, `duplicated`, `copied` or `edited` (with `before` for an edit). `elements` is the
final state of every form the call touched, read after the last step. `materialSource` is `direct` (set on the form),
`parameter` (driven by the named family parameter) or `byCategory` (empty). In a preview the ids are those of the
discarded run; do not reuse them.

## Inspection sections

`revit_get_model_family` with these sections:

| Section | Rows |
|---|---|
| `materials` | Each material: `id`, `name`, `color`, `transparency`, `class`, `usedBy` (ids of forms whose Material is set directly to it), `defaultOf` (`[{parameter, types: [names]}]`: material parameters whose value in those types is it) |
| `materialAssignments` | Each form that has a Material property: `id`, `description`, `kind`, `isSolid`, `materialSource`, `material`, `parameter` (the driving family parameter), `parameterIsInstance` (true allows an override after loading) |

## Examples

Preview, then run (a loaded family, loaded back as one undo step):

```json
{
  "target": {"family": "EL_Device Housing"},
  "materials": [
    {"name": "Plastic White", "color": [250, 250, 245], "shininess": 64, "materialClass": "Plastic"},
    {"name": "Steel Brushed", "duplicateFrom": "Metal", "color": [180, 182, 186]}
  ],
  "assign": [{"elements": [3320], "material": "Plastic White"}],
  "associate": [{"elements": [3345], "parameter": "Housing Material", "create": {"instance": true}}],
  "defaults": [
    {"parameter": "Housing Material", "material": "Steel Brushed", "type": "Type A"},
    {"parameter": "Housing Material", "material": "Plastic White", "type": "Type B"}
  ]
}
```

Copying a project material and clearing a direct assignment:

```json
{
  "target": {"familyDocument": "active"},
  "materials": [{"name": "Concrete, Cast-in-Place", "copyFromProject": true}],
  "assign": [{"elements": [3320], "material": null}]
}
```

## Verifying the acceptance scenario of #173

"Two solids with independent assignments, two types with different finish defaults, and an instance material
override after loading." The family has two solid extrusions (ids 3320 and 3345) and two types, `Type A` and `Type B`
(types are made with the family parameters tool, #172).

1. **Solid A direct, solid B by an instance parameter.** One call:

   ```json
   {
     "target": {"family": "EL_Device Housing"},
     "materials": [
       {"name": "Plastic White", "color": [250, 250, 245]},
       {"name": "Steel Brushed", "color": [180, 182, 186]}
     ],
     "assign": [{"elements": [3320], "material": "Plastic White"}],
     "associate": [{"elements": [3345], "parameter": "Housing Material", "create": {"instance": true}}]
   }
   ```

   Solid A (3320) is `direct` "Plastic White"; solid B (3345) is `parameter`, driven by the instance parameter
   `Housing Material`. They change independently.

2. **Different finish defaults per type.**

   ```json
   {
     "target": {"family": "EL_Device Housing"},
     "defaults": [
       {"parameter": "Housing Material", "material": "Steel Brushed", "type": "Type A"},
       {"parameter": "Housing Material", "material": "Plastic White", "type": "Type B"}
     ]
   }
   ```

   `revit_get_model_family` section `materials` shows `Housing Material` under `defaultOf` of both materials, with
   `Type A` and `Type B` respectively. For an instance parameter this per-type value is the material a new instance of
   that type starts with.

3. **Override on one placed instance after loading.** Place an instance of each type
   (`revit_place_family_instances`), then override one instance's parameter with the existing project tool:

   ```
   revit_set_parameter  elementIds=[<instance id>]  parameterName="Housing Material"  value="Plastic White"  exactMatch=true
   ```

   Read it back with `revit_get_element_parameters` (the instance shows the override; the other instance of the same
   type still shows the type's default). The material has to exist in the project to be a value there. It travels
   with the family when a type default or a direct assignment uses it (steps 1 and 2 do), which is why "Plastic White"
   is available in this example.

## Limitations

- Materials are created from the properties above only. Appearance assets, physical assets, patterns and
  identity data are not set; `duplicateFrom` and `copyFromProject` carry whatever Revit copies with the material.
- `copyFromProject` uses `ElementTransformUtils.CopyElements` between the project and the family. What comes with
  the material (appearance asset, texture files) is Revit's decision; the tool keeps the family's own elements when a
  name collides and reports the material it found afterwards.
- Only forms (extrusion, blend, revolution, sweep, swept blend) are listed in `materialAssignments`. `assign` and
  `associate` accept any element id that has a Material property. Void forms normally do not have one.
- Materials cannot be deleted or renamed here (only created, duplicated, copied and edited).
- `defaults` changes the family's current type while it runs and restores it at the end. The values of the other
  parameters are not touched.
- The API calls used exist in Revit 2024 and 2026; there is no `#if` difference in this feature.
- A loaded family is loaded back as in [model-families.md](model-families.md): the project's own parameter values are
  kept unless `overwriteParameterValues`, so a per-type default only reaches existing instances when that is set.

## Live test

Run in Revit with a family that has two solid extrusions and two types (for example from `revit_create_model_family`
and the forms and parameters tools), loaded in a project that has no `Plastic White` material yet.

1. `revit_get_model_family` sections `materials` and `materialAssignments`: note the forms and the family's materials.
2. Preview the call of step 1 above: `ready` is true, nothing changes in the family or the project.
3. Run it (`revit_edit_model_family_materials`). `materials` lists both ids as `created`; `elements` shows solid A
   `direct` and solid B `parameter` with `parameterIsInstance: true`.
4. Run the same call again: the materials are `edited` with `changed: false`, no duplicates appear, a warning is not
   expected.
5. Try `assign` of a misspelt name (`"Plastic Whit"`): the problem lists `Plastic White` as a close name. Try
   `"plastic white"`: it is used with a warning. Create `steel brushed` and note the near-duplicate warning.
6. Try `assign` on solid B without `replaceAssociation`: the problem names `Housing Material`. Repeat with
   `replaceAssociation: true` on a copy of the family and check `materialSource` becomes `direct`.
7. Try `assign` on a reference plane id: the problem names its kind.
8. Run step 2 (defaults). `materials` shows `defaultOf` for both materials. Ask for a type that does not exist: the
   problem lists `Type A` and `Type B`.
9. Run `copyFromProject` for a project material and with a name the project lacks (a problem naming the project).
10. Load the family into the project (the `target` does it). Place an instance of Type A and one of Type B: solid B
    shows Steel Brushed and Plastic White respectively; solid A is Plastic White in both.
11. Override `Housing Material` on one Type A instance with `revit_set_parameter` and read it back: only that instance
    changes.

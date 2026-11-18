# Model family parameters, types, formulas and associations

Part of [model-families.md](model-families.md) (#168, issue #172). Create and edit family, instance, type, shared and
reporting parameters, set values per family type, set formulas, and drive an element property (a dimension, a
visibility flag, a material) from a parameter. Everything is checked before Revit is asked, and read back afterwards.

| Tool | Permission | What it does |
|---|---|---|
| `revit_preview_edit_model_family_parameters` | read only | Runs the edit in a rolled-back transaction group. Returns `ready`, `problems`, `impact` and what would happen. |
| `revit_edit_model_family_parameters` | approval | The same edit, applied. Loads/saves per `target` (see model-families.md). |

Both are in the `familyeditor` group. The `target` object, `document`, `ready`/`problems`, rollback and the result
envelope are described in [model-families.md](model-families.md).

## Arguments and order

Processed in this order, so one call can remove a parameter, add its replacement and set values:

| # | Argument | Meaning |
|---|---|---|
| 1 | `associations` with `familyParameter: null` | Dissociations. They run first so the parameter they held can be removed in the same call. |
| 2 | `removeParameters: [names]` | Removes family and shared parameters. Refused while a formula of a parameter that stays uses it. Labelled dimensions and associated element parameters are released, with a warning listing their ids. |
| 3 | `parameters` | Create or edit (below). Formulas that clear (`formula: null`) run here, before values. |
| 4 | `types` | Create, rename, delete family types; set values per type. |
| 5 | `formulas` | `[{parameter, formula}]`, applied after the types exist (same as `parameters[].formula`). The last entry for a parameter wins. |
| 6 | `associations` | `{element: id, parameter, familyParameter}`; also `elements: [ids]`. |
| | `sharedParameterFile` | Shared parameter file for shared parameters. Default: the config's `parameters.sharedParameterFile`, else Revit's setting (as `revit_create_detail_family`). It is only read, never written. |

### `parameters[]`

| Key | Meaning |
|---|---|
| `name` | The parameter. An existing name is an edit, a new name creates. May be left out for a shared parameter given by `guid`. |
| `dataType` | Friendly name as in `revit_create_parameters` and `revit_create_detail_family`: `text`, `multilineText`, `url`, `integer`, `number`, `length`, `area`, `volume`, `angle`, `slope`, `currency`, `yesNo`, `material`, `image`, `electricalCurrent`, `electricalPotential`, `electricalPower`, `apparentPower`, `wattage`, `numberOfPoles`, or a forge id. Needed for a new non-shared parameter. An unknown name lists the valid ones. The data type of an existing parameter cannot change (remove and add it again). |
| `group` | Group label or name (`Data`, `Text`, `Dimensions`, `Identity Data`, `Graphics`, `Electrical`, `Materials and Finishes`, …). Default: by data type. Only used when creating; Revit's API cannot change the group of an existing parameter (a warning says so). |
| `instance` | `true` for an instance parameter. Default for a new one: type. Omit on an existing one to keep it (`MakeInstance` / `MakeType`). |
| `shared` | `true`, or `{guid}`, or `false`. A shared parameter is an existing definition of the shared parameter file, found by `guid`, else by name. Its data type is the definition's. |
| `guid` | Same as `shared: {guid}`. |
| `formula` | A formula string; `null` clears it. |
| `reporting` | `true`/`false` (`MakeReporting` / `MakeNonReporting`). A reporting parameter must be an instance parameter. |
| `tooltip` | The parameter's description (shown as tooltip). |
| `rename` | New name of an existing, non-shared parameter. Formulas and labels follow in Revit. |
| `value` | Sets the current type's value. Prefer `types`. |

Giving `shared: true` for an existing family parameter of the same data type **replaces** it with the shared parameter
(`FamilyManager.ReplaceParameter`): formulas, labels and associations are kept. The result lists it under
`replacedWithShared`. A different data type is a problem.

### `types[]`

`{name, rename?, delete?: true, values?: {"<parameter>": value}, makeCurrent?: bool}`. A missing type is created.
Values are set by switching the current type; the original current type is restored at the end unless `makeCurrent`.

| Data type | Value |
|---|---|
| `length` | millimetres |
| `angle` | degrees |
| `area`, `volume` | m², m³ |
| `yesNo` | `true` / `false` |
| `text`, `url` | string |
| `integer`, `number`, `currency` | number |
| `material` | the name of a material **of the family** (or `null` for none). A missing material is a problem naming close matches; create it first with the materials tool ([model-family-materials.md](model-family-materials.md)). |
| electrical, `slope` | number in the document's display unit for that data type |

A parameter driven by a formula takes no value (clear the formula in the same call to set one). Deleting the last type
is refused by Revit and reported before anything changes; deleting the current type switches first.

### `associations[]`

`{element: <id> | elements: [<ids>], parameter: "<element parameter>", familyParameter: "<name>" | null}`. `parameter` is
the element parameter's name or its `BuiltInParameter` name (language independent), for example `MATERIAL_ID_PARAM`,
`IS_VISIBLE_PARAM`, `RBS_ELEC_VOLTAGE`. `null` dissociates. Before anything changes the tool checks that the element
exists, that the parameter can be driven, and that the data types match; a name it does not find lists the parameters
that can be associated. Each association is read back, and the result gives `before` and `after`.

## Validation (before Revit is asked)

* **Formula cycles** over the state after the edit, reported as `A → B → A`. Parameter names with spaces are matched
  longest-first; function names (`if`, `and`, `or`, `not`, `sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `sqrt`, `abs`,
  `exp`, `log`, `ln`, `pi`, `round`, `roundup`, `rounddown`), numbers with units and quoted text are skipped. An
  identifier that is none of the family's parameters is a **warning**, not a problem: Revit decides, and its refusal
  rolls the edit back with the failing step named.
* **Instance and type rules:** a type parameter's formula cannot use an instance parameter; a reporting parameter must
  be an instance parameter.
* **Specs and units:** unknown data type, a value that cannot be converted (with the expected form), a formula on a
  parameter that also gets a value.
* **Shared parameters:** see below.
* **Impact:** `impact.removed[]` and `impact.changed[]` list, per parameter, the parameters whose formulas use it
  (transitively), the dimensions it labels and the element parameters it drives.

## Shared parameters and the company file

Shared parameters are resolved from the shared parameter file and **never created** by this tool. The file is chosen
as in `revit_create_detail_family`: `sharedParameterFile`, else the project, user or company config's
`parameters.sharedParameterFile`, else Revit's setting; the result's `sharedParameterFile` says which. Revit's own
setting is restored afterwards.

* `guid` finds the definition exactly; `name` finds it by name. Use `guid` for the company file.
* Reported as problems, with nothing changed:
  * the file has two definitions with the same name and different GUIDs (ambiguous: pass `guid`; the problem lists both);
  * the family already has a parameter of that name that is shared with a different GUID (same name, different GUID);
  * the family has a non-shared parameter of that name and the data types differ;
  * `dataType` differs from the definition's;
  * the GUID is not in the file, or belongs to another name.
* After `AddParameter`, the GUID is read back (`FamilyParameter.GUID`) and compared with the definition's; a difference
  rolls the edit back.

To check a definition first: `revit_list_shared_parameters` lists the file with GUIDs.

## Inspection

`revit_get_model_family` gets three sections from this feature:

* `parameters`: every parameter with `name`, `dataType` (friendly) and `dataTypeLabel`, `group`, `instance`, `shared`,
  `guid`, `builtIn`, `reporting`, `formula`, `dependsOn` / `usedBy` (from the formula graph), `labels` (ids of
  dimensions labelled with it) and `associated` (`{elementId, element, parameter, builtIn}`); `formulaCycle` when there
  is one.
* `types`: each type with its values (mm, degrees, names) and `currentType`.
* `associableParameters`: for the ids in the `elementIds` argument, the parameters of each element a family parameter
  can drive, with `builtIn` names and what drives them now. Without `elementIds` it says so.

## Result

```json
{
  "sharedParameterFile": { "path": "C:\\EULE\\shared.txt", "source": "company config (...)" },
  "parameters": {
    "created": [ { "id": 411, "name": "Show Label", "dataType": "yesNo", "group": "Graphics", "instance": true, "shared": false, "guid": null, "reporting": false, "formula": null } ],
    "changed": [ { "name": "Width", "changes": ["instance"] } ],
    "removed": [ { "name": "Old" } ],
    "replacedWithShared": [ { "name": "Device Code", "shared": true, "guid": "5f9d3a3c-..." } ],
    "all": [ ]
  },
  "types": {
    "created": ["Wide"], "renamed": [], "deleted": [],
    "values": [ { "type": "Wide", "parameter": "Width", "before": 300, "after": 600 } ],
    "currentType": "Standard"
  },
  "associations": [ { "element": "Extrusion 90", "elementId": 90, "parameter": "MATERIAL_ID_PARAM", "familyParameter": "Body Material", "before": null, "after": "Body Material", "status": "associated" } ],
  "impact": { "removed": [ ], "changed": [ ] }
}
```

`context.Summary` is one line, for example `2 parameter(s) created, 1 type(s) added, 4 value(s) set, 3 association(s)`.

## Example

```json
{
  "target": { "path": "C:\\fam\\Device.rfa", "overwrite": true },
  "parameters": [
    { "name": "Width", "dataType": "length", "instance": false },
    { "name": "Show Cover", "dataType": "yesNo", "instance": true },
    { "name": "Cover Material", "dataType": "material" },
    { "guid": "5f9d3a3c-0f5e-4f0e-8d7e-1d2a1c3b4a5f", "instance": true }
  ],
  "types": [
    { "name": "Narrow", "values": { "Width": 300, "Show Cover": true, "Cover Material": "Steel" } },
    { "name": "Wide", "values": { "Width": 600, "Show Cover": false, "Cover Material": "Aluminium" }, "makeCurrent": true }
  ],
  "associations": [
    { "element": 2231, "parameter": "IS_VISIBLE_PARAM", "familyParameter": "Show Cover" },
    { "element": 2231, "parameter": "MATERIAL_ID_PARAM", "familyParameter": "Cover Material" }
  ]
}
```

## Limitations

* Shared parameter definitions are read, never created or edited. A group of an existing parameter is not changed
  (the API has no setter).
* Revit refuses some things only when it is asked (a formula with a unit mismatch, an association Revit cannot make).
  Those are not predicted: the step is named in `revitFailures` and the whole edit is rolled back.
* Formula parsing is name matching, not Revit's parser. A formula that uses a parameter name containing operators or
  brackets may be read wrongly; the cycle check then misses a cycle Revit would still reject.
* `NewType` makes the new type current; which values it starts with is Revit's choice. Set the values you care about.
* Built-in parameters cannot be removed, renamed, made instance/type or shared.
* Revit 2024 (net48) and 2026 (net8.0-windows) use the same calls (`ForgeTypeId` overloads); no API differences were
  needed here.

## Live test

Open a Revit project with the EULE shared parameter file configured, and a model family (for example a Generic Model)
with two solids, one dimension and one nested or void element. Use `revit_get_model_family` for ids.

1. **Create parameters.** Preview, then apply: `Width` (length, type), `Show Cover` (yesNo, instance), `Cover Material`
   (material, type). Check section `parameters`; check that the preview changed nothing.
2. **Two types.** Create `Narrow` and `Wide` with different `Width`, `Show Cover` and `Cover Material` (the materials
   must exist; create them with the materials tool first). Section `types` shows both, and `currentType` is as before.
3. **Dimension association.** Label a dimension with `Width` (the `label` argument of the reference tools,
   [model-family-references.md](model-family-references.md)); change a type's `Width` with `types.values` and check
   the geometry follows in a flex test or in the Family Editor.
4. **Visibility association.** `associations`: `{element: <form>, parameter: "IS_VISIBLE_PARAM", familyParameter: "Show Cover"}`.
   Check `associableParameters` (elementIds) lists `IS_VISIBLE_PARAM` and then shows `associatedWith: "Show Cover"`.
5. **Material association.** `{element: <form>, parameter: "MATERIAL_ID_PARAM", familyParameter: "Cover Material"}`.
6. **Across two types.** Switch the type (`makeCurrent`, or load the family and place both types): the dimension
   changes size, the form shows or hides, the material changes. Section `parameters` lists the `labels` and `associated`.
7. **Dissociate and remove in one call.** `associations` with `familyParameter: null` for step 5, plus
   `removeParameters: ["Cover Material"]`. Then try removing `Width` while another parameter's formula uses it: the
   preview must refuse and name the formula; the dimension label is listed in `impact.removed`.
8. **Formulas.** `Area = Width * Depth`; then `Width = Area / Depth` in the same family: a problem `Area → Width → Area`
   before Revit is asked. A type parameter with a formula that uses an instance parameter: a problem with the fix.
9. **Company shared parameter.** With a company definition `D` (note its GUID with `revit_list_shared_parameters`):
   add it with `{guid: "<guid>", instance: true}` and no `sharedParameterFile`; the result's `sharedParameterFile.source`
   names the config. Read the parameter back: `guid` is `D`'s. Repeat with `{name: "<name>"}` where the file holds the
   name twice with different GUIDs (or with a family that already has the name under another GUID): the preview reports
   the conflict and changes nothing.
10. **After load.** Load the family into the project (`target.load: true`), place an instance, and set a known value on
    the shared parameter with `revit_set_parameter_values` (`parameterGuid` = D's GUID). Check it appears in an EULE tag
    that uses the company definition (`revit_find_managed_tags`, or place one with `revit_place_tags`) and in a schedule
    (`revit_get_schedule`) whose field is that definition.
11. **Replace with shared.** In a family with a non-shared parameter of the same name and data type as a company
    definition, run `{name, shared: true}`: it appears under `replacedWithShared`, its formulas and labels are kept.

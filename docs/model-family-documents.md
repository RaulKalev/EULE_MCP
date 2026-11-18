# Model-family documents

Create a loadable 3D family from a template, read what a family holds, change its category and family settings, and
save as, save back, reload or load it (#169). Part of the model-family tools ([model-families.md](model-families.md)):
the `target` argument, previews, rollback and the result envelope are described there.

| Tool | Permission | What it does |
|---|---|---|
| `revit_get_model_family` | read-only | Reads a family: category, placement, settings, types, views, levels, element counts, and the sections of the other model-family tools. |
| `revit_preview_create_model_family` | read-only | Builds the new family in memory, sets category and settings, reads them back and discards it. |
| `revit_create_model_family` | approval | Creates a family from a template (default `Metric Generic Model.rft`), saves it, optionally loads it. |
| `revit_preview_edit_model_family` | read-only | Runs a category/settings change on a family and rolls it back. |
| `revit_edit_model_family` | approval | Changes the category and settings; with neither, it is the save as / reload tool. |

## The flow

1. **Create**: `revit_preview_create_model_family`, then `revit_create_model_family` with
   `target {savePath, familyName}` and the device's `category`. The result has the view and level ids for later calls.
2. **Inspect**: `revit_get_model_family target {path}` (or `{family}` once loaded, `{familyDocument}` while open).
3. **Build**: the other model-family tools (reference planes, forms, parameters, materials, connectors, …).
4. **Edit, save, reload**: `revit_edit_model_family` with `category`/`settings`, or with neither to save a copy
   (`{path, savePath}`), save back (`{path, overwrite: true}`), reload into the project (`{family}`) or load
   (`load: true`).

## revit_get_model_family

| Argument | Meaning |
|---|---|
| `target` | `{family}`, `{path}` or `{familyDocument}` (see [model-families.md](model-families.md)). Nothing is saved or loaded. |
| `include` | Section names to return (default all). An unknown name is a problem that lists the known ones. |
| `limit` | Most items per list, default 200, 1–2000. A cut list is named in `warnings`. |
| `elementIds` | Element ids that sections describe in detail (`elements.requested` has a row per id). |
| `document` | The project (for `{family}`); empty = active. |

The result always starts with `sections` (every registered section's `name` and `summary`), then one property per
included section, then `counts` (views, types, parameters). A section that fails to read is `{ "error": "…" }` with a
warning; the other sections are still returned.

Sections of this feature:

| Section | Contents |
|---|---|
| `family` | `name`, `category {name, builtIn, id}`, `placement` (FamilyPlacementType), `hostingBehavior` (the Host parameter as text), `isHosted`, `placeableWithoutHost`, `placementNote`, `workPlaneBased`, `alwaysVertical`, `shared`, `cutsWithVoids`, `roomCalculationPoint`, `partType` (PartType name) and `partTypeText` where the family has them (null otherwise), `changeableSettings`, `currentType`, `typeNames`, `path`, `revitVersion`, `isModified`. |
| `views` | `[{id, name, viewType}]`. |
| `levels` | `[{id, name, elevationMm}]`. |
| `elements` | `byClass` and `byCategory` counts of forms (Extrusion, Blend, …), reference planes and points, dimensions, connectors, nested instances, curves (ModelLine, SymbolicCurve, …), imports and text, plus `FamilyParameter` and `FamilyType`; `requested` for `elementIds`. |

Other features add their sections (reference planes, forms, parameters, types, materials, connectors, nested
components); their docs describe them.

```json
{ "target": { "path": "C:\\fam\\ELV_Device.rfa" }, "include": ["family", "views", "levels"] }
```

## revit_create_model_family / revit_edit_model_family

Both take the same arguments; the previews take them too.

| Argument | Meaning |
|---|---|
| `target` | Create: `{savePath, familyName?, templatePath?, overwrite?, load?, overwriteParameterValues?}`. Edit: `{family}`, `{path}` or `{familyDocument}` with the save options of [model-families.md](model-families.md). |
| `category` | The family category: a built-in name (`OST_ElectricalFixtures`, `OST_CommunicationDevices`, `OST_DataDevices`, `OST_FireAlarmDevices`, `OST_SecurityDevices`, `OST_NurseCallDevices`, `OST_TelephoneDevices`, `OST_LightingDevices`, `OST_GenericModel`, …), the same without `OST_`, or the category name (`Electrical Fixtures`), any case. It must be a model category of the family document. |
| `settings` | `{workPlaneBased?, alwaysVertical?, shared?, cutsWithVoids?, roomCalculationPoint?: true/false, partType?: "Normal" \| "Junction Box" \| …}`. |

Order and checks:

1. The category is set first (`Family.FamilyCategory`), because it decides which settings and parameters the family has.
   Revit's refusal (some templates fix the category) is a problem naming the current category. The category is read
   back; if it did not take, the edit fails and nothing is changed.
2. Then the settings, through the family's built-in parameters:

   | Key | Parameter | Revit label |
   |---|---|---|
   | `workPlaneBased` | `FAMILY_WORK_PLANE_BASED` | Work Plane-Based |
   | `alwaysVertical` | `FAMILY_ALWAYS_VERTICAL` | Always Vertical |
   | `shared` | `FAMILY_SHARED` | Shared |
   | `cutsWithVoids` | `FAMILY_ALLOW_CUT_WITH_VOIDS` | Cut with Voids When Loaded |
   | `roomCalculationPoint` | `ROOM_CALCULATION_POINT` | Room Calculation Point |
   | `partType` | `FAMILY_CONTENT_PART_TYPE` | Part Type (MEP categories; PartType names, spaces and case ignored) |

   A setting the category or template does not have, or has read-only, is a problem that lists the settings this
   family allows. Each value is read back; one that does not stick is a problem, and the whole edit is rolled back.
3. Parameters that the category change removed are listed in `droppedParameters` with a warning.

The result (inside the envelope's `result`):

- `categoryBefore`, `categoryAfter` (`{name, builtIn, id}`), `settings` (`[{key, before, after, requested}]`);
- `placementBefore`, `placement`, `hostingBehavior`, `isHosted`, `placeableWithoutHost`, `placementNote`: whether an
  instance can be placed without selecting a host face (true for the non-hosted `Metric Generic Model.rft`);
- `unchangedCounts` and `changedCounts`: elements by class before and after. **Only what is named is changed**: forms,
  reference planes, dimensions, parameters, types, connectors and nested families are not touched, and an unexpected
  count change is a warning;
- `views` and `levels` with their ids.

```json
{
  "target": { "savePath": "C:\\fam", "familyName": "ELV_Device", "load": true },
  "category": "OST_FireAlarmDevices",
  "settings": { "workPlaneBased": false, "shared": false }
}
```

### Save as and reload (no category, no settings)

`revit_edit_model_family` with neither argument changes nothing in the family; the target does the work:

| Target | Effect |
|---|---|
| `{path, savePath}` | Save as: a copy at `savePath`; the original file is untouched. `overwrite: true` replaces an existing file at `savePath`, otherwise an existing file is a problem. |
| `{path, overwrite: true}` | Saves the file back in the running Revit version (a warning says when that upgrades it). |
| `{family}` | Reloads the loaded family into the project as one undo step. The project's parameter values of existing types are kept; `overwriteParameterValues: true` uses the family's. `savePath` also saves a copy. |
| `{familyDocument, save: true}` / `{familyDocument, savePath}` | Saves the open family document; it stays open. After `savePath` the window edits the new file, as Save As does in Revit. If the save fails, the edit stays in the open document (one undo step) and the result is `partial`. |
| `load: true` | Loads the result into the project (`document`); a family of that name already there is reloaded (`load.status` is `reloaded`), keeping the project's parameter values unless `overwriteParameterValues`. |

Conflicts are explicit: an existing `savePath` needs `overwrite`, a file open in Revit is refused with the title to pass
as `familyDocument`, and a loaded family that is open in the Family Editor is refused. Placed instances, their instance
parameter values and the project's types are preserved by a reload; types the family no longer has stay in the project.

```json
{ "target": { "path": "C:\\fam\\ELV_Device.rfa", "savePath": "C:\\fam\\ELV_Device_v2.rfa", "load": true } }
```

## Limitations

- **Templates that fix the category or host.** Hosted templates (wall-, ceiling-, floor-, face-based) keep their host
  after a category change: `isHosted` stays true and a warning says so. For a device placed without a host face start
  from `Metric Generic Model.rft`. Some templates (for example profile, detail or annotation templates) do not accept a
  model category at all; Revit's refusal is reported.
- **Category-specific settings.** `partType` exists for MEP categories only, `roomCalculationPoint` and `alwaysVertical`
  only where the category has them; `changeableSettings` in the `family` section lists what this family allows.
  Changing category can drop parameters of the old category and reset the part type.
- **Placement type** is read from `Family.FamilyPlacementType` after the edit. Revit derives it; the tools never set it.
- **Revit 2024 and 2026.** The API members used (`Family.FamilyCategory`, `FamilyPlacementType`, `Category.BuiltInCategory`,
  the built-in parameters above, the `PartType` and `FamilyPlacementType` values) are the same in both reference
  assemblies; no version-specific code is needed. An `.rfa` saved by Revit 2026 cannot be opened by Revit 2024.
- **Section errors.** A section of another feature that throws is returned as `{error}`; the read itself succeeds.

## Live test

Run with a project open alongside (for example an EN model), in Revit 2024 and in Revit 2026.

1. `revit_preview_create_model_family target {savePath: "C:\\temp\\fam", familyName: "ELV_Test"} category "OST_FireAlarmDevices"`:
   `ready` is true, `categoryAfter.builtIn` is `OST_FireAlarmDevices`, `placement` is `OneLevelBased`,
   `placeableWithoutHost` is true. No file is written.
2. `revit_create_model_family` with the same arguments. `savedTo` is `C:\temp\fam\ELV_Test.rfa`; `unchangedCounts`
   lists the template's reference planes and `changedCounts` is empty.
3. `revit_get_model_family target {path: "C:\\temp\\fam\\ELV_Test.rfa"}`: the file is reopened from disk; `family.category.builtIn`
   is still `OST_FireAlarmDevices` (the category persisted through save and reopen), `views` and `levels` have ids.
4. `revit_preview_edit_model_family target {path: …, overwrite: true} settings {partType: "Normal", workPlaneBased: true}`:
   each requested value reads back; try `settings {partType: "Nonsense"}` and check the problem suggests names.
5. `revit_edit_model_family target {path: …, overwrite: true} category "Generic Models"` on a family built from a
   hosted template (for example `Metric Generic Model face based.rft`): `isHosted` stays true and a warning says so.
6. `revit_edit_model_family target {path: "C:\\temp\\fam\\ELV_Test.rfa", savePath: "C:\\temp\\fam\\ELV_Test_copy.rfa", load: true}`
   (no category, no settings): the copy is written, the original unchanged, `load.status` is `loaded`.
7. Repeat step 6 with `overwrite: true`: `load.status` is `reloaded`; without `overwrite` the existing file is a problem.
8. In the project: `revit_place_family_instances` with a type of `ELV_Test_copy` at a point on a level, without a
   host: the instance is placed, and its category in the project is Fire Alarm Devices.
9. `revit_edit_model_family target {family: "ELV_Test_copy"} settings {shared: true}`: the family is reloaded as one
   undo step; the placed instance keeps its position.
10. With the family open in the Family Editor, `revit_get_model_family target {familyDocument: "active"}` reads the
    open window; `revit_edit_model_family target {family: "ELV_Test_copy"}` is refused with the title to use.

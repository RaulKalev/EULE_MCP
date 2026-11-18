# Model families: acceptance runbook (#168)

One numbered run of the tracker's acceptance scenario: build a non-hosted ELV device from `Metric Generic Model.rft`,
flex it, save and reopen it, load and place both types in a test project, connect power and data, and check the
company shared parameter in a schedule. Run it once in **Revit 2024** and once in **Revit 2026** and fill in the
results table at the end.

Every call below is the exact tool call (arguments as JSON). `<…>` is a value from an earlier step's result. Each step
says what to check; record a failure with the step number, the Revit version and the result's `message`. The feature
docs explain every argument: [model-families.md](model-families.md) and the pages it links.

## Before you start

- A test project is open (for example an EN model) with a level `Level 1`, an electrical panel for power circuits and
  one for data circuits (their names are `<power panel>` and `<data panel>` below), and the EULE shared parameter file
  configured. Note the GUID of one company shared parameter (`<company GUID>`) with
  `revit_list_shared_parameters {"nameContains": "<part of its name>"}` and its name (`<company parameter>`).
- A folder `C:\temp\fam` exists and the file access policy allows it (`%USERPROFILE%` or `C:\Projects` paths work by
  default; use one of those if `C:\temp` is refused).
- A plan-symbol family file, for example `C:\Library\Plan symbol.rfa` (a detail component or generic annotation).
- Use the same tool session for the whole run; ids found in one step are used in later ones.

## Build the family

### 1. Create the family from the non-hosted template and set the ELV category (#169)

```json
revit_preview_create_model_family
{ "target": { "savePath": "C:\\temp\\fam", "familyName": "ELV_Device" },
  "category": "OST_SecurityDevices", "settings": { "workPlaneBased": false, "shared": false } }
```

Check: `ready` is true, `categoryAfter.builtIn` is `OST_SecurityDevices` (use the device's real category, for example
`OST_FireAlarmDevices`, `OST_DataDevices` or `OST_CommunicationDevices`), `placement` is `OneLevelBased`,
`placeableWithoutHost` is true. Then run the same arguments with `revit_create_model_family`: `savedTo` is
`C:\temp\fam\ELV_Device.rfa`. Note the plan view id (`views`).

### 2. Length parameters and the first type (#172)

The dimensions of step 3 are labelled with these parameters, so they come first.

```json
revit_edit_model_family_parameters
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "parameters": [
    { "name": "Width", "dataType": "length", "instance": false },
    { "name": "Height", "dataType": "length", "instance": false },
    { "name": "Depth", "dataType": "length", "instance": false } ],
  "types": [ { "name": "ELV 200", "values": { "Width": 200, "Height": 150, "Depth": 80 }, "makeCurrent": true } ] }
```

Run `revit_preview_edit_model_family_parameters` with the same arguments first (every edit step below: preview, then
apply). Check: `parameters.created` lists the three, `types.created` is `["ELV 200"]`.

### 3. Reference planes, origin, labelled and EQ dimensions (#170)

```json
revit_edit_model_family_references
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "referencePlanes": [
    { "name": "Left",  "origin": [-100, 0, 0], "direction": [0, 1, 0], "normal": [-1, 0, 0], "isReference": "left" },
    { "name": "Right", "origin": [100, 0, 0],  "direction": [0, 1, 0], "normal": [1, 0, 0],  "isReference": "right" },
    { "name": "Front", "origin": [0, -80, 0],  "direction": [1, 0, 0], "normal": [0, -1, 0], "isReference": "front" },
    { "name": "Top",   "origin": [0, 0, 150],  "direction": [1, 0, 0], "normal": [0, 0, 1],  "isReference": "top" },
    { "plane": "Center (Front/Back)", "rename": "Rear", "isReference": "back", "definesOrigin": true },
    { "plane": "Center (Left/Right)", "isReference": "centerLeftRight", "definesOrigin": true } ],
  "dimensions": [
    { "references": ["Left", "Center (Left/Right)", "Right"], "equal": true },
    { "references": ["Left", "Right"], "label": "Width", "linePoint": [0, -300, 0] },
    { "references": ["Rear", "Front"], "label": "Depth", "linePoint": [300, 0, 0] },
    { "references": ["level", "Top"], "label": "Height" } ] }
```

Check: `origin.changes` names Center (Front/Back) → Rear; `revit_get_model_family {"target": {"path": "C:\\temp\\fam\\ELV_Device.rfa"}, "include": ["referencePlanes", "dimensions", "origin"]}`
shows the planes with the `isReference` labels asked for, the Width, Depth and Height dimensions labelled and the EQ
dimension with `equal: true`; `origin.insertionPoint` names Rear and Center (Left/Right).

### 4. The other parameters, the company shared parameter and the second type (#172)

```json
revit_edit_model_family_parameters
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "parameters": [
    { "name": "Show Symbol", "dataType": "yesNo", "instance": true },
    { "name": "Housing Material", "dataType": "material", "instance": false },
    { "name": "Voltage", "dataType": "electricalPotential", "instance": false },
    { "name": "Apparent Load", "dataType": "apparentPower", "instance": false },
    { "guid": "<company GUID>", "instance": true } ],
  "types": [
    { "name": "ELV 200", "values": { "Show Symbol": true, "Voltage": 230, "Apparent Load": 15 } },
    { "name": "ELV 300", "values": { "Width": 300, "Height": 200, "Depth": 100, "Show Symbol": true, "Voltage": 230, "Apparent Load": 25 } } ] }
```

Check: `sharedParameterFile.source` names the company configuration; the shared parameter's row has `shared: true`
and `guid` = `<company GUID>`; `types` lists `ELV 200` and `ELV 300` with their values (Voltage in V, Apparent Load in
VA); the current type is still `ELV 200`.

### 5. Enclosure on the planes and a cutout void (#171)

```json
revit_edit_model_family_forms
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "extrusions": [
    { "profile": { "rectangle": { "min": [-100, -80, 0], "max": [100, 0, 0] } }, "depth": 150 },
    { "profile": { "rectangle": { "min": [-30, -80, 40], "max": [30, -80, 100] } }, "workPlane": "Front",
      "start": -15, "end": 15, "solid": false } ],
  "cuts": [ { "void": { "created": "extrusions[1]" }, "solid": { "created": "extrusions[0]" } } ] }
```

Check: `created[0].sizeCheck` is `ok`; the cut's `volumeAfter` is smaller than `volumeBefore`. Note the enclosure id
(`<enclosure>`) and the void id (`<void>`).

**Variant (#177): the enclosure from a DWG profile or from family curves.** Run it on a second copy of the family
made after step 4 (for example `C:\temp\fam\ELV_Device_dwg.rfa`), or instead of the first extrusion above, then
continue with the alignments below and step 6 using the id it created as `<enclosure>`. See
[model-family-profiles.md](model-family-profiles.md).

1. No tool imports a DWG into a family document. Open the `.rfa` in Revit, go to the plan of the reference level,
   **Insert > Import CAD** a DWG whose outline layer is a 200 × 80 mm rectangle drawn from (−100, −80) to (100, 0)
   (positioning origin to origin), then save and close the family so the path target can open it.
2. Analyse the outline (`<dwg name>` and `<outline layer>` as the import shows them):

```json
revit_get_model_family_profiles
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa" },
  "source": { "import": "<dwg name>", "layers": ["<outline layer>"] }, "workPlane": "level" }
```

   Check: `workPlane.coincidesWith.kind` is `level`; a usable loop (`<loop>`, usually 0) has `width` 200, `height` 80
   and `centre` `[0, -40, 0]`; `issues` is empty (or explain each one).
3. Preview with `revit_preview_create_model_family_form_from_profile`, then create (same `source` and `workPlane`):

```json
revit_create_model_family_form_from_profile
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "source": { "import": "<dwg name>", "layers": ["<outline layer>"] }, "workPlane": "level",
  "forms": [ { "loop": <loop>, "holes": "none", "depth": 150, "name": "Enclosure",
               "expected": { "width": 200, "height": 80, "centre": [0, -40, 0] } } ] }
```

   Check: `created[0].sizeCheck` is `ok`; `created[0].id` is the new `<enclosure>`. Make the cutout void on it with the
   forms tool (the second extrusion of step 5 with `"cuts": [ { "void": { "created": "extrusions[0]" }, "solid": <enclosure> } ]`).
4. Family curves instead of a DWG: draw the outline as model lines, then use their ids as the source and remove them:

```json
revit_edit_model_family_components
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "modelLines": [ { "workPlane": "level", "curves": [
    { "line": { "start": [-100, -80, 0], "end": [100, -80, 0] } }, { "line": { "start": [100, -80, 0], "end": [100, 0, 0] } },
    { "line": { "start": [100, 0, 0], "end": [-100, 0, 0] } },     { "line": { "start": [-100, 0, 0], "end": [-100, -80, 0] } } ] } ] }
```

   Then `revit_get_model_family_profiles` and `revit_create_model_family_form_from_profile` as above with
   `"source": { "curves": [<the four line ids from the result>] }` and `"removeSourceCurves": true`: the form is created
   and the lines are gone.

Lock the enclosure's faces to the planes (#170):

```json
revit_edit_model_family_references
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "alignments": [
    { "reference": "Left",  "element": { "element": <enclosure>, "face": "left" } },
    { "reference": "Right", "element": { "element": <enclosure>, "face": "right" } },
    { "reference": "Rear",  "element": { "element": <enclosure>, "face": "back" } },
    { "reference": "Front", "element": { "element": <enclosure>, "face": "front" } },
    { "reference": "Top",   "element": { "element": <enclosure>, "face": "top" } },
    { "reference": "level", "element": { "element": <enclosure>, "face": "bottom" } } ] }
```

Check: six rows in the `alignments` section of `revit_get_model_family`, each `locked: true`.

### 6. Materials and the material parameter (#173)

```json
revit_edit_model_family_materials
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "materials": [
    { "name": "Plastic White", "color": [250, 250, 245], "materialClass": "Plastic" },
    { "name": "Steel Brushed", "color": [180, 182, 186], "materialClass": "Metal" } ],
  "associate": [ { "elements": [<enclosure>], "parameter": "Housing Material" } ],
  "defaults": [
    { "parameter": "Housing Material", "material": "Plastic White", "type": "ELV 200" },
    { "parameter": "Housing Material", "material": "Steel Brushed", "type": "ELV 300" } ] }
```

Check: `elements` shows the enclosure with `materialSource: parameter`, `parameter: Housing Material`; `defaults`
shows both types.

### 7. Nested plan symbol and visibility (#175)

```json
revit_edit_model_family_components
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "nestedFamilies": [ { "path": "C:\\Library\\Plan symbol.rfa", "ifExists": "overwrite" } ],
  "instances": [ { "family": "Plan symbol", "position": [0, 0, 0], "view": "plan", "visibleParameter": "Show Symbol", "name": "symbol" } ],
  "visibility": [ { "elements": [<enclosure>], "views": { "planRcp": false } } ] }
```

Check: `revit_get_model_family … "include": ["nestedInstances", "visibility"]`: the symbol at `[0, 0, 0]` with
`visibleParameter: "Show Symbol"`, the enclosure hidden in plan/RCP. Note the symbol's id (`<symbol>`).

### 8. Power and data connectors with associations (#174)

```json
revit_edit_model_family_connectors
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true },
  "connectors": [
    { "name": "Power", "domain": "electrical", "host": { "element": <enclosure>, "face": "back" }, "systemType": "powerBalanced",
      "position": [0, 0, 75], "primary": true,
      "properties": { "RBS_ELEC_NUMBER_OF_POLES": 1, "RBS_ELEC_POWER_FACTOR": 1 },
      "associations": { "RBS_ELEC_VOLTAGE": "Voltage", "RBS_ELEC_APPARENT_LOAD": "Apparent Load" } },
    { "name": "Data", "domain": "electrical", "host": { "element": <enclosure>, "face": "back" }, "systemType": "data",
      "position": [0, 0, 40] } ] }
```

Check: two `created` rows with `domain: electrical`, `systemType` `powerBalanced` and `data`, `direction` `[0, 1, 0]`,
`host.face` `back`, and the Power connector's `associations` naming Voltage and Apparent Load. Note the connector ids.

## Flex, save and reopen

### 9. Flex both types (#176)

```json
revit_flex_model_family
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa" },
  "ranges": [
    { "parameter": "Width", "min": 150, "max": 400 },
    { "parameter": "Height", "min": 100, "max": 300 },
    { "parameter": "Depth", "min": 60, "max": 160 } ],
  "cases": [ { "name": "steel 200", "type": "ELV 200", "values": { "Housing Material": "Steel Brushed" } } ],
  "checks": [
    { "id": "enclosure", "kind": "boundingBox", "element": <enclosure>,
      "size": { "x": { "parameter": "Width" }, "y": { "parameter": "Depth" }, "z": { "parameter": "Height" } },
      "min": [ { "parameter": "Width", "factor": -0.5 }, { "parameter": "Depth", "factor": -1 }, 0 ],
      "max": [ { "parameter": "Width", "factor": 0.5 }, 0, { "parameter": "Height" } ] },
    { "id": "centredX", "kind": "centred", "element": <enclosure>, "axis": "x" },
    { "id": "rearPlane", "kind": "plane", "plane": "Rear", "at": { "axis": "y", "value": 0 }, "fixed": true },
    { "id": "centrePlane", "kind": "plane", "plane": "Center (Left/Right)", "at": { "axis": "x", "value": 0 }, "fixed": true },
    { "id": "rearFace", "kind": "face", "element": <enclosure>, "face": "back", "onPlane": "Rear", "fixed": true },
    { "id": "mounting", "kind": "face", "element": <enclosure>, "face": "bottom", "at": { "axis": "z", "value": 0 }, "fixed": true },
    { "id": "symbol", "kind": "symbol", "instance": <symbol>, "at": [0, 0, 0], "fixed": true },
    { "id": "housing", "kind": "material", "element": <enclosure>, "equals": { "parameter": "Housing Material" } },
    { "id": "power", "kind": "connector", "connector": "Power",
      "origin": [0, 0, { "parameter": "Height", "factor": 0.5 }], "onFace": { "element": <enclosure>, "face": "back" },
      "direction": [0, 1, 0], "domain": "electrical", "systemType": "powerBalanced",
      "values": { "RBS_ELEC_VOLTAGE": { "parameter": "Voltage" }, "RBS_ELEC_APPARENT_LOAD": { "parameter": "Apparent Load" } } },
    { "id": "data", "kind": "connector", "connector": "Data", "onFace": { "element": <enclosure>, "face": "back" },
      "direction": [0, 1, 0], "systemType": "data" },
    { "id": "cutout", "kind": "cut", "void": <void>, "solid": <enclosure> },
    { "id": "exists", "kind": "exists", "elements": [<enclosure>, <void>, <symbol>] } ] }
```

Check: `summary.refused` is 0 and every case is `ok`; `restored` is true. The insertion point and the mounting plane
(#170) hold while flexing: `rearPlane`, `centrePlane`, `rearFace`, `mounting` and `symbol` have no `fixed` failure in
any case. Record any `failedChecks` with their `expected`/`actual`/`difference`: a `power` `origin.z` failure means the
connector does not follow Height/2 (constrain it, see [model-family-connectors.md](model-family-connectors.md), or
record it as a limitation). Then `revit_get_model_family {"target": {"path": "C:\\temp\\fam\\ELV_Device.rfa"}, "include": ["types"]}`:
the current type and every value are as before this step (Undo in Revit has nothing new either).

Refusal: run the flex again with only `"ranges": [ { "parameter": "Width", "min": 0.5, "max": 1 } ]` (no `checks`).
Expected: the range cases are `refused`, `error` quotes Revit, `revitFailures[].constraints` names the dimension with
`label: "Width"`, the type cases `ELV 200` and `ELV 300` are still `ok` (a refused case does not leak into the
next), and `restored` is true. If Revit regenerates a 0.5 mm enclosure
without complaint, record the case as accepted and repeat with a value below a locked inner dimension.

### 10. Save, reopen and inspect

The file was saved by every edit. Save a copy, which reopens the family from disk:

```json
revit_edit_model_family
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "savePath": "C:\\temp\\fam\\ELV_Device_reopened.rfa" } }
```

Then read the copy from disk:

```json
revit_get_model_family
{ "target": { "path": "C:\\temp\\fam\\ELV_Device_reopened.rfa" },
  "include": ["family", "types", "parameters", "forms", "materialAssignments", "connectors", "nestedInstances", "origin"] }
```

Check: category, `placeableWithoutHost`, both types and their values, the shared parameter's GUID, the enclosure's
`cutBy` (the void), the material association, both connectors with their associations and the symbol are all there.
Run step 9 again with `"reopenCopy": true` (and without the `cases`): `reopen.status` is `compared` and `reopen.equal`
is true.

## Project

### 11. Load the family into the test project

```json
revit_edit_model_family
{ "target": { "path": "C:\\temp\\fam\\ELV_Device.rfa", "overwrite": true, "load": true }, "document": "<test project>" }
```

Check: `load.status` is `loaded` and `load.types` lists `ELV 200` and `ELV 300`.

### 12. Place both types without a host

```json
revit_place_family_instances
{ "placements": "[{\"x\": 0, \"y\": 0, \"z\": 1200}]", "familyName": "ELV_Device", "typeName": "ELV 200", "levelName": "Level 1", "document": "<test project>" }
```

```json
revit_place_family_instances
{ "placements": "[{\"x\": 3000, \"y\": 0, \"z\": 1200, \"rotationDegrees\": 90}]", "familyName": "ELV_Device", "typeName": "ELV 300", "levelName": "Level 1", "document": "<test project>" }
```

Check: one instance each, placed without picking a host; note their ids (`<i200>`, `<i300>`).

### 13. Check the placed instances (#176)

```json
revit_check_model_family_instances
{ "family": "ELV_Device", "types": ["ELV 200", "ELV 300"], "document": "<test project>",
  "checks": [
    { "id": "size", "kind": "boundingBox", "size": { "x": { "parameter": "Width" }, "y": { "parameter": "Depth" }, "z": { "parameter": "Height" } } },
    { "id": "centred", "kind": "centred", "axis": "x" },
    { "id": "notHosted", "kind": "hosted" },
    { "id": "housing", "kind": "material", "equals": { "parameter": "Housing Material" } },
    { "id": "power", "kind": "connector", "connector": "Power", "origin": [0, 0, { "parameter": "Height", "factor": 0.5 }],
      "direction": [0, 1, 0], "domain": "electrical",
      "values": { "RBS_ELEC_VOLTAGE": { "parameter": "Voltage" }, "RBS_ELEC_APPARENT_LOAD": { "parameter": "Apparent Load" } } },
    { "id": "data", "kind": "connector", "connector": "Data", "direction": [0, 1, 0], "domain": "electrical" },
    { "id": "one200", "kind": "count", "type": "ELV 200", "min": 1, "max": 1 },
    { "id": "one300", "kind": "count", "type": "ELV 300", "min": 1, "max": 1 } ] }
```

Check: both instances `ok` (the rotated one too: geometry is read in family coordinates), `missingTypes` is empty,
both counts `ok`. Note each instance's connector ids from `measurements.power.connectorId` and
`measurements.data.connectorId`. `systemType` is left out here: Revit documents the system type of an unconnected
placed connector as undefined, so it is checked after step 14 (the family's classification was checked in step 9).

### 14. Connect power and data to their systems (#174)

```json
revit_create_electrical_circuit
{ "elementIds": [<i200>], "connectorId": <power connectorId of i200>, "systemType": "PowerCircuit", "panelName": "<power panel>", "document": "<test project>" }
```

```json
revit_create_electrical_circuit
{ "elementIds": [<i200>], "connectorId": <data connectorId of i200>, "systemType": "Data", "panelName": "<data panel>", "document": "<test project>" }
```

Repeat for `<i300>`. Then step 13 again with `"connected": true` and the system type added to the `power` check
(`"systemType": "powerBalanced"`) and the `data` check (`"systemType": "data"`): both pass and
`measurements.power.system` and `measurements.data.system` name two different circuits (#174: independent power and
data connections on one device). If `measurements.power.systemType` is `powerCircuit` rather than `powerBalanced`,
Revit reports the circuit's type on a connected connector: record it. Also
`revit_get_circuit_info {"circuitId": <circuit id from the create result>, "includeElements": true, "document": "<test project>"}`
for each circuit, or `revit_get_electrical_circuits {"panelName": "<power panel>", "includeElements": true, "document": "<test project>"}`
and the same for the data panel, lists each instance once.

### 15. The company shared parameter in a schedule (and a tag)

```json
revit_set_parameter_values
{ "parameterGuid": "<company GUID>", "values": [ { "elementId": <i200>, "value": "ELV-001" }, { "elementId": <i300>, "value": "ELV-002" } ],
  "document": "<test project>" }
```

```json
revit_create_schedule
{ "name": "ELV acceptance", "category": "Security Devices", "fields": ["Family and Type", "<company parameter>"], "document": "<test project>" }
```

```json
revit_get_schedule
{ "schedule": "ELV acceptance", "include": "rows", "document": "<test project>" }
```

Check: the rows show `ELV-001` and `ELV-002` under the shared parameter's column (use the category of step 1; preview
each write first). Optionally place a tag that reads the company definition with `revit_place_tags` (`elementIds`,
`tagFamilyName`) and check its text, or find an existing one with `revit_find_managed_tags`.

## Known template, category and API limitations

Compiled from the feature docs' "Limitations" sections.

| Area | Limitation | Doc |
|---|---|---|
| Template | Hosted templates (wall, ceiling, floor, face based) keep their host after a category change; start a device placed without a host from `Metric Generic Model.rft`. Profile, detail and annotation templates refuse model categories. | [documents](model-family-documents.md) |
| Category | `partType` only for MEP categories, `roomCalculationPoint` and `alwaysVertical` only where the category has them; a category change can drop parameters and reset the part type. Placement type is derived by Revit, never set. | [documents](model-family-documents.md) |
| Category | A Generic Model category gives a warning for connectors: placed instances of that category may not join systems. Set the ELV category and part type first. | [connectors](model-family-connectors.md) |
| References | Linear and angular dimensions only; an alignment never moves geometry (references must coincide within 0.1 mm); a labelled dimension cannot be locked; `isReference` values 12–14 are from Revit's behaviour, not the API docs. | [references](model-family-references.md) |
| Forms | 3D model families only (annotation and detail families refused; conceptual mass and adaptive not covered); profile replacement is not supported in a family document; blends' vertex mapping is Revit's choice; sweeps take sketched profiles only and a path on one work plane; size checks are skipped on tilted planes; in non-conceptual families a cut is made by combining, and `unjoin` deletes the combination. | [forms](model-family-forms.md) |
| Profiles | Extrusions only; no tool imports a DWG into a family (Insert > Import CAD by hand); units and scale are reported only where Revit exposes them (use `expected`); splines and ellipses are approximated by straight segments; overlapping or crossing curves are reported, not repaired; curves that divide an outline make minimal regions, not the outline; pair checks are skipped above 4000 segments; `removeSourceCurves` also deletes curves shared with other loops. | [profiles](model-family-profiles.md) |
| Parameters | Shared parameter definitions are read, never created; a parameter's group cannot be changed; some refusals (a formula unit mismatch, an association Revit cannot make) show only when Revit is asked and roll the edit back; formula parsing is name matching; `NewType` starting values are Revit's choice; built-in parameters cannot be removed or renamed. | [parameters](model-family-parameters.md) |
| Materials | Created from colour, transparency, shininess, smoothness and class only (no appearance or physical assets); not deleted or renamed; only forms are listed in `materialAssignments` (voids normally have no Material property); `defaults` changes the current type while it runs and restores it; a reloaded family keeps the project's per-type values unless `overwriteParameterValues`. | [materials](model-family-materials.md) |
| Connectors | Hosted on planar faces of forms only; the initial position is Revit's choice and a connector is not guaranteed to be a dimension target; the host is inferred; `systemType` and `shape` of an existing connector change only where Revit accepts it; load classifications must exist in the family. | [connectors](model-family-connectors.md) |
| Components | Nested instance visibility is `visible` and `visibleParameter` only; nested type selection depends on Revit allowing the association; locked alignments to non-centre instance references cannot be checked in advance; `fromProject` refuses a family open in the Family Editor; symbolic lines take one subcategory per group and lines, arcs and circles only. | [components](model-family-components.md) |
| Flex | Bounding boxes come from tessellated solid edges (forms hidden by a visibility parameter still count); formula results and associated element values are read from the regenerated current type (Revit behaviour, verified live); `onFace` expects a connector to point out of its face; `reopenCopy` skips a document with unsaved changes and needs the file access policy to allow `%TEMP%` or the original's folder. | [flex](model-family-flex.md) |
| Instances | A placed electrical connector's system type is undefined until connected; connector values by BuiltInParameter are converted to V, VA, A, W only; a level host does not count as a host. | [flex](model-family-flex.md) |
| Revit 2024 / 2026 | The same API members are used in both versions; no version branches in any feature. `RBS_ELEC_APPARENT_LOAD` is labelled "Apparent Load" in 2024 and "Apparent Power" in 2026: use BuiltInParameter names. An `.rfa` saved by Revit 2026 cannot be opened by Revit 2024: build the 2024 run's family in 2024. | all |

## Results

Fill in pass / fail (with the failing check or message) for each step.

| Step | Revit 2024 | Revit 2026 |
|---|---|---|
| 1. Create, ELV category, non-hosted | | |
| 2. Length parameters, first type | | |
| 3. Planes, origin, labelled and EQ dimensions | | |
| 4. Parameters, company shared parameter, second type | | |
| 5. Enclosure, cutout void, alignments | | |
| 5. Variant: DWG profile / family curves (#177) | | |
| 6. Materials and material parameter | | |
| 7. Nested plan symbol and visibility | | |
| 8. Power and data connectors | | |
| 9. Flex both types; refusal reported and restored | | |
| 10. Save, reopen, inspect; reopenCopy equal | | |
| 11. Load into the test project | | |
| 12. Place both types without a host | | |
| 13. Placed instances pass | | |
| 14. Power and data in their systems | | |
| 15. Shared parameter in schedule / tag | | |

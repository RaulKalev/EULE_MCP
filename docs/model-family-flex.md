# Model families: flex tests and placed-instance checks

Part of [model-families.md](model-families.md) (#168), issue #176. Two read-only tools:

| Tool | Permission | What it does |
|---|---|---|
| `revit_flex_model_family` | read-only | Runs every family type and caller-given parameter ranges, regenerates each case, evaluates expectations and reports the cases Revit refuses with the constraints it names. Every change is undone. |
| `revit_check_model_family_instances` | read-only | Checks placed instances of a loaded family in a project against the same kinds of expectations, in family coordinates. |

Lengths are millimetres, angles degrees, points `[x, y, z]`; values use the wire units of the parameter tool
([model-family-parameters.md](model-family-parameters.md)): mm, degrees, true/false, text, material names, electrical
values in the document's display unit (V, VA).

## revit_flex_model_family

| Argument | Meaning |
|---|---|
| `target` | `{family}`, `{path}` or `{familyDocument}` (see [model-families.md](model-families.md)). No save arguments: nothing is saved or loaded. |
| `types` | Type names to exercise. Default: every type. A family without types is flexed once in its current state. |
| `ranges` | `[{parameter, min, max, steps?, types?}]`: writable parameters flexed from `min` to `max`. `steps` 2 (default, min and max) to 5 (evenly spaced). Numbers, or `false`/`true` for yes/no. `types` limits a range to some types. |
| `cases` | `[{name?, type?, values: {"<parameter>": value}}]`: explicit combinations. `type` defaults to the current type. Text and material values go here. |
| `combine` | `oneAtATime` (default): each range value alone on each type, the other parameters at the type's values. `extremes`: also all-minimum and all-maximum per type. |
| `maxCases` | Cap on the number of cases, default 60, at most 300. More is a problem that says how many were requested. |
| `checks` | Expectations evaluated after regeneration in every case (table below). |
| `reopenCopy` | `true`: after the trial, save a temporary copy, reopen it and run the type cases again; `reopen` compares the two runs per type. |

The cases are: every selected type as it is, then each range value (`oneAtATime`), then the extremes, then the explicit
cases. A duplicate (same type and values) is dropped (`duplicatesDropped`); repeated names get ` (2)`.

A parameter driven by a formula, a reporting parameter, a built-in parameter or a read-only one cannot be a range or
case input: the problem names it and why ("driven by the formula 'Width / 2'; flex the parameters the formula reads").
A material value must be a material of the family.

### How a case runs, and the two safety nets

1. Before the first case the tool records the current type and, for every type a case changes, the stored value of
   every parameter it sets (raw, in Revit's internal units).
2. Each case runs in its own **transaction group** inside the session's, holding **one transaction**: set the current
   type, set the values, regenerate, commit. Revit raises constraint failures at commit ("Constraints are not
   satisfied"), so a case must be committed to learn whether it is valid. A case Revit refuses is rolled back alone and
   reported as `refused`.
3. A committed case is measured: the checks read the regenerated family, then the case's status is `ok` or `failed`.
4. **First net:** after measuring, the case's transaction group is rolled back. That undoes the case exactly: the
   values it set, formula results, geometry Revit moved to satisfy constraints, and the current type. Every case starts
   from the original state, so results do not depend on the order of the cases. The current type and the recorded
   values are then **read back**; anything that does not read back as it was is a hard problem that stops the run.
   `restored` says whether all of this held.
5. **Second net:** the whole run happens inside a transaction group of `ModelFamilySession.Trial`, which is always rolled
   back before the tool returns, also in a family document the user has open. Nothing survives the call, even when a
   restore failed or Revit threw.

### Check kinds

Each check is `{id?, kind, …}`; `id` (default `checks[i]`) names it in failures. A number can be a literal or
`{"parameter": "<name>", "factor"?: 1, "offset"?: 0}`, evaluated against the case's parameter values after regeneration
(formula results included), so one expectation holds for every type and case. `tolerance` is in mm (default 0.5;
`parameter` checks: 0.001 in the parameter's unit). Unknown kinds and keys are problems.

| Kind | Keys | Passes when |
|---|---|---|
| `boundingBox` | `element?` (a form id; default all solid forms together), `size?: {x?, y?, z?}`, `min?: [x,y,z]`, `max?: [x,y,z]` (`null` entries are not checked), `tolerance?` | The box of the solids (from their tessellated edges) has that size / corners. |
| `centred` | `element?`, `axis: x\|y\|z`, `about?` (default 0), `tolerance?` | The middle of the box on that axis is `about`. |
| `plane` | `plane` (reference plane name or id), `at?: {axis, value}`, `fixed?`, `tolerance?` | The plane is perpendicular to `axis` at `value`; `fixed`: it is at the same place in every case. |
| `face` | `element`, `face: left\|right\|front\|back\|top\|bottom`, `at?`, `fixed?`, `onPlane?` (reference plane), `tolerance?` | As `plane`, for the face of a form ([model-family-forms.md](model-family-forms.md)); `onPlane`: the face lies on the plane and is parallel to it. |
| `parameter` | `parameter`, `equals?` (any value or `{parameter…}`), `min?`, `max?`, `tolerance?` | The family parameter's value in the case. Numbers within the tolerance, text without case, yes/no as true/false. |
| `material` | `element`, `equals` (material name or `{"parameter": "<material parameter>"}`) | The form's Material property is that material. |
| `connector` | `connector` (element id or description), `origin?: [x,y,z]`, `originFixed?`, `onFace?: {element, face}`, `direction?: [x,y,z]`, `systemType?`, `domain?`, `values?: {"<connector parameter>": value or {parameter…}}`, `tolerance?` | Origin, direction (within 1°), system type and domain as the connector tools name them ([model-family-connectors.md](model-family-connectors.md)), connector values (BuiltInParameter names such as `RBS_ELEC_VOLTAGE`, or the parameter's name). `onFace`: the origin lies on the face and the connector points out of it. |
| `symbol` | `instance` (nested instance id), `at?: [x,y,z]`, `fixed?`, `tolerance?` | The nested instance's insertion point ([model-family-components.md](model-family-components.md)). |
| `exists` | `elements: [ids]` | Every element exists, and every solid form among them has volume. |
| `cut` | `void`, `solid` | The void still cuts the solid (its `cutBy`, as [model-family-forms.md](model-family-forms.md) reads it). |

`fixed` and `originFixed` take the first committed case as the reference; every other case where the position moved by
more than the tolerance fails with how far it moved.

### Result

```json
{
  "types": ["ELV 200", "ELV 300"], "originalType": "ELV 200", "casesRequested": 10, "duplicatesDropped": 0,
  "cases": [
    { "case": "ELV 200: Depth = 120", "kind": "range", "type": "ELV 200", "inputs": { "Depth": 120 },
      "status": "failed",
      "failedChecks": [ { "id": "rearPlane", "kind": "plane", "what": "fixed", "expected": 0, "actual": -20,
                          "difference": -20, "message": "Moved -20 mm along its normal from where it is in case 'ELV 200' (tolerance 0.5)." } ],
      "revitFailures": [],
      "measurements": { "rearPlane": { "plane": { "point": [0, -20, 0], "normal": [0, -1, 0], "axis": "y", "coordinate": -20 } } },
      "parameters": { "Depth": 120, "Width": 200 } },
    { "case": "ELV 200: Width = 50", "kind": "range", "type": "ELV 200", "inputs": { "Width": 50 }, "status": "refused",
      "error": "Flex case 'ELV 200: Width = 50': Constraints are not satisfied.",
      "revitFailures": [ { "message": "Constraints are not satisfied", "severity": "error", "elementIds": [2410],
                           "elements": [ { "id": 2410, "element": "Dimension 2410" } ],
                           "constraints": [ { "id": 2410, "kind": "linear", "label": "Width", "value": 200, "locked": false } ] } ] }
  ],
  "summary": { "cases": 10, "passed": 8, "failed": 1, "refused": 1, "checks": [ { "id": "rearPlane", "kind": "plane", "failedCases": 1, "cases": ["ELV 200: Depth = 120"] } ] },
  "restored": true,
  "reopen": { "status": "compared", "copy": "temp", "equal": true, "types": [ { "type": "ELV 200", "equal": true, "differences": [] } ] }
}
```

`measurements` holds what each check read (box `min`, `max`, `size`, `centre`; plane/face `point`, `normal`,
`coordinate`; connector `point`, `direction`, `systemType`, `domain`, `values`; …) and `parameters` the values the
checks and inputs refer to. A refused case has no measurements; its `revitFailures` list the elements Revit named, and
the dimensions and alignments among them with their labels (`constraints`).

### reopenCopy

After the first trial has returned (never inside its transaction group):

| Target | The copy |
|---|---|
| `{path}` | `File.Copy` of the file. Skipped when the file is open in Revit with unsaved changes. |
| `{family}` | The loaded family opened as a copy (`EditFamily`), `SaveAs` to the temporary folder, closed. Skipped when it is open in the Family Editor. |
| `{familyDocument}` | `File.Copy` of its file, only when it has one and has no unsaved changes; the user's document is never saved. |

Whether the document has unsaved changes is decided before the first trial, so the trial itself can never be the
reason. The copy goes to `%TEMP%\RevitMCP\ModelFamilyFlex\<guid>\<name>.rfa` under its own name, or as
`<name>.flexcopy.rfa` there when the original is open in Revit (so two open documents do not share a file name). When
the file access policy does not allow that folder, a path or open-document copy goes beside the original as
`<name>.flexcopy.<guid>.rfa`. The copy is opened as a path target, flexed with the same `types` and `checks` (not the
ranges and explicit cases), closed and deleted afterwards. `reopen.types[]` says per type whether status, failed
checks, measurements and the parameter values both runs read are the same (0.01 tolerance) or lists the differences.
`reopen.status` is `skipped` with the reason when no copy can be made.

### Example: the ELV device

A non-hosted device ([model-family-references.md](model-family-references.md) recipe): `Left`/`Right` at ±Width/2 with
an EQ dimension, `Rear` at y = 0 defining the origin, `Front` at y = −Depth, height from the level to `Top`. The
enclosure extrusion is 2345, its cutout void 2346, the nested plan symbol 2400, the power connector "Power" on the rear
face at Height/2. `Housing Material`, `Voltage` and `Apparent Load` are family parameters.

```json
{
  "target": { "path": "C:\\fam\\ELV_Device.rfa" },
  "ranges": [
    { "parameter": "Width", "min": 150, "max": 400 },
    { "parameter": "Height", "min": 100, "max": 300 },
    { "parameter": "Depth", "min": 60, "max": 160 }
  ],
  "cases": [ { "name": "steel housing", "type": "ELV 200", "values": { "Housing Material": "Steel Brushed" } } ],
  "checks": [
    { "id": "enclosure", "kind": "boundingBox", "element": 2345,
      "size": { "x": { "parameter": "Width" }, "y": { "parameter": "Depth" }, "z": { "parameter": "Height" } },
      "min": [ { "parameter": "Width", "factor": -0.5 }, { "parameter": "Depth", "factor": -1 }, 0 ],
      "max": [ { "parameter": "Width", "factor": 0.5 }, 0, { "parameter": "Height" } ] },
    { "id": "centredX", "kind": "centred", "element": 2345, "axis": "x" },
    { "id": "rearPlane", "kind": "plane", "plane": "Rear", "at": { "axis": "y", "value": 0 }, "fixed": true },
    { "id": "rearFace", "kind": "face", "element": 2345, "face": "back", "onPlane": "Rear", "fixed": true },
    { "id": "symbol", "kind": "symbol", "instance": 2400, "at": [0, 0, 0], "fixed": true },
    { "id": "housing", "kind": "material", "element": 2345, "equals": { "parameter": "Housing Material" } },
    { "id": "power", "kind": "connector", "connector": "Power",
      "origin": [0, 0, { "parameter": "Height", "factor": 0.5 }], "onFace": { "element": 2345, "face": "back" },
      "direction": [0, 1, 0], "domain": "electrical", "systemType": "powerBalanced",
      "values": { "RBS_ELEC_VOLTAGE": { "parameter": "Voltage" }, "RBS_ELEC_APPARENT_LOAD": { "parameter": "Apparent Load" } } },
    { "id": "cutout", "kind": "cut", "void": 2346, "solid": 2345 }
  ],
  "reopenCopy": true
}
```

## revit_check_model_family_instances

Works on a **project** document (`document`, default the active one), not through the family session.

| Argument | Meaning |
|---|---|
| `family` | The loaded family whose instances are checked (every instance, unless `elementIds`). |
| `elementIds` | The instances to check (all of `family` when it is given too). |
| `types` | Only instances of these types; also the types `missingTypes` looks for (default every type of `family`). |
| `checks` | Expectations, as for the flex, with the kinds below. |

**Coordinates.** Every geometric expectation is written in **family coordinates**: the tool applies the inverse of the
instance's `GetTotalTransform()` to its geometry, connector origins and directions. An instance placed at (5000, 2000)
and rotated 90° still has its rear face at y = 0 and its power connector at `[0, 0, Height/2]` pointing `[0, 1, 0]`.
`location` in the row is the instance's insertion point in project coordinates.

| Kind | Keys | Passes when |
|---|---|---|
| `boundingBox` | `size?`, `min?`, `max?`, `tolerance?` | The instance's solids (nested ones included) in family coordinates. |
| `centred` | `axis`, `about?`, `tolerance?` | As in the flex. |
| `parameter` | `parameter`, `equals?`, `min?`, `max?`, `tolerance?` | The instance's parameter, else its type's (name or BuiltInParameter name). `{parameter…}` expressions read the instance (then type) values. |
| `material` | `parameter?`, `equals` | With `parameter`: that material parameter's value. Without: one of the instance's solids has the material. |
| `connector` | `connector` (connector id on the instance, description, or system type such as `data`), `origin?`, `direction?`, `domain?`, `systemType?`, `values?`, `connected?`, `system?`, `tolerance?` | As in the flex, plus membership: `connected` (physically connected or in a system) and `system` (the MEP system's name, for example the circuit). `values` with a BuiltInParameter name read the connector's own value (`RBS_ELEC_VOLTAGE` in V, `RBS_ELEC_APPARENT_LOAD` in VA); any other name reads the instance's parameter. |
| `hosted` | `hosted?` (default false) | The instance has no host (a level does not count) and the family's placement type needs none. |
| `count` | `type`, `min?`, `max?` | The number of checked instances of that type. Evaluated once, in `counts`. |

Result: `instances` (per instance `id`, `family`, `type`, `level`, `location`, `rotation`, `status`, `failedChecks`,
`measurements`, `parameters`), `counts`, `summary` (`instances`, `passed`, `failed`, failures per check) and
`missingTypes` (types named, or of the family, that have no instance). Nothing is published; rows carry element ids for
`revit_select_instance` or `revit_publish_page`.

```json
{
  "family": "ELV_Device",
  "types": ["ELV 200", "ELV 300"],
  "checks": [
    { "id": "size", "kind": "boundingBox", "size": { "x": { "parameter": "Width" }, "y": { "parameter": "Depth" }, "z": { "parameter": "Height" } } },
    { "id": "centred", "kind": "centred", "axis": "x" },
    { "id": "notHosted", "kind": "hosted" },
    { "id": "housing", "kind": "material", "equals": { "parameter": "Housing Material" } },
    { "id": "power", "kind": "connector", "connector": "Power", "origin": [0, 0, { "parameter": "Height", "factor": 0.5 }],
      "direction": [0, 1, 0], "domain": "electrical", "systemType": "powerBalanced", "connected": true,
      "values": { "RBS_ELEC_VOLTAGE": { "parameter": "Voltage" }, "RBS_ELEC_APPARENT_LOAD": { "parameter": "Apparent Load" } } },
    { "id": "data", "kind": "connector", "connector": "Data", "systemType": "data", "connected": true },
    { "id": "one200", "kind": "count", "type": "ELV 200", "min": 1 },
    { "id": "one300", "kind": "count", "type": "ELV 300", "min": 1 }
  ]
}
```

## Limitations

- **Geometry is measured, not inferred.** Bounding boxes come from tessellated solid edges: exact for planar forms, a
  close inner approximation for curved ones. Forms hidden by a visibility parameter still count in the family; a
  check on `element` avoids that. Symbolic lines and model lines are not solids and never count.
- **Formula results** are read from the current type after regeneration (`FamilyType.AsDouble` and friends). That a
  type reports a formula's computed value there is expected from Revit's behaviour, not stated by the API docs.
- **Associated element values** (a form's material, a connector's voltage) are read from the element after
  regeneration; that they follow the current type in a family document is Revit's behaviour, verified only live.
- **Connector orientation.** `onFace` expects a connector to point out of its face (the face's outward normal);
  `direction` compares within 1°.
- **Placed connectors.** System type of a placed connector comes from `Connector.ElectricalSystemType` /
  `DuctSystemType` / `PipeSystemType`, membership from `Connector.MEPSystem` and `IsConnected`. Revit documents the
  electrical system type of an unconnected connector as undefined. Connector values by BuiltInParameter come from
  `MEPFamilyConnectorInfo.GetConnectorParameterValue`; voltage, apparent load, current and power are converted to V,
  VA, A and W, any other value is returned in Revit's internal units.
- **Hosting.** `Host` may be a level for some placement types; a level is not counted as a host. A family whose
  placement type is `OneLevelBasedHosted` always counts as hosted.
- **reopenCopy** needs the file access policy to allow reading the temporary folder (`%TEMP%` is allowed by default)
  or, for files, writing beside the original. A loaded family has no file to copy beside, so a policy that refuses
  `%TEMP%` skips it. A document with unsaved changes is skipped, never saved.
- **Revit 2024 and 2026.** The members used (`FamilyManager.CurrentType`/`Set`, `FamilyType.As*`, `Instance.GetTotalTransform`,
  `Connector.GetMEPConnectorInfo`, `MEPFamilyConnectorInfo.GetConnectorParameterValue`, `Connector.MEPSystem`,
  `Document.SaveAs`, `Document.IsModified`) exist in both reference assemblies; no version branches. The connector
  parameter label of `RBS_ELEC_APPARENT_LOAD` differs ("Apparent Load" in 2024, "Apparent Power" in 2026): use the
  BuiltInParameter name in `values`.

## Live test

Run in Revit 2024 and again in Revit 2026 with the ELV device of [model-family-acceptance.md](model-family-acceptance.md)
(two types `ELV 200` and `ELV 300`) and a test project open alongside.

1. `revit_get_model_family target {path}` include `["types", "forms", "referencePlanes", "connectors", "nestedInstances"]`:
   note the current type, every type's values and the ids the example uses.
2. Run the example without `reopenCopy`. Every type case is `ok`; `restored` is true. Read the family again: the
   current type and every type value are unchanged (compare with step 1), and Undo has nothing new in the Family Editor.
3. Make a refused case: a `Width` range down to a value smaller than a locked inner dimension (or 0.5 mm). The case is
   `refused`, `error` quotes Revit, `revitFailures[].constraints` names the dimension and its label; the next case is
   `ok` and `restored` is still true.
4. Break an expectation on purpose: `rearPlane` with `at.value: 10`. Every case fails `at.y` with expected 10,
   actual 0 and difference −10; `summary.checks` counts them.
5. Flex Depth with `rearPlane` fixed: Rear stays (no `fixed` failure); add a `face` check `front` with `fixed: true`
   and see it fail in the Depth cases with how far it moved.
6. Housing material per type: the `housing` check passes for both types and for the `steel housing` case.
7. Connector: `power` passes in every case: origin z follows Height/2 (if Revit keeps the connector at a fixed height,
   it fails with the difference: constrain it as [model-family-connectors.md](model-family-connectors.md) says),
   direction `[0, 1, 0]`, voltage and apparent load equal the family parameters.
8. With the family open in the Family Editor and unsaved, run the flex with `target {familyDocument: "active"}`: it runs,
   the window is unchanged afterwards; `reopenCopy: true` is `skipped` with the reason "unsaved changes". Save it, run
   again: `reopen.status` is `compared`, `equal` true; the temporary folder is gone.
9. `reopenCopy` with `target {path}` and with `target {family}`: both compare equal.
10. Load the family, place both types without a host (rotate one by 90°), connect power and data
    ([model-family-acceptance.md](model-family-acceptance.md) steps), then run the instance example: both instances
    pass; `connected` and `system` name the circuits; with one type deleted, `missingTypes` names it and its `count`
    fails.

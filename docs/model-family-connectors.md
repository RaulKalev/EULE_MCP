# Model family connectors

Create, inspect, edit and remove the MEP connectors of a loadable family (#174, part of [#168](model-families.md)):
electrical (power and ELV), duct, pipe, conduit and cable tray. Connector properties (voltage, apparent load, flow,
size…) can be set directly or associated with family parameters, so types drive them.

The `target` argument, previews, rollback and save/load work as described in [model-families.md](model-families.md).
Lengths are millimetres, angles degrees, points `[x, y, z]` from the family origin.

| Tool | Permission | What it does |
|---|---|---|
| `revit_preview_edit_model_family_connectors` | read-only | Runs the same edit and rolls it back: `ready`, `problems`, the read-back rows. |
| `revit_edit_model_family_connectors` | approval | Removes, creates and edits connectors, links them, reads every one back. |
| `revit_get_model_family` section `connectors` | read-only | Every connector of the family with the same row. |

## Arguments

| Argument | Meaning |
|---|---|
| `target` | Which family ([model-families.md](model-families.md)). |
| `remove` | Connector element ids to delete. Runs **first**. A connector linked to a removed one loses its link (warning). |
| `connectors` | Connectors to create. Runs **second**, in list order. |
| `edit` | Existing connectors to change. Runs **third**. |

Links (`linkTo`) are set last, once every connector of the call exists. Every step reads back what Revit stored; a
value Revit refuses is a problem and the whole edit is rolled back; a value Revit adjusts is a warning.

### `connectors[]` (create)

| Key | Meaning |
|---|---|
| `name` | Your label. Echoed in the result (so two connectors on one device can be told apart) and written to the connector's description (`RBS_CONNECTOR_DESCRIPTION`). |
| `domain` | `electrical`, `duct`, `pipe`, `conduit` or `cableTray`. Required. |
| `host` | Where it sits; see [Host](#host). Required. |
| `systemType` | Required for electrical, duct and pipe; none for conduit and cable tray. See [System types](#system-types). |
| `shape` | Duct only: `round`, `rectangular` or `oval`. Inferred from `diameter` (round) or `width`+`height` (rectangular). Pipe and conduit are round, cable tray rectangular. |
| `diameter` / `width` / `height` | Size in mm (round: `diameter`; rectangular and oval: `width`, `height`). Set through `CONNECTOR_DIAMETER` (or `CONNECTOR_RADIUS`), `CONNECTOR_WIDTH`, `CONNECTOR_HEIGHT`. Electrical connectors have no size. |
| `position` | `[x, y, z]` mm. A new connector sits where Revit puts it on the face; the point is projected onto the face plane and the connector moved by the in-plane difference. The result gives `requested`, `projected` and `actual`. |
| `flip` | `true` reverses the direction (`ConnectorElement.FlipDirection`). |
| `angle` | Degrees, written to `CONNECTOR_ANGLE` where the connector has it (not electrical). |
| `primary` | `true` makes it the primary connector of its domain (`AssignAsPrimary`); the others of that domain become secondary. |
| `linkTo` | A connector id, or `{"created": "connectors[i]"}` for one created in the same call: a linked connector (`SetLinkedConnectorElement`). Same domain only. |
| `properties` | `{"<parameter>": value}` by display name or BuiltInParameter name. See [Properties and units](#properties-and-units). |
| `associations` | `{"<connector parameter>": "<family parameter>"}`: the connector parameter is driven by the family parameter. The family parameter must exist (create it with the parameters tool of #172, [model-family-parameters.md](model-family-parameters.md)); the data types must match. |

### `edit[]`

`{connector: <id>, systemType?, shape?, diameter?, width?, height?, position?, flip?, angle?, primary?, linkTo?, properties?, associations?}`
with the same meanings, plus:

- `linkTo: null` removes the link; `associations: {"RBS_ELEC_VOLTAGE": null}` dissociates.
- `systemType` is set through `ConnectorElement.SystemClassification` after `IsSystemClassificationValid`. When Revit
  does not accept it, the problem says to remove the connector and create it again.
- `shape` is tried through the `CONNECTOR_PROFILE_TYPE` parameter and read back; when Revit keeps the old shape, the
  problem says to remove and recreate.
- `primary: false` is refused: make another connector of that domain primary instead.

### Host

| Selector | Meaning |
|---|---|
| `{"element": <form id>, "face": "back"}` | The planar face of that form whose outward normal is within 1° of the label's direction: `left` −X, `right` +X, `front` −Y, `back` +Y, `top` +Z, `bottom` −Z. When several faces match, the outermost (furthest along the normal) is used and a warning says so. No match: the problem lists the directions the form's planar faces have. |
| `{"element": <form id>, "face": [nx, ny, nz]}` | The same for any direction. |
| `{"reference": "<stable representation>"}` | A planar face by its stable reference (the result gives `requestedHost.reference` for every face it used). |
| `"<reference plane name>"` or a reference plane id | A plane-hosted connector, where Revit allows it. The API documents only planar faces; when Revit refuses, the problem says to host the connector on a face (which can be locked to the plane). |

A connector on a face of the device's own geometry needs no host in the project: the device places without selecting
a host face (when the family is not face-based).

### System types

| Domain | `systemType` | Revit classification |
|---|---|---|
| electrical | `powerBalanced`, `powerUnBalanced`, `data`, `telephone`, `security`, `fireAlarm`, `nurseCall`, `controls`, `communication` | `data` is `DataCircuit`; the others keep their name |
| duct | `supplyAir`, `returnAir`, `exhaustAir`, `otherAir`, `fitting`, `global` | same name |
| pipe | `supplyHydronic`, `returnHydronic`, `sanitary`, `domesticHotWater`, `domesticColdWater`, `fireProtectWet`, `fireProtectDry`, `fireProtectPreaction`, `fireProtectOther`, `vent`, `otherPipe`, `fitting`, `global` | same name |
| conduit, cableTray | none | `CableTrayConduit` |

Names are case-insensitive. A wrong one is a problem listing the valid names of that domain.

### Properties and units

A number is in the display unit of the parameter's type; `{"value": 2, "unit": "kVA"}` gives an explicit unit. Text
values name enum values or load classifications; `true`/`false` set yes/no parameters.

| Parameter type | Display unit | Other units accepted |
|---|---|---|
| Length | mm | cm, m |
| Electrical potential (`RBS_ELEC_VOLTAGE`) | V | kV, mV |
| Apparent power (`RBS_ELEC_APPARENT_LOAD`) | VA | kVA |
| Electrical power | W | kW |
| Current | A | mA, kA |
| Air flow (`RBS_DUCT_FLOW_PARAM`) | L/s | m3/h, m3/s |
| Pipe flow (`RBS_PIPE_FLOW_PARAM`) | L/s | L/min, m3/h |
| Angle | deg | rad |
| HVAC / piping pressure | Pa | kPa (piping also bar) |
| Number (`RBS_ELEC_POWER_FACTOR`), integer (`RBS_ELEC_NUMBER_OF_POLES`) | none | none |

Enumerated parameters: `RBS_DUCT_FLOW_DIRECTION_PARAM` / `RBS_PIPE_FLOW_DIRECTION_PARAM` take `in`, `out`,
`bidirectional`; `RBS_DUCT_FLOW_CONFIGURATION_PARAM` takes `calculated`, `preset`, `system` (pipe also `demand`).
`RBS_ELEC_LOAD_CLASSIFICATION` takes the name of a load classification of the family document; the problem lists the
names there are.

Before setting, the tool checks that the connector has the parameter and that it is not read-only; the problem lists
the connector's settable parameters (for `properties`) or associable ones (for `associations`). A parameter given in
both `properties` and `associations` is a problem: the association drives the value.

The family category is checked against the domain: a duct connector in an Electrical Fixtures family, or any connector
in a Generic Model family, is a warning. For MEP categories the family's Part Type (#169 `settings.partType`) also
decides how the device behaves in systems.

## Result

```json
{
  "removed": [],
  "created": [
    {
      "index": 0, "name": "Power", "id": 4021, "description": "Power",
      "domain": "electrical", "systemType": "powerBalanced", "classification": "PowerBalanced",
      "shape": null, "size": null, "origin": [0, 30, 100], "direction": [0, 1, 0],
      "host": { "kind": "face", "element": 3010, "face": "back", "normal": [0, 1, 0], "inferred": true },
      "isPrimary": true, "linkedConnector": null,
      "properties": { "Voltage": { "value": 230, "unit": "V", "builtIn": "RBS_ELEC_VOLTAGE" } },
      "associations": { "Voltage": "Voltage", "Apparent Load": "Apparent Load" },
      "requestedHost": { "kind": "face", "element": 3010, "face": "back", "normal": [0, 1, 0], "reference": "…" },
      "applied": { "properties": { "Number of Poles": { "requested": 1, "stored": 1, "unit": null } },
                   "associations": { "RBS_ELEC_VOLTAGE": "Voltage" },
                   "position": { "requested": [0, 30, 100], "projected": [0, 30, 100], "actual": [0, 30, 100] } }
    }
  ],
  "edited": []
}
```

`host` is inferred from the connector's origin and direction (a face of a form, or a reference plane it lies on):
Revit does not expose a connector's host. `revit_get_model_family` returns the same rows under `connectors`
(`count`, `truncated`, `connectors`).

## Examples

Preview a power and a data connector on the rear face of the device body (form 3010):

```json
{
  "target": { "path": "C:\\fam\\ELV_Device.rfa" },
  "connectors": [
    { "name": "Power", "domain": "electrical", "host": { "element": 3010, "face": "back" }, "systemType": "powerBalanced",
      "position": [0, 30, 100], "primary": true,
      "properties": { "RBS_ELEC_NUMBER_OF_POLES": 1, "RBS_ELEC_POWER_FACTOR": 1 },
      "associations": { "RBS_ELEC_VOLTAGE": "Voltage", "RBS_ELEC_APPARENT_LOAD": "Apparent Load" } },
    { "name": "Data", "domain": "electrical", "host": { "element": 3010, "face": "back" }, "systemType": "data",
      "position": [0, 30, 40] }
  ]
}
```

Edit: move the data connector, change it to communication and remove an old one:

```json
{
  "target": { "path": "C:\\fam\\ELV_Device.rfa", "overwrite": true },
  "remove": [4100],
  "edit": [ { "connector": 4022, "systemType": "communication", "position": [0, 30, 60] } ]
}
```

## Limitations

- Connectors are hosted on planar faces of forms in the family. Faces of nested components and curved faces are not
  offered. A plane-hosted connector (reference plane) is attempted but the API documents only planar faces; expect
  Revit to refuse it.
- A new connector's initial position is Revit's choice on the face; `position` moves it within the face. When Revit
  keeps it in place, the warning says to constrain it with dimensions to reference planes
  ([model-family-references.md](model-family-references.md)); a connector is not guaranteed to be a dimension target.
- Revit does not expose a connector's host; `host` in the rows is inferred from geometry.
- `systemType` and `shape` of an existing connector change only where Revit accepts it; otherwise remove and recreate.
- Load classifications must already exist in the family document. Generic Model templates usually have none; leave
  the classification to the project then.
- The display name of `RBS_ELEC_APPARENT_LOAD` is "Apparent Load" in Revit 2024 and "Apparent Power" in Revit 2026.
  Use the BuiltInParameter name to be independent of version and language. All other connector members used here are
  the same in both APIs.
- The family category decides whether placed instances join systems; a Generic Model family gets a warning. Set the
  category and Part Type first (#169).

## Live test

Run in Revit 2024 and 2026. Steps 1–5 cover the power connector driven by voltage and apparent load, 6 the duct
connector, 7–9 the device with independent power and data connectors.

1. Create a non-hosted family from Metric Generic Model and set its category to an ELV device category, for example
   Data Devices (#169). Add a box form (#171) and family parameters `Voltage` (electrical potential) and
   `Apparent Load` (apparent power), type parameters (#172). Note the form id from `revit_get_model_family`.
2. Preview and apply the power connector:

   ```json
   { "target": { "path": "C:\\fam\\ELV_Device.rfa", "overwrite": true },
     "connectors": [ { "name": "Power", "domain": "electrical", "host": { "element": <form id>, "face": "back" },
       "systemType": "powerBalanced", "primary": true, "properties": { "RBS_ELEC_NUMBER_OF_POLES": 1 },
       "associations": { "RBS_ELEC_VOLTAGE": "Voltage", "RBS_ELEC_APPARENT_LOAD": "Apparent Load" } } ] }
   ```

   Check: no problems; `host.face` is `back`; `direction` is `[0, 1, 0]`; `associations` lists both.
3. Create two types with different `Voltage` (230, 400) and `Apparent Load` (100, 500) and flex them (#176,
   [model-family-flex.md](model-family-flex.md)). After each flex, `revit_get_model_family` section `connectors`
   shows the type's values under `properties`.
4. Save, close and reopen the RFA; run `revit_get_model_family` again: the same connector id, origin, direction,
   classification and associations.
5. Load into a project and place both types with `revit_place_family_instances` without selecting a host face (the
   connector references the device's own geometry). Read the instance's electrical values with
   `revit_get_element_parameters`, then circuit it with `revit_create_electrical_circuit` (`elementIds` = the
   instance, `systemType` PowerCircuit) and check it with `revit_get_circuit_info`.
6. Duct connector driven by family width and height (family category Mechanical Equipment or Duct Terminal, family
   parameters `Width` and `Height`, length):

   ```json
   { "target": { "path": "C:\\fam\\Grille.rfa", "overwrite": true },
     "connectors": [ { "name": "Supply", "domain": "duct", "host": { "element": <form id>, "face": "bottom" },
       "systemType": "supplyAir", "shape": "rectangular", "width": 300, "height": 200,
       "properties": { "RBS_DUCT_FLOW_DIRECTION_PARAM": "in", "RBS_DUCT_FLOW_PARAM": { "value": 360, "unit": "m3/h" } },
       "associations": { "CONNECTOR_WIDTH": "Width", "CONNECTOR_HEIGHT": "Height" } } ] }
   ```

   Flex `Width`/`Height`; the row's `size` follows. Place it in a project and draw a duct from the connector by hand;
   the duct takes the connector size.
7. One ELV device with power and data connectors: run the first example above (two connectors on the back face,
   `Power` at z 100, `Data` at z 40). Check the two rows: different ids and descriptions, `systemType`
   `powerBalanced` and `data`, the requested origins, both directions `[0, 1, 0]`, associations on the power one only.
8. Flex the types and reload the family into the project (`target.load: true`); `revit_get_model_family` still shows
   both rows with the same classification, position, orientation and associations.
9. Place one instance. Create a power circuit on the power connector and a data circuit on the data connector with
   `revit_create_electrical_circuit` (one `elementIds` entry plus `connectorId` and `systemType` PowerCircuit / Data).
   Check both memberships with `revit_get_circuits_for_selected_elements` or `revit_get_circuit_info`.
10. Plane host: try `"host": "Center (Front/Back)"` in a preview and record what Revit answers (a connector, or the
    problem that says to use a face).

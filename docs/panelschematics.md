# PanelSchematics plugin data

The `panelschematics` tool group lets an agent read and change what the PanelSchematics plugin
(Kilbiskeemid, [RaulKalev/PanelSchematics](https://github.com/RaulKalev/PanelSchematics), branch `Version2`)
keeps in a project: the panel tables, the "empty rows" that have no Revit circuit, the parameters a row is written
to, and the plugin's Extensible Storage (ES) stores.

The connector does not call the plugin. It follows the plugin's storage formats, which are described in one
place: [`PanelSchematicsContract.cs`](../RevitMCP.Addin/PanelSchematics/PanelSchematicsContract.cs). That file
records the plugin commit it was read from. **When the plugin changes a schema, a row field or a parameter name,
update the contract with it.**

## Tools

| Tool | Access | Purpose |
|---|---|---|
| `revit_panelschematics_guide` | Read-only | Row fields, parameter mapping, stores, metadata keys, rules. Read first. |
| `revit_panelschematics_get_panel_table` | Read-only | A panel's circuits and empty rows merged in Rea nr order. Without `panel`: panels that have rows. |
| `revit_panelschematics_get_data` | Read-only | Everything else the plugin stores, per panel and for the project. |
| `revit_panelschematics_preview_edit_empty_rows` / `revit_panelschematics_edit_empty_rows` | Preview / approval | Add, change, remove empty rows. |
| `revit_panelschematics_preview_set_circuit_rows` / `revit_panelschematics_set_circuit_rows` | Preview / approval | Write row values to circuits, connected elements and detail items. |
| `revit_panelschematics_preview_set_panel_data` / `revit_panelschematics_set_panel_data` | Preview / approval | Metadata, Üheaegsustegur, Pingelang, cable length, orientation, colours, main panel, info link, schematic view. |
| `revit_panelschematics_preview_set_row_groups` / `revit_panelschematics_set_row_groups` | Preview / approval | Replace a panel's row groups. |

The group is not in the `core` profile: find the tools with `revit_tools_search`, or start the bridge with
`--tool-groups panelschematics`.

## The row model

A panel table is the panel's circuits plus its empty rows, ordered by **Rea nr**. Rea nr is shared: no two rows
of a panel may have the same number.

| Row | Where its values live | Row id (ties it to its detail items) | Group identity |
|---|---|---|---|
| Circuit | Parameters of the electrical circuit | The circuit's `id` text parameter | `C:` + circuit `UniqueId` |
| Empty row | JSON in the panel's `PanelSchematicsEmptyRows` entity | The row's `UniqueId` (a GUID) | `E:` + row `UniqueId` |

Schematic detail items carry the row id in their own `id` parameter.

### Which parameter gets which value

| Row field | Circuit | Connected elements | Detail items |
|---|---|---|---|
| `ReaNr` | `Rea Nr` (integer) | `Rea Nr` | `Rea Nr` |
| `GroupName` | `Group Name` | `Group Name` | `Group Name` |
| `GroupNr` | `Group Nr` | `Group Nr` | `Group Nr` |
| `CircuitBreaker` | `Circuit Breaker` | `Circuit Breaker` | `Circuit Breaker` |
| `WireTypeName` | cable type (2026) or wire type by name, plus text `Kaabel` | `Kaabel` | `Wire Type` (text) |
| `KlemmiNr` | `Klemmi Nr` | – | `Klemmi Nr` |
| `ApparentLoad` | `kW` (text, one decimal) | – | `kW` |
| `LisaKW` | `Lisa kW` | – | `Lisa kW` |
| `CircuitPhase` | `Phase` (integer) | – | `Phase` |
| `Samm` | `Samm` (integer) | – | `Samm` |
| `Symbol` | `Sümbol` (empty → `Kaitselüliti`) | – | `Sümbol` |
| `Rikkevooluga` | `Rikkevooluga` (`Jah` / `Ei`) | – | `Rikkevooluga` (yes/no parameters get 1/0) |

Empty group values and a Rea nr of 0 are not mirrored to connected elements, as in the plugin. Parameters that
do not exist or are read-only are reported in the preview (`missing`, `readOnly`) and skipped.

### Symbol names

A row's `Symbol` is a name from the team's `KilbiskeemiTingmärgid.json` (`SymbolMappings[].DisplayName`), which
the plugin maps to a detail family and type. The connector finds the file the way the plugin does (the EULE
Dropbox under the user profile, then other drives) and only reads it. `revit_panelschematics_guide` lists the
names by category, and a write whose `Symbol` is not in the file gets a warning with the closest names. It is a
warning, not a refusal, because the file may be out of reach on a machine.

## Extensible Storage stores

| Key | Schema | Stored on | Content |
|---|---|---|---|
| `emptyRows` | `PanelSchematicsEmptyRows` | Panel | JSON list of rows |
| `rowGroups` | `PanelSchematicsRowGroupsV1` | Panel | `{"v":1,"groups":[{"id","parent","deps","links"}]}` |
| `panelRecord` | `PanelSchematicsPanelRecordV1` | Panel | Panel → schematic view, by UniqueId |
| `panelMetadata` | `PanelSchematicsPanelMetadataV1` | Project Information | map `<panelId>_<fieldKey>` → text |
| `overviewValues` | `PanelSchematicsOverviewValuesV2` | Project Information | Üheaegsustegur and Pingelang per panel |
| `cableLengths` | `PanelCableLengthOverrides` | Project Information | override and original length per panel, metres |
| `orientations` | `PanelOrientations` | Project Information | 1 = horizontal box |
| `panelColors` | `PanelSchematicsPanelColorsV1` | Project Information | colour labels and each panel's label |
| `mainPanel` | `PanelSchematicsMainPanel` | Project Information | main panel id |
| `panelInfoLinks` | `PanelSchematicsPanelInfoLinksV2` | Project Information | panel → title-page detail item |

Read-only here: the legacy `PanelOverviewValues` and `PanelInfoLinks` stores (their entries are copied into the
V2 store on the first write, as the plugin does) and the picker histories `PanelSchematicsRecentCablesV1` /
`PanelSchematicsRecentSymbolsV1`, which the plugin maintains itself.

## Safety rules

Every write tool:

1. **Has a preview** that returns before/after values and a `stateToken`. Pass it to the write as
   `expectedStateToken` to have the write refused when the stored data changed in between.
2. **Needs approval** (unless Direct Edit is on), bound to the document like every other write tool.
3. **Is refused while the plugin's Kilbiskeemid window is open.** That window keeps the table in memory and
   saves it whole; its next save would overwrite the change. Other plugin windows only produce a warning.
4. **Never overwrites data it could not read.** An unreadable store, a conflicting schema or a payload from a
   newer plugin version stops the write.
5. **Edits stored JSON in place**, so other rows and fields the connector does not know stay exactly as stored.
6. **Checks worksharing ownership** of the panel, Project Information and every element it sets a parameter on.
7. **Writes a backup first** to `%LOCALAPPDATA%\RevitMCP\PanelSchematicsBackups\`; no backup, no write.
8. **Runs in one transaction and reads the result back**; a mismatch rolls the transaction back.

A schema that is not loaded yet (the plugin has not run in the session) is created exactly as the plugin would
create it: same GUID, name, vendor id, access levels and fields.

## What the connector does not do

- It does not draw. New or changed rows reach the schematic when the user runs *Kirjuta + Joonista* in the
  plugin; values of detail items that already exist are updated directly.
- It does not delete the detail items of a removed empty row (it warns about them).
- It does not write the title-page detail item when metadata changes; the plugin does that from Kilbi andmed.

## Empty-row operations

```json
[
  { "action": "add", "reaNr": 12, "insert": true,
    "values": { "GroupNr": "F12", "GroupName": "Reserv", "CircuitBreaker": "C16", "CircuitPhase": "1" } },
  { "action": "update", "uniqueId": "<row UniqueId>", "values": { "Symbol": "Kaitselüliti" } },
  { "action": "remove", "uniqueId": "<row UniqueId>" }
]
```

- `add` without `reaNr` puts the row at the end. With a taken `reaNr` it fails unless `insert` is true; then
  the unbroken run of rows from that number on moves down by one, including circuits (their `Rea Nr` parameter
  is written in the same transaction). Rows after the first free number keep their numbers.
- `update` and `remove` select the row by `uniqueId`, or by `reaNr` when exactly one empty row has it.
- A new row is built like the plugin's *Lisa rida*: `Rikkevooluga` = `Ei`, ticked for drawing, a fresh GUID.
- Removing the last row deletes the entity, as the plugin does.

## Testing

Pure logic (row editing, payload formats, contract lookups) is covered by `PanelSchematicsTests.cs`. The Revit
side needs a project with panels; a manual pass:

1. `revit_panelschematics_get_panel_table` for a panel and compare with the plugin's table.
2. Close the plugin window. Preview and add an empty row, open the plugin: the row is there and can be drawn.
3. Preview and change a circuit's `GroupName`: the circuit, its devices and its detail item show the new name.
4. Open the plugin's table window and repeat a write: it is refused with `plugin_window_open`.
5. `revit_panelschematics_get_data` before and after a `set_panel_data` call, and check Kilpide ülevaade.

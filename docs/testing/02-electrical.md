# Smoke tests — Electrical circuits

Part of the [smoke test checklist](../../TESTING.md).

## 14. Electrical Circuit Discovery Tools

Test each read-only tool from Claude Code or Codex.

### Prompts

```
List all electrical circuits in the model.

List all electrical circuits on panel "DB-L1".

Get detailed info for circuit 2520343.

List available panels.

List available wire types.

List available cable types.

Check whether the current selection can be added to circuit 2520343.
```

### Verify

- Response is valid JSON with `"success": true`.
- `revit_get_electrical_circuits` returns circuit list; `panelName` filter narrows results.
- `revit_get_circuit_info` returns connected elements, load, wire type, and parameters.
- `revit_get_available_panels` returns electrical equipment with names and IDs.
- `revit_get_available_wire_types` and `revit_get_available_cable_types` return wire/cable types.
- `revit_get_circuit_compatible_elements` checks compatibility and reports per-element reasons.
- Read-only tools do not create Revit Transactions (no entry in Undo history).
- Panel/circuit filters work; supplying a non-existent panel returns an empty or filtered result.
- Invalid circuit IDs (e.g. `circuitId = 99999999`) return `"success": false` with a clear error message.
- No active document returns `"success": false` with a clear error message.

---

## 15. Electrical Circuit Modification Tools

Test each write tool from Claude Code or Codex.

### Prompts

```
Create a new power circuit from the currently selected devices and assign it to panel "DB-L1".

Add the current selection to circuit 2520343.

Reassign circuit 2520343 to panel "DB-L2".

Change the wire type of circuit 2520343 to "XX_EN_IT_Cat6a".

Set circuit parameter "Cable Type" to "XX_EN_IT_Cat6a" on circuits 2520343 and 2520353.
```

### Verify per write tool

For each tool (`revit_create_electrical_circuit`, `revit_add_elements_to_circuit`, `revit_reassign_circuit_panel`, `revit_change_circuit_cable_or_wire_type`, `revit_set_circuit_parameter`):

1. Trigger the tool from Claude Code or Codex.
2. Confirm response status is `"approval_required"` (not yet executed).
3. Confirm a pending item appears in the **Pending** tab of the Revit MCP window.
4. Confirm the pending item summary clearly identifies the operation (circuit ID, target panel, value, etc.).
5. **Reject** the pending item.
6. Confirm the model was **not** changed (check Revit Undo — no new entry).
7. Repeat and **approve** the pending item.
8. Confirm the model was changed (parameters/assignment visible in Revit).
9. Confirm the change appears in **Revit Undo** history.
10. Undo the change and confirm the model returns to its previous state.

---

## 16. Approval / Reject / Undo Tests

Full approval workflow verification.

### Test: Approve flow

1. With Direct Edit disabled, call a write tool (e.g. `revit_set_circuit_parameter`).
2. Response status must be `"approval_required"`.
3. Pending tab auto-selects and shows the request.
4. Click **Approve**.
5. Confirm model is modified.
6. Confirm Revit Undo shows the transaction name (e.g. `"Revit MCP - Set Circuit Parameter"`).
7. Confirm the Activity tab logs the operation as Success.

### Test: Reject flow

1. Call a write tool.
2. Click **Reject** in Pending tab.
3. Confirm model is **not** modified.
4. Confirm Activity tab logs the operation as Rejected.
5. Confirm Revit Undo has no new entry.

### Test: Reject All

1. Queue multiple write tools in sequence.
2. Click **Reject All**.
3. Confirm all pending items are cleared.
4. Confirm the model was not changed.

### Test: Undo after approval

1. Approve a write tool that changes a parameter.
2. Press `Ctrl+Z` in Revit.
3. Confirm the parameter reverts to its previous value.
4. Confirm Undo stack shows the MCP transaction name.

---

## 17. Direct Edit Safety Tests

Direct Edit mode bypasses approval. Every user can turn it on with the switch on the Status page. These tests verify that the feature behaves correctly and is safe by default.

### Test: Default state

1. Start Revit with the add-in loaded.
2. Open the MCP Connector window.
3. Confirm the **Direct Edit** switch on the Status page is off and reads "Off: every change waits for your approval."
4. Confirm no confirmation dialog has appeared.

### Test: Enabling Direct Edit

1. With Approval Required enabled, call a write tool.
2. Confirm approval appears in Pending tab (not executed).
3. Reject the pending item.
4. Click the **Direct Edit** switch.
5. Confirm a confirmation sheet appears inside the window.
6. Confirm it clearly states that agents will change the model **without asking**.
7. Confirm the focused button is **Cancel** (not Turn on), and that Esc cancels.
8. Click **Cancel**: confirm the switch is still off.
9. Click the switch again, confirm the sheet appears.
10. Click **Turn on**.
11. Confirm the switch is on (red), the card turns red and reads "On: agents change the model without asking."

### Test: Write tool with Direct Edit enabled

1. With Direct Edit enabled, call a write tool (e.g. `revit_set_circuit_parameter`).
2. Confirm the tool executes **immediately** — no pending approval appears.
3. Confirm the model is changed.
4. Confirm Revit Undo shows the transaction.

### Test: Disabling Direct Edit

1. Click the **Direct Edit** switch while it is on.
2. Confirm Direct Edit is disabled **without** a confirmation dialog.
3. Call a write tool.
4. Confirm approval appears in Pending tab (approval required again).

### Test: Restart resets Direct Edit

1. Enable Direct Edit.
2. Close and reopen Revit (or reload the add-in).
3. Confirm Direct Edit starts **disabled** again.

---

## 18. Electrical QA and Inspection Tools (Phase 3 + 5)

### 18.1 Find Uncircuited Elements

```
Find all electrical fixtures that are not on any circuit.

Find all fire alarm devices with no circuit assigned.

Find all uncircuited elements in the current selection.
```

Verify:
- `revit_find_uncircuited_elements` returns elements not in any circuit.
- `categoriesChecked` in response lists scanned categories.
- `returnParameters` returns specific parameter values per element.
- `filters` narrow results (e.g. by ELENEA_Osasüsteem).
- Default categories cover: Electrical Fixtures, Lighting Fixtures, Electrical Equipment, Data Devices, Fire Alarm Devices, Security Devices, Communication Devices.
- Elements already on circuits are not returned.
- No Revit transaction is created.

### 18.2 Check Circuit Health

```
Run a full circuit health check on the model.

Check circuits on panel "DB-L1" for missing cable type.

Check all circuits for duplicate circuit numbers.
```

Verify:
- `revit_check_circuit_health` returns `issueSummary` and per-circuit issue list.
- `panelName` filter narrows results.
- `systemType` filter narrows results.
- `checks` parameter allows requesting specific checks only.
- `MissingPanel`, `EmptyCircuitNumber`, `DuplicateCircuitNumbers`, `MissingCableType` (one issue per circuit with no cable or wire type; `MissingWireType` is accepted as the same check), `MissingLoadName`, `NoConnectedElements` are detected.
- `circuitId` is returned for each issue so it can be used in follow-up tools.
- No Revit transaction is created.

### 18.3 Export Panel Circuit List to Excel

```
Export all circuits to Excel.

Export circuits for panel "DB-L1" to Excel.
```

Verify:
- `revit_export_panel_circuit_list_to_excel` returns a file path.
- File is created in `Documents\RKTools\RevitMCP\Exports`.
- File opens successfully in Excel.
- `Summary` sheet includes export time, model name, circuit count.
- `Panel Circuits` sheet has frozen header, autofilter, autosized columns.
- `Circuit Elements` sheet (when `includeElements=true`) lists each element per circuit.
- `Health Issues` sheet (when `includeHealthCheck=true`) lists issue rows.
- `panelName` filter produces a smaller export covering only those circuits.

### 18.4 Find Circuits by Element Parameter

```
Find circuits containing fire alarm devices in room 201.

Find circuits containing elements where ELENEA_Osasüsteem = "ATS".
```

Verify:
- `revit_find_circuits_by_element_parameter` returns distinct circuits.
- `matchedElementIds` are correct.
- `circuitCount` matches the circuits returned.
- Works with category + filters combination.
- No Revit transaction is created.

### 18.5 Trace Circuit

```
Trace the selected element back to its circuit and panel.

Trace circuit 2520343 to its panel.
```

Verify:
- `revit_trace_circuit` returns circuit number, load name, panel name, panel element ID.
- `useSelection=true` traces the currently selected element.
- `circuitId` traces a circuit directly.
- `elementId` traces a specific element.
- `connectedElements` lists elements when `includeConnectedElements=true`.
- An element on multiple circuits returns all circuits.
- An uncircuited element returns `circuitCount=0` with `success=true`.
- A non-FamilyInstance or element without MEP model returns a clear error.

---

## 19. Circuit Parameter Completeness, Selection, and Export Tools

### 19.1 Check Circuit Parameter Completeness

**Prompts:**
```
Check that all circuits on panel DB-L1 have Circuit Number, Load Name, and Cable Type filled.
What percentage of circuits are missing a Load Name?
```

**Verify:**
- `revit_check_circuit_parameter_completeness` returns per-parameter `filledCount`, `emptyCount`, `fillRate`.
- `emptyCircuitIds` lists IDs of circuits with missing values.
- Works with `panelName` and `systemType` filters.

---

### 19.2 Select Circuit Elements

**Prompts:**
```
Select all elements connected to circuit 2520343.
```

**Verify:**
- Approval appears in Pending tab with circuit ID in summary.
- After approval, elements are selected in Revit.
- `zoomToSelection=true` zooms to the selection.

---

### 19.3 Select Uncircuited Elements

**Prompts:**
```
Select all electrical fixtures that are not connected to any circuit.
```

**Verify:**
- Approval appears in Pending tab with category list and limit.
- After approval, uncircuited elements are selected in Revit.
- `replaceSelection=false` adds to existing selection.

---

### 19.4 Export Circuit Health to Excel

**Prompts:**
```
Export a circuit health report for panel DB-L1 to Excel.
```

**Verify:**
- `revit_export_circuit_health_to_excel` returns a file path in `Documents\RKTools\RevitMCP\Exports\`.
- File has Summary and Health Issues sheets with autofilter.

---

### 19.5 Export Uncircuited Elements to Excel

**Prompts:**
```
Export all uncircuited fire alarm devices to Excel including ELENEA_Nimetus and ELENEA_Tootja.
```

**Verify:**
- Returns file path.
- Extra parameter columns appear when `returnParameters` is set.

---

### 19.6 Get Circuits for Selected Elements

**Prompts:**
```
Select some devices in Revit, then ask: What circuits are these devices on?
```

**Verify:**
- `revit_get_circuits_for_selected_elements` returns unique circuits only.
- Uncircuited selected elements are reported in warnings.

---

### 19.7 Find Elements on Circuit

**Prompts:**
```
List all elements on circuit 2520343.
Show elements on circuit JK-1/12 including ELENEA_Nimetus.
```

**Verify:**
- Returns element IDs, category, family, type, level.
- `returnParameters` adds extra values per element.

---

## 20. Load Summary, Panel Utilization, Numbering, and Bulk Tools

### 20.1 Get Circuit Load Summary

**Prompts:**
```
Summarize total loads by panel.
Show load by panel and system type.
Show load by cable type.
```

**Verify:**
- `revit_get_circuit_load_summary` groups circuits by the requested keys.
- Each group has `circuitCount` and `totalApparentLoad`.
- `includeCircuitDetails=true` adds per-circuit breakdown.

---

### 20.2 Check Panel Utilization

**Prompts:**
```
Check all panels for missing cable types and load names.
What is the total load on panel DB-L1?
```

**Verify:**
- `revit_check_panel_utilization` returns per-panel: `circuitCount`, `totalApparentLoad`, `missingCableType`, `missingLoadName`, `emptyCircuitNumbers`.
- `includeCircuitDetails=true` adds per-circuit rows.

---

### 20.3 Preview Circuit Numbering

**Prompts:**
```
Preview renumbering circuits on panel DB-L1 starting from 1.
Preview renumbering circuits on JK-1 sorted by load name.
```

**Verify:**
- `revit_preview_circuit_numbering` returns a `changes` list with `oldCircuitNumber`, `newCircuitNumber`, `willChange`.
- Model is NOT modified.
- `changedCount` is returned.

---

### 20.4 Apply Circuit Numbering

**Prompts:**
```
Apply the renumbering shown in the preview to panel DB-L1.
```

**Verify:**
- Approval appears in Pending tab with count of circuits to change.
- After approval, circuit numbers are updated in Revit.
- Revit Undo shows the transaction "Revit MCP - Apply Circuit Numbering".
- Rejection does NOT modify the model.

---

### 20.5 Preview Circuit Load Names

**Prompts:**
```
Preview load names for panel JK-1 using template '{Room Number} {Category}'.
Preview load names for panel DB-L1 from connected elements.
```

**Verify:**
- `revit_preview_circuit_load_names` returns proposals with `oldLoadName`, `newLoadName`, `willChange`.
- Model is NOT modified.
- `{ParameterName}` placeholders are resolved from connected elements or circuit parameters.

---

### 20.6 Apply Circuit Load Names

**Prompts:**
```
Apply the load name proposals for panel JK-1.
```

**Verify:**
- Approval appears in Pending tab with count of circuits to change.
- After approval, Load Name parameters are updated.
- Revit Undo shows "Revit MCP - Apply Circuit Load Names".

---

### 20.7 Set Circuit Parameters Bulk

**Prompts:**
```
Set Comments to "Reviewed" on all circuits on panel DB-L1.
Set Cable Type to "XX_EN_IT_Cat6a" on circuits 2520343 and 2520353 in one operation.
```

**Verify:**
- Approval appears in Pending tab with parameter count and target description.
- After approval, all specified parameters are updated on all target circuits.
- Revit Undo shows "Revit MCP - Set Circuit Parameters Bulk".
- Per-circuit, per-parameter success/failure is reported.

---

## 21. Electrical Dashboard Tools

### 21.1 Get Electrical Dashboard Summary

**Prompts:**
```
Give me an electrical dashboard summary for this model.

Show a summary of electrical issues across all panels.
```

**Verify:**
- `revit_get_electrical_dashboard_summary` returns valid JSON with `"success": true`.
- Response includes panel count and circuit count.
- Issue breakdown is included (circuits missing panel, cable type, load name, duplicate circuit numbers).
- System type summary is present.
- Load summary is present.
- No Revit transaction is created.

---

### 21.2 Get Panel Issue Summary

**Prompts:**
```
Which panels have the most electrical issues?

Show panel issue summary for panel DB-L1.
```

**Verify:**
- `revit_get_panel_issue_summary` groups data by panel.
- `panelName` filter works as a partial match.
- `includeCircuitDetails` controls whether circuit-level rows are included.
- `includeIssueDetails` controls whether per-issue rows are included.
- No Revit transaction is created.

---

### 21.3 Export Electrical Dashboard to Excel

**Prompts:**
```
Export the electrical dashboard summary to Excel.
```

**Verify:**
- `revit_export_electrical_dashboard_to_excel` returns a file path.
- File is created in `Documents\RKTools\RevitMCP\Exports`.
- Workbook opens in Excel.
- Workbook contains a Dashboard sheet and a per-panel Issues sheet.
- Headers are readable and columns are auto-sized.
- No Revit transaction is created.

---

## 22. Circuit Route and Length Estimation Tools

### 22.1 Get Circuit Route Assumptions

**Prompts:**
```
Show route assumptions for circuit 2520343.
```

**Verify:**
- `revit_get_circuit_route_assumptions` returns valid JSON with panel location when available.
- Connected element locations are returned when `includeLocations=true`.
- Location source field indicates the source: `LocationPoint`, `LocationCurve midpoint`, or `BoundingBox center`.
- Missing locations are reported as warnings rather than crashing.
- No length estimate is returned by this tool.
- No Revit transaction is created.

---

### 22.2 Estimate Circuit Length (Single)

**Prompts:**
```
Estimate the length of circuit 2520343 using ManhattanMax with a 1.25 multiplier.

Estimate the length of circuit 2520343 using StraightLineMax.
```

**Verify:**
- `revit_estimate_circuit_length` returns `rawLengthMeters` and `estimatedLengthMeters`.
- Length values are in metres.
- `routingMultiplier` affects `estimatedLengthMeters`.
- All supported methods work without error:
  - `StraightLineMax`
  - `StraightLineSum`
  - `ManhattanMax`
  - `ManhattanSum`
  - `NearestNeighborPath`
- Warnings clearly state that the result is preliminary and not certified cable routing.
- `elementBreakdown` is included only when explicitly requested.
- No Revit transaction is created.

---

### 22.3 Estimate Circuit Lengths (Bulk)

**Prompts:**
```
Estimate circuit lengths for all circuits on panel DB-L1.

Estimate circuit lengths for all PowerCircuit circuits.
```

**Verify:**
- `revit_estimate_circuit_lengths` accepts explicit `circuitIds`.
- `panelName` filter works.
- `systemType` filter works.
- `limit` is respected.
- Bulk failures are reported per circuit without failing the entire tool call.
- No Revit transaction is created.

---

## 23. Voltage-Drop Input Export and Precheck

### 23.1 Export Voltage-Drop Input to Excel

**Prompts:**
```
Export voltage-drop input data for all circuits on panel DB-L1.

Export voltage-drop input data only for circuits 2520343 and 2520353.
```

**Verify:**
- `revit_export_voltage_drop_input_to_excel` returns a file path.
- File is created in `Documents\RKTools\RevitMCP\Exports`.
- Workbook opens in Excel.
- Expected sheets exist: `Summary`, `Voltage Drop Input`, `Circuit Elements`, `Assumptions`, `Failures`.
- `Voltage Drop Input` sheet includes columns: Panel, Circuit Number, Circuit Id, Load Name, System Type, Voltage, Apparent Load, Current Estimate, Cable Type, Wire Type, Estimated Length m, Length Method, Routing Multiplier, Connected Element Count, Elements Missing Location, Warnings.
- `Assumptions` sheet includes the engineering disclaimer.
- `Failures` sheet is populated when data is missing.
- `circuitIds` argument exports only the requested circuits and ignores `panelName`/`systemType`.
- `panelName`/`systemType` filters work correctly when `circuitIds` is not provided.
- No Revit transaction is created.

---

### 23.2 Voltage-Drop Precheck

**Prompts:**
```
Check whether circuit 2520343 has enough data for voltage-drop calculation.

Check voltage-drop readiness for circuits 2520343 and 2520353.
```

**Verify:**
- `revit_get_voltage_drop_precheck` accepts a single `circuitId`.
- `revit_get_voltage_drop_precheck` accepts multiple `circuitIds` as an array.
- Result reports availability of: `voltage`, `load`, `cableType`, `wireType`, `estimatedLength`, `panelLocation`, `elementLocations`.
- `missing` list and `recommendations` are returned.
- Bulk response includes `readyCount` and `notReadyCount` summary.
- Response includes the disclaimer that this is a readiness check only, not a compliance check.
- No Revit transaction is created.

---

## 24. Fire Alarm / ATS Preset Tools

### 24.1 Run Fire Alarm Circuit Preset

**Prompts:**
```
Run the fire alarm circuit preset.

Run the fire alarm circuit preset for panel ATS KS.

Show Ahel 01 device list sorted by Seadme Nr.
```

**Verify:**
- `revit_run_fire_alarm_circuit_preset` collects `Fire Alarm Devices` category only.
- Devices are grouped by `Ahela nr.`.
- Device number is read from `Seadme Nr.`.
- Device type is read using `ContainsNormalized` / partial parameter matching (`ELENEA_Nimetus` matches `ELENEA_ÜLD 001_Nimetus`).
- Description is read from instance or type parameter where available.
- `Seadme Nr. = XXX` is accepted when `allowDeviceNumberXXX=true`; a warning is returned but the tool succeeds.
- Devices are sorted by level and `Seadme Nr.` where available.
- Circuit info is included when available: Revit circuit ID, circuit number, panel, cable type / wire type, circuit length.
- No Revit transaction is created.

---

### 24.2 Loop Classification

**Verify:**
- Loops are classified as one of: `AddressableLoop`, `ConventionalSounderLine`, `ModuleLoop`, `Unknown`.
- `Ahela nr.` containing `#` classifies as `ConventionalSounderLine`.
- Device types containing `sireen` / `vilkur` / `alarmseade` classify as `ConventionalSounderLine`.
- Device types containing `moodul` / `sisend` / `väljund` / `SIM` / `SOM` classify as `ModuleLoop` or `AddressableLoop`.
- Uncertain classification returns `Unknown` with a warning and does not fail the tool.

---

### 24.3 Export Fire Alarm Circuit Preset to Excel

**Prompts:**
```
Export the fire alarm circuit preset to Excel.
```

**Verify:**
- `revit_export_fire_alarm_circuit_preset_to_excel` returns a file path.
- File is created in `Documents\RKTools\RevitMCP\Exports`.
- Workbook opens in Excel.
- Expected sheets exist: `Summary`, `Loop Summary`, `Device List`, `Circuit Info`, `Voltage Drop Input`, `Warnings`.
- `Loop Summary` groups by `Ahela nr.`.
- `Device List` includes element ID, level, `Ahela nr.`, `Seadme Nr.`, device type, description, circuit ID, panel, cable type.
- `Voltage Drop Input` sheet includes length/current/resistance/voltage-drop fields where available.
- `Warnings` sheet includes missing-data warnings.
- No Revit transaction is created.

---

### 24.4 Get Fire Alarm Visualization Data

**Prompts:**
```
Generate fire alarm visualization data for panel ATS KS.
```

**Verify:**
- `revit_get_fire_alarm_visualization_data` returns valid structured JSON.
- Data is grouped by `Ahela nr.`.
- Panel information is included when available.
- Loop kind is included.
- Device count is included.
- Cable type and length are included where available.
- Device coordinates are included where available.
- Warnings are included for missing location or circuit data.
- No HTML file is created by this tool.
- No Revit transaction is created.

> **Note:** Standalone HTML/SVG export (`revit_export_fire_alarm_visualization_html`) is not part of the current implementation. Test `revit_get_fire_alarm_visualization_data` instead. HTML export is intentionally deferred until the JSON format is validated on real projects.

---

### 24.5 Get Fire Alarm Voltage-Drop Summary

**Prompts:**
```
Give me a fire alarm voltage-drop summary using 50 mA per sounder.

Which fire alarm loops are closest to the resistance or voltage-drop limits?
```

**Verify:**
- `revit_get_fire_alarm_voltage_drop_summary` groups results by `Ahela nr.`.
- Addressable loops return loop resistance style data.
- Conventional sounder lines return voltage-drop style data.
- `sounderCurrentMilliAmps` affects total current and voltage-drop result.
- `sounderSupplyVoltage` affects estimated end voltage.
- `minimumSounderVoltage` affects status.
- `addressableLoopMaxResistanceOhm` affects status.
- `fallbackResistanceOhmPerMeter` is used when no cable profile matches.
- Warnings and engineering disclaimers are included in the response.
- No Revit transaction is created.

---

## 25. Cable Resistance Profile Tools

### 25.1 List Cable Resistance Profiles

**Prompts:**
```
List cable resistance profiles.
```

**Verify:**
- `revit_list_cable_resistance_profiles` returns all configured profiles.
- Default profiles are created on first use if the config file is missing.
- Config file is at `%AppData%\RKTools\RevitMCP\electrical-cable-profiles.json`.
- Each profile includes match string(s), description, and resistance value (Ω/m).
- No Revit transaction is created.

---

### 25.2 Get Matching Cable Resistance Profile

**Prompts:**
```
Which cable resistance profile matches cable type "Varjestatud tulepüsiv kaabel 1×2×1.0 FE180/PH90/E90"?

Which cable resistance profile matches "2×0.8 CCA"?
```

**Verify:**
- `revit_get_matching_cable_resistance_profile` returns the best match by cable type name.
- Matching is case-insensitive and uses contains-style lookup.
- When no match exists the tool returns `"success": true` with no match and a clear warning rather than an error.
- No Revit transaction is created.

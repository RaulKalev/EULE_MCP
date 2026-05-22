# Revit MCP Connector — Smoke Test Checklist

Run through this checklist after any code change to verify nothing is broken. All MCP responses should be valid JSON with `"success": true`.

---

## 1. Revit Startup

1. Build Release: `dotnet build RevitMCP.slnx -c Release`
2. Load add-in in Revit 2026 (AppLoader or `.addin` manifest).
3. Open a test model.
4. Open **RK Tools > MCP Connector**.
5. Click **Start Connector**.
6. Confirm status chip turns **Running** (green).

---

## 2. Claude Code Connection

1. Run `RevitMCP.Config\Install\Install-Claude-MCP.bat`.
2. Restart Claude Code.
3. Ask: `call revit_get_connection_status`.
4. Confirm valid JSON response with `"success": true`.

---

## 3. Codex Connection

1. Run `RevitMCP.Config\Install\Install-Codex-MCP.bat`.
2. Paste generated TOML into `%USERPROFILE%\.codex\config.toml`.
3. Restart Codex.
4. Ask: `call revit_get_connection_status`.
5. Confirm valid JSON response with `"success": true`.

---

## 4. Read Tools

```
call revit_get_selected_elements
call revit_count_elements with category = "Fire Alarm Devices"
call revit_get_element_parameters with useSelection = true
call revit_list_views
call revit_list_sheets
call revit_list_schedules
```

All should return valid JSON.

---

## 5. Query Tools

```
Find Fire Alarm Devices where ELENEA_Nimetus contains "andur".
Get element info for Fire Alarm Devices and return ELENEA_Nimetus, ELENEA_Tootja, ELENEA_Mudel.
Group Fire Alarm Devices by ELENEA_Nimetus and ELENEA_Tootja.
```

Verify:
- `revit_find_elements_by_parameter` returns filtered results.
- `revit_get_elements_info` returns parameter values.
- `revit_group_elements` returns grouped counts.
- Parameter partial matching works (e.g. `ELENEA_Nimetus` matches `ELENEA_ULD 001_Nimetus`).

---

## 6. Excel Export

```
Export Fire Alarm Devices grouped by ELENEA_Nimetus and ELENEA_Tootja to Excel.
```

Verify:
- File is created in `Documents\RKTools\RevitMCP\Exports`.
- Open the `.xlsx` and confirm sheets exist: Summary, Groups, Elements, Parameters.
- Data matches the query filters (not unfiltered full model).

---

## 7. Error Handling

### Bridge-level JSON parsing

Invalid JSON is caught at the bridge before it reaches the Revit add-in. All error responses are valid JSON with `"success": false`.

1. Send invalid `filters` JSON (e.g. `filters = "not json"`) to `revit_find_elements_by_parameter` — expected: `"success": false`, clear filters parse error.
2. Send invalid `filters` JSON to `revit_get_elements_info` — expected: `"success": false`, clear filters parse error.
3. Send invalid `groupBy` JSON (e.g. `groupBy = "{ invalid"`) to `revit_group_elements` — expected: `"success": false`, clear groupBy parse error.
4. Send invalid `filters` JSON to `revit_export_query_to_excel` — expected: `"success": false`, no Excel file created.
5. Send invalid `groupBy` JSON to `revit_export_query_to_excel` — expected: `"success": false`, no Excel file created.

### Add-in-level validation

- Call `revit_get_elements_info` with no category, no selection, no element IDs — should return a clear error message.

### Expected error response shape

All parse errors return valid JSON matching the standard response shape:

```json
{
  "success": false,
  "message": "filters could not be parsed as JSON array: ...",
  "durationMs": 0,
  "data": null,
  "warnings": [],
  "errors": ["filters could not be parsed as JSON array: ..."]
}
```

---

## 8. Parameter Discovery

```
What parameters are available for Fire Alarm Devices?
```

Verify:
- `revit_get_available_parameters` returns parameter metadata.
- Response includes `name`, `scope`, `storageType`, `isShared`, `exampleValues`.
- Fill statistics (`existsOnCount`, `emptyCount`, `nonEmptyCount`) are correct.

---

## 9. Query Presets

```
List available query presets.
Run the Fire Alarm Device Report preset.
Run the Fire Alarm Device Report preset and export to Excel.
```

Verify:
- `revit_list_query_presets` lists presets from `%AppData%\RKTools\RevitMCP\query-presets.json`.
- `revit_run_query_preset` runs a preset by name.
- Missing preset name returns available preset names.
- Excel export works when `exportToExcel = true`.

---

## 10. Parameter Completeness Check

```
Check Fire Alarm Devices for missing ELENEA_Nimetus, ELENEA_Tootja, and ELENEA_Mudel.
```

Verify:
- `revit_check_parameter_completeness` returns completionPercent.
- Missing and empty parameters are detected.
- Problem elements list includes element IDs and issue descriptions.

---

## 11. View/Sheet/Schedule Exports

```
Export all views to Excel.
Export all sheets to Excel.
Export all schedules to Excel.
```

Verify:
- Files created in `Documents\RKTools\RevitMCP\Exports`.
- Each file has frozen header row, autofilter, and autosized columns.
- `revit_export_view_list_to_excel` includes sheet placement info.
- `revit_export_sheet_list_to_excel` includes placed view names.
- `revit_export_schedule_list_to_excel` includes field names.

---

## 12. Selection Tools

```
Select elements 12345 and 67890 in Revit.
Find Fire Alarm Devices missing ELENEA_Tootja and select them in Revit.
```

Verify:
- `revit_select_elements` selects by explicit IDs.
- `revit_select_elements_by_query` selects by category/filter query.
- Invalid element IDs are reported in warnings.
- Selection does not modify model data.

---

## 13. Write Tool (Set Parameter)

```
Set Comments to "Checked by AI" for the current selection.
```

Verify:
- `revit_set_parameter` modifies parameter values inside a Transaction.
- Transaction name is "Revit MCP - Set Parameter".
- Read-only parameters are reported as failures.
- Missing parameters are reported as failures.
- Revit Undo shows the transaction.
- Modified and failed element counts are returned.

---

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

Direct Edit mode bypasses approval and should only be used for development/testing. These tests verify that the feature behaves correctly and is safe by default.

### Test: Default state

1. Start Revit with the add-in loaded.
2. Open the MCP Connector window.
3. Confirm the **Approval Required** button shows **Enabled** (Direct Edit is disabled by default).
4. Confirm no confirmation dialog has appeared.

### Test: Enabling Direct Edit

1. With Approval Required enabled, call a write tool.
2. Confirm approval appears in Pending tab (not executed).
3. Reject the pending item.
4. Click the **Approval Required: Enabled** button to toggle Direct Edit on.
5. Confirm a confirmation dialog appears.
6. Confirm the dialog clearly states that write tools will execute **immediately** without queuing.
7. Confirm the dialog clearly labels this as **Dev/Admin Only**.
8. Confirm the default focused button is **Cancel** (not Enable).
9. Click **Cancel** — confirm Direct Edit is still disabled.
10. Click the button again, confirm the dialog appears.
11. Click **Enable Direct Edit**.
12. Confirm the button now shows **Approval Required: Disabled** (red/warning color).

### Test: Write tool with Direct Edit enabled

1. With Direct Edit enabled, call a write tool (e.g. `revit_set_circuit_parameter`).
2. Confirm the tool executes **immediately** — no pending approval appears.
3. Confirm the model is changed.
4. Confirm Revit Undo shows the transaction.

### Test: Disabling Direct Edit

1. Click the **Approval Required: Disabled** button.
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
- `MissingPanel`, `EmptyCircuitNumber`, `DuplicateCircuitNumbers`, `MissingCableType`, `MissingWireType`, `MissingLoadName`, `NoConnectedElements` are detected.
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

---

## 26. View/Sheet Discovery Tools

### 26.1 View Sheet Summary

**Prompts:**
```
Give me a view and sheet summary for this model.
How many views are unplaced?
```

**Verify:**
- `revit_get_view_sheet_summary` returns `totalSheets`, `totalViews`, `placedViews`, `unplacedViews`, `viewsWithTemplate`, `sheetsWithTitleBlock`.
- No Revit transaction is created.

---

### 26.2 List Title Blocks

**Prompts:**
```
List all title blocks loaded in the model.
Which title block should I use when creating new sheets?
```

**Verify:**
- `revit_list_titleblocks` returns `familySymbolId`, `familyName`, `typeName`, `isInUse` per entry.
- `familySymbolId` can be used directly in `revit_create_sheets_from_table`.
- No Revit transaction is created.

---

### 26.3 List View Templates

**Prompts:**
```
List all view templates.
List all floor plan view templates.
```

**Verify:**
- `revit_list_view_templates` returns `elementId`, `name`, `viewType`, `assignedViewCount`.
- Optional `viewType` filter (e.g. `"FloorPlan"`) narrows results.
- `elementId` can be used directly in `revit_apply_view_template`.
- No Revit transaction is created.

---

### 26.4 List Revisions

**Prompts:**
```
List all revisions in the model.
```

**Verify:**
- `revit_list_revisions` returns at least `elementId`, `sequenceNumber`, `revisionDate`, `description`, `issuedBy`, `visibility`.
- No Revit transaction is created.

---

### 26.5 Get Sheet Viewports

**Prompts:**
```
What views are placed on sheet "E-01"?
Show viewport details for sheets E-01 and E-02.
```

**Verify:**
- `revit_get_sheet_viewports` accepts `sheetIds` (long[]) or `sheetNumbers` (string[]).
- Returns `viewportId`, `viewId`, `viewName`, `viewType`, `sheetPosition`, `detailNumber` per viewport.
- Invalid or unknown sheet IDs/numbers return a clear warning rather than an error.
- No Revit transaction is created.

---

### 26.6 Find Unplaced Views

**Prompts:**
```
Find all views not placed on any sheet.
Find all unplaced floor plan views.
Find the first 10 unplaced views.
```

**Verify:**
- `revit_find_unplaced_views` returns views not currently on any sheet.
- `viewTypes` filter (e.g. `["FloorPlan"]`) narrows results.
- `nameFilter` matches on partial name.
- `includeTemplates=false` (default) excludes view templates.
- `limit` caps the result count.
- No Revit transaction is created.

---

### 26.7 Enhanced revit_list_views

**Prompts:**
```
List all floor plan and section views with name containing "KORRUS".
List views and return their Discipline and View Scale parameters.
```

**Verify:**
- `viewTypes` parameter filters by Revit view type name.
- `nameFilter` does a substring match on the view name.
- `returnParameters` reads extra parameters per view using partial name matching.
- `includeTemplates=false` (default) hides view templates.
- `limit` caps results.
- New return fields: `uniqueId`, `sheetId`, `sheetNumber`, `sheetName` (when placed), `viewTemplateId`, `viewTemplateName`.

---

### 26.8 Enhanced revit_list_sheets

**Prompts:**
```
List all sheets with number starting with "E-".
List sheets and return the Märkus and Sheet Issue Date parameters.
List sheets with full viewport detail.
List sheets using default Estonian parameters.
```

**Verify:**
- `numberFilter` does a substring match on sheet number.
- `nameFilter` does a substring match on sheet name.
- `returnParameters=["default"]` expands to 10 standard sheet params: Sheet Number, Sheet Name, Project Number, Project Status, Projekti osa, Grupi tähis, Järjekorra tähis, Current Revision, Märkus, Sheet Issue Date.
- `includeViewports=true` returns viewport detail (viewId, viewName, position, detailNumber) per sheet.
- `limit` caps results.
- New return fields: `uniqueId`, `titleBlockId`, `titleBlockName`.

---

## 27. View/Sheet Preview Tools

All preview tools are read-only — they return proposals without modifying the model.

### 27.1 Preview Place Views on Sheets

**Prompts:**
```
Preview placing unplaced floor plan views on sheets.
Preview matching views to sheets using fuzzy matching with threshold 0.7.
```

**Verify:**
- `revit_preview_place_views_on_sheets` requires `viewIds`.
- Match modes work: `ExactName`, `Contains`, `Fuzzy`, `SheetNumberPrefix`, `SheetNumberSuffix`, `CustomParameter`.
- `fuzzyThreshold` (0–1) affects which views get a match in Fuzzy mode.
- `skipAlreadyPlaced=true` (default) excludes views already on sheets.
- Returns `proposals` list with `viewId`, `viewName`, `matchedSheetId`, `matchedSheetNumber`, `score`, `reason`.
- No Revit transaction is created.

---

### 27.2 Preview Duplicate Sheets

**Prompts:**
```
Preview duplicating sheets E-01 and E-02 with suffix "_V2".
```

**Verify:**
- Returns proposals with `sourceSheetNumber`, `newSheetNumber`, `newSheetName`, `titleBlockName`, `conflict`.
- `conflict=true` when the generated sheet number already exists.
- No Revit transaction is created.

---

### 27.3 Preview Create Sheets From Table

**Prompts:**
```
Preview creating 3 new sheets: E-10 "Elektripaigaldis", E-11 "Valgustus", E-12 "Maandus".
```

**Verify:**
- Returns per-row validation: `valid`, `conflict`, `issues`.
- Rows with duplicate sheet numbers in the input set or that conflict with existing sheets are flagged.
- `titleBlockId` must be a valid family symbol ID (from `revit_list_titleblocks`).
- No Revit transaction is created.

---

### 27.4 Preview Rename Views

**Prompts:**
```
Preview renaming all floor plan views — replace "KORRUS" with "Level".
Preview adding prefix "EL-" to all electrical section views.
```

**Verify:**
- `mode` must be one of: `FindReplace`, `PrefixSuffix`, `Template`, `RegexFindReplace`.
- `FindReplace` requires `find`; `replace` defaults to empty string.
- `PrefixSuffix` requires at least one of `prefix`, `suffix`.
- `Template` requires `template` with `{Name}` placeholder.
- Returns `oldName`, `newName`, `willChange` per view.
- Views where the new name equals the old name have `willChange=false`.
- No Revit transaction is created.

---

### 27.5 Preview Rename Sheets

**Prompts:**
```
Preview renaming sheet names — replace "Elektripaigaldis" with "Electrical".
Preview adding suffix " (Rev A)" to sheet names for sheets matching number "E-".
Preview renaming sheet numbers — replace "E-" with "EL-".
```

**Verify:**
- `target` parameter: `Name`, `Number`, or `Both`.
- Same rename modes as `revit_preview_rename_views`.
- `numberFilter` / `nameFilter` narrow which sheets are included.
- Returns `oldName`, `newName`, `oldNumber`, `newNumber`, `willChange` per sheet.
- No Revit transaction is created.

---

## 28. View/Sheet Write Tools (Requires Approval)

All write tools follow the standard approval flow: response shows `approval_required`, Pending tab shows the request, Approve/Reject controls are available.

### 28.1 Place Views on Sheets

**Prompts:**
```
Run revit_preview_place_views_on_sheets first, then place the views.
```

**Verify:**
1. Run preview first — confirm proposals look correct.
2. Call `revit_place_views_on_sheets` with same parameters.
3. Approval appears in Pending tab.
4. Reject → no change, Revit Undo unchanged.
5. Approve → views appear on matched sheets in Revit.
6. Revit Undo shows `"Revit MCP - Place Views on Sheets"`.
7. Already-placed views are skipped when `skipAlreadyPlaced=true`.

---

### 28.2 Duplicate Sheets

**Prompts:**
```
Duplicate sheets E-01 and E-02 with number suffix "_COPY".
```

**Verify:**
- Approval required; new sheets created after approval.
- New sheet numbers and names match the preview.
- `keepTitleBlock=true` gives new sheets the same title block family.
- `copyParameters=true` copies instance parameter values.
- Transaction name: `"Revit MCP - Duplicate Sheets"`.

---

### 28.3 Create Sheets From Table

**Prompts:**
```
Create sheets E-10 "Elektripaigaldis", E-11 "Valgustus" with title block ID [from revit_list_titleblocks].
```

**Verify:**
- Run preview first to confirm no conflicts.
- After approval, new sheets appear in Revit.
- Sheets with conflicting numbers are skipped with a warning; others still created.
- Parameter values from the row are applied after sheet creation.
- Transaction name: `"Revit MCP - Create Sheets From Table"`.

---

### 28.4 Duplicate Views

**Prompts:**
```
Duplicate view 12345 with option DuplicateWithDetailing and suffix " - Copy".
```

**Verify:**
- `duplicateOption`: `Duplicate`, `DuplicateWithDetailing`, `AsDependent`.
- Views that do not support duplication are skipped with a warning.
- New view names follow `{namePrefix}{originalName}{nameSuffix}`.
- Transaction name: `"Revit MCP - Duplicate Views"`.

---

### 28.5 Apply View Template

**Prompts:**
```
Apply view template 56789 to all floor plan views containing "KORRUS".
```

**Verify:**
- `viewTemplateId` must be a valid view template element ID.
- Can target by explicit `viewIds`, or by `viewTypes` + `nameFilter`.
- `limit` caps how many views are updated.
- Invalid template ID returns `"success": false` with a clear error.
- Transaction name: `"Revit MCP - Apply View Template"`.

---

### 28.6 Rename Views

**Prompts:**
```
Rename all section views — replace "Section" with "Lõige".
Add prefix "EL-" to all floor plan views with name containing "KORRUS".
```

**Verify:**
1. Run `revit_preview_rename_views` first to confirm proposals.
2. Call `revit_rename_views` with same parameters.
3. Approval in Pending tab.
4. After approval, view names updated in Revit.
5. Transaction name: `"Revit MCP - Rename Views"`.
6. Rejection leaves all names unchanged.

---

### 28.7 Rename Sheets

**Prompts:**
```
Rename sheet names — replace "Elektripaigaldis" with "Electrical".
Rename sheet numbers — replace prefix "E-" with "EL-".
```

**Verify:**
- Same modes as rename views.
- `target=Name` only renames the sheet name; `target=Number` only the sheet number; `target=Both` renames both.
- Transaction name: `"Revit MCP - Rename Sheets"`.
- Duplicate resulting sheet numbers are rejected with a per-sheet error; others still renamed.

---

### 28.8 Set Sheet Parameters Bulk

**Prompts:**
```
Set "Märkus" to "Rev A" on sheets E-01 and E-02.
Set "Project Status" to "Issued for Construction" on all sheets with number starting with "E-".
```

**Verify:**
- Accepts `sheetIds`, `sheetNumbers`, and/or `nameFilter` to identify target sheets.
- `parameters` is a key→value map of parameter name to value.
- Partial parameter name matching works (Contains mode).
- Read-only parameters are reported as per-sheet failures without aborting the batch.
- Transaction name: `"Revit MCP - Set Sheet Parameters Bulk"`.

---

### 28.9 Set View Parameters Bulk

**Prompts:**
```
Set "Comments" to "Reviewed" on all floor plan views.
```

**Verify:**
- Same verification as 28.8 but targets views.
- `includeTemplates=false` (default) skips view templates.
- Transaction name: `"Revit MCP - Set View Parameters Bulk"`.

---

## 29. View/Sheet Destructive Delete Tools

> **Safety:** `revit_delete_views` and `revit_delete_sheets` use `DestructiveRequiresManualApproval` — they **always** require manual approval in the Pending tab even when Direct Edit mode is enabled. This cannot be overridden.

### 29.1 Preview Delete Views

**Prompts:**
```
Preview deleting all unplaced section views with name containing "Working".
```

**Verify:**
- `revit_preview_delete_views` returns the views that would be deleted with `elementId`, `name`, `viewType`.
- `skipPlacedOnSheets=true` (default) excludes views currently placed on a sheet.
- `viewTypes` + `nameFilter` narrow scope.
- No Revit transaction is created.

---

### 29.2 Delete Views

**Prompts:**
```
Delete views 12345 and 67890.
```

**Verify:**
1. Run `revit_preview_delete_views` first.
2. Call `revit_delete_views` with confirmed view IDs.
3. **Direct Edit enabled** → approval still required (not bypassed).
4. Rejection → model unchanged.
5. Approval → views are deleted.
6. `skipPlacedOnSheets=true` causes placed views to be skipped with a warning.
7. Transaction name: `"Revit MCP - Delete Views"`.
8. Revit Undo restores deleted views.

---

### 29.3 Preview Delete Sheets

**Prompts:**
```
Preview deleting sheets with number containing "_COPY".
```

**Verify:**
- `revit_preview_delete_sheets` returns sheets with `elementId`, `sheetNumber`, `sheetName`, `viewportCount`.
- `skipSheetsWithViews=true` (default) excludes sheets that have placed viewports.
- No Revit transaction is created.

---

### 29.4 Delete Sheets

**Prompts:**
```
Delete sheets with number suffix "_COPY".
```

**Verify:**
1. Run `revit_preview_delete_sheets` first.
2. Call `revit_delete_sheets` with confirmed IDs or numbers.
3. **Direct Edit enabled** → approval still required (not bypassed).
4. Rejection → model unchanged.
5. Approval → sheets are deleted.
6. `skipSheetsWithViews=true` skips occupied sheets with a per-sheet warning.
7. Transaction name: `"Revit MCP - Delete Sheets"`.
8. Revit Undo restores deleted sheets.

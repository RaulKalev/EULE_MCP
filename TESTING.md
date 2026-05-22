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

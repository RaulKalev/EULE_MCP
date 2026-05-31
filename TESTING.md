# Revit MCP Connector — Smoke Test Checklist

Run through this checklist after any code change to verify nothing is broken. All MCP responses should be valid JSON with `"success": true`.

> **Note:** Several tool families were consolidated into discriminator-based tools
> (e.g. `revit_delete_views`/`revit_delete_sheets` → `revit_delete` with
> `target`). Some tool names referenced below are the pre-consolidation names.
> See [docs/tool-consolidation.md](docs/tool-consolidation.md) for the full
> old → new mapping.

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
call revit_list_views with viewTypes=["FloorPlan","Section"] and nameFilter="KORRUS"
call revit_list_views with returnParameters=["Discipline"] and includePlacedStatus=true
call revit_list_sheets
call revit_list_sheets with numberFilter="E-" and returnParameters=["default"]
call revit_list_sheets with includeViewports=true
call revit_list_schedules
```

All should return valid JSON. `revit_list_views` and `revit_list_sheets` accept optional filter and parameter arguments — see Section 26.7 and 26.8 for full parameter test coverage.

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

- Call `revit_get_elements_info` with no category, no selection, no element IDs — should return a clear error message that mentions `summaryOnly=true`.

### Query Safety Smoke Tests

Run these manually through an MCP client connected to Revit:

**1 — Broad detailed query is rejected**
```json
{ "tool": "revit_get_elements_info", "arguments": {} }
```
Expected: `success=false`, message mentions `summaryOnly=true`.

**2 — Broad `summaryOnly=true` query succeeds**
```json
{ "tool": "revit_get_elements_info", "arguments": { "summaryOnly": true } }
```
Expected: `success=true`, `data.summary` contains `totalElements`, `categories`, `families`; `data.elements` is empty.

**3 — Large `pageSize` is clamped to MaxPageSize (500)**
```json
{ "tool": "revit_get_elements_info", "arguments": { "category": "Fire Alarm Devices", "pageSize": 999999 } }
```
Expected: `success=true`, `data.pageSize <= 500`, warning mentions clamping.

**4 — Default page size applies (100) when `pageSize` is omitted**
```json
{ "tool": "revit_get_elements_info", "arguments": { "category": "Fire Alarm Devices" } }
```
Expected: `success=true`, `data.pageSize == 100` (or fewer if fewer than 100 elements matched).

**5 — Explicit smaller page size is respected**
```json
{ "tool": "revit_get_elements_info", "arguments": { "category": "Fire Alarm Devices", "pageSize": 10 } }
```
Expected: `success=true`, `data.returned <= 10`, `data.hasMore=true` if more than 10 matched.

**6 — Second page works**
```json
{ "tool": "revit_get_elements_info", "arguments": { "category": "Fire Alarm Devices", "pageSize": 10, "page": 1 } }
```
Expected: `success=true`, `data.page=1`, `data.returned <= 10`.

**7 — Long parameter values are truncated**
```json
{ "tool": "revit_get_elements_info", "arguments": { "category": "Walls", "truncateStringLength": 20, "pageSize": 5 } }
```
Expected: `success=true`, no parameter value in `data.elements` exceeds 20 chars (long values end with `... [truncated]`).

**8 — Parameter count is capped**
```json
{ "tool": "revit_get_elements_info", "arguments": { "category": "Walls", "maxParametersPerElement": 3, "pageSize": 5 } }
```
Expected: `success=true`, each element in `data.elements` has at most 3 parameters.

**9 — ResponseGuard catches oversized response**
Query a category with thousands of elements and no pageSize (will be capped at 100 by default). If that still exceeds 1 MB, expect `success=false`, `status="ResponseTooLarge"`, `data.suggestedActions` with narrowing advice.

**10 — Timeout returns a clean error**
If a long-running query times out (30 s limit per `QueryLimits.TimeoutSeconds`), expect `success=false`, message indicates timeout rather than freezing the MCP client.

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

### 26.9 Revision Numbering Sequences

**Prompts:**
```
List all revision numbering sequences in the model.
```

**Verify:**
- `revit_list_revision_numbering_sequences` returns a list (may be empty on projects with no custom sequences).
- Each entry has `sequenceId`, `name`, `numberingType`, `prefix`, `suffix`, `minimumDigits`.
- Tool returns `success: true` with an explanatory message when the list is empty.
- No Revit transaction is created.

---

### 26.10 Sheet Revisions

**Prompts:**
```
What revisions are shown on sheet E-01?
Get revision details for sheets E-01, E-02, and E-03.
```

**Verify:**
- `revit_get_sheet_revisions` accepts `sheetIds` (long[]) or `sheetNumbers` (string[]).
- Requires at least one of `sheetIds` or `sheetNumbers`.
- Each entry has `sheetId`, `sheetNumber`, `sheetName`, `revisionCount`, `revisions`.
- Each revision entry has `revisionId`, `sequenceNumber`, `revisionNumber`, `revisionDate`, `description`, `issuedBy`, `issuedTo`.
- `includeRevisionDetails=false` returns counts only (no per-revision list).
- Sheets not found by ID or number produce a warning but the tool still succeeds for valid sheets.
- No Revit transaction is created.
- **Note:** Cloud-specific revision visibility (workshared model cloud tracking) may not be surfaced by the Revit 2026 API and may not appear in results.

---

### 26.11 List View/Sheet Presets

**Prompts:**
```
List available PlaceViews presets.
```

**Verify:**
- `revit_list_view_sheet_presets` checks `C:\ProgramData\RK Tools\PlaceViews\SheetManagerSettings`.
- If the folder does not exist, returns `success: false` with a clear path message.
- Each preset entry has `FileName`, `DetectedType`, `SizeBytes`, `modifiedUtc`.
- No Revit transaction is created.

---

### 26.12 Get View/Sheet Preset

**Prompts:**
```
Read the preset file "MyPreset.json".
Read preset "MyPreset" (without extension).
```

**Verify:**
- `revit_get_view_sheet_preset` accepts `presetName` with or without `.json` extension.
- Returns `workflowType`, `parsedContent`.
- Path traversal attempts (e.g. `presetName = "../../../etc/passwd"`) return `success: false` without reading any file.
- Non-existent preset returns `success: false` with a clear error.
- No Revit transaction is created.

---

### 26.13 Validate View/Sheet Preset

**Prompts:**
```
Validate the preset "MyPreset.json".
```

**Verify:**
- `revit_validate_view_sheet_preset` returns `isValid`, `workflowType`, `errors[]`, `suggestions[]`.
- An empty JSON `{}` returns `isValid: false` with an appropriate error.
- A valid preset returns `isValid: true`.
- No Revit transaction is created.

---

### 26.14 Run View/Sheet Workflow Preset (planning only)

**Prompts:**
```
Plan the workflow from preset "MyPreset.json".
What steps would running preset "SheetManagerSettings.json" perform?
```

**Verify:**
- `revit_run_view_sheet_workflow_preset` returns `workflowType`, `stepCount`, `steps[]`, `notes[]`.
- Notes confirm that no model changes were made.
- Steps list human-readable descriptions with suggested follow-up tool calls.
- No Revit transaction is created.

> **Deferred:** `revit_create_sheets_from_preset` (direct single-call execution of a PlaceViews preset) is not yet implemented. Use `revit_run_view_sheet_workflow_preset` to plan the workflow, then execute steps with `revit_create_sheets_from_table`, `revit_duplicate_sheets`, etc.

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

> **Note – Direct Edit mode**: When Direct Edit is enabled in the Revit panel, `RequiresApproval` tools execute immediately without queuing an approval request (see `ExternalEventHandler.cs`). In that mode the tests in this section will NOT show the approval flow — the action executes directly. To verify the approval flow, ensure **Direct Edit is disabled**. Exception: `DestructiveRequiresManualApproval` tools (Delete Views, Delete Sheets) always require manual approval regardless of Direct Edit state.

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

**Test: EmptyDetailOnly returns a clear error (deferred)**

```
Duplicate view 12345 with option EmptyDetailOnly.
```

Verify:
- `revit_duplicate_views` returns `success: false` with message: `"EmptyDetailOnly is not implemented in this MCP version. Supported duplicateOption values are Duplicate, DuplicateWithDetailing, and AsDependent."`
- `revit_preview_duplicate_views` returns the same error for `EmptyDetailOnly`.
- Model is not modified.

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



---

## 30. Full MCP Tool Coverage Matrix

This section enumerates every MCP tool exposed by `RevitMCP.Bridge` and describes a one-shot smoke test for an agent to run. The intent is end-to-end runnability without a human present (except for steps explicitly noted as **manual approval**).

**Column legend:**
- **Permission** — One of `ReadOnly` (RO), `RequiresApproval` (RA), `DestructiveRequiresManualApproval` (DRA). Source: `RevitMCP.Core/Models/ToolPermission.cs`.
- **Smoke Prompt** — A natural-language prompt to send through the MCP client.
- **Expected Result** — What success looks like at the JSON layer.
- **Approval** — Approval behavior expected. `Direct Edit` (DE) **bypasses RA** but **never** bypasses DRA.
- **Undo** — Revit Undo expectation.
- **Notes** — Required test data / caveats.

**Baseline ReadOnly expectation (applies to every RO row):** returns valid JSON, does **not** open a Revit transaction, does **not** modify the model, returns a clear error/warning for invalid input (unknown id, empty array, bad parameter name).

**Baseline RequiresApproval expectation:** preview tool first → call writer → `approval_required` returned when Direct Edit is OFF → pending item appears in MCP window → user can approve or reject → on approval a single named Revit transaction commits → Revit Undo reverses it. With Direct Edit ON the same writer executes immediately, no `approval_required`.

**Baseline DestructiveRequiresManualApproval expectation:** preview tool returns dry-run JSON → destructive writer **always** returns `approval_required` (even with Direct Edit ON) → approval summary contains a `DESTRUCTIVE` warning line → on approval a named Revit transaction commits → Revit Undo restores deleted items.

---

### 30.1 Connection & General

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Connection | `revit_get_connection_status` | RO | "Check the Revit MCP connection status." | JSON with `connected: true`, document title, version | N/A | N/A | Requires Revit running, addin loaded, document open |
| Selection | `revit_get_selected_elements` | RO | "List my currently selected elements in Revit." | Array of `{elementId, category, name}` or empty array | N/A | N/A | Pre-select 1+ elements in Revit before running |
| Selection | `revit_inspect_selected_elements` | RO | "Inspect my currently selected elements." | Array with `category`, `familyName`, `typeName`, `location`, `boundingBoxMm`, `parameters` per element | N/A | N/A | Pre-select 1+ elements; full coverage in S1 |

### 30.2 Query & Parameter

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Query | `revit_list_views` | RO | "List the first 50 views." | Array of view dicts with id/name/type/scale | N/A | N/A | — |
| Query | `revit_list_sheets` | RO | "List all sheets." | Array of `{id, number, name, titleBlockType}` | N/A | N/A | At least 1 sheet recommended |
| Query | `revit_list_schedules` | RO | "List schedules in the project." | Array of schedule names + ids | N/A | N/A | At least 1 schedule recommended |
| Query | `revit_count_elements` | RO | "Count elements in category Walls." | `{category, count}` JSON | N/A | N/A | — |
| Query | `revit_group_by_parameter` | RO | "Group walls by 'Type Name'." | Dictionary of groups with element ids | N/A | N/A | — |
| Query | `revit_find_elements_by_parameter` | RO | "Find walls where Comments contains 'TEST'." | Filtered element list | N/A | N/A | Requires populated parameter values |
| Query | `revit_get_elements_info` | RO | "Get info for element id 12345." | Array of `ElementInfoDto` | N/A | N/A | Use a known id from list/count |
| Query | `revit_group_elements` | RO | "Group selected elements by category." | Grouping result JSON | N/A | N/A | — |
| Params | `revit_get_element_parameters` | RO | "Show parameters for element 12345." | Array of `{name, value, isReadOnly}` | N/A | N/A | — |
| Params | `revit_get_available_parameters` | RO | "List parameters available on Walls." | Array of `{name, type, source}` | N/A | N/A | — |
| Params | `revit_check_parameter_completeness` | RO | "Check which walls are missing 'Fire Rating'." | Stats + list of missing elements | N/A | N/A | — |
| Presets | `revit_list_query_presets` | RO | "List query presets." | Array of preset names + descriptions | N/A | N/A | Presets live under user data folder |
| Presets | `revit_run_query_preset` | RO | "Run query preset 'WallAudit'." | Same shape as the underlying query | N/A | N/A | Skip if no presets configured |

### 30.3 Generic Excel Export

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Export | `revit_export_query_to_excel` | RO | "Export a Walls query to Excel." | `{filePath}` of created `.xlsx` | N/A | N/A | File created under export path; no Revit transaction |
| Export | `revit_export_view_list_to_excel` | RO | "Export the view list to Excel." | `{filePath}` | N/A | N/A | — |
| Export | `revit_export_sheet_list_to_excel` | RO | "Export the sheet list to Excel." | `{filePath}` | N/A | N/A | — |
| Export | `revit_export_schedule_list_to_excel` | RO | "Export the schedule list to Excel." | `{filePath}` | N/A | N/A | — |

### 30.4 Selection

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Selection | `revit_select_elements` | RO | "Select elements with ids [12345, 12346]." | `{count}` and elements highlighted in Revit UI | N/A | N/A | Selection is UI-only, not a model change — runs in UI thread but uses no DB transaction |
| Selection | `revit_select_elements_by_query` | RO | "Select all walls on Level 1." | `{count}` and elements highlighted | N/A | N/A | Same UI-only semantics |

### 30.5 Generic Write

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Write | `revit_set_parameter` | RA | "Set Comments='SMOKE' on element 12345." | Preview-like summary → approval flow → applied | Required (DE bypasses) | Single Undo | Pick a writable, non-instance-locked parameter |


### 30.6 Electrical Circuit Discovery

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Electrical | `revit_get_electrical_circuits` | RO | "List all electrical circuits on panel 'LP-1'." | Array of circuit dicts | N/A | N/A | Requires electrical model with a panel |
| Electrical | `revit_get_circuit_info` | RO | "Get info for circuit id 99999." | Circuit DTO with load/length/panel | N/A | N/A | Use a circuit id from list call |
| Electrical | `revit_get_available_panels` | RO | "List panels in the model." | Array of panels with id/name/voltage | N/A | N/A | Requires at least 1 panel |
| Electrical | `revit_get_available_cable_types` | RO | "List cable types." | Array of cable types | N/A | N/A | — |
| Electrical | `revit_get_available_wire_types` | RO | "List wire types." | Array of wire types | N/A | N/A | — |
| Electrical | `revit_get_circuit_compatible_elements` | RO | "List elements compatible with circuit id 99999." | Array of element refs | N/A | N/A | — |
| Electrical | `revit_find_uncircuited_elements` | RO | "Find uncircuited Lighting Fixtures." | Array of element refs | N/A | N/A | Useful even if empty |
| Electrical | `revit_find_circuits_by_element_parameter` | RO | "Find circuits where 'Load Name' contains 'EXIT'." | Array of circuits | N/A | N/A | — |
| Electrical | `revit_trace_circuit` | RO | "Trace circuit id 99999." | Tree of element/panel relationships | N/A | N/A | — |
| Electrical | `revit_check_circuit_parameter_completeness` | RO | "Check circuit parameter completeness on panel 'LP-1'." | Stats + missing list | N/A | N/A | — |
| Electrical | `revit_get_circuits_for_selected_elements` | RO | "Get circuits for my selected elements." | Array of circuits | N/A | N/A | Pre-select elements |
| Electrical | `revit_find_elements_on_circuit` | RO | "List elements on circuit id 99999." | Array of element refs | N/A | N/A | — |
| Electrical | `revit_get_circuit_load_summary` | RO | "Show load summary for panel 'LP-1'." | Aggregated load JSON | N/A | N/A | — |
| Electrical | `revit_check_panel_utilization` | RO | "Check utilization for panel 'LP-1'." | Utilization stats | N/A | N/A | — |
| Electrical | `revit_check_circuit_health` | RO | "Run circuit health check on panel 'LP-1'." | Issues array | N/A | N/A | — |

### 30.7 Electrical Circuit Modification

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Electrical | `revit_create_electrical_circuit` | RA | "Create a 1-phase 20A circuit on panel 'LP-1' with elements [id1,id2]." | Preview summary → approval flow → new circuit id | Required (DE bypasses) | Single Undo | Use compatible elements from `get_circuit_compatible_elements` |
| Electrical | `revit_add_elements_to_circuit` | RA | "Add element 12345 to circuit id 99999." | Approval flow → updated circuit | Required (DE bypasses) | Single Undo | — |
| Electrical | `revit_reassign_circuit_panel` | RA | "Reassign circuit 99999 to panel 'LP-2'." | Approval flow → circuit moved | Required (DE bypasses) | Single Undo | Requires 2 panels |
| Electrical | `revit_change_circuit_cable_or_wire_type` | RA | "Change cable type of circuit 99999 to 'THHN-12'." | Approval flow → type changed | Required (DE bypasses) | Single Undo | Cable type must exist in model |
| Electrical | `revit_set_circuit_path_mode` | RA | "Set path mode to All Devices for circuits on panel 'LP-1'." | `approval_required` → `{updated, skippedCustomPathCount, skippedUnsupportedModeCount}` | Required (DE bypasses) | Single Undo | Skips circuits with custom/manual paths; scope: `useSelection`, `circuitIds`, or all circuits |
| Electrical | `revit_set_circuit_parameter` | RA | "Set Comments='SMOKE' on circuit 99999." | Approval flow → parameter set | Required (DE bypasses) | Single Undo | — |
| Electrical | `revit_set_circuit_parameters_bulk` | RA | "Set Comments='SMOKE' on circuits [99999, 99998]." | Approval summary lists circuit count + parameter count → flow → applied | Required (DE bypasses) | Single Undo | — |

### 30.8 Electrical Numbering & Load Names

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Electrical | `revit_preview_circuit_numbering` | RO | "Preview renumbering circuits on panel 'LP-1' starting at 1." | Dry-run mapping `{circuitId → newNumber}` | N/A | N/A | — |
| Electrical | `revit_apply_circuit_numbering` | RA | "Apply that renumbering preview." | Approval flow → numbers committed | Required (DE bypasses) | Single Undo | Run preview first |
| Electrical | `revit_preview_circuit_load_names` | RO | "Preview load name updates on panel 'LP-1'." | Dry-run mapping | N/A | N/A | — |
| Electrical | `revit_apply_circuit_load_names` | RA | "Apply that load-name preview." | Approval flow → load names committed | Required (DE bypasses) | Single Undo | Run preview first |

### 30.9 Electrical Selection

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Electrical | `revit_select_circuit_elements` | RA | "Select all elements on circuit 99999." | Approval flow → UI selection updated | Required (DE bypasses) | N/A (UI-only) | Marked RA in addin; no model change but classified as RA |
| Electrical | `revit_select_uncircuited_elements` | RA | "Select all uncircuited Lighting Fixtures." | Approval flow → UI selection updated | Required (DE bypasses) | N/A (UI-only) | Same UI-only semantics |

### 30.10 Electrical Excel Export

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Export | `revit_export_panel_circuit_list_to_excel` | RO | "Export panel 'LP-1' circuit list to Excel." | `{filePath}` | N/A | N/A | — |
| Export | `revit_export_circuit_health_to_excel` | RO | "Export circuit health for panel 'LP-1' to Excel." | `{filePath}` | N/A | N/A | — |
| Export | `revit_export_uncircuited_elements_to_excel` | RO | "Export uncircuited Lighting Fixtures to Excel." | `{filePath}` | N/A | N/A | — |

### 30.11 Electrical Dashboard

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Dashboard | `revit_get_electrical_dashboard_summary` | RO | "Show electrical dashboard summary." | Aggregated panel/circuit/load JSON | N/A | N/A | — |
| Dashboard | `revit_get_panel_issue_summary` | RO | "Show panel issue summary." | Per-panel issue counts | N/A | N/A | — |
| Dashboard | `revit_export_electrical_dashboard_to_excel` | RO | "Export the electrical dashboard to Excel." | `{filePath}` | N/A | N/A | — |

### 30.12 Voltage-Drop Preparation

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| VDrop | `revit_get_circuit_route_assumptions` | RO | "Show circuit route assumptions." | Assumption JSON | N/A | N/A | — |
| VDrop | `revit_estimate_circuit_length` | RO | "Estimate length for circuit 99999." | `{length, method, assumptions}` | N/A | N/A | — |
| VDrop | `revit_estimate_circuit_lengths` | RO | "Estimate lengths for circuits on panel 'LP-1'." | Array of `{circuitId, length}` | N/A | N/A | — |
| VDrop | `revit_export_voltage_drop_input_to_excel` | RO | "Export voltage-drop input for panel 'LP-1' to Excel." | `{filePath}` | N/A | N/A | — |
| VDrop | `revit_get_voltage_drop_precheck` | RO | "Run voltage-drop precheck on panel 'LP-1'." | Per-circuit precheck JSON | N/A | N/A | — |

### 30.13 Fire Alarm / ATS Preset

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| FireAlarm | `revit_run_fire_alarm_circuit_preset` | RO | "Run the fire alarm circuit preset on panel 'FACP'." | Preset result JSON | N/A | N/A | Requires fire alarm panel naming convention |
| FireAlarm | `revit_export_fire_alarm_circuit_preset_to_excel` | RO | "Export the fire alarm preset for panel 'FACP' to Excel." | `{filePath}` | N/A | N/A | — |
| FireAlarm | `revit_get_fire_alarm_visualization_data` | RO | "Get fire alarm visualization data for panel 'FACP'." | Visualization JSON | N/A | N/A | HTML export tool is deferred — see Special Notes |
| FireAlarm | `revit_get_fire_alarm_voltage_drop_summary` | RO | "Get fire alarm voltage drop summary for panel 'FACP'." | Summary JSON | N/A | N/A | — |

### 30.14 Cable Resistance Profiles

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Profiles | `revit_list_cable_resistance_profiles` | RO | "List cable resistance profiles." | Array of profile entries | N/A | N/A | Profiles ship with the addin |
| Profiles | `revit_get_matching_cable_resistance_profile` | RO | "Match resistance profile for THHN-12 copper." | Matched profile or null + reason | N/A | N/A | — |


### 30.15 Documentation Discovery

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Docs | `revit_list_titleblocks` | RO | "List title block types." | Array of `{id, family, type}` | N/A | N/A | At least 1 title block family required |
| Docs | `revit_list_view_templates` | RO | "List view templates." | Array of `{id, name, viewType}` | N/A | N/A | — |
| Docs | `revit_list_revisions` | RO | "List project revisions." | Array of `{id, sequenceNumber, description, date, issued}` | N/A | N/A | Empty array OK |
| Docs | `revit_list_revision_numbering_sequences` | RO | "List revision numbering sequences." | Array of sequence configs | N/A | N/A | — |
| Docs | `revit_get_sheet_revisions` | RO | "Show revisions on sheet number 'A-101'." | Per-sheet revision array | N/A | N/A | Cloud revision note: results reflect what the model can see — see Special Notes |
| Docs | `revit_get_sheet_viewports` | RO | "List viewports on sheet 'A-101'." | Array of `{viewportId, viewId, viewName, position}` | N/A | N/A | — |
| Docs | `revit_find_unplaced_views` | RO | "Find views not placed on any sheet." | Array of view refs | N/A | N/A | `viewTypes` filter respected; DrawingSheet excluded |
| Docs | `revit_get_view_sheet_summary` | RO | "Get the view/sheet summary." | Counts + sample arrays | N/A | N/A | — |

### 30.16 PlaceViews Workflow Preset

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Preset | `revit_list_view_sheet_presets` | RO | "List view/sheet workflow presets." | Array of preset summaries | N/A | N/A | Presets live under user data folder |
| Preset | `revit_get_view_sheet_preset` | RO | "Get preset 'AutoPlaceFloorPlans'." | Full preset JSON | N/A | N/A | — |
| Preset | `revit_validate_view_sheet_preset` | RO | "Validate preset 'AutoPlaceFloorPlans'." | `{valid, issues[]}` | N/A | N/A | — |
| Preset | `revit_run_view_sheet_workflow_preset` | RO | "Run preset 'AutoPlaceFloorPlans' as dry-run." | Dry-run summary (no transaction) | N/A | N/A | Preset runner itself is RO; underlying writers go through their own RA flow |

### 30.17 Documentation Preview

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Docs | `revit_preview_place_views_on_sheets` | RO | "Preview placing unplaced FloorPlans on new sheets." | Dry-run sheet/view assignments | N/A | N/A | — |
| Docs | `revit_preview_duplicate_sheets` | RO | "Preview duplicating sheets ['A-101']." | Dry-run `{source → target}` list | N/A | N/A | — |
| Docs | `revit_preview_create_sheets_from_table` | RO | "Preview creating sheets from a 3-row table." | Dry-run with row count and per-row validation | N/A | N/A | Row counter handles JArray/object[]/string |
| Docs | `revit_preview_duplicate_views` | RO | "Preview duplicating views [12345]." | Dry-run mapping | N/A | N/A | — |
| Docs | `revit_preview_rename_views` | RO | "Preview renaming views matching pattern 'OLD_*' to 'NEW_*'." | Dry-run mapping | N/A | N/A | — |
| Docs | `revit_preview_rename_sheets` | RO | "Preview renaming sheets matching 'A-1*' to add suffix '_R1'." | Dry-run mapping | N/A | N/A | — |

### 30.18 Documentation Write

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Docs | `revit_place_views_on_sheets` | RA | "Place those previewed views on those sheets." | Approval flow → views placed | Required (DE bypasses) | Single Undo | Run preview first |
| Docs | `revit_duplicate_sheets` | RA | "Duplicate sheets ['A-101'] with suffix '_COPY'." | Approval flow → new sheets created | Required (DE bypasses) | Single Undo | — |

---

## Phase 1 New Tool Smoke Tests

These tests cover tools added in the Phase 1 finalization pass. Run them against a real Revit model after deployment.

### S1 — `revit_inspect_selected_elements`

**Manual steps:**

1. Open a test Revit model and load the add-in.
2. Select one fire alarm device in the model.
3. Run `revit_inspect_selected_elements` with default arguments.
4. Confirm `category`, `familyName`, `typeName`, `level` are correct.
5. Confirm `location.kind` is `"Point"` and `location.pointMm.x/y/z` are non-zero millimeter values (not internal feet values — a typical room coordinate should be in the thousands of mm, not tens of feet).
6. Confirm `boundingBoxMm.min`, `max`, `size`, `center` are all present and `size` values are small (device footprint ~60–200 mm).
7. Select a cable tray or conduit.
8. Run again. Confirm `location.kind` is `"Curve"` and `curveStartMm`, `curveEndMm`, `lengthMm` are present.
9. Select 60+ elements. Confirm response includes `warnings` indicating the result was limited and `totalSelected` vs `returned` counts differ.
10. Select nothing. Confirm the tool returns a clear message like `"No elements selected."`.

**Pass criteria:**
- Coordinates are in mm (point coordinates > 1 000 for typical project grid).
- `boundingBoxMm.size` values are non-zero for geometry-bearing elements.
- `geometrySummary.solidCount >= 1` for most family instances.
- No exception or null reference errors for system families, generic models, or annotation elements.

---

### S2 — `file_inspect`

1. Run `file_inspect` on an existing `.xlsx` file in the project folder.
2. Confirm `exists=true`, `type="file"`, `extension=".xlsx"`, `sizeBytes > 0`, `createdAtUtc` and `modifiedAtUtc` are valid ISO timestamps.
3. Run with `includeHash=true`. Confirm `hashSha256` is a 64-character hex string.
4. Run on a folder path. Confirm `type="folder"` and `sizeBytes` is null/absent.
5. Run on a path that does not exist. Confirm `exists=false` and `success=true` (not an error).
6. Run on a path outside the allowed roots. Confirm the tool returns an error and does not reveal file system info.

---

### S3 — `file_copy`

1. Run `file_copy` from an existing source to a new destination in the allowed write root.
2. Confirm the destination file exists and its content matches the source.
3. Run again without `overwrite=true`. Confirm the tool returns an error (destination already exists).
4. Run again with `overwrite=true`. Confirm success and `overwritten=true` in the response.
5. Run with `createDirectories=false` and a destination whose parent folder does not exist. Confirm the tool returns an error without creating the folder.

---

### S4 — `file_backup`

1. Run `file_backup` on an existing file with no `backupDirectory` specified (same-folder backup).
2. Confirm a new file exists in the same folder with the pattern `{stem}_backup_{yyyy-MM-dd_HHmmss}.{ext}`.
3. Confirm the backup content matches the original.
4. Run with a specific `backupDirectory`. Confirm the backup is created in that folder.
5. Run on a file that does not exist. Confirm a clear error is returned.

---

### S5 — `file_write_text` with `backupBeforeOverwrite`

1. Write a text file using `file_write_text` with `overwrite=false` and confirm success.
2. Write again with `overwrite=true` and `backupBeforeOverwrite=true` (default).
3. Confirm `backupPath` is present in the response and the backup file exists.
4. Confirm the original file now has the new content and the backup has the original content.
5. Write again with `backupBeforeOverwrite=false`. Confirm no backup file is created this time.

---

### S6 — Config tools

1. Run `config_read` with `scope="user"` and `createIfMissing=false`. If the file doesn't exist, confirm a clear "not found" error.
2. Run again with `createIfMissing=true`. Confirm the file is created with an empty `{}` object.
3. Run `config_update` with `scope="user"` and `updates={"testKey":"testValue"}`. Confirm success.
4. Run `config_read` again. Confirm `testKey` is present in the returned JSON.
5. Run `config_update` with a dot-path key: `{"$.nested.setting":"true"}`. Confirm `nested.setting` is present after reading back.
6. Run `config_set_project_config` with a `projectRoot` pointing to a test folder. Confirm the `.rktools\mcp.project.config.json` file is created.
7. Run `config_get_project_config` for the same root. Confirm the saved values are returned.
8. Run `config_write` with invalid JSON. Confirm the tool returns an error and the existing config file is unchanged.

---

### S7 — Excel dry-run

1. Open an existing `.xlsx` file. Run `excel_update_cells` with `dryRun=true`. Confirm:
   - Response contains `dryRun=true`.
   - `plannedUpdateCount` matches the number of cells provided.
   - `updates` array contains `oldValue` (current cell content) and `newValue`.
   - File modified timestamp is unchanged after the call.
2. Run `excel_insert_rows` with `dryRun=true`. Confirm:
   - Response contains `dryRun=true`, `affectedRange`, `plannedRowCount`.
   - File is not modified.
3. Run `excel_append_table_rows` with `dryRun=true`. Confirm:
   - Response contains `plannedStartRow`, `plannedRowCount`.
   - File is not modified.
| Docs | `revit_create_sheets_from_table` | RA | "Create sheets from this 3-row table." | Approval summary shows row count → flow → sheets created | Required (DE bypasses) | Single Undo | Row count must match input rows |
| Docs | `revit_duplicate_views` | RA | "Duplicate views [12345] with suffix '_COPY'." | Approval flow → new views created | Required (DE bypasses) | Single Undo | — |
| Docs | `revit_apply_view_template` | RA | "Apply view template id 67890 to FloorPlans matching 'L1_*'." | Approval summary lists template name + filter + matched view count → flow → applied | Required (DE bypasses) | Single Undo | Summary uses `viewTemplateId` + filters |
| Docs | `revit_set_sheet_parameters_bulk` | RA | "Set Drawn By='SMOKE' on sheets [A-101, A-102]." | Approval summary lists sheet count + parameter count → flow → applied | Required (DE bypasses) | Single Undo | Parameter count via `CountDictionary()` |
| Docs | `revit_set_view_parameters_bulk` | RA | "Set Discipline='Architectural' on views [12345, 12346]." | Approval summary lists view count + parameter count → flow → applied | Required (DE bypasses) | Single Undo | Parameter count via `CountDictionary()` |
| Docs | `revit_rename_views` | RA | "Rename views matching 'OLD_*' to 'NEW_*'." | Approval flow → views renamed | Required (DE bypasses) | Single Undo | Run preview first |
| Docs | `revit_rename_sheets` | RA | "Rename sheets matching 'A-1*' adding suffix '_R1'." | Approval flow → sheets renamed | Required (DE bypasses) | Single Undo | Run preview first |

### 30.19 Documentation Destructive

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Docs | `revit_preview_delete_views` | RO | "Preview deleting views ['L1_DEMO_COPY']." | Dry-run with placement/dependency warnings | N/A | N/A | `emptyDetailOnly` guard prevents accidental scope creep |
| Docs | `revit_delete_views` | **DRA** | "Delete views ['L1_DEMO_COPY']." | `approval_required` even with DE on → summary contains `DESTRUCTIVE` line | **Manual approval ALWAYS required** | Revit Undo restores | DE never bypasses |
| Docs | `revit_preview_delete_sheets` | RO | "Preview deleting sheets with suffix '_COPY'." | Dry-run with `skipSheetsWithViews` honored | N/A | N/A | — |
| Docs | `revit_delete_sheets` | **DRA** | "Delete sheets with suffix '_COPY'." | `approval_required` even with DE on → summary contains `DESTRUCTIVE` line | **Manual approval ALWAYS required** | Revit Undo restores | DE never bypasses |

### 30.20 Coordination / Clash Detection

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Coord | `revit_list_clashable_categories` | RO | "List clashable categories." | Array of `{category, elementCount}` objects | N/A | N/A | Includes all occupied categories in active doc |
| Coord | `revit_list_clashable_links` | RO | "List loaded Revit links." | Array of `{linkId, linkName, isLoaded}` or empty array | N/A | N/A | Unloaded links appear with `isLoaded:false` |
| Coord | `revit_get_clash_candidates` | RO | "Get candidates for Electrical Equipment vs Mechanical Equipment." | `{sourceCandidateCount, targetCandidateCount}` | N/A | N/A | No geometry extraction; counts only |
| Coord | `revit_detect_hard_clashes` | RO | "Detect hard clashes between Electrical Equipment and Mechanical Equipment." | `{clashes:[...], totalCount:N}` with element IDs and location | N/A | N/A | Returns empty list when no clashes found |
| Coord | `revit_detect_clearance_clashes` | RO | "Detect 100 mm clearance clashes between Fire Alarm Devices and Ducts." | Clashes include `distanceMm` and `requiredClearanceMm:100` | N/A | N/A | MVP uses expanded bounding-box approximation |
| Coord | `revit_get_clash_summary` | RO | "Summarise the clash results." | `{byRule, bySeverity, byStatus, byLevel, totalCount}` | N/A | N/A | Pass `clashes` array as argument |
| Coord | `revit_list_clash_presets` | RO | "List clash presets." | At least 2 built-in presets returned | N/A | N/A | Built-ins: "Electrical vs HVAC", "Fire Alarm Devices Placement QA" |
| Coord | `revit_get_clash_preset` | RO | "Get preset 'Electrical vs HVAC'." | Full preset JSON with `name`, `rules[]` | N/A | N/A | Returns error if preset not found |
| Coord | `revit_validate_clash_preset` | RO | "Validate preset JSON." | `{isValid:true, ruleCount:N, errors:[]}` | N/A | N/A | Pass invalid preset to verify `isValid:false` path |
| Coord | `revit_run_clash_preset` | RO | "Run preset 'Electrical vs HVAC'." | `{totalClashCount, ruleResults:[...]}` cached for review | N/A | N/A | Result cached to `LastClashRun.json` |
| Coord | `revit_export_clash_report_to_excel` | RO | "Export last clash run to Excel." | `{filePath: "...ClashReport_*.xlsx"}` | N/A | N/A | File has Summary + Details sheets |
| Coord | `revit_get_clash_dashboard_summary` | RO | "Get clash dashboard summary." | `{totalClashes, byRule, bySeverity, byStatus}` | N/A | N/A | — |
| Coord | `revit_get_next_clash` | RO | "Get next clash." | `{currentIndex, totalCount, clash}` | N/A | N/A | Returns error when no cache exists |
| Coord | `revit_get_previous_clash` | RO | "Get previous clash." | `{currentIndex, totalCount, clash}` with index decremented | N/A | N/A | Wraps to last clash at index 0 |
| Coord | `revit_select_clash_elements` | RA | "Select elements of clash CL-0001." | `approval_required` → both elements selected in UI after approval | Pending tab | N/A | Linked elements: selects `RevitLinkInstance` as fallback |
| Coord | `revit_focus_clash` | RA | "Focus clash CL-0001 in active view." | `approval_required` → active 3D view zooms + elements selected | Pending tab | N/A | Requires an open 3D view |
| Coord | `revit_create_clash_review_view` | RA | "Create clash review view for clash CL-0001." | `approval_required` → reusable 3D view `MCP Clash Review` created or reused, section box scoped to clash | Pending tab | Revit Undo removes view (if newly created) | Transaction: `"Revit MCP - Create Clash Review View"` |

### 30.21 Issue Reports

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Issues | `revit_export_issues_json` | RA | "Export this issue report as JSON." | `{filePath}` pointing to a `.json` file | Pending tab | N/A | Pass `reportJson` (full `IssueReportDto` serialised as JSON string) |
| Issues | `revit_export_issues_excel` | RA | "Export the issue report to Excel." | `{filePath}` pointing to a `.xlsx` file with Summary and Issues sheets | Pending tab | N/A | Pass a valid `IssueReportDto` JSON as argument |
| Issues | `revit_export_issues_markdown` | RA | "Export the issue report to Markdown." | `{filePath}` pointing to a `.md` file with issue table | Pending tab | N/A | — |
| Issues | `revit_merge_issue_reports` | RO | "Merge two issue reports." | Single merged `IssueReportDto` with combined issues and updated metadata | N/A | N/A | Pass `reportJsonArray` array |
| Issues | `revit_export_issues_html_dashboard` | RA | "Export this issue report as an HTML dashboard." | `{filePath}` pointing to a standalone `.html` file with filtering and severity cards | Pending tab | N/A | `includeEmbeddedJson=true` (default) embeds raw JSON in the HTML |

### 30.22 Delivery

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Delivery | `delivery_scan_folder` | RO | "Scan the delivery folder at `C:\Temp\Delivery`." | `{files:[...], issueCount, issues:[...]}` — lists parsed EULE drawing files, flags missing revisions/descriptions | N/A | N/A | Folder must contain EULE-format file names |
| Delivery | `delivery_check_against_revit_sheets` | RO | "Check delivery folder against current Revit sheets." | `{matchedCount, missingFromRevit:[...], extraInFolder:[...]}` | N/A | N/A | Requires open Revit document with sheets |
| Delivery | `delivery_check_against_excel_register` | RO | "Check delivery folder against register at `C:\Temp\register.xlsx`." | `{matchedCount, missingFromRegister:[...], missingFiles:[...], duplicates:[...]}` | N/A | N/A | Auto-detects header row by column name |
| Delivery | `delivery_run_full_check` | RO | "Run full delivery check on `C:\Temp\Delivery`." | Merged `IssueReportDto`; optional `exportExcelReport` / `exportMarkdownReport` flags produce files in the delivery folder | N/A | N/A | Combines all three checks |

### 30.23 File System

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| FileSystem | `file_read_text` | RO | "Read the file at `C:\Temp\test.txt`." | `{filePath, sizeBytes, content}` — UTF-8 content capped at `maxBytes` | N/A | N/A | Path must be inside an allowed-read root |
| FileSystem | `file_write_text` | RA | "Write 'Hello World' to `C:\Temp\out.txt`." | `approval_required` → `{filePath, sizeBytes}` confirming written | Pending tab | N/A | Path must be in allowed-write root; `overwrite:false` by default |
| FileSystem | `file_inspect` | RO | "Inspect metadata for `C:\Temp\test.xlsx`." | `{exists, type, extension, sizeBytes, createdAtUtc, modifiedAtUtc}` | N/A | N/A | `includeHash=true` adds `hashSha256`; full coverage in S2 |
| FileSystem | `file_copy` | RA | "Copy `C:\Temp\source.txt` to `C:\Temp\dest.txt`." | `approval_required` → `{sourcePath, destinationPath, overwritten}` | Pending tab | N/A | `overwrite=false` by default; full coverage in S3 |
| FileSystem | `file_backup` | RA | "Create a backup of `C:\Temp\sample.xlsx`." | `approval_required` → `{backupPath}` with `{stem}_backup_{yyyy-MM-dd_HHmmss}.{ext}` pattern | Pending tab | N/A | Optional `backupDirectory`; full coverage in S4 |
| FileSystem | `file_list_directory` | RO | "List files in `C:\Temp`." | `{entries:[{name, path, isDirectory, sizeBytes}], totalCount}` | N/A | N/A | `searchPattern` / `recursive` filter results |

### 30.24 Excel (Standalone)

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Excel | `excel_inspect_workbook` | RO | "Inspect the workbook at `C:\Temp\sample.xlsx`." | `{sheets:[{name, usedRange, rowCount, columnCount, headers}]}` | N/A | N/A | `includePreviewRows:true` adds sample data rows |
| Excel | `excel_read_range` | RO | "Read range A1:D10 from sheet 'Data' of `C:\Temp\sample.xlsx`." | Array of `{address, value, formula, dataType}` per cell | N/A | N/A | `includeFormulas:true` returns formula strings |
| Excel | `excel_update_cells` | RA | "Set cell B2 to 'Updated' in sheet 'Data' of `C:\Temp\sample.xlsx`." | `approval_required` → `{updatedCount, backupFilePath}` | Pending tab | Restore from backup | Path must be in allowed-write root |
| Excel | `excel_insert_rows` | RA | "Insert 1 row at row 5 in sheet 'Data' of `C:\Temp\sample.xlsx`, with A='New', B='Row'." | `approval_required` → `{insertedCount, backupFilePath}` | Pending tab | Restore from backup | Style copied from row above by default |
| Excel | `excel_append_table_rows` | RA | "Append a row {\"Name\": \"Test\", \"Value\": \"42\"} to sheet 'Data' of `C:\Temp\sample.xlsx`." | `approval_required` → `{appendedCount, backupFilePath}` | Pending tab | Restore from backup | `matchHeaders:true` maps by column name |
| ParameterQA | `revit_list_parameter_qa_rule_sets` | RO | "List available parameter QA rule sets." | `{count, ruleSets:[{name, description, ruleCount, rules}]}` | N/A | N/A | Reads from `%AppData%\RKTools\RevitMCP\parameter-qa-rules.json`; creates default file on first call |
| ParameterQA | `revit_run_parameter_qa_rule_set` | RO | "Run the 'ELENEA Basic QA' rule set." | `{ruleSetName, totalElements, totalWithIssues, rules:[…], issueReport}` | N/A | N/A | Each rule checks one Revit category; missing rule-set name returns descriptive error |

---

### 30.25 Config

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Config | `config_read` | RO | "Read the user config file." | JSON config object; `createIfMissing=true` creates empty `{}` on first call | N/A | N/A | Detailed coverage in S6; scope: `user`, `project`, or `addin` |
| Config | `config_get_project_config` | RO | "Get the project config for `C:\Temp\ProjectRoot`." | Saved project config JSON or not-found error | N/A | N/A | Config file is `.rktools\mcp.project.config.json` under the project root |
| Config | `config_write` | RA | "Write `{\"testKey\":\"testValue\"}` to the user config." | `approval_required` → `{filePath, keyCount}` | Pending tab | N/A | Overwrites entire file; prefer `config_update` for partial edits |
| Config | `config_update` | RA | "Update user config: set `testKey` to `updatedValue`." | `approval_required` → `{updatedKeys}` | Pending tab | N/A | Merges into existing; dot-path keys supported |
| Config | `config_set_project_config` | RA | "Set project config for `C:\Temp\ProjectRoot`." | `approval_required` → `{filePath}` | Pending tab | N/A | Creates `.rktools\mcp.project.config.json` in project root |

### 30.26 Skills

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Skills | `revit_list_skills` | RO | "List all company skills." | Array of `{id, name, version, taskCount, hasProjectOverride}` | N/A | N/A | Reads from `CompanySkills/` under config folder |
| Skills | `revit_get_skill_details` | RO | "Get details for skill 'company.electrical.qa'." | Full skill JSON with task definitions and settings | N/A | N/A | `includeProjectOverride=true` merges project override into response |
| Skills | `revit_preview_skill_run` | RO | "Preview running skill 'company.electrical.qa'." | `{tasks:[{id, name, modifiesModel}], requiresConfirmation}` | N/A | N/A | Call before `revit_run_skill` to understand impact |
| Skills | `revit_run_skill` | RA | "Run skill 'company.electrical.qa'." | `approval_required` → per-task `{success, issueCount}` | Required (DE bypasses) | Single Undo (model-writing tasks) | Call `revit_preview_skill_run` first; read-only tasks run without transaction |
| Skills | `revit_run_skill_task` | RA | "Run task 'check.cabletray.vs.ducts' in skill 'company.electrical.qa'." | `approval_required` → single-task result | Required (DE bypasses) | Single Undo | Useful for re-running or debugging one task |
| Skills | `revit_create_project_skill_override` | RO | "Create a project override for skill 'company.electrical.qa' for project 'P-2026-001'." | `{overridePath, skillId, projectId}` confirming file created | N/A | N/A | `changesJson`: `{\"tasks\":{\"<taskId>\":{\"enabled\":true,\"settings\":{...}}}}` |
| Skills | `revit_update_project_skill_override` | RO | "Update the project override: disable task 'check.facp.clearance'." | `{updated: true, mergedKeys}` | N/A | N/A | Merges `changesJson` into existing override |
| Skills | `revit_reset_project_skill_override` | RO | "Reset project override for skill 'company.electrical.qa' on project 'P-2026-001'." | `{deleted: true}` or not-found message | N/A | N/A | Reverts to company master skill |
| Skills | `revit_configure_sheet_naming_skill` | RO | "Configure sheet naming skill for project 'P-2026-001' with Excel register at `C:\Temp\register.xlsx`." | `{overridePath, configuredTasks}` | N/A | N/A | Convenience wrapper for `company.lehed.nimetamise-kontroll`; set `enableExcelComparison=true` |

### 30.27 Skills Admin

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Skills Admin | `revit_compare_skill_override_to_master` | RO | "Compare project override for skill 'company.electrical.qa' on project 'P-2026-001' to master." | `{changedSettings, disabledTasks, newTasksInMaster, versionMismatch}` diff JSON | N/A | N/A | Returns `upToDate: true` when no diffs found |
| Skills Admin | `revit_propose_master_skill_update` | RO | "Propose a master update for skill 'company.electrical.qa' from project 'P-2026-001'." | `{proposalPath}` confirming proposal JSON written to local proposals folder | N/A | N/A | Never modifies company master files directly |
| Skills Admin | `revit_export_skill_override_diff_markdown` | RO | "Export a Markdown diff of the override for skill 'company.electrical.qa' on project 'P-2026-001'." | `{filePath}` pointing to a `.md` diff report in the exports folder | N/A | N/A | Does not modify any skill files |

### 30.28 Standards

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Standards | `standards_list_sources` | RO | "List company standards sources." | Array of `{id, name, enabled, fileCount, indexedAt}` | N/A | N/A | Config in `StandardsSources.json` under user data folder |
| Standards | `standards_index_sources` | RO | "Index all enabled company standards sources." | `{indexed, skipped, errors}` counts per source | N/A | N/A | `force=true` rebuilds stale indexes; run before `standards_search` |
| Standards | `standards_search` | RO | "Search company standards for 'cable tray sizing'." | Array of `{chunkId, source, heading, score, snippet}` | N/A | N/A | Run `standards_index_sources` first; `discipline` hint improves relevance |
| Standards | `standards_get_document_chunk` | RO | "Get document chunk 'src1::chunk-042' with 1 context chunk before and after." | `{chunkId, text, source, heading, contextBefore:[], contextAfter:[]}` | N/A | N/A | `chunkId` from `standards_search` results |
| Standards | `standards_validate_source_config` | RO | "Validate the standards source configuration." | `{valid, issues:[{source, issue}]}` — creates example config if none exists | N/A | N/A | Run first to diagnose missing or misconfigured sources |

### 30.29 Family Creation

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Families | `revit_create_panel_schematic_symbol_from_dwg` | RA | "Create a panel schematic symbol from DWG `C:\Temp\QF_3P.dwg` with name 'QF_3P'." | `approval_required` → `{familyPath, familyName}` — `.rfa` saved to preset output folder | Required (DE bypasses) | N/A (file operation) | Family is NOT loaded into the project; version suffix `_01/_02` applied if file already exists |

---

**Coverage check:** This matrix covers **161/161** registered MCP tools (verified by enumerating `RevitMCP.Bridge/RevitMcpTools.cs` and `RevitMCP.Addin/Tools/*.cs`). Bridge tool names and addin `Name` properties are in 1:1 parity.


---

## 31. Agent Test Run Result Template

When an agent runs the Section 30 matrix, it should record one row per tool in a results table with the columns below. Use the **allowed Result values** verbatim so reports stay grep-friendly.

**Allowed `Result` values (use exactly one):**
- `Pass`
- `Fail`
- `Skipped - model data missing`
- `Skipped - destructive action not approved`
- `Skipped - requires manual setup`
- `Blocked - connector issue`

**Result table template:**

| Tool | Tested | Result | Notes | Error | ApprovalBehavior | UndoBehavior |
|------|--------|--------|-------|-------|------------------|--------------|
| `revit_get_connection_status` | Yes | Pass | Connected to test model `RevitMCP_TestModel.rvt` | — | N/A | N/A |
| `revit_list_views` | Yes | Pass | Returned 42 views | — | N/A | N/A |
| `revit_set_parameter` | Yes | Pass | Set Comments='SMOKE' on Wall id 12345 | — | Approval received, applied on accept | Single Undo reversed change |
| `revit_delete_views` | Yes | Pass | DRA: DE on, still required manual approval; `DESTRUCTIVE` warning present | — | Manual approval received | Revit Undo restored view |
| `revit_run_fire_alarm_circuit_preset` | No | Skipped - model data missing | No fire alarm panel in test model | — | — | — |
| `revit_create_electrical_circuit` | No | Skipped - destructive action not approved | Reviewer rejected approval to test rollback | — | Rejected pending item; model unchanged | N/A |
| `revit_export_query_to_excel` | Yes | Blocked - connector issue | Bridge returned `transport-closed` mid-call | `transport-closed` | — | — |

**Column rules:**
- **Tool** — Exact MCP tool name (with `revit_` prefix), wrapped in backticks.
- **Tested** — `Yes` if the tool was invoked, `No` if it was skipped.
- **Result** — One of the six allowed values above.
- **Notes** — One-line context (test data used, observed behavior).
- **Error** — Exact error string if `Fail` or `Blocked`, else `—`.
- **ApprovalBehavior** — For RA/DRA tools: did the approval flow behave per the Section 30 baseline? `N/A` for RO.
- **UndoBehavior** — Did Revit Undo reverse the change as expected? `N/A` if no transaction (RO, UI-only selection).

**Run mode notes:**
- Record whether Direct Edit was **ON** or **OFF** for each writer test. The matrix expects both modes to be smoke-tested for at least one RA tool and the DRA pair (`revit_delete_views`, `revit_delete_sheets`).
- For DRA tools, run the test twice: once with DE OFF, once with DE ON. Both must return `approval_required` for `Pass`.


---

## 32. Recommended Test Model Setup

To exercise the entire Section 30 matrix without manual setup between tools, the test Revit model should contain (at minimum) the following data. Mark missing items as `Skipped - model data missing` rather than `Fail`.

### 32.1 General

- A saved `.rvt` file (any Revit 2026 template will do) named something like `RevitMCP_TestModel.rvt`.
- At least one **Level** and one **Floor Plan view** placed on a sheet.
- At least one **Title Block** family loaded.
- At least one **Sheet** with the loaded title block.
- At least one **View Template**.

### 32.2 Walls / Generic Elements (Query & Parameter tests)

- ~10 walls of mixed types so `count_elements`, `group_by_parameter`, `find_elements_by_parameter`, and `check_parameter_completeness` return meaningful data.
- Populate `Comments` on at least one wall (used by `find_elements_by_parameter` and `set_parameter` tests).

### 32.3 Electrical

- At least **2 Electrical Panels** named e.g. `LP-1` and `LP-2` so `reassign_circuit_panel` can move a circuit between them.
- At least **3 Electrical Equipment / Lighting Fixtures / Receptacles** that can host a circuit (verified via `get_circuit_compatible_elements`).
- At least **1 existing circuit** on `LP-1` so the discovery and modification tools have a target id.
- At least **1 uncircuited Lighting Fixture** so `find_uncircuited_elements` returns a non-empty result.
- A **Cable Type** matching one of the cable resistance profiles shipped with the addin (so `get_matching_cable_resistance_profile` returns a hit).

### 32.4 Fire Alarm (optional but recommended)

- At least **1 panel** named to match the project's fire alarm preset convention (e.g. `FACP`). If absent, mark Section 30.13 rows as `Skipped - model data missing`.

### 32.5 Documentation

- At least **2 sheets** so duplicate/rename/delete preview operations have material.
- At least **1 unplaced view** so `find_unplaced_views` returns a non-empty result.
- At least **1 sheet with a placed viewport** so `get_sheet_viewports` returns rows.
- A **revision** added to the project (issued or pending) so `list_revisions`, `list_revision_numbering_sequences`, and `get_sheet_revisions` return non-empty data.

### 32.6 Presets

- At least **1 query preset** under the user data folder so `list_query_presets` / `run_query_preset` are exercised. If none, mark as `Skipped - requires manual setup`.
- At least **1 view-sheet workflow preset** so `list_view_sheet_presets` / `get_view_sheet_preset` / `validate_view_sheet_preset` / `run_view_sheet_workflow_preset` return data. If none, mark as `Skipped - requires manual setup`.

### 32.7 Direct Edit Coverage

The agent should run the matrix **twice**:
1. **Direct Edit OFF** — verifies every RA tool returns `approval_required` and that the pending-approval queue, approve/reject, and Undo all work.
2. **Direct Edit ON** — verifies every RA tool executes immediately (no `approval_required`), and verifies that **DRA tools still require manual approval** (`revit_delete_views`, `revit_delete_sheets`).

### 32.8 Recommended Save State Between Runs

- After each writer test that succeeds, immediately invoke Revit Undo and **do not save**. This keeps the test model reusable for the next pass.
- For destructive tests, save a copy of the model before running so the `Undo` step can be verified by file-diff if Revit Undo is unavailable.

---

## 33. Special Notes / Deferred Features

The following items are intentionally **not** covered in the Section 30 matrix because they are not registered as MCP tools at this time:

- **`revit_export_fire_alarm_visualization_html`** — Mentioned in earlier design notes but not registered in `RevitMCP.Bridge/RevitMcpTools.cs` or `RevitMCP.Addin/App.cs`. The JSON-only `revit_get_fire_alarm_visualization_data` is the supported path; HTML export remains deferred.
- **`EmptyDetailOnly` option for delete tools** — Implemented as a safety guard inside `preview_delete_views` / `delete_views` but not exposed as a standalone tool. No separate matrix row needed.
- **`revit_create_sheets_from_preset`** — Considered during the preset work but not implemented. The current path is `revit_create_sheets_from_table` (already in Section 30.18) which accepts an inline table or preset payload.

**Known classification quirks (intentional, do not "fix" without coordination):**
- `revit_select_elements` and `revit_select_elements_by_query` are marked `ReadOnly` in the addin even though they change the Revit UI selection. They do not open a DB transaction, so they cannot be Undone.
- `revit_select_circuit_elements` and `revit_select_uncircuited_elements` are marked `RequiresApproval` for symmetry with other electrical-domain writers, even though they also only touch the UI selection.
- The view/sheet workflow preset runner (`revit_run_view_sheet_workflow_preset`) is itself `ReadOnly` (dry-run by default). Any actual writes happen through the individual RA writers, each of which goes through its own approval flow.


---

## 34. Coordination / Clash Detection Tools — Overview

The 17 coordination tools implement a **Revit-native bounding-box clash detection pipeline** across five sub-areas:

| Sub-area | Tools |
|----------|-------|
| Discovery | `revit_list_clashable_categories`, `revit_list_clashable_links`, `revit_get_clash_candidates` |
| Detection | `revit_detect_hard_clashes`, `revit_detect_clearance_clashes`, `revit_get_clash_summary` |
| Presets | `revit_list_clash_presets`, `revit_get_clash_preset`, `revit_validate_clash_preset`, `revit_run_clash_preset` |
| Reporting | `revit_export_clash_report_to_excel`, `revit_get_clash_dashboard_summary` |
| Review | `revit_get_next_clash`, `revit_get_previous_clash`, `revit_create_clash_review_view`, `revit_focus_clash`, `revit_select_clash_elements` |

**Prerequisites:**
- Revit model open with elements in at least two categories.
- For link-vs-host tests: a Revit link must be loaded (not unloaded).
- Run **Start Connector** before any test.

---

## 35. Coordination Discovery Tools

### 35.1 `revit_list_clashable_categories`

**Prompt:**
```
call revit_list_clashable_categories
```
**Expected:** JSON array of objects `{category, elementCount}`. At minimum one entry with `elementCount > 0`.

### 35.2 `revit_list_clashable_links`

**Prompt:**
```
call revit_list_clashable_links
```
**Expected (no links):** `{success: true, data: [], message: "0 link instances found"}`.

**Expected (links loaded):** Each entry has `linkId`, `linkName`, `isLoaded: true`.

**Expected (links unloaded):** `isLoaded: false` entries appear; no crash.

### 35.3 `revit_get_clash_candidates`

**Prompt:**
```
call revit_get_clash_candidates with sourceCategories=["Electrical Equipment"] and targetCategories=["Mechanical Equipment"]
```
**Expected:** `{success: true, data: {sourceCandidateCount, targetCandidateCount}}`.

**Prompt (link):**
```
call revit_get_clash_candidates with sourceCategories=["Ducts"] and targetCategories=["Conduits"] and linkId=<id>
```
**Expected:** Counts sourced from the link model.

---

## 36. Coordination Detection Tools

### 36.1 `revit_detect_hard_clashes`

**Prompt:**
```
call revit_detect_hard_clashes with sourceCategories=["Electrical Equipment"] and targetCategories=["Mechanical Equipment"]
```
**Expected:** `{success: true, data: {clashes: [...], totalCount: N}}`. Each clash has `clashId`, `source.elementId`, `target.elementId`, `location.x/y/z`.

**No-clash model:** Returns `data.totalCount: 0` and empty array.

### 36.2 `revit_detect_clearance_clashes`

**Prompt:**
```
call revit_detect_clearance_clashes with sourceCategories=["Fire Alarm Devices"] and targetCategories=["Ducts"] and toleranceMm=100
```
**Expected:** Clashes include `distanceMm` and `requiredClearanceMm=100`.

### 36.3 `revit_get_clash_summary`

**Prompt:**
```
call revit_get_clash_summary with clashes=<paste clash array from above>
```
**Expected:** `{byRule: {...}, bySeverity: {...}, byStatus: {...}, byLevel: {...}, totalCount: N}`.

---

## 37. Coordination Preset Tools

### 37.1 `revit_list_clash_presets`

**Prompt:**
```
call revit_list_clash_presets
```
**Expected:** At least 2 built-in presets (`"Electrical vs HVAC"`, `"Fire Alarm Devices Placement QA"`).

### 37.2 `revit_get_clash_preset`

**Prompt:**
```
call revit_get_clash_preset with name="Electrical vs HVAC"
```
**Expected:** Full preset JSON with `name`, `rules[]` where each rule has `sourceCategoryNames`, `targetCategoryNames`, `type`.

### 37.3 `revit_validate_clash_preset`

**Valid preset:**
```
call revit_validate_clash_preset with preset=<valid preset JSON>
```
**Expected:** `{isValid: true, ruleCount: N, errors: []}`.

**Invalid preset (missing rules):**
```
call revit_validate_clash_preset with preset={}
```
**Expected:** `{isValid: false, errors: ["missing rules"]}`.

### 37.4 `revit_run_clash_preset`

**Prompt:**
```
call revit_run_clash_preset with presetName="Electrical vs HVAC"
```
**Expected:** `{success: true, data: {totalClashCount, ruleResults: [...]}}`. Result is cached for step-through review.

---

## 38. Coordination Reporting Tools

### 38.1 `revit_export_clash_report_to_excel`

**Prompt:**
```
call revit_export_clash_report_to_excel with run=<clash run result JSON>
```
**Expected:** `{success: true, data: {filePath: "...ClashReport_*.xlsx"}}`. File opens in Excel with Summary and Details sheets.

**Verify:**
1. Summary sheet has totals by rule.
2. Details sheet has one row per clash with `ClashId`, `RuleName`, `ClashType`, `Severity`, `Status`, source/target element IDs and categories, location X/Y/Z, DistanceMm, RequiredClearanceMm.

### 38.2 `revit_get_clash_dashboard_summary`

**Prompt:**
```
call revit_get_clash_dashboard_summary with run=<clash run result JSON>
```
**Expected:** `{totalClashes, byRule: {...}, bySeverity: {...}, byStatus: {...}}`.

---

## 39. Coordination Review Tools

### 39.1 `revit_get_next_clash` / `revit_get_previous_clash`

**Prompt (after running a preset):**
```
call revit_get_next_clash
```
**Expected:** Returns next clash in sequence. Wraps around at end. Returns `{currentIndex, totalCount, clash}`.

```
call revit_get_previous_clash
```
**Expected:** Steps back. Wraps around at beginning.

**No cache (cold start):**
```
call revit_get_next_clash
```
**Expected:** `{success: false, message: "No clash run cached..."}`.

### 39.2 `revit_select_clash_elements` *(requires approval)*

**Prompt:**
```
call revit_select_clash_elements with sourceElementId=<id> and targetElementId=<id>
```
1. Approval in Pending tab.
2. After approval — both elements highlighted in Revit UI.
3. Rejection — no selection change.

### 39.3 `revit_focus_clash` *(requires approval)*

**Prompt:**
```
call revit_focus_clash with sourceElementId=<id> and targetElementId=<id> and location={"x":1,"y":2,"z":3}
```
1. Approval in Pending tab.
2. After approval — active 3D view zooms to clash location; both elements selected.

### 39.4 `revit_create_clash_review_view` *(requires approval)*

**Prompt:**
```
call revit_create_clash_review_view with clashId="CL-0001" and sourceElementId=<id> and targetElementId=<id>
```
1. Approval in Pending tab.
2. After approval — the reusable 3D view named `MCP Clash Review` appears in Project Browser (created if missing, otherwise reused) with a section box isolating the two elements.
3. Rejection — no view created.
4. Transaction name: `"Revit MCP - Create Clash Review View"`.

---

## 40. Coordination Unit Tests

Run via:
```
dotnet test RevitMCP.slnx
```

**Coordination-specific tests (19 facts in `ClashDetectionTests.cs`):**

| Test | Asserts |
|------|---------|
| `BoundingBoxMath_Overlaps_WhenIntersecting` | Two identical boxes → overlap = true |
| `BoundingBoxMath_NotOverlaps_WhenApart` | Non-touching boxes → overlap = false |
| `BoundingBoxMath_Overlaps_AfterExpand` | Originally apart boxes overlap after 50 mm expand |
| `BoundingBoxMath_ApproximateDistance_IsPositive` | Distance between non-overlapping boxes > 0 |
| `BoundingBoxMath_ApproximateDistance_ZeroWhenOverlapping` | Overlapping boxes → distance = 0 |
| `ClashSummaryService_GroupByRule` | Clashes grouped by rule name correctly |
| `ClashSummaryService_GroupByLevel` | Clashes grouped by level correctly |
| `ClashSummaryService_TotalCount` | Total count matches input list size |
| `ClashSummaryService_AllStatusesPresent` | All 3 statuses appear in summary |
| `ClashPresetService_DefaultPresets_Count` | GetDefaultPresets returns ≥ 2 built-in presets |
| `ClashPresetService_Validate_ValidPreset` | Valid preset → isValid = true, no errors |
| `ClashPresetService_Validate_MissingRules` | Preset with empty rules → isValid = false |
| `ClashPresetService_Validate_NullSourceCategories` | Null sourceCategoryNames → isValid = false |
| `ClashRunCacheService_SaveAndLoad` | Saved run → loaded run has same total count |
| `ClashRunCacheService_HasCache_FalseWhenEmpty` | No cache → HasCache = false |
| `ClashRunCacheService_GetNext_WrapsAround` | GetNext twice on 2-clash run → wraps to first |
| `ClashRunCacheService_GetPrevious_WrapsAround` | GetPrevious from start → wraps to last |
| `ClashRunCacheService_EmptyRunReturnsNull` | GetNext on 0-clash run → returns null |
| `ClashResultDto_Defaults` | New ClashResultDto has Status="New", Severity="Medium" |

---

## 41. Coordination Tool Safety Notes and Known Limitations

### 41.1 Linked Model Element Selection

When a clashing element resides inside a linked Revit model, the Revit API does **not** allow selecting linked elements the same way as host elements.

Expected behavior in `revit_select_clash_elements` and `revit_focus_clash`:
- If the source element is in the host model: selects it directly.
- If the source element is in a linked model: selects the `RevitLinkInstance` as a fallback.
- The tool result includes a `warnings[]` entry explaining that linked element selection is approximate — the `linkInstanceId` is provided for reference.

Do **not** treat a warning about linked-element fallback as a bug.

### 41.2 Clearance Clash Approximation (Expanded Bounding Box)

MVP clearance clash detection uses the **ExpandedBoundingBox** method:
- Source element bounding box is expanded uniformly by `toleranceMm` in all directions.
- An overlap with the target element's bounding box is reported as a clearance violation.

**This is an approximation.** Bounding boxes are axis-aligned and do not follow element geometry curves or offsets. The reported `distanceMm` is the minimum axis-aligned distance between the two boxes and may differ from the true geometric clearance.

Include `"clearance check uses bounding-box approximation"` in smoke test notes. Do not flag approximate clearance results as failures unless the distance value is wildly inconsistent.

### 41.3 Imported / Generic Model Geometry Warnings

When source or target categories include `Generic Models`, `ImportInstance`, or IFC-origin elements:
- Geometry extraction may fail for some elements (no solid, no geometry iterator).
- These elements are **skipped silently** and counted in `warnings[]` as `{skippedGeometryCount}`.
- A non-zero `skippedGeometryCount` is **expected** for IFC-heavy models and is not a failure.

Verify that the result still returns `success: true` and that the `skippedGeometryCount` warning appears when IFC or generic geometry is present.

### 41.4 Unloaded Links

If a Revit link is unloaded:
- `revit_list_clashable_links` returns it with `isLoaded: false`.
- `revit_get_clash_candidates`, `revit_detect_hard_clashes`, and `revit_detect_clearance_clashes` skip unloaded links entirely — no crash, no error.
- The tool result includes a warning listing the skipped link names.

### 41.5 No-Cache State

If `revit_get_next_clash` or `revit_get_previous_clash` is called before any preset has been run:
- The tool returns `{success: false, message: "No clash run cached. Run a preset first."}` (or similar).
- This is the expected cold-start behavior — not a bug.

Always run `revit_run_clash_preset` at least once per Revit session before using the step-through review tools.

---

## 42. Hard Clash False Positive Regression Tests

These tests verify that the strict solid-intersection path does **not** report false positives for elements that are spatially adjacent but do not physically intersect, and that the `allowBoundingBoxFallback` flag behaves correctly.

### 42.1 Fire Alarm Detector vs Pipe — Strict Mode (default)

**Setup:** A fire alarm smoke detector mounted near (but not intersecting) a sprinkler pipe. Both are present in the host model or a loaded link.

**Prompt:**
```
Detect hard clashes between Fire Alarm Devices and Pipe Accessories with toleranceMm=5 and allowBoundingBoxFallback=false.
```

**Expected:**
- `clashes` array is **empty** (or contains only genuine intersections if any exist).
- `warnings` contains the strict-mode notice: `"Hard clash detection uses bounding-box overlap only as a candidate pre-check..."`
- If solids could not be extracted, `warnings` includes a line starting `"Skipped N candidate pair(s) because usable solids could not be extracted."`
- **No false positives** — the detector is not reported as clashing with the pipe due to bounding-box proximity alone.

### 42.2 Fire Alarm Detector vs Pipe — Fallback Mode (opt-in)

**Setup:** Same geometry as 42.1.

**Prompt:**
```
Detect hard clashes between Fire Alarm Devices and Pipe Accessories with toleranceMm=5 and allowBoundingBoxFallback=true.
```

**Expected:**
- If solids are unavailable for either element, a clash result **may** appear with `detectionMethod: "BoundingBoxFallback"` and `confidence: "Low"`.
- The clash `message` contains the phrase `"bounding-box fallback only; verify visually"`.
- `warnings` includes `"Bounding-box fallback was enabled. Some hard clash results are low-confidence and must be reviewed visually."`

### 42.3 Solid-Confirmed Hard Clash Returns High Confidence

**Setup:** Two pipes that genuinely intersect in the model (or a host + link element that physically overlaps).

**Prompt:**
```
Detect hard clashes between Pipes and Pipes (or Pipes and Mechanical Equipment) with toleranceMm=1.
```

**Expected:**
- At least one clash result has `detectionMethod: "SolidIntersection"` and `confidence: "High"`.
- `intersectionVolume` is a positive number (cubic mm).
- No `"BoundingBoxFallback"` results appear in the same run.

### 42.4 Clearance Result Has Medium Confidence

**Prompt:**
```
Detect 100 mm clearance clashes between Fire Alarm Devices and Ducts.
```

**Expected:**
- Every clash result (if any) has `detectionMethod: "ExpandedBoundingBox"` and `confidence: "Medium"`.
- `warnings` includes `"Clearance detection uses expanded bounding-box approximation. Results should be visually reviewed."`

### 42.5 Summary Includes byDetectionMethod and byConfidence

**Prompt:**
```
Summarise the last clash run results.
```

**Expected:**
- Response includes a `byDetectionMethod` key (e.g. `{"SolidIntersection": 5, "BoundingBoxFallback": 2}`).
- Response includes a `byConfidence` key (e.g. `{"High": 5, "Low": 2}`).

### 42.6 Excel Export Includes Detection Method and Confidence Columns

**Prompt:**
```
Export the last clash run to Excel.
```

**Expected:**
- The generated `.xlsx` file has a `Clashes` sheet with columns `Detection Method` (column 15) and `Confidence` (column 16).
- High-confidence solid-intersection rows show `SolidIntersection` / `High`.
- Low-confidence fallback rows (if any) show `BoundingBoxFallback` / `Low`.

---

## 43. Parameter QA Rule Sets

### 43.1 List Rule Sets Returns Default Set

**Prompt:**
```
List all parameter QA rule sets.
```

**Expected:**
- `success: true`
- `ruleSets` contains at least one entry: `"ELENEA Basic QA"`.
- Each rule set entry includes `name`, `description`, `ruleCount`, and `rules` array.
- If `%AppData%\RKTools\RevitMCP\parameter-qa-rules.json` did not exist, it is created automatically with the default content.

### 43.2 Run Rule Set Against Active Model

**Prompt:**
```
Run the 'ELENEA Basic QA' parameter QA rule set.
```

**Expected:**
- `success: true`
- `ruleSetName: "ELENEA Basic QA"`
- `rules` array contains one entry per rule in the set (e.g. `"Fire Alarm Devices"`, `"Lighting Fixtures"`).
- Each rule entry includes `totalElements`, `completeElements`, `incompleteElements`, `completionPercent`, and `parameters`.
- If the model has no elements of a rule's category, `totalElements: 0` is returned for that rule and a warning is added.
- If `returnIssueReport: true` (default), `issueReport` is included with individual element issues.

### 43.3 Unknown Rule Set Returns Descriptive Error

**Prompt:**
```
Run the 'NonExistent QA Set' parameter QA rule set.
```

**Expected:**
- `success: false`
- `message` contains the unknown rule set name and a hint to use `revit_list_parameter_qa_rule_sets`.

### 43.4 Unit Tests Pass (192 tests)

```
dotnet test RevitMCP.slnx
```

**Expected:**
- `Passed! - Failed: 0, Passed: 192`
- New tests: `RevitSheetNumberParserTests` (11 facts), `DeliveryFolderPolicyCheckerTests` (12 facts/theories), `ParameterQaRuleSetServiceTests` (7 facts).

---

## 44. IFC Space to Revit Room — Full Pipeline (Phases 1–4)

These tests cover the complete IFC Space → Room pipeline across all four phases.
All phases must be tested **in sequence** since later phases depend on output from earlier ones.

**Prerequisites:**
- A Revit host model with at least 2 Levels and their floor-plan views.
- A loaded Revit link containing an IFC model with at least one `IfcSpace` element.
- Run **Start Connector** before any test.

---

### 44.1 Phase 1 — Discovery and Preview (`ifc_list_links`, `ifc_preview_spaces`)

#### Step A — List Links

**Prompt:**
```
call ifc_list_links
```

**Expected:**
- `success: true`
- At least one entry with `linkInstanceId` and `linkName`.
- `linkInstanceId` is used in all subsequent calls.

---

#### Step B — Preview Spaces (default, confirmed-only)

**Prompt:**
```
call ifc_preview_spaces with linkInstanceId=<id>
```

**Expected:**
- `success: true`
- `spaces` array: each entry has `linkedElementId`, `number`, `name`, `storeyName`, `detectionConfidence: "Confirmed"`, `targetLevelId`, `targetLevelName`, `status`, `canConvertLater`.
- `summary.totalCandidates >= 1`
- All returned spaces have `detectionConfidence: "Confirmed"` (no Probable unless `includeProbable=true`).
- `numberSource` and `nameSource` are present when values are non-null (e.g. `"Room Number"`, `"LongName"`).
- `status` values: `Ready`, `Warning`, `AlreadyExists`, `MissingMetadata`, `NoLevelMatch`.
- No Revit transaction created.

---

#### Step C — Preview Spaces (including Probable)

**Prompt:**
```
call ifc_preview_spaces with linkInstanceId=<id> and includeProbable=true
```

**Expected:**
- Probable candidates appear with `detectionConfidence: "Probable"` and `canConvertLater: false` and `status: "NotIfcSpace"`.
- Confirmed candidates still appear with `detectionConfidence: "Confirmed"`.
- No model modification.

---

#### Step D — Metadata source tracing

From Step B results:
- Verify at least one space has `numberSource` set to `"Number"`, `"Room Number"`, or `"Reference"` (never `"LongName"` or `"IfcLongName"`).
- Verify at least one space has `nameSource` set to `"LongName"`, `"IfcLongName"`, or `"Room Name"`.
- If `number` and `name` are the same value and `numberSource` = `"LongName"` → this is a **bug** (LongName must NOT be in Number priority list).

---

### 44.2 Phase 2 — Geometry Extraction (`ifc_preview_space_geometry`)

**Prompt:**
```
call ifc_preview_space_geometry with linkInstanceId=<id>
```

**Expected:**
- `success: true`
- Each item has `status` (see `IfcGeometryStatus` constants).
- Items with `status: "GeometryReady"` have `approxAreaM2 > 0` and `placementPoint.xMm/yMm/zMm`.
- `outerLoopCurveCount >= 3` for GeometryReady items.
- `summary.geometryReady >= 1`
- No Revit transaction created.

**Prompt (with loop coordinates):**
```
call ifc_preview_space_geometry with linkInstanceId=<id> and includeLoopCoordinates=true and maxCoordinatePoints=50
```

**Expected:**
- `loops[0].points` is present and has ≤ 50 entries.
- `loops[0].isOuter: true`.
- No Revit transaction created.

---

### 44.3 Phase 3 — Room Creation (`convert_ifc_spaces_to_rooms`)

#### Step A — Dry Run

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and dryRun=true
```

**Expected:**
- `success: true`
- `dryRun: true`
- Items with `status: "DryRunReady"` indicate they would be created.
- **No model modifications** — check Revit Undo, no new entry should appear.
- No new Rooms in the model.

---

#### Step B — Explicit Element IDs + Dry Run

From Phase 2, collect `linkedElementId` values for `GeometryReady` spaces.

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and linkedElementIds=[<id1>,<id2>] and dryRun=true
```

**Expected:**
- Only the specified spaces are processed.
- `items` has exactly as many entries as supplied IDs (where elements could be found).

---

#### Step C — Live Conversion (Requires Approval)

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and linkedElementIds=[<id1>] and dryRun=false
```

**Approval flow:**
1. Approval in Pending tab.
2. Summary clearly shows link ID and space count.
3. **Reject** → no model change, Revit Undo unchanged, `status: "SkippedExisting"` NOT returned (never reached write step).
4. Re-call, **Approve**:
   - `status: "Created"` for each successful space.
   - New Room appears in Revit on the correct Level.
   - Room Number and Name match IFC metadata (no IFC GUIDs, no Comments, no shared parameters written).
   - Revit Undo shows `"MCP: Create Room '<number> <name>'"` entries.
   - `createdBoundaryLineCount > 0` when `createRoomSeparationLines=true`.

---

#### Step D — Duplicate conflict handling

After Step C, re-run with the same spaces and default `duplicateMode="skip_existing"`.

**Expected:**
- `status: "SkippedExisting"` for exact Number+Name+Level matches.
- `status: "SkippedDuplicateWarning"` when Number+Level match but Name differs.

With `duplicateMode="allow_conflicts"`:
- Number+Level conflict (but different Name) is allowed through → Room created (with warning).

---

#### Step E — Missing boundary view guard

Test with `allowCreateMissingBoundaryViews=false` (default) on a Level that has no floor-plan view.

**Expected:**
- `status: "SkippedNoView"` for that space.
- Error message mentions `allowCreateMissingBoundaryViews=true`.

With `allowCreateMissingBoundaryViews=true` (Requires Approval):
- A view named `"MCP_IFC Room Boundary - {LevelName}"` is created inside the run's setup transaction.
- After approval, the Room is created.
- Transaction `"MCP: Create IFC Room Boundary Views"` appears in Undo history (before per-space transactions).

---

#### Step F — Probable conversion guard

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and allowProbableConversion=false
```
(auto-collect mode — no `linkedElementIds`)

**Expected:** Only Confirmed elements are processed. Probable candidates are excluded silently.

**Prompt:**
```
call convert_ifc_spaces_to_rooms with linkInstanceId=<id> and allowProbableConversion=true
```

**Expected:** Probable candidates are included. Each has an advisory `warnings` entry stating the element could not be confirmed as IfcSpace.

---

### 44.4 Phase 4 — Validation (`validate_ifc_space_room_conversion`)

Run after Phase 3 has created at least some Rooms.

**Prompt:**
```
call validate_ifc_space_room_conversion with linkInstanceId=<id>
```

**Expected:**
- `success: true`
- `permission: ReadOnly` — no model changes.
- Each item has `confidence` (`High`, `Medium`, `Low`, `Ambiguous`, `None`) and `status` (see `ValidationStatus` constants).
- High-confidence matches: `status: "HighConfidenceMatch"`, `recommendedAction: "None"`.
- Missing rooms (not yet created): `status: "MissingRoom"`, `confidence: "None"`, `recommendedAction: "CreateRoom"`.
- Ambiguous matches: `status: "AmbiguousMatch"`, `recommendedAction: "ReviewAndResolve"`, `warnings` lists both candidate IDs.
- `summary.totalIfcSpaces >= 1`.
- No Revit transaction created.

---

### 44.5 Phase 4 — Controlled Sync (`sync_ifc_space_room_data`)

#### Step A — Dry Run (default)

From Phase 4 validation, identify a High- or Medium-confidence match where Room Name differs from IFC Name.

**Prompt:**
```
call sync_ifc_space_room_data with linkInstanceId=<id> and syncItems=[{"linkedElementId":<spaceId>,"roomId":<roomId>,"updateName":true,"updateNumber":false}] and dryRun=true
```

**Expected:**
- `success: true`
- `dryRun: true`
- `status: "DryRunWouldUpdateName"` for the targeted item.
- No model changes — Revit Undo unchanged.

---

#### Step B — Live Sync (Requires Approval)

**Prompt:**
```
call sync_ifc_space_room_data with linkInstanceId=<id> and syncItems=[{"linkedElementId":<spaceId>,"roomId":<roomId>,"updateName":true,"updateNumber":false}] and dryRun=false
```

**Approval flow:**
1. Approval in Pending tab.
2. After approval: `status: "UpdatedName"`.
3. Room.Name in Revit updated to IFC Name.
4. Revit Undo shows the transaction.
5. No shared parameters, no IFC GUIDs, no Comments modified.

---

#### Step C — Ambiguous match is blocked

Identify an Ambiguous match from Phase 4 validation.

**Prompt:**
```
call sync_ifc_space_room_data with linkInstanceId=<id> and syncItems=[{"linkedElementId":<ambiguousSpaceId>,"roomId":<anyRoomId>,"updateName":true}] and dryRun=false
```

**Expected (even after approval):**
- `status: "BlockedAmbiguousMatch"` — sync is refused, no write performed.
- Error message explains the ambiguity.

---

#### Step D — Low-confidence match is blocked by default

**Expected:**
- `status: "BlockedLowConfidence"` without explicit opt-in.

---

### 44.6 Non-Negotiables Checklist

After the full pipeline run, verify:

| Requirement | How to verify |
|---|---|
| No shared parameters created | Open Revit's Manage → Shared Parameters — no new entries from MCP |
| No IFC GUIDs in Room fields | Inspect created Room parameters — no GUID strings in Number, Name, Comments |
| No Comments field written | Check Room.Comments — must be empty/unchanged |
| No Extensible Storage used | Check room element properties — no data storage schemas attached |
| Existing Rooms not overwritten | Run Phase 3 twice on same spaces — second run returns `SkippedExisting` not `Created` |
| Ambiguous matches always blocked in sync | Verify Step C above |
| One bad space does not abort batch | Corrupt one element's geometry artificially (or use a non-IfcSpace element ID) and verify others still succeed |
| Dry-run makes zero model changes | Run dry-run and confirm Revit Undo history is empty |
| Read-only tools have no transactions | `ifc_list_links`, `ifc_preview_spaces`, `ifc_preview_space_geometry`, `validate_ifc_space_room_conversion` must NOT appear in Revit Undo |

---

### 44.7 Section 30 Matrix Rows for IFC Tools

Add these rows to the Section 30 matrix:

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Coord | `ifc_list_links` | RO | "List all IFC links." | Array of `{linkInstanceId, linkName, isLoaded}` | N/A | N/A | Empty array when no links loaded |
| Coord | `ifc_preview_spaces` | RO | "Preview IFC spaces for link `<id>`." | `{spaces:[...], summary:{totalCandidates, ready, ...}}` with `detectionConfidence`, `numberSource`, `nameSource` | N/A | N/A | Default: confirmed-only. `includeProbable=true` shows Probable candidates |
| Coord | `ifc_preview_space_geometry` | RO | "Preview geometry for IFC spaces in link `<id>`." | `{items:[...], summary:{geometryReady:N, ...}}` | N/A | N/A | `status=GeometryReady` items have `approxAreaM2` and `placementPoint` |
| Coord | `convert_ifc_spaces_to_rooms` | RA | "Convert IFC spaces to rooms for link `<id>`, dryRun=true first." | DryRun: `{items:[{status:DryRunReady}]}` → no change. Live: `{status:Created}` for each space → Rooms in Revit | Required (DE bypasses) | Per-space `"MCP: Create Room"` Undo entry | Default: skip\_existing blocks Number+Level conflicts. `allow_conflicts` to opt in |
| Coord | `validate_ifc_space_room_conversion` | RO | "Validate IFC space to room conversion for link `<id>`." | `{items:[{confidence, status, matchedRoomId}], summary}` | N/A | N/A | No model changes. Ambiguous matches flagged with both candidate IDs |
| Coord | `sync_ifc_space_room_data` | RA | "Sync room name for space `<spaceId>` to room `<roomId>`, dryRun=true." | DryRun: `{status:DryRunWouldUpdateName}`. Live after approval: `{status:UpdatedName}` | Required (DE bypasses) | Per-item sync transaction Undo entry | Default dryRun=true. Ambiguous always blocked. Low/Medium blocked unless opted in |


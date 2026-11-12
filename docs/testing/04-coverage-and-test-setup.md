# Smoke tests — Tool coverage matrix, result template and test model

Part of the [smoke test checklist](../../TESTING.md).

## 30. Full MCP Tool Coverage Matrix

This section enumerates every MCP tool exposed by `RevitMCP.Bridge` and describes a one-shot smoke test for an agent to run. The intent is end-to-end runnability without a human present (except for steps explicitly noted as **manual approval**).

**Column legend:**
- **Permission** — One of `ReadOnly` (RO), `RequiresApproval` (RA), `DirectEdit` (DE: changes the UI selection or connector state, not the model; no approval, not read-only), `DestructiveRequiresManualApproval` (DRA). Source: `RevitMCP.Core/Models/ToolPermission.cs`.
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
| Selection | `revit_select_elements` | DE | "Select elements with ids [12345, 12346]." | `{count}` and elements highlighted in Revit UI | N/A | N/A | Selection is UI-only, not a model change: no approval, but not read-only |
| Selection | `revit_select_elements_by_query` | DE | "Select all walls on Level 1." | `{count}` and elements highlighted | N/A | N/A | Same UI-only semantics |

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
| Electrical | `revit_move_elements_between_circuits` | RA | "Move element 12345 from its circuit to circuit id 99999." | Approval flow → element on target circuit, source circuit warning if emptied | Required (DE bypasses) | Single Undo | — |
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
| Electrical | `revit_select_circuit_elements` | DE | "Select all elements on circuit 99999." | UI selection updated | N/A | N/A (UI-only) | Selection only: no approval, not read-only |
| Electrical | `revit_select_uncircuited_elements` | DE | "Select all uncircuited Lighting Fixtures." | UI selection updated | N/A | N/A (UI-only) | Same UI-only semantics |

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

1. Run `config_read` with `scope="user"`. If the file doesn't exist, confirm a clear "not found" error.
2. Run `config_update` with `scope="user"`, `createIfMissing=true` and `updates={"created":"true"}` (approval). Confirm the file is created; `config_read` itself never creates one (read-only).
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
| Elements | `revit_preview_delete_elements` | RO | "Preview deleting elements [12345]." | Dry-run with per-element `wouldDelete`/`reason`; pinned protected; views/sheets rejected with tool hint | N/A | N/A | `skipPinned=true` default; HIGH RISK warning for Levels/links |
| Elements | `revit_delete_elements` | **DRA** | "Delete elements [12345]." | `approval_required` even with DE on → summary contains `DESTRUCTIVE` line | **Manual approval ALWAYS required** | Revit Undo restores | DE never bypasses; `totalRemoved` counts cascade-deleted dependents |

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
| Coord | `revit_run_clash_preset` | RO | "Run preset 'Electrical vs HVAC'." | `{totalClashCount, ruleResults:[...]}` cached for review | N/A | N/A | Result cached per document (`ClashRuns/<model id>.json`); next/previous keep a cursor per client |
| Coord | `revit_export_clash_report_to_excel` | RO | "Export last clash run to Excel." | `{filePath: "...ClashReport_*.xlsx"}` | N/A | N/A | File has Summary + Details sheets |
| Coord | `revit_get_clash_dashboard_summary` | RO | "Get clash dashboard summary." | `{totalClashes, byRule, bySeverity, byStatus}` | N/A | N/A | — |
| Coord | `revit_get_next_clash` | RO | "Get next clash." | `{currentIndex, totalCount, clash}` | N/A | N/A | Returns error when no cache exists |
| Coord | `revit_get_previous_clash` | RO | "Get previous clash." | `{currentIndex, totalCount, clash}` with index decremented | N/A | N/A | Wraps to last clash at index 0 |
| Coord | `revit_select_clash_elements` | DE | "Select elements of clash CL-0001." | Both elements selected in the UI | N/A | N/A | Linked elements: selects `RevitLinkInstance` as fallback |
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
| Config | `config_read` | RO | "Read the user config file." | JSON config object; a missing file is reported, never created | N/A | N/A | Detailed coverage in S6; scope: `user`, `project`, or `addin` |
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
| Skills | `revit_run_skill` | RA | "Run skill 'company.electrical.qa'." | `approval_required` only when an enabled task changes the model → per-task `{success, issueCount}` | Required for model-changing tasks (DE bypasses) | Single Undo (model-writing tasks) | Call `revit_preview_skill_run` first; read-only tasks run without transaction |
| Skills | `revit_run_skill_task` | RA | "Run task 'check.cabletray.vs.ducts' in skill 'company.electrical.qa'." | `approval_required` → single-task result | Required (DE bypasses) | Single Undo | Useful for re-running or debugging one task |
| Skills | `revit_create_project_skill_override` | RO | "Create a project override for skill 'company.electrical.qa' for project 'P-2026-001'." | `{overridePath, skillId, projectId}` confirming file created | N/A | N/A | `changesJson`: `{\"tasks\":{\"<taskId>\":{\"enabled\":true,\"settings\":{...}}}}` |
| Skills | `revit_update_project_skill_override` | RO | "Update the project override: disable task 'check.facp.clearance'." | `{updated: true, mergedKeys}` | N/A | N/A | Merges `changesJson` into existing override |
| Skills | `revit_reset_project_skill_override` | RO | "Reset project override for skill 'company.electrical.qa' on project 'P-2026-001'." | `{deleted: true}` or not-found message | N/A | N/A | Reverts to company master skill |
| Skills | `revit_configure_sheet_naming_skill` | DE | "Configure sheet naming skill for project 'P-2026-001' with Excel register at `C:\Temp\register.xlsx`." | `{overridePath, configuredTasks}` | N/A | N/A | Convenience wrapper for `company.lehed.nimetamise-kontroll`; set `enableExcelComparison=true` |
| Skills | `revit_skill_builder_guide` | RO | "I want to create a new skill." | Static guided-interview workflow text | N/A | N/A | Bridge-side static tool; no pipe/Revit connection needed |
| Skills | `revit_list_skill_tasks` | RO | "What task building blocks are available for skills?" | Array of `{id, name, changesModel, exampleSettings, usedBySkills}` | N/A | N/A | Example settings harvested from loaded skills |
| Skills | `revit_create_skill` | RA | "Create a skill 'user.test.demo' that scans a delivery folder and exports an Excel report." | `approval_required` → skill file written, registry reloaded, activation instructions returned | Required (DE bypasses) | Delete the `.skill.json` file to revert | Validates task ids; refuses company-master ids; skill content must be English |
| Skills | `revit_update_skill` | RA | "Rename skill 'user.test.demo' to 'Demo Delivery Check'." | `approval_required` → file rewritten, `updatedFields` listed | Required (DE bypasses) | Re-run with previous values to revert | Company masters refused with pointer to override tools; `tasks` is a FULL replacement |

### 30.27 Skills Admin

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Skills Admin | `revit_compare_skill_override_to_master` | RO | "Compare project override for skill 'company.electrical.qa' on project 'P-2026-001' to master." | `{changedSettings, disabledTasks, newTasksInMaster, versionMismatch}` diff JSON | N/A | N/A | Returns `upToDate: true` when no diffs found |
| Skills Admin | `revit_propose_master_skill_update` | DE | "Propose a master update for skill 'company.electrical.qa' from project 'P-2026-001'." | `{proposalPath}` confirming proposal JSON written to local proposals folder | N/A | N/A | Never modifies company master files directly |
| Skills Admin | `revit_export_skill_override_diff_markdown` | RO | "Export a Markdown diff of the override for skill 'company.electrical.qa' on project 'P-2026-001'." | `{filePath}` pointing to a `.md` diff report in the exports folder | N/A | N/A | Does not modify any skill files |

### 30.28 Standards

| Area | Tool | Permission | Smoke Prompt | Expected Result | Approval | Undo | Notes |
|------|------|------------|--------------|-----------------|----------|------|-------|
| Standards | `standards_list_sources` | RO | "List company standards sources." | Array of `{id, name, enabled, fileCount, indexedAt}` | N/A | N/A | Config in `StandardsSources.json` under user data folder |
| Standards | `standards_index_sources` | DE | "Index all enabled company standards sources." | `{indexed, skipped, errors}` counts per source | N/A | N/A | `force=true` rebuilds stale indexes; run before `standards_search` |
| Standards | `standards_search` | RO | "Search company standards for 'cable tray sizing'." | Array of `{chunkId, source, heading, score, snippet}` | N/A | N/A | Run `standards_index_sources` first; `discipline` hint improves relevance |
| Standards | `standards_get_document_chunk` | RO | "Get document chunk 'src1::chunk-042' with 1 context chunk before and after." | `{chunkId, text, source, heading, contextBefore:[], contextAfter:[]}` | N/A | N/A | `chunkId` from `standards_search` results |
| Standards | `standards_validate_source_config` | RO | "Validate the standards source configuration." | `{valid, issues:[{source, issue}]}` — with no config, its path and `exampleConfig`; creates nothing | N/A | N/A | Run first to diagnose missing or misconfigured sources |

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

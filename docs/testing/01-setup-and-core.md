# Smoke tests — Setup, connection and core tools

Part of the [smoke test checklist](../../TESTING.md).

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

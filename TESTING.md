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

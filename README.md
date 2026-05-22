# EULE MCP — Revit MCP Connector

A local [Model Context Protocol](https://modelcontextprotocol.io) connector that lets **Claude Code** and **Codex** interrogate and work with a live **Autodesk Revit 2026** model in real time.

**66 tools** across two functional areas:
- **General** (22 tools) — element discovery, parameter QA, grouping, Excel exports, selection, and write operations
- **Electrical** (44 tools) — full circuit lifecycle: discovery, QA, creation, panel assignment, cable/wire type management, load naming, circuit numbering, Excel reporting, electrical dashboard & panel QA, voltage drop prep, and fire alarm circuit preset workflows

---

## Supported Clients

| Client | Status | Setup |
|--------|--------|-------|
| [Claude Code](https://claude.ai/code) | Supported | `Install-Claude-MCP.bat` |
| [Codex CLI](https://github.com/openai/codex) | Supported | `Install-Codex-MCP.bat` |
| ChatGPT / other | Not targeted | — |

Both clients connect through the same `RevitMCP.Bridge.exe`. The bridge is started by the AI client over STDIO; client identity is passed via `--client` argument so logs correctly identify who made each request.

---

## Architecture

```
Claude Code / Codex
    │  MCP JSON-RPC 2.0 over STDIO
    ▼
RevitMCP.Bridge.exe          ← .NET 8 console app
    │  Named Pipe (RKTools.RevitMCP.2026)
    ▼
RevitMCP.Addin.dll           ← Revit 2026 add-in
    │  ExternalEvent (Revit API thread)
    ▼
Revit 2026 model             ← live read-only access
```

All Revit API calls are routed through Revit's `ExternalEvent` mechanism — no threading violations, no crashes.

---

## Projects

| Project | Target | Role |
|---------|--------|------|
| `RevitMCP.Core` | net8.0 | Shared DTOs — `McpToolRequest`, `McpToolResult`, enums |
| `RevitMCP.Addin` | net8.0-windows | Revit add-in DLL — pipe server, tool registry, WPF UI |
| `RevitMCP.Bridge` | net8.0 | STDIO MCP server — forwards tool calls over named pipe |
| `RevitMCP.Config` | — | Install scripts and default configs |
| `RevitMCP.Tests` | net8.0 | xUnit unit tests for pure-logic helpers (no Revit runtime required) |

---

## Available Tools

| Tool | Description |
|------|-------------|
| `revit_get_connection_status` | Revit version, document title, active view, worksharing info, selection count |
| `revit_get_selected_elements` | Category, family, type, level, location, bounding box for selected elements |
| `revit_list_views` | All non-template printable views with type, scale, discipline, sheet placement |
| `revit_list_sheets` | All sheets with number, name, and placed view names |
| `revit_list_schedules` | All schedules with category and field names |
| `revit_get_element_parameters` | Reads instance/type parameters for element IDs or selection, including shared parameter metadata |
| `revit_count_elements` | Element counts grouped by Category or FamilyAndType, with optional category filter |
| `revit_group_by_parameter` | Convenience tool for grouping elements by one parameter |
| `revit_find_elements_by_parameter` | Finds elements using one or more parameter filters |
| `revit_get_elements_info` | Returns structured element info with selected parameter values |
| `revit_group_elements` | Groups elements by category, family, type, level, or multiple parameters |
| `revit_export_query_to_excel` | Exports query/grouping results to a formatted `.xlsx` file |
| `revit_get_available_parameters` | Discovers available parameters with fill stats and example values |
| `revit_list_query_presets` | Lists reusable query presets from config |
| `revit_run_query_preset` | Runs a saved preset by name, optionally exports to Excel |
| `revit_check_parameter_completeness` | Checks required parameters exist and are filled (model QA) |
| `revit_export_view_list_to_excel` | Exports all views to `.xlsx` with type, scale, sheet placement |
| `revit_export_sheet_list_to_excel` | Exports all sheets to `.xlsx` with placed views |
| `revit_export_schedule_list_to_excel` | Exports all schedules to `.xlsx` with fields |
| `revit_select_elements` | Selects elements by IDs in Revit UI *(requires approval)* |
| `revit_select_elements_by_query` | Selects elements by query in Revit UI *(requires approval)* |
| `revit_set_parameter` | Sets a parameter value on elements — supports String, Integer, Double, and **ElementId** storage types *(requires approval, runs in transaction)* |

### Electrical Circuit Tools

| Tool | Description |
|------|-------------|
| `revit_get_electrical_circuits` | Lists electrical circuits with optional filters by panel name, circuit number, and system type (PowerCircuit / Data / FireAlarm / etc.) |
| `revit_get_circuit_info` | Detailed information for one circuit by element ID — connected elements, load, wire type, parameters |
| `revit_get_available_panels` | Lists electrical equipment / distribution boards that circuits can be assigned to |
| `revit_get_available_cable_types` | Lists cable types defined in the project (warns if not separately defined — wire types used as fallback) |
| `revit_get_available_wire_types` | Lists all wire types available in the active document |
| `revit_get_circuit_compatible_elements` | Checks whether elements can be added to a circuit; optionally validates against a target circuit ID |
| `revit_create_electrical_circuit` | Creates a new electrical circuit from a selection or query *(requires approval)* |
| `revit_add_elements_to_circuit` | Adds elements to an existing circuit *(requires approval)* |
| `revit_reassign_circuit_panel` | Reassigns a circuit to a different panel *(requires approval)* |
| `revit_change_circuit_cable_or_wire_type` | Changes the cable/wire type on a circuit; prefers cable type, falls back to wire type *(requires approval)* |
| `revit_set_circuit_parameter` | Sets **any** parameter on one or more circuits — fully handles `ElementId` storage type (Cable Type and similar) by resolving a numeric element ID or an exact element name *(requires approval)* |
| `revit_find_uncircuited_elements` | Finds elements in electrical/lighting/data/fire/security categories that are not assigned to any circuit; supports category lists, parameter filters, and parameter return |
| `revit_check_circuit_health` | Central circuit QA tool — configurable checks: `MissingPanel`, `EmptyCircuitNumber`, `DuplicateCircuitNumbers`, `MissingCableType` (strict: Revit 2026 CableType ElementId not set), `MissingWireType` (lenient: neither CableType nor legacy WireType resolves to a name), `MissingLoadName`, `NoConnectedElements`. Flagged circuits include the resolved `wireType` for inline cross-checking against `revit_get_circuit_info` |
| `revit_export_panel_circuit_list_to_excel` | Exports a panel-organized circuit report to `.xlsx` with Summary, Panel Circuits, Circuit Elements, and Health Issues sheets |
| `revit_find_circuits_by_element_parameter` | Finds circuits containing elements matching category and parameter filters (e.g. circuits in room 201, circuits with specific device types) |
| `revit_trace_circuit` | Traces an element or circuit back to its panel — returns circuit number, load name, wire type, apparent load, and panel details |
| `revit_check_circuit_parameter_completeness` | Checks required parameters on circuit elements — returns per-parameter fill rates and IDs of circuits with empty values |
| `revit_select_circuit_elements` | Selects all elements connected to a circuit in the Revit UI *(requires approval)* |
| `revit_select_uncircuited_elements` | Selects elements not assigned to any circuit across electrical categories *(requires approval)* |
| `revit_export_circuit_health_to_excel` | Exports circuit QA issues (missing panel, duplicate numbers, missing cable type, missing load name) to `.xlsx` |
| `revit_export_uncircuited_elements_to_excel` | Exports elements not assigned to any circuit to `.xlsx` with optional parameter columns |
| `revit_get_circuits_for_selected_elements` | Returns all circuits for the current Revit selection, de-duplicated. Reports `notTraceableCount` (not a FamilyInstance/MEPModel) and `noCircuitCount` (traceable but unassigned) as separate warning categories |
| `revit_find_elements_on_circuit` | Lists all elements connected to a specific circuit with category, family, type, and optional parameters |
| `revit_get_circuit_load_summary` | Summarizes circuit apparent loads grouped by Panel, SystemType, CableType, or WireType |
| `revit_check_panel_utilization` | Checks circuit count, total load, and data quality issues (missing cable type, load name, circuit number) per panel |
| `revit_preview_circuit_numbering` | Previews renumbering proposals for panel circuits without modifying the model |
| `revit_apply_circuit_numbering` | Applies previewed circuit number changes *(requires approval, transaction-wrapped)* |
| `revit_preview_circuit_load_names` | Previews load name proposals using a `{ParameterName}` template resolved from connected elements |
| `revit_apply_circuit_load_names` | Applies previewed load name changes *(requires approval, transaction-wrapped)* |
| `revit_set_circuit_parameters_bulk` | Sets multiple parameters on multiple circuits in a single transaction *(requires approval)* |

### Electrical Dashboard & Panel QA Tools

| Tool | Description |
|------|-------------|
| `revit_get_electrical_dashboard_summary` | Aggregated dashboard: circuit counts by panel/system type, total load per panel, missing-data stats (no cable type, no load name, no panel, duplicate numbers) |
| `revit_get_panel_issue_summary` | Per-panel issue breakdown — duplicate numbers, missing cable types, missing load names, unassigned circuits |
| `revit_export_electrical_dashboard_to_excel` | Exports the dashboard summary to `.xlsx` with a Dashboard sheet and a per-panel Issues sheet |

### Voltage Drop Preparation Tools

| Tool | Description |
|------|-------------|
| `revit_get_circuit_route_assumptions` | Returns the routing assumptions for a circuit (installation method, conductor material, temperature rating) used as voltage drop inputs |
| `revit_estimate_circuit_length` | Estimates the cable length for a single circuit using element locations and a configurable method (StraightLine, Manhattan, Estimate) |
| `revit_estimate_circuit_lengths` | Bulk version of `revit_estimate_circuit_length` — estimates lengths for multiple circuits in one call |
| `revit_export_voltage_drop_input_to_excel` | Exports voltage drop input data (circuit, panel, load, estimated length, cable type) to `.xlsx`. Accepts `circuitIds` (array — exports only those circuits), or `panelName`/`systemType` filters when no IDs given |
| `revit_get_voltage_drop_precheck` | Pre-checks one or more circuits for voltage drop calculation readiness. Accepts `circuitIds` (array, preferred) or single `circuitId`. Flags missing cable type, load, voltage, and unreachable locations. Returns per-circuit results with a bulk summary when multiple IDs are provided |

### Fire Alarm Circuit Preset Tools

| Tool | Description |
|------|-------------|
| `revit_run_fire_alarm_circuit_preset` | Analyses fire alarm circuits, classifies each loop (AddressableLoop / ConventionalSounderLine / ModuleLoop), and returns a structured preset with device counts and recommended cable types |
| `revit_export_fire_alarm_circuit_preset_to_excel` | Exports the fire alarm circuit preset to `.xlsx` with a Summary sheet and a per-loop Devices sheet |
| `revit_get_fire_alarm_visualization_data` | Returns structured location data for fire alarm devices grouped by loop — used for spatial visualisation. Standalone HTML/SVG export (`revit_export_fire_alarm_visualization_html`) is intentionally deferred until the JSON format is validated on real projects; AI clients can generate temporary HTML from this JSON if needed |
| `revit_get_fire_alarm_voltage_drop_summary` | Summarises estimated voltage drop inputs per fire alarm loop using classified loop types and estimated cable lengths |
| `revit_list_cable_resistance_profiles` | Lists all cable resistance profiles (Ω/m) from the config file (`electrical-cable-profiles.json`) |
| `revit_get_matching_cable_resistance_profile` | Finds the best-matching cable resistance profile for a given cable type name |

Advanced tools that accept `filters` and `groupBy` expect valid JSON arrays. See [Example JSON Arguments](#example-json-arguments) below.

Selection tools affect the active Revit UI selection. Write tools require approval inside Revit before changes are applied.

Parameter name matching uses `ContainsNormalized` mode by default — partial names like `ELENEA_Nimetus` match full shared parameter names like `ELENEA_ÜLD 001_Nimetus`.

---

## Example Prompts

```
How many fire alarm devices are in the model?

Group fire alarm devices by ELENEA_Nimetus and ELENEA_Tootja.

Find all elements where ELENEA_Nimetus contains "andur".

Get element info for Fire Alarm Devices and return Nimetus, Tähis, Tootja, and Mudel.

Export all Fire Alarm Devices grouped by ELENEA_Nimetus and ELENEA_Tootja to Excel.

What parameters are available for Fire Alarm Devices?

Run the Fire Alarm Device Report preset and export it to Excel.

Check Fire Alarm Devices for missing ELENEA_Nimetus, ELENEA_Tootja, and ELENEA_Mudel.

Export all sheets to Excel.

Find all Fire Alarm Devices missing ELENEA_Tootja and select them in Revit.

Set Comments to "Checked by AI" for the current selection.

List all electrical circuits on panel "DB-L1".

What wire type is assigned to circuit 2520343?

Show me all circuits without a cable type assigned.

Assign cable type "XX_EN_IT_Cat6a" to circuits 2520343 and 2520353.

Create a new power circuit from the currently selected devices and assign it to panel "DB-L1".

Change the wire type of circuit 2518001 to "XX_EN_IT_Cat6a".
```

---

## Example JSON Arguments

### `revit_find_elements_by_parameter`

```json
{
  "category": "Fire Alarm Devices",
  "filters": [
    {
      "parameterName": "ELENEA_Nimetus",
      "operator": "contains",
      "value": "andur",
      "matchMode": "ContainsNormalized",
      "scope": "InstanceAndType"
    }
  ],
  "returnParameters": ["ELENEA_Nimetus", "ELENEA_Tootja", "ELENEA_Mudel"],
  "limit": 200
}
```

### `revit_group_elements`

```json
{
  "category": "Fire Alarm Devices",
  "groupBy": [
    {
      "type": "Parameter",
      "parameterName": "ELENEA_Nimetus",
      "scope": "InstanceAndType",
      "matchMode": "ContainsNormalized"
    },
    {
      "type": "Parameter",
      "parameterName": "ELENEA_Tootja",
      "scope": "InstanceAndType",
      "matchMode": "ContainsNormalized"
    }
  ],
  "includeElements": false,
  "limit": 5000
}
```

### `revit_export_query_to_excel`

```json
{
  "category": "Fire Alarm Devices",
  "groupBy": [
    { "type": "Parameter", "parameterName": "ELENEA_Nimetus" },
    { "type": "Parameter", "parameterName": "ELENEA_Tootja" }
  ],
  "parameters": ["ELENEA_Nimetus", "ELENEA_Tähis", "ELENEA_Tootja", "ELENEA_Mudel"],
  "outputMode": "Both",
  "fileName": "FireAlarm_Device_Report.xlsx",
  "limit": 5000
}
```

### Invalid JSON Handling

Advanced tools that accept `filters` or `groupBy` expect valid JSON arrays. If malformed JSON is provided, the bridge returns a structured JSON error response and does not forward the request to Revit.

For Excel export (`revit_export_query_to_excel`), invalid `filters` or `groupBy` will abort the export to avoid accidentally exporting unfiltered model data.

---

## Electrical Circuit Examples

### Assign a cable type to multiple circuits

```
revit_set_circuit_parameter(
  circuitIds: [2520343, 2520353],
  parameterName: "Cable Type",
  value: "XX_EN_IT_Cat6a"     ← element name, or use the numeric ID "2518789"
)
```

`revit_set_circuit_parameter` resolves `ElementId`-type parameters automatically: provide either the **element name** (looked up in the document) or the **numeric element ID**.

### Find circuits missing a cable type

```json
{
  "filters": [
    { "parameterName": "Cable Type", "operator": "isEmpty" }
  ],
  "limit": 500
}
```

### Create a circuit from a selection

```
revit_create_electrical_circuit(
  useSelection: true,
  systemType: "PowerCircuit",
  panelName: "DB-L1",
  wireTypeName: "XX_EN_IT_Cat6a"
)
```

---

## MCP Window

The **MCP Connector** window has three tabs:

| Tab | Contents |
|-----|----------|
| **Status** | Running/Stopped chip, pipe name, active model, active view, worksharing flag, selected element count, Start/Stop/Panic controls |
| **Pending** | Queue of tool calls awaiting approval — Approve / Reject per request. Auto-switches to this tab when a new approval arrives |
| **Activity** | Live DataGrid of tool call history — Time, Tool, Duration (ms), Status (colour-coded); row tooltip shows the result message. "Open Log Folder" and "Clear" buttons at the bottom |

### Approval Required Toggle

The **Approval Required** button in the title bar controls whether write tools (`RequiresApproval`) must be confirmed in the Pending tab before executing.

- **Enabled** (default) — all write operations queue for manual approval inside Revit
- **Disabled** (Direct Edit mode) — write operations execute immediately without queuing; a confirmation dialog warns before enabling this mode

> **Safety note:** Direct Edit mode is intended for development and admin testing only. In normal company use, keep Approval Required enabled so every write action appears in the Pending tab before execution. Direct Edit always starts disabled when Revit loads. A confirmation dialog must be acknowledged before it can be enabled.

---

## Requirements

- Revit 2026
- .NET 8 SDK
- Claude Code CLI (`claude`) **or** Codex CLI (`codex`)

---

## Build

```bash
# Release
dotnet build RevitMCP.slnx -c Release

# Debug (faster — recommended during development)
dotnet build RevitMCP.Addin/RevitMCP.Addin.csproj -c Debug
```

`RevitMCP.Addin.dll` is a **single self-contained DLL** — all dependencies (MaterialDesignThemes, Newtonsoft.Json, etc.) are embedded via Costura.Fody, so no extra files need to be deployed.

---

## Loading the Add-in

### Option A — ricaun AppLoader (recommended for development)

Costura.Fody embeds all dependencies, so the addin is a true single-DLL plugin compatible with [ricaun.Revit.AppLoader](https://github.com/ricaun-io/ricaun.Revit.AppLoader) and similar hot-reload tools.

Point AppLoader at:
```
RevitMCP.Addin\bin\Debug\net8.0-windows\RevitMCP.Addin.dll
```

AppLoader shadow-copies the DLL so the file stays writable — rebuild while Revit is running, hit Reload, done. No Revit restart needed.

### Option B — `.addin` manifest (permanent install)

Run `RevitMCP.Config\Install\Install-RevitMCP-Addin.bat`

This creates a manifest in `%ProgramData%\Autodesk\Revit\Addins\2026\` that points to the Release build output. Revit loads the plugin automatically on startup.

> **Note:** Don't use both options at the same time — Revit will load the plugin twice.

---

## MCP Server Setup

### Claude Code

Run `RevitMCP.Config\Install\Install-Claude-MCP.bat`

This registers `RevitMCP.Bridge.exe` as a user-scoped MCP server named `revit-mcp` with `--client "Claude Code"` so logs show the correct client name.

### Codex

Run `RevitMCP.Config\Install\Install-Codex-MCP.bat`

This generates `RevitMCP.Config\Install\codex-mcp-snippet.toml` with the absolute bridge path already filled in. Paste its contents into `%USERPROFILE%\.codex\config.toml` and restart Codex.

---

## Usage

1. Open Revit 2026 and load a model
2. Load the addin (via AppLoader or the `.addin` manifest)
3. On the **RK Tools** ribbon tab, click **MCP Connector**
4. Click **Start Connector** in the window
5. Ask anything about your model from Claude Code or Codex:

```
How many walls are in this model?
List all floor plan views on sheets.
What parameters does element 12345 have?
Group all fire alarm devices by the ELENEA_Nimetus parameter.
```

---

## Logging

Activity is logged to `%AppData%\RKTools\RevitMCP\Logs\{date}.jsonl` — one JSON line per tool call. The **Activity** tab in the MCP window shows a live view; click **Open Log Folder** to browse the raw files.

---

## Project Structure

```
EULE_MCP/
├── RevitMCP.Core/
│   └── Models/          McpToolRequest, McpToolResult, enums
├── RevitMCP.Addin/
│   ├── App.cs           IExternalApplication entry point
│   ├── Commands/        OpenMcpWindowCommand
│   ├── Electrical/      Circuit services, QA helpers, dashboard, voltage-drop prep, fire alarm preset, cable resistance
│   ├── Services/        PipeServer, ExternalEventHandler, ConnectorService
│   ├── Tools/           One file per MCP tool
│   ├── UI/              WPF window (Status + Pending + Activity tabs) + ViewModels + themes
│   └── Interfaces/      IRevitMcpTool
├── RevitMCP.Bridge/
│   ├── Program.cs       MCP host setup
│   ├── RevitMcpTools.cs [McpServerToolType] — exposes tools to Claude
│   └── RevitPipeClient  Named pipe client
├── RevitMCP.Config/
│   └── Install/         .bat install scripts
└── RevitMCP.Tests/
    └── *.Tests.cs             xUnit unit tests for pure-logic helpers (no Revit runtime required)
```

---

## License

MIT

# EULE MCP — Revit MCP Connector

Ask your AI assistant about a live **Autodesk Revit 2026** model in plain English — count elements, inspect circuits, run QA checks, generate Excel reports — without writing scripts or code. EULE MCP is a local [Model Context Protocol](https://modelcontextprotocol.io) connector that gives Claude Code, Codex, and Antigravity CLI direct read/write access to an open Revit model through 150 tools across twelve functional areas.

**150 tools** across twelve functional areas:
- **General** (25 tools) — element discovery, parameter QA, grouping, Excel exports, selection, write operations, config-driven parameter QA rule sets, and detailed geometry inspection of selected elements
- **Electrical** (44 tools) — full circuit lifecycle: discovery, QA, creation, panel assignment, cable/wire type management, path mode control, load naming, circuit numbering, Excel reporting, electrical dashboard & panel QA, voltage drop prep, and fire alarm circuit preset workflows
- **Documentation** (22 tools) — view and sheet management: discovery, summary, preview/apply workflows for placing views, creating/duplicating/renaming sheets and views, bulk parameter updates, revision tracking, preset inspection, and safe destructive delete with mandatory manual approval
- **Coordination** (15 tools) — Revit-native clash detection: category/link discovery, solid-intersection hard-clash and clearance checking, preset management, Excel reporting, and step-through review views
- **Family Creation** (1 tool) — generate Detail Item families (.rfa) from DWG source files using company presets
- **Skills** (10 tools) — multi-step QA workflow engine: run built-in or project-specific quality-check skill definitions, inspect task breakdowns, manage per-project setting overrides, compare overrides against master, propose master updates, and export Markdown diff reports
- **Issue Reports** (2 tools) — shared structured issue model (`IssueDto` / `IssueReportDto`) with JSON, Excel, Markdown, and interactive HTML export; multi-report merge; foundation used by all QA tools
- **File System** (6 tools) — read, write, list, inspect, copy, and backup local files with configurable path-policy enforcement (allowed-root lists, traversal blocking, size limits)
- **Excel** (5 tools) — standalone Excel workbook tools (no open document required): inspect workbooks, read ranges, update cells, insert rows, and append table rows with automatic backup, header-matching, and dry-run preview support
- **Delivery** (4 tools) — pre-issue delivery folder QA: scan folders for EULE-format drawing files, cross-check against Revit sheets or an Excel document register, and run a combined full-check with optional Issue Report and Excel/Markdown export
- **Standards** (5 tools) — company document standards lookup: index PDF/Word/Markdown/text files, full-text search with TF-IDF scoring, retrieve specific chunks with surrounding context, and validate source config
- **Configuration** (5 tools) — read and update JSON config files at company, user, tool-state, and project scopes

---

## You can ask things like

```
How many fire alarm devices are in the model?
List all electrical circuits on panel "DB-L1".
Detect hard clashes between Electrical Equipment and Mechanical Equipment.
Run the ELENEA Basic QA rule set and export the issue report to Excel.
Scan delivery folder C:\Projects\1626\Export for temp files and old revisions.
```

→ [See the full list of example prompts](#example-prompts)

---

## Requirements

- **Revit 2026** (.NET 8) — full feature set, all 150 tools
- **Revit 2024** (.NET Framework 4.8) — *read-only subset* (~114 tools): write/edit tools, WPF approval window, IFC space-to-room, and skill-run tools are disabled
- .NET 9 SDK (to build the `.slnx`; the Revit 2026 add-in targets .NET 8)
- Claude Code CLI (`claude`), Codex CLI (`codex`), **or** Antigravity CLI (`agy`)

### Revit 2024 read-only mode

The addin multi-targets `net8.0-windows` and `net48`. In Revit 2024 the AppLoader picks up `RevitMCP.Addin\bin\Release\net48\RevitMCP.Addin.dll`; in Revit 2026 it loads the `net8.0-windows` build. The bridge and pipe protocol are unchanged. Tools that mutate the model, write parameters, place/duplicate/rename/delete sheets or views, create circuits, run skills, or open the WPF UI are not registered for the net48 build — clients see a smaller tool list. Approvals are auto-bypassed (no UI) but **destructive tools are also unavailable**, so safety is preserved.

---

## Supported Clients

| Client | Status | Setup |
|--------|--------|-------|
| [Claude Code](https://claude.ai/code) | Supported | `Install-Claude-MCP.bat` |
| [Codex CLI](https://github.com/openai/codex) | Supported | `Install-Codex-MCP.bat` |
| [Antigravity CLI](https://antigravity.google) | Supported | `Install-AntigravityCLI-MCP.bat` |
| ChatGPT / other | Not targeted | — |

All clients connect through the same `RevitMCP.Bridge.exe`. The bridge is started by the AI client over STDIO; client identity is passed via `--client` argument so logs correctly identify who made each request.

---

## Getting Started

### Step 1 — Build

```bash
# Release
dotnet build RevitMCP.slnx -c Release

# Debug (faster — recommended during development)
dotnet build RevitMCP.Addin/RevitMCP.Addin.csproj -c Debug
```

`RevitMCP.Addin.dll` is a **single self-contained DLL** — all dependencies (MaterialDesignThemes, Newtonsoft.Json, etc.) are embedded via Costura.Fody, so no extra files need to be deployed.

### Step 2 — Load the Add-in into Revit

#### Option A — ricaun AppLoader (recommended for development)

Costura.Fody embeds all dependencies, so the addin is a true single-DLL plugin compatible with [ricaun.Revit.AppLoader](https://github.com/ricaun-io/ricaun.Revit.AppLoader) and similar hot-reload tools.

Point AppLoader at:
```
RevitMCP.Addin\bin\Debug\net8.0-windows\RevitMCP.Addin.dll
```

AppLoader shadow-copies the DLL so the file stays writable — rebuild while Revit is running, hit Reload, done. No Revit restart needed.

#### Option B — `.addin` manifest (permanent install)

Run `RevitMCP.Config\Install\Install-RevitMCP-Addin.bat`

This creates a manifest in `%ProgramData%\Autodesk\Revit\Addins\2026\` that points to the Release build output. Revit loads the plugin automatically on startup.

> **Note:** Don't use both options at the same time — Revit will load the plugin twice.

### Step 3 — Register with your AI client

**Claude Code**

Run `RevitMCP.Config\Install\Install-Claude-MCP.bat`

This registers `RevitMCP.Bridge.exe` as a user-scoped MCP server named `revit-mcp` with `--client "Claude Code"` so logs show the correct client name.

**Codex**

Run `RevitMCP.Config\Install\Install-Codex-MCP.bat`

This generates `RevitMCP.Config\Install\codex-mcp-snippet.toml` with the absolute bridge path already filled in. Paste its contents into `%USERPROFILE%\.codex\config.toml` and restart Codex.

**Antigravity CLI**

Run `RevitMCP.Config\Install\Install-AntigravityCLI-MCP.bat`

This registers `RevitMCP.Bridge.exe` in `%USERPROFILE%\.gemini\config\mcp_config.json` with `--client AntigravityCLI`. This is Antigravity's global MCP config, shared by the CLI (`agy`), the Antigravity IDE, and Antigravity 2.0 — all three pick up the server automatically on startup. In the CLI, type `/mcp` to verify the connection.

> **Note:** Google retired Gemini CLI on June 18, 2026 in favor of Antigravity CLI. The old registration in `%USERPROFILE%\.gemini\settings.json` (`mcpServers` block) is no longer read — Antigravity uses the standalone `mcp_config.json` instead.

### Step 4 — Connect and start asking

1. Open Revit 2026 and load a model
2. Load the addin (via AppLoader or the `.addin` manifest)
3. On the **RK Tools** ribbon tab, click **MCP Connector**
4. Click **Start Connector** in the window
5. Ask anything about your model from Claude Code, Codex, or Antigravity CLI:

```
How many walls are in this model?
List all floor plan views on sheets.
What parameters does element 12345 have?
Group all fire alarm devices by the ELENEA_Nimetus parameter.
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

## Logging

Activity is logged to `%AppData%\RKTools\RevitMCP\Logs\{date}.jsonl` — one JSON line per tool call. The **Activity** tab in the MCP window shows a live view; click **Open Log Folder** to browse the raw files.

---

## Available Tools

| Tool | Description |
|------|-------------|
| `revit_get_connection_status` | Revit version, document title, active view, worksharing info, selection count |
| `revit_get_selected_elements` | Category, family, type, level, location, bounding box for selected elements |
| `revit_inspect_selected_elements` | Detailed inspection of selected elements: category, family/type, level, mm location (point or curve), mm bounding box (min/max/size/center), optional parameters preview, geometry summary (solid/mesh/curve counts, volume mm³). Args: `includeParameters` (true), `parameterNames[]`, `includeGeometrySummary` (true), `limit` (50). |
| `revit_list_views` | All non-template printable views with type, scale, discipline, sheet placement |
| `revit_list_sheets` | All sheets with number, name, and placed view names |
| `revit_list_schedules` | All schedules with category and field names |
| `revit_get_element_parameters` | Reads instance/type parameters for element IDs or selection, including shared parameter metadata |
| `revit_count_elements` | Element counts grouped by Category or FamilyAndType, with optional category filter |
| `revit_group_by_parameter` | Convenience tool for grouping elements by one parameter |
| `revit_find_elements_by_parameter` | Finds elements using one or more parameter filters. Supports `useSelection`, `elementIds`, `includeInstanceParameters/TypeParameters`. Pagination: `page`, `pageSize`. Safety caps: `maxParametersPerElement`, `truncateStringLength`. Set `summaryOnly=true` for category/family counts. Response includes `hasMore`, `nextPageToken`. |
| `revit_get_elements_info` | Returns structured element info with selected parameter values. Requires `useSelection`, `elementIds`, `category`, or `summaryOnly=true`. Pagination: `page`, `pageSize`. Safety caps: `maxParametersPerElement`, `truncateStringLength`. Response includes `hasMore`, `nextPageToken`, and `summary` when `summaryOnly=true`. |
| `revit_group_elements` | Groups elements by category, family, type, level, or multiple parameters |
| `revit_export_query_to_excel` | Exports query/grouping results to a formatted `.xlsx` file |
| `revit_get_available_parameters` | Discovers available parameters with fill stats and example values |
| `revit_list_query_presets` | Lists reusable query presets from config |
| `revit_run_query_preset` | Runs a saved preset by name, optionally exports to Excel |
| `revit_check_parameter_completeness` | Checks required parameters exist and are filled (model QA). Pass `returnIssueReport=true` to include a structured `IssueReportDto` in the response. |
| `revit_export_view_list_to_excel` | Exports all views to `.xlsx` with type, scale, sheet placement |
| `revit_export_sheet_list_to_excel` | Exports all sheets to `.xlsx` with placed views |
| `revit_export_schedule_list_to_excel` | Exports all schedules to `.xlsx` with fields |
| `revit_select_elements` | Selects elements by IDs in Revit UI *(requires approval)* |
| `revit_select_elements_by_query` | Selects elements by query in Revit UI *(requires approval)* |
| `revit_set_parameter` | Sets a parameter value on elements — supports String, Integer, Double, and **ElementId** storage types *(requires approval, runs in transaction)* |

### Query Safety & Pagination

All element query tools have built-in response guards and safety defaults enforced by `QueryLimits`:

| Limit | Default | Description |
|-------|---------|-------------|
| `DefaultPageSize` | 100 | Elements returned per page when `pageSize` is not specified |
| `MaxPageSize` | 500 | Hard ceiling for `pageSize`; larger values are clamped with a warning |
| `MaxParametersPerElement` | 40 | Parameters per element; 0 (omit) uses this default |
| `MaxStringLength` | 500 chars | Parameter value truncation; 0 (omit) uses this default |
| `MaxResponseBytes` | 1 MB | Serialized response hard limit; exceeded responses return `ResponseTooLarge` |
| `TimeoutSeconds` | 30 s | Pipe dispatch timeout per tool call |

**Key behaviors:**

- **ResponseGuard** — applied at the pipe boundary. If a serialized response exceeds **1 MB**, the tool returns a `ResponseTooLarge` error with remediation suggestions. Cannot be disabled.
- **Pagination** — `pageSize` defaults to 100 when omitted. Pass `page` (0-based) to walk through pages. Responses include `hasMore` and `nextPageToken`.
- **Per-element limits** — `maxParametersPerElement` caps the number of parameters per element; `truncateStringLength` truncates long parameter values (appends `... [truncated]`). Both default to the safety defaults above when omitted.
- **Summary mode** — `summaryOnly=true` on `revit_get_elements_info` or `revit_find_elements_by_parameter` returns category/family counts without building element DTOs. This is the recommended first step for broad model scans. Broad detailed queries without `category`, `elementIds`, or `useSelection` are blocked unless `summaryOnly=true`.
- **Clamping warnings** — when caller-supplied `pageSize`, `maxParametersPerElement`, or `truncateStringLength` exceed the limits, the engine clamps the value and adds an explanatory warning to the response.

**Example prompts:**

Get a summary of all elements in the model without returning element details:
```json
{ "summaryOnly": true }
```

Find fire alarm devices but return only 50 at a time:
```json
{ "category": "Fire Alarm Devices", "pageSize": 50, "page": 0 }
```

Get element info with tight parameter and string caps to keep responses small:
```json
{ "category": "Electrical Fixtures", "pageSize": 50, "maxParametersPerElement": 10, "truncateStringLength": 200 }
```

If a query is too broad and would produce an oversized response, prefer: `summaryOnly=true` → `category` filter → specific `parameterNames` → `pageSize: 20` → `includeTypeParameters: false`.

### Parameter QA Rule Set Tools

Config-driven rule sets stored in `%AppData%\RKTools\RevitMCP\parameter-qa-rules.json`. Each rule set contains one or more category rules (category name + required parameters). A default "ELENEA Basic QA" rule set is created on first use.

| Tool | Description |
|------|-------------|
| `revit_list_parameter_qa_rule_sets` | Lists all available parameter QA rule sets — returns name, description, rule count, and per-rule category/parameter details. |
| `revit_run_parameter_qa_rule_set` | Runs all rules in a named rule set. For each rule, collects elements by category and checks required parameters. Merges all issues into one `IssueReportDto`. Args: `ruleSetName` (required), `limitPerRule` (default 5000), `returnIssueReport` (default true). |

### Electrical Circuit Tools

These tools cover the full electrical circuit lifecycle in a live Revit model — from reading panel data and checking QA to creating circuits, applying numbering, and setting load names. All write operations require approval unless Direct Edit mode is enabled.

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
| `revit_set_circuit_path_mode` | Changes electrical circuits from **Farthest Device** to **All Devices** path mode. Preserves manual/custom paths using both `CircuitPathMode == Custom` and `HasCustomCircuitPath`. Scope: `useSelection=true` (circuits containing selected elements), `circuitIds` (explicit list), or all circuits when neither is provided *(requires approval)* |
| `revit_set_circuit_parameter` | Sets **any** parameter on one or more circuits — fully handles `ElementId` storage type (Cable Type and similar) by resolving a numeric element ID or an exact element name *(requires approval)* |
| `revit_find_uncircuited_elements` | Finds elements in electrical/lighting/data/fire/security categories that are not assigned to any circuit; supports category lists, parameter filters, and parameter return |
| `revit_check_circuit_health` | Central circuit QA tool — configurable checks: `MissingPanel`, `EmptyCircuitNumber`, `DuplicateCircuitNumbers`, `MissingCableType` (strict: Revit 2026 CableType ElementId not set), `MissingWireType` (lenient: neither CableType nor legacy WireType resolves to a name), `MissingLoadName`, `NoConnectedElements`. Flagged circuits include the resolved `wireType` for inline cross-checking against `revit_get_circuit_info`. Pass `returnIssueReport=true` to include a structured `IssueReportDto` in the response. |
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

### Documentation / View and Sheet Tools

Browse and manage views, sheets, viewports, and revisions in your open model. All write tools have a matching read-only preview step — check what would change before committing anything.

#### Discovery

| Tool | Description |
|------|-------------|
| `revit_get_view_sheet_summary` | High-level summary: total sheets/views, placed vs unplaced counts, template and title block coverage |
| `revit_list_titleblocks` | Lists all title block family symbols loaded in the document — returns `familySymbolId`, `familyName`, `typeName`, `isInUse` |
| `revit_list_view_templates` | Lists view templates with optional `viewType` filter — returns `elementId`, `name`, `viewType`, `assignedViewCount` |
| `revit_list_revisions` | Lists all revisions — returns sequence number, date, description, issued-by, issued-to, visibility |
| `revit_list_revision_numbering_sequences` | Lists revision numbering sequences — returns sequenceId, name, numberingType, prefix, suffix, minimumDigits. Returns empty list on projects with no custom sequences |
| `revit_get_sheet_revisions` | Returns revisions visible on one or more sheets (by `sheetIds` or `sheetNumbers`) — revisionId, sequenceNumber, revisionNumber, date, description, issuedBy, issuedTo per sheet. *Cloud-specific revision visibility (workshared model cloud tracking) may not be accessible via the Revit 2026 API.* |
| `revit_get_sheet_viewports` | Returns viewport detail for one or more sheets (by `sheetIds` or `sheetNumbers`) — view name, type, sheet position, detail number |
| `revit_find_unplaced_views` | Finds views not placed on any sheet — filterable by `viewTypes`, `nameFilter`, `includeTemplates`, `limit` |
| `revit_list_view_sheet_presets` | Lists available PlaceViews / Sheet Manager preset JSON files from the RK Tools preset folder. Returns fileName, detectedType, sizeBytes, modifiedUtc |
| `revit_get_view_sheet_preset` | Reads and returns the contents of a named preset file — returns workflowType and parsedContent |
| `revit_validate_view_sheet_preset` | Validates preset structure — returns isValid, workflowType, errors[], suggestions[] |
| `revit_run_view_sheet_workflow_preset` | Plans a preset workflow (read-only) — returns workflowType, steps[], notes[]. Does not execute any changes; use separate write tools to execute steps |

`revit_list_views` and `revit_list_sheets` are enhanced in this version:

| Parameter | `revit_list_views` | `revit_list_sheets` |
|-----------|-------------------|--------------------|
| `viewTypes` | Filter by one or more view types | — |
| `nameFilter` | Substring filter on view name | Substring filter on sheet name |
| `numberFilter` | — | Substring filter on sheet number |
| `includeTemplates` | Include view templates | — |
| `returnParameters` | Extra parameters to read per view | Accepts `["default"]` to expand to 10 Estonian/Revit sheet params |
| `includeViewports` | — | Add viewport detail (view, position, detail number) per sheet |
| `limit` | Cap result count | Cap result count |

#### Preview Tools (read-only, no changes)

| Tool | Description |
|------|-------------|
| `revit_preview_place_views_on_sheets` | Shows which views would be placed on which sheets using configurable match modes (ExactName, Contains, Fuzzy, SheetNumberPrefix, SheetNumberSuffix, CustomParameter) |
| `revit_preview_duplicate_sheets` | Shows new sheet numbers/names that would result from duplicating selected sheets |
| `revit_preview_create_sheets_from_table` | Validates a table of `{sheetNumber, sheetName, ...params}` rows — flags conflicts and issues without creating anything |
| `revit_preview_duplicate_views` | Shows new view names that would result from duplicating views with configurable duplicate option |
| `revit_preview_rename_views` | Shows before/after names for a batch rename using FindReplace, PrefixSuffix, Template, or RegexFindReplace mode |
| `revit_preview_rename_sheets` | Same as above for sheets — targets Name, Number, or Both |

#### Write Tools (requires approval)

| Tool | Description |
|------|-------------|
| `revit_place_views_on_sheets` | Places views on matched sheets in a transaction — same parameters as the preview tool |
| `revit_duplicate_sheets` | Creates empty sheet copies with the same title block and optionally copied parameters |
| `revit_create_sheets_from_table` | Creates multiple sheets from a row table in one transaction |
| `revit_duplicate_views` | Duplicates views with Duplicate, DuplicateWithDetailing, or AsDependent option |
| `revit_apply_view_template` | Applies a view template to one or more views filtered by ID, type, or name |
| `revit_set_sheet_parameters_bulk` | Sets parameters on multiple sheets in one transaction |
| `revit_set_view_parameters_bulk` | Sets parameters on multiple views in one transaction |
| `revit_rename_views` | Renames views using FindReplace, PrefixSuffix, Template, or RegexFindReplace mode |
| `revit_rename_sheets` | Renames sheet names, numbers, or both using the same rename modes |

> **Deferred (not yet implemented):**
> - `EmptyDetailOnly` duplicate option: `revit_duplicate_views` and `revit_preview_duplicate_views` return a clear error if `duplicateOption="EmptyDetailOnly"` is requested. Only `Duplicate`, `DuplicateWithDetailing`, and `AsDependent` are supported.
> - `revit_create_sheets_from_preset`: Creating sheets directly from a PlaceViews preset file is not yet implemented. Use `revit_run_view_sheet_workflow_preset` to plan the workflow, then execute steps manually with `revit_create_sheets_from_table`, `revit_duplicate_sheets`, etc.

#### Destructive Delete Tools (always requires manual approval — Direct Edit does NOT bypass)

| Tool | Description |
|------|-------------|
| `revit_preview_delete_views` | Shows which views would be deleted — never modifies the model |
| `revit_delete_views` | **Permanently deletes views.** Always requires explicit manual approval regardless of Direct Edit mode. Optional `skipPlacedOnSheets=true` (default) protects placed views |
| `revit_preview_delete_sheets` | Shows which sheets would be deleted — never modifies the model |
| `revit_delete_sheets` | **Permanently deletes sheets.** Always requires explicit manual approval. Optional `skipSheetsWithViews=true` (default) protects occupied sheets |

> **Safety:** `revit_delete_views` and `revit_delete_sheets` use `DestructiveRequiresManualApproval` permission — they always queue for manual approval in the Pending tab even when Direct Edit is enabled. This cannot be overridden.

### Coordination / Clash Detection Tools

Run clash detection directly from your AI session without leaving the chat. Define rules as reusable JSON presets, step through results one clash at a time, and export findings to Excel — all against the live model.

> **Note:** Hard clash detection uses bounding-box overlap as a fast candidate pre-filter only. Reported hard clashes are confirmed by solid-geometry boolean intersection — elements without extractable solids (e.g. some fire alarm devices, imported DWG geometry) are skipped by default (`Confidence = High`). Pass `allowBoundingBoxFallback = true` to also return unconfirmed bounding-box overlaps (`Confidence = Low`). Clearance detection uses expanded bounding-box approximation — distances are conservative estimates, not true surface-to-surface. Linked models must be loaded to be clashable.

#### Discovery

| Tool | Description |
|------|-------------|
| `revit_list_clashable_categories` | Lists all element categories present in the model with element counts — used to select source/target category sets for clash rules |
| `revit_list_clashable_links` | Lists all loaded Revit link instances — returns linkId, linkName, isLoaded, transform summary |
| `revit_get_clash_candidates` | Collects candidate elements for source/target category sets; optionally filters to a named Revit link — returns element counts only, no geometry |

#### Detection

| Tool | Description |
|------|-------------|
| `revit_detect_hard_clashes` | Detects hard (physical intersection) clashes between two category sets using solid-geometry boolean intersection (`Confidence = High`). Bounding-box is used only as a fast candidate pre-filter. Set `allowBoundingBoxFallback = true` to also return unconfirmed bbox-only overlaps (`Confidence = Low`) when solids are unavailable. Pass `returnIssueReport=true` for a structured `IssueReportDto`. |
| `revit_detect_clearance_clashes` | Detects clearance violations — expands source bounding boxes by a configurable tolerance (mm) before intersection test. **MVP approximation:** uses bounding-box expansion, not true surface-to-surface distance. Results may include false positives for non-rectangular geometry. `Confidence = Medium`. Use for early QA screening; results must be visually reviewed in the generated clash review view. `distanceMode` controls the measurement method: `ExpandedBoundingBox` (default, `Confidence = Medium`) or `SolidCentroidApproximation` (centre-to-centre distance, `Confidence = Low`). True geometry-based clearance distance is planned as a future enhancement. |
| `revit_get_clash_summary` | Aggregates a clash run result — returns total counts grouped by rule name, severity, status, level, detection method, and confidence |

#### Presets

| Tool | Description |
|------|-------------|
| `revit_list_clash_presets` | Lists all clash preset JSON files from the clash presets folder (built-in defaults + user-saved) |
| `revit_get_clash_preset` | Reads and returns the full contents of a named clash preset |
| `revit_validate_clash_preset` | Validates a clash preset structure — returns isValid, ruleCount, errors[], suggestions[] |
| `revit_run_clash_preset` | Runs all rules in a clash preset and caches the combined result for step-through review. Hard clash rules use strict solid-intersection by default; set `allowBoundingBoxFallback = true` for low-confidence fallback results. Pass `returnIssueReport=true` for a structured `IssueReportDto`. |

#### Reporting

| Tool | Description |
|------|-------------|
| `revit_export_clash_report_to_excel` | Exports a clash run result to `.xlsx` with a Summary sheet and a per-clash Details sheet |
| `revit_get_clash_dashboard_summary` | Returns aggregated dashboard stats: total clashes, by-rule breakdown, severity distribution, status distribution |

#### Review Navigation

| Tool | Description |
|------|-------------|
| `revit_get_next_clash` | Advances to the next clash in the cached run and returns its details |
| `revit_get_previous_clash` | Steps back to the previous clash in the cached run |
| `revit_create_clash_review_view` | Creates a temporary 3D section box view isolating a single clash *(requires approval)* |
| `revit_focus_clash` | Zooms the active 3D view to a clash location and selects both clashing elements *(requires approval)* |
| `revit_select_clash_elements` | Selects both elements of a specific clash in the Revit UI *(requires approval)* |

### Family Creation Tools

| Tool | Description |
|------|-------------|
| `revit_create_panel_schematic_symbol_from_dwg` | Creates a Detail Item family (`.rfa`) from a local DWG using a company preset. Saved to the configured output folder — not loaded into the active project. Applies `Kilp_` prefix + user-defined name; adds `_01`/`_02` suffix on conflicts. *(requires approval)* |

### Skills Tools

Skills are named multi-step QA workflows stored as `.skill.json` files. Built-in skills ship with the addin; project overrides let you enable/disable tasks or change settings per job. Use `revit_preview_skill_run` to inspect what a skill will do before running it.

**Built-in skills:**
| Skill ID | Name | What it checks |
|----------|------|----------------|
| `company.lehed.nimetamise-kontroll` | Lehtede Nimetamise Kontroll | Sheet naming QA |
| `company.delivery.check` | Delivery Check | Delivery folder vs Revit sheets |
| `company.parameter.qa` | Parameter QA | Required parameter completeness |
| `company.coordination.qa` | Coordination QA | Clash detection |
| `company.project.pre-delivery` | Pre-Delivery Combined Check | All checks in one run |
| `company.lehed.nimetamine` | Lehtede Nimetamine | Auto-apply sheet numbers |
| `company.security.valve-labipaas-spec-check` | Valve/Läbipääs Spec Check | Audit EN spec section 4 against the model |

| Tool | Description |
|------|-------------|
| `revit_list_skills` | Lists all available company skills with IDs, names, versions, and task counts. Optional `projectId` flags which skills have a project override |
| `revit_get_skill_details` | Returns the full task breakdown and settings for a skill. `includeProjectOverride=true` merges the project-level override into the response |
| `revit_preview_skill_run` | Read-only preview: shows task list, which tasks would modify the model, and whether approval is required. Always call this before `revit_run_skill` |
| `revit_run_skill` | Runs all enabled tasks in a skill. Some tasks may require approval. Use `useProjectOverride=true` to apply job-specific settings |
| `revit_run_skill_task` | Runs a single task within a skill — useful for re-running or debugging one check without running the full skill |
| `revit_create_project_skill_override` | Creates a project-level settings override for a skill. `changesJson` uses the structure `{"tasks":{"<taskId>":{"enabled":true,"settings":{...}}}}` |
| `revit_update_project_skill_override` | Merges additional changes into an existing project override |
| `revit_reset_project_skill_override` | Deletes the project override, reverting the skill to company-master defaults |
| `revit_configure_sheet_naming_skill` | Helper that creates or updates the project override for the built-in sheet naming skill (`company.lehed.nimetamise-kontroll`). Supports `enableExcelComparison` with optional `excelFilePath` and `worksheetName`, and per-task enable/disable flags. Returns the saved override JSON. |

#### Skill Admin Tools

| Tool | Description |
|------|-------------|
| `revit_compare_skill_override_to_master` | Compares a project skill override against the current master definition — returns obsolete tasks, new tasks added to master, changed task settings, and version mismatch flag. Args: `skillId`, `projectId`. |
| `revit_export_skill_override_diff_markdown` | Exports a human-readable Markdown diff report of a project override vs master to the exports folder (`%USERPROFILE%\Documents\RKTools\RevitMCP\Exports`). Returns `filePath`, `skillId`, `projectId`, `changeCount`. Args: `skillId`, `projectId`. |
| `revit_propose_master_skill_update` | Analyses a project override and proposes a new master skill definition that incorporates project-level changes. Returns a proposal JSON with `proposedMaster`, `rationale`, and `breakingChanges`. Args: `skillId`, `projectId`, `includeRationale`. |

### Issue Report Tools

Shared structured issue model used as the output format for all QA tools. Issues carry `severity` (Info / Warning / Error / Critical), `status`, `category`, `discipline`, `phase`, `elementId`, `sheetNumber`, and a run-prefixed ID (`<runId>-<NNNN>`). Three export formats and a multi-report merge tool.

| Tool | Description |
|------|-------------|
| `revit_export_issues_json` | Exports an `IssueReportDto` (passed as `reportJson`) to a `.json` file. Returns `filePath`, `totalIssues`, `runId`. *(requires approval)* |
| `revit_export_issues_excel` | Exports an `IssueReportDto` to a formatted `.xlsx` file with Summary and Issues sheets, severity colour coding, and auto-filter. Returns `filePath`. *(requires approval)* |
| `revit_export_issues_markdown` | Exports an `IssueReportDto` to a `.md` file with summary table, category breakdown, and issues table. Returns `filePath`. *(requires approval)* |
| `revit_export_issues_html_dashboard` | Exports an `IssueReportDto` to a self-contained offline HTML file with interactive filtering by severity/category/status, sortable table, summary cards, and chart. Returns `filePath`. Args: `reportJson`, `title` (optional), `fileName` (optional). *(requires approval)* |
| `revit_merge_issue_reports` | Merges multiple `IssueReportDto` JSON strings (`reportJsonArray`) into a single consolidated report. Returns the merged report JSON and summary counts. |

### Delivery Tools

QA tools for pre-issue drawing deliveries. File names are expected to follow the EULE pattern: `{projectNumber}_{stage}_{discipline}-{group}-{sequence}[_{description}][_{revision}].{ext}`. Files that do not match the pattern are still listed but are flagged as unrecognised.

All four tools accept a `returnIssueReport` flag (default `false`). When `true`, the response includes a structured `IssueReportDto` compatible with `revit_export_issues_excel` / `revit_export_issues_markdown`.

| Tool | Description |
|------|-------------|
| `delivery_scan_folder` | Scans a local folder for drawing files, parses EULE-format names, and returns a file list with metadata. Args: `folderPath`, `recursive`, `includeExtensions`, `maxResults`. Optional policy checks: `checkTempFiles`, `checkOldRevisions`, `checkSuspiciousExtensions`, `checkRequiredFolders`, `requiredFolders[]`, `allowedExtraExtensions[]`. |
| `delivery_check_against_revit_sheets` | Compares files in a delivery folder against sheets in the currently open Revit model. Flags missing PDFs/DWGs, orphan files with no matching sheet, duplicates, and suspiciously small files. Optional `stageFilter` and `disciplineFilter`. Sheet numbers in the format `1626_TP_EL-5-01` are correctly parsed — `disciplineFilter: ["EL"]` matches both short (`EL-5-01`) and full-prefix sheet numbers. |
| `delivery_check_against_excel_register` | Compares files in a delivery folder against rows in an Excel document register. Auto-detects the header row (looks for columns containing "nr", "number", or "dokumendi nr"). Flags register rows with no matching file, files not in the register, and duplicate document numbers. |
| `delivery_run_full_check` | Runs all three checks in sequence (scan → Revit sheet check → Excel register check) and merges results into one `IssueReportDto`. Optional `exportExcelReport` and `exportMarkdownReport` flags write output files to the delivery folder. Supports the same folder-policy checks as `delivery_scan_folder` (`checkTempFiles`, `checkOldRevisions`, `checkRequiredFolders`, `requiredFolders[]`, `checkSuspiciousExtensions`, `allowedExtraExtensions[]`, `requiredProjectFileExtensions[]`, `ignoredPatterns[]`). |

---

### Standards Lookup Tools

Indexes company document files (PDF, Word `.docx`, Markdown, plain text) into a local full-text index and provides chunk-level search and retrieval. The source list is configured in `%ProgramData%\RKTools\MCP\Config\StandardsSources.json`.

See [docs/standards-lookup.md](docs/standards-lookup.md) for setup and usage.

| Tool | Description |
|------|-------------|
| `standards_list_sources` | Lists all configured standards sources with their ID, label, path, and last-indexed date. |
| `standards_index_sources` | Indexes (or re-indexes) one or all configured sources — chunks documents by heading/paragraph and writes a local TF-IDF index. Args: `sourceId` (optional; omit to index all). |
| `standards_search` | Full-text search across indexed standards. Returns ranked chunks with source label, file name, heading path, and relevance score. Args: `query`, `sourceId` (optional filter), `topK` (default 10), `minScore` (default 0.01). |
| `standards_get_document_chunk` | Retrieves a specific indexed chunk by its `chunkId` with optional surrounding context. Args: `chunkId`, `sourceId` (optional), `contextBefore` (default 1, max 5), `contextAfter` (default 1, max 5). Returns `{ targetChunk, contextChunks }`. |
| `standards_validate_source_config` | Validates the `StandardsSources.json` config — checks paths exist, IDs are unique, and file types are supported. Returns `isValid`, `errors[]`, `warnings[]`. |

---

### File System Tools

General-purpose file utilities for reading, writing, copying, and backing up files on disk. Access is restricted by the `FileAccessPolicy` config — if absent, permissive defaults apply (user home, `C:\Projects`, temp).

| Tool | Description |
|------|-------------|
| `file_read_text` | Reads a text file and returns its content. `maxBytes` caps the read (default 1 MB). |
| `file_write_text` | Writes text to a file. `overwrite=false` (default) refuses to clobber existing files. `createDirectories=true` creates missing parent folders. `backupBeforeOverwrite=true` creates a timestamped backup before overwriting. *(requires approval)* |
| `file_list_directory` | Lists files and subdirectories in a folder. Supports `searchPattern`, `recursive`, and `maxResults`. |
| `file_inspect` | Returns metadata for a file or directory: existence, type, size, extension, created/modified timestamps, read-only flag, and optional SHA-256 hash (`includeHash=true`, skipped for files > 100 MB). |
| `file_copy` | Copies a file to a destination path. Args: `sourcePath`, `destinationPath`, `overwrite` (false), `createDirectories` (true), `preserveTimestamps` (true). *(requires approval)* |
| `file_backup` | Creates a timestamped copy of a file — `{stem}_{suffix}_{yyyy-MM-dd_HHmmss}{ext}`. Args: `filePath`, `backupDirectory` (default: same directory), `suffix` (default `backup`), `preserveTimestamps` (true). *(requires approval)* |

**FileAccessPolicy config** (`FileAccessPolicy.json`):
```json
{
  "AllowedReadRoots": ["%USERPROFILE%", "C:\\Projects", "%TEMP%"],
  "AllowedWriteRoots": ["%USERPROFILE%\\Documents", "C:\\Projects", "%TEMP%"],
  "AllowNetworkPaths": false,
  "AllowProgramDataWrites": false,
  "MaxReadBytesDefault": 1048576
}
```

### Configuration / State Tools

Read and update JSON config files at four scopes without requiring a running Revit session. All paths are resolved by `ConfigPathResolver`:

| Scope | Path |
|-------|------|
| `company` | `%ProgramData%\RKTools\MCP\Config\company.config.json` |
| `user` | `%AppData%\RKTools\MCP\user.config.json` |
| `tool-state` | `%AppData%\RKTools\MCP\State\tool-state.json` |
| `project` | `<projectRoot>\.rktools\mcp.project.config.json` |

| Tool | Description |
|------|-------------|
| `config_read` | Reads a config file at the given scope. `createIfMissing=true` (default false) creates an empty `{}` file if absent. |
| `config_write` | Writes a full JSON object to a config file, replacing existing content. Atomic write (`.tmp` → validate → rename). `backupBeforeOverwrite=true` creates a timestamped backup. *(requires approval)* |
| `config_update` | Merges a set of key/value pairs into an existing config file. Supports dot-path keys (`$.excel.defaultBackupBeforeSave`). `createIfMissing=true` creates the file if absent. `backupBeforeOverwrite=true` takes a backup before changes. *(requires approval)* |
| `config_get_project_config` | Reads the project-scoped config (`mcp.project.config.json`) for a given `projectRoot` folder. |
| `config_set_project_config` | Writes or updates the project-scoped config. Accepts a `jsonContent` string (full replace) or `updates` object (partial merge). *(requires approval)* |

---

### Excel Tools

Operate on `.xlsx` and `.xlsm` files directly on disk — no open Revit document required, compatible with any Excel file on the allowed-write paths. All write tools support a `dryRun` preview mode and go through the approval queue.

| Tool | Description |
|------|-------------|
| `excel_inspect_workbook` | Returns worksheet names, used ranges, row/column counts, detected headers, and optional preview rows for each visible sheet. |
| `excel_read_range` | Reads a specific cell range from a worksheet — returns rows of cells with address, value, formula, and data type. |
| `excel_update_cells` | Updates one or more cells by address (e.g. `"B2"`, `"C5"`) — preserves existing cell styles. Optional backup. `dryRun=true` returns a preview of planned changes without modifying the file. *(requires approval — even in dry-run mode)* |
| `excel_insert_rows` | Inserts new rows at a specified row number — copies style from a template row, writes cell values by column letter key. Optional backup. `dryRun=true` returns row/range preview without modifying the file. *(requires approval — even in dry-run mode)* |
| `excel_append_table_rows` | Appends rows after the last data row in a sheet or named table. `matchHeaders=true` maps values by header name; falls back to column letter (`"A"`, `"B"`) when unmatched. Extends named table if present. Optional backup. `dryRun=true` returns append-position preview without modifying the file. *(requires approval — even in dry-run mode)* |

> **Excel dry-run and approval:** All three Excel write tools go through the approval queue regardless of `dryRun` setting. This ensures the Revit user always has visibility over pending operations. If you only want a preview without triggering an approval request, use `excel_inspect_workbook` or `excel_read_range` instead.

> **Excel write limitations (MVP):** Write tools copy row height and cell styles (number format, font, fill, border, alignment). Advanced Excel features — merged cells, data validation, conditional formatting, and existing cell formulas — are preserved in cells that are not written to, but are **not** carried over to newly inserted rows. Verify complex worksheets manually after insertion or appending.

---

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

List all categories in the model that I can run clashes against.

Are there any loaded Revit links I can clash against?

Detect hard clashes between Electrical Equipment and Mechanical Equipment.

Detect clearance clashes between Fire Alarm Devices and Ducts with a 100 mm clearance.

Run the built-in "Electrical vs HVAC" clash preset.

Export the last clash run to Excel.

Show me the next clash from the last run.

Select the elements involved in clash CL-0003.

List available parameter QA rule sets.

Run the ELENEA Basic QA parameter rule set and show me any missing parameters.

Run the ELENEA Basic QA rule set and export the issue report to Excel.

Scan delivery folder C:\\Projects\\1626\\Export for temp files and old revisions.

Run the full delivery check for stage TP, discipline EL only, and flag any temp or backup files.
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

## For Developers

### Architecture

```
Claude Code / Codex / Antigravity CLI
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

### Projects

| Project | Target | Role |
|---------|--------|------|
| `RevitMCP.Core` | net8.0 | Shared DTOs — `McpToolRequest`, `McpToolResult`, enums, `Safety/` guard classes |
| `RevitMCP.Addin` | net8.0-windows | Revit add-in DLL — pipe server, tool registry, WPF UI |
| `RevitMCP.Bridge` | net8.0 | STDIO MCP server — forwards tool calls over named pipe |
| `RevitMCP.Config` | — | Install scripts and default configs |
| `RevitMCP.Tests` | net8.0 | xUnit unit tests for pure-logic helpers (no Revit runtime required) |

### Project Structure

```
EULE_MCP/
├── RevitMCP.Core/
│   └── Models/          McpToolRequest, McpToolResult, enums
├── RevitMCP.Addin/
│   ├── App.cs           IExternalApplication entry point
│   ├── Commands/        OpenMcpWindowCommand
│   ├── Electrical/      Circuit services, QA helpers, dashboard, voltage-drop prep, fire alarm preset, cable resistance
│   ├── Documentation/   Pure-logic helpers: RenameEngine, FuzzyNameMatcher, PlacementPointResolver, ViewSheetMatchingService
│   ├── FileSystem/      FilePathPolicy (allowed-root enforcement), FileSystemService (read/write/list/inspect/copy/backup)
│   ├── Configuration/   ConfigPathResolver (scope → path), JsonConfigService (read/write/update with atomic write)
│   ├── Excel/           ExcelWorkbookInspector, ExcelWorkbookModifier (dryRun support), ExcelBackupService, ExcelHeaderDetector, ExcelStyleCopyService
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

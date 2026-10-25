# EULE MCP — Revit MCP Connector

EULE MCP connects Claude Code, Codex, and Antigravity CLI to a live Autodesk Revit model through the [Model Context Protocol](https://modelcontextprotocol.io). It provides local model queries, discipline workflows, controlled writes, QA, reporting, and file operations without requiring users to write Revit API scripts.

The current bridge exposes **195 MCP tools** across eleven documented capability groups.

## Documentation

The [EULE MCP Wiki](https://github.com/RaulKalev/EULE_MCP/wiki) is the main documentation site.

| Topic | Wiki page |
|---|---|
| First installation | [Getting Started](https://github.com/RaulKalev/EULE_MCP/wiki/Getting-Started) |
| Permanent and team deployment | [Installation and Deployment](https://github.com/RaulKalev/EULE_MCP/wiki/Installation-and-Deployment) |
| Complete tool catalog | [Tools and Workflows](https://github.com/RaulKalev/EULE_MCP/wiki/Tools-and-Workflows) |
| Practical prompts and JSON | [Examples and Recipes](https://github.com/RaulKalev/EULE_MCP/wiki/Examples-and-Recipes) |
| Approval and transaction safety | [Safety and Approvals](https://github.com/RaulKalev/EULE_MCP/wiki/Safety-and-Approvals) |
| Architecture | [Architecture](https://github.com/RaulKalev/EULE_MCP/wiki/Architecture) |
| Development and testing | [Development and Testing](https://github.com/RaulKalev/EULE_MCP/wiki/Development-and-Testing) |
| Common problems | [Troubleshooting](https://github.com/RaulKalev/EULE_MCP/wiki/Troubleshooting) |

## Capabilities

| Area | Tools | Examples |
|---|---:|---|
| [General queries](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-General-Queries) | 27 | Model discovery, parameters, extensible storage, grouping, selection, presets, QA, Excel exports |
| [Element placement and annotation](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Tagging-Annotation) | 25 | Placement from DWG locations and from loose DWG line work, alignment against walls/ceilings/floors, lining up and distributing within a view, SmartTags-compatible tagging, selected-example templates, retagging, dimensions |
| [Electrical](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Electrical) | 47 | Circuits, panels, patch panels, cable types, dashboards, voltage drop, fire alarm |
| [Views and sheets](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Documentation) | 29 | Placement, duplication, CAD import/layer graphics, naming, revisions, controlled deletion |
| [Coordination](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Coordination) | 15 | Hard and clearance clashes, presets, reporting, review navigation |
| [Skills and QA](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Skills-QA) | 16 | Company skills, project overrides, skill authoring, parameter rule sets |
| [Reports and delivery](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Reports-Delivery) | 6 | Shared issue reports, exports, folder/register/Revit-sheet checks |
| [Standards](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Standards) | 5 | Offline indexing, search, and contextual retrieval |
| [Files, Excel, and configuration](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Files-Excel-Configuration) | 16 | Policy-scoped files, standalone workbooks, scoped JSON state |
| [Family types, creation, and IFC](https://github.com/RaulKalev/EULE_MCP/wiki/Tool-Reference-Family-IFC) | 10 | Family type duplication and editing, DWG-to-Detail-Item families, IFC Space-to-Room |
| [Worker document](docs/worker-document.md) | 9 | Open your own local copy of a central in the background, worksets, ownership, checkout and relinquish, reload latest, close; `document=` on the core and query reads |
| [Jobs: unattended work](docs/jobs.md) | 6 | Submit a whole job of tool calls with result binding; one approval, the connector runs it step by step between your own work, stops on failures or new Revit errors, survives a restart and publishes a report page |
| [Plans: one approval per job](docs/plans.md) | 4 | Submit a plan (tools, element ids, categories, limits), approve once, then write calls inside it run without prompts; status, close, per-session plan id |
| [Checkpoints and change review](docs/checkpoints.md) | 6 | Revit model warnings, checkpoints before a batch of writes, what changed since (by tool or user, before/now values, moves, new warnings), compensating rollback with manual approval |
| [Schedules](docs/schedules.md) | 8 | Read schedule definitions and displayed rows, list schedulable fields, create and edit schedules from a spec (preview first), place them on sheets next to a viewport, export CSV/TXT/XLSX |
| [Model graph (routing layer)](docs/model-graph.md) | 4 | Per-model SQLite graph of element ids and relationships: build, freshness status, structured queries (neighbors/find/path/subtree), summary |

## Supported environment

| Target | Runtime | Feature surface |
|---|---|---|
| Revit 2026 | .NET 8 | Full connector |
| Revit 2024 | .NET Framework 4.8 | Same UI and tools except IFC Space-to-Room |

Supported clients:

- Claude Code
- Codex CLI
- Antigravity CLI

All clients launch the same local `RevitMCP.Bridge.exe` over STDIO. The bridge routes requests to a per-process named pipe hosted by the selected Revit add-in.

## Quick start

```powershell
dotnet build RevitMCP.slnx -c Release
```

Then:

1. Load the matching add-in DLL through ricaun AppLoader or install its `.addin` manifest.
2. Register the bridge with one supported client using the scripts in `RevitMCP.Config\Install`.
3. Open a Revit model and select **RK Tools → MCP Connector**.
4. Verify the connector is running.
5. Call `revit_get_connection_status`.

See [Getting Started](https://github.com/RaulKalev/EULE_MCP/wiki/Getting-Started) for exact paths and [Installation and Deployment](https://github.com/RaulKalev/EULE_MCP/wiki/Installation-and-Deployment) for team packaging.

## Connector window

**RK Tools → MCP Connector** opens a compact window on the RK Tools design system (dark and light, follows Revit's
theme until you pick one, honours Windows high contrast, transparency and animation settings).

- **Status**: whether the connector runs (Start / Stop), the model, and the **Direct Edit** switch (available to every
  user, with a confirmation; the card turns red while it is on).
- **Pending**: changes waiting for approval (Approve / Reject, Reject all), active plans (Show changes / Revoke) and
  recent decisions. The window switches here when a new request arrives; the switcher shows the count.
- **Jobs**: unattended jobs with progress and the actions that apply (Pause, Resume, Cancel, Open report).
- **Emergency stop** (pinned at the bottom) rejects everything pending and disconnects all agents.
- **Technical details** (checkbox on the Status page, Ctrl+Shift+D, remembered) adds the view, selection,
  worksharing, pipe name, agent routing (*Use this Revit*), Project Village, plan limits, tool names, and the
  **Activity** page with the usage report and log folder.

Ctrl+1…4 switch pages. Window bounds, appearance and the detail level are kept in `%LOCALAPPDATA%\RevitMCP\window-state.json`.

## Safety model

- Revit API work is dispatched through `ExternalEvent`.
- Model mutations execute inside a Revit `Transaction`.
- Eligible writes enter the modeless approval queue before execution.
- Pending requests are bound to the captured document, change stamp, and selection context.
- Destructive operations remain explicitly approval-gated.
- A [plan](docs/plans.md) replaces per-call approvals with one approval for an envelope of tools, elements and limits; calls inside it run in a transaction group that is rolled back when Revit's changes leave the envelope, and plans never cover deletes.
- A [job](docs/jobs.md) runs its steps through the same dispatcher as any client, one step per dispatch, under its plan; steps outside it wait in the Pending tab, and Emergency stop (formerly Panic Stop) cancels every job.
- Background file, Excel, report, standards, and configuration work uses a separate serialized lane.

Read [Safety and Approvals](https://github.com/RaulKalev/EULE_MCP/wiki/Safety-and-Approvals) before enabling Direct Edit or adding write tools.

## Development

```powershell
dotnet restore RevitMCP.slnx --locked-mode
dotnet build RevitMCP.slnx -c Release --no-restore
dotnet test RevitMCP.Tests\RevitMCP.Tests.csproj -c Release --no-build
```

Important repository references:

- [`INSTALL.md`](INSTALL.md) — installer and manifest details
- [`TESTING.md`](TESTING.md) — Revit smoke-test matrix
- [`docs/alignment.md`](docs/alignment.md) — moving elements against walls, ceilings, and floors, including linked IFC
- [`docs/cad-management.md`](docs/cad-management.md) — CAD import layer visibility and graphic overrides
- [`docs/compatibility.md`](docs/compatibility.md) — supported framework and API boundaries
- [`docs/execution-safety.md`](docs/execution-safety.md) — execution guarantees
- [`docs/extensible-storage.md`](docs/extensible-storage.md) — reading add-in data stored on elements
- [`docs/family-types.md`](docs/family-types.md) — duplicating, renaming, and re-parameterising family types
- [`docs/mcp-performance.md`](docs/mcp-performance.md) — lower-latency queries and reduced-credit tool profiles
- [`docs/project-village.md`](docs/project-village.md) — opt-in "Project Village" activity visualizer with a flyer board of query results and Show in Revit (no AI calls, no model changes)
- [`docs/model-graph.md`](docs/model-graph.md) — per-model routing graph: find element ids cheaply before querying the live model
- [`docs/move-elements.md`](docs/move-elements.md) — moving existing elements onto exact coordinates or by a displacement, including Detail Items and other view-specific elements
- [`docs/copy-elements.md`](docs/copy-elements.md) — copying any element Revit can copy, within a view, across views or in the model
- [`docs/place-on-face.md`](docs/place-on-face.md) — placing face-based families on wall, ceiling and floor faces found from points, in the host model or in links
- [`docs/place-from-cad.md`](docs/place-from-cad.md) — placing families at locations marked in an imported DWG, and reconstructing fixtures from loose DWG line work
- [`docs/schedules.md`](docs/schedules.md) — reading schedule definitions and rows, creating and editing schedules from a spec, placing them on sheets, exporting CSV/TXT/XLSX
- [`docs/skills.md`](docs/skills.md) — company skills, project overrides, and the skill runner
- [`docs/shared-memory.md`](docs/shared-memory.md) — shared company memory: notes every colleague's agent recalls, kept in sync without rebuilds, learned from users' workflows, reviewed on a page and consolidated weekly
- [`docs/standards-lookup.md`](docs/standards-lookup.md) — offline standards indexing and retrieval
- [`docs/tool-consolidation.md`](docs/tool-consolidation.md) — discriminator-based tools and the old → new name mapping
- [`docs/usage-reporting.md`](docs/usage-reporting.md) — per-user usage collection (opt-in, after telling staff), monthly HTML/Excel reports, credit cost join and estimated time saved
- [`docs/view-alignment.md`](docs/view-alignment.md) — lining up and distributing tags, text, and elements within a view

## License

MIT

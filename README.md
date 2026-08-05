# EULE MCP — Revit MCP Connector

EULE MCP connects Claude Code, Codex, and Antigravity CLI to a live Autodesk Revit model through the [Model Context Protocol](https://modelcontextprotocol.io). It provides local model queries, discipline workflows, controlled writes, QA, reporting, and file operations without requiring users to write Revit API scripts.

The current bridge exposes **191 MCP tools** across ten documented capability groups.

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

## Safety model

- Revit API work is dispatched through `ExternalEvent`.
- Model mutations execute inside a Revit `Transaction`.
- Eligible writes enter the modeless approval queue before execution.
- Pending requests are bound to the captured document, change stamp, and selection context.
- Destructive operations remain explicitly approval-gated.
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
- [`docs/move-elements.md`](docs/move-elements.md) — moving existing elements onto exact model coordinates in bulk
- [`docs/place-from-cad.md`](docs/place-from-cad.md) — placing families at locations marked in an imported DWG, and reconstructing fixtures from loose DWG line work
- [`docs/skills.md`](docs/skills.md) — company skills, project overrides, and the skill runner
- [`docs/standards-lookup.md`](docs/standards-lookup.md) — offline standards indexing and retrieval
- [`docs/tool-consolidation.md`](docs/tool-consolidation.md) — discriminator-based tools and the old → new name mapping
- [`docs/view-alignment.md`](docs/view-alignment.md) — lining up and distributing tags, text, and elements within a view

## License

MIT

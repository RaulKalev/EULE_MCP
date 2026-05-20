# EULE MCP — Revit MCP Connector

A local [Model Context Protocol](https://modelcontextprotocol.io) connector that lets **Claude Code** (and other MCP-compatible AI agents) interrogate and work with a live **Autodesk Revit 2026** model in real time.

---

## Architecture

```
Claude Code
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

---

## Available Tools

| Tool | Description |
|------|-------------|
| `revit_get_connection_status` | Revit version, document title, active view, worksharing info, selection count |
| `revit_get_selected_elements` | Category, family, type, level, location, bounding box for selected elements |
| `revit_list_views` | All non-template printable views with type, scale, discipline, sheet placement |
| `revit_list_sheets` | All sheets with number, name, and placed view names |
| `revit_list_schedules` | All schedules with category and field names |
| `revit_get_element_parameters` | All parameters for given element IDs or current selection |
| `revit_count_elements` | Element counts grouped by Category or FamilyAndType, with optional category filter |
| `revit_group_by_parameter` | Groups elements by a named parameter value with counts; partial name match (e.g. `ELENEA_Nimetus` hits `ELENEA_ÜLD 001_Nimetus`); checks instance then type parameters with per-type caching |

---

## MCP Window

The **MCP Connector** window has two tabs:

| Tab | Contents |
|-----|----------|
| **Status** | Running/Stopped chip, pipe name, active model, active view, worksharing flag, selected element count, Start/Stop/Panic controls, theme toggle |
| **Activity** | Live DataGrid of tool call history — Time, Tool, Duration (ms), Status (colour-coded); row tooltip shows the result message. "Open Log Folder" and "Clear" buttons at the bottom |

---

## Requirements

- Revit 2026
- .NET 8 SDK
- Claude Code CLI (`claude`)

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

## Claude Code MCP Server

Run `RevitMCP.Config\Install\Install-Claude-MCP.bat`

This registers `RevitMCP.Bridge.exe` as a user-scoped MCP server named `revit-mcp`.

---

## Usage

1. Open Revit 2026 and load a model
2. Load the addin (via AppLoader or the `.addin` manifest)
3. On the **RK Tools** ribbon tab, click **MCP Connector**
4. Click **Start Connector** in the window
5. In Claude Code, ask anything about your model:

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
│   ├── Services/        PipeServer, ExternalEventHandler, ConnectorService
│   ├── Tools/           One file per MCP tool (8 tools)
│   ├── UI/              WPF window (Status + Activity tabs) + ViewModels + themes
│   └── Interfaces/      IRevitMcpTool
├── RevitMCP.Bridge/
│   ├── Program.cs       MCP host setup
│   ├── RevitMcpTools.cs [McpServerToolType] — exposes tools to Claude
│   └── RevitPipeClient  Named pipe client
└── RevitMCP.Config/
    └── Install/         .bat install scripts
```

---

## License

MIT

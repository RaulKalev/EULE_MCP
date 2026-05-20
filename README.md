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

---

## Requirements

- Revit 2026
- .NET 8 SDK
- Claude Code CLI (`claude`)

---

## Build

```bash
dotnet build RevitMCP.slnx -c Release
```

---

## Install

### 1. Revit Add-in

Run `RevitMCP.Config\Install\Install-RevitMCP-Addin.bat`

This creates the `.addin` manifest in `%ProgramData%\Autodesk\Revit\Addins\2026\`.

### 2. Claude Code MCP Server

Run `RevitMCP.Config\Install\Install-Claude-MCP.bat`

This registers `RevitMCP.Bridge.exe` as a user-scoped MCP server named `revit-mcp`.

---

## Usage

1. Open Revit 2026 and load a model
2. On the **RK Tools** ribbon tab, click **MCP Connector**
3. Click **Start Connector** in the window
4. In Claude Code, ask anything about your model:

```
How many walls are in this model?
List all floor plan views on sheets.
What parameters does element 12345 have?
```

---

## Logging

Activity is logged to `%AppData%\RKTools\RevitMCP\Logs\{date}.jsonl` — one JSON line per tool call.

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
│   ├── Tools/           One file per MCP tool
│   ├── UI/              WPF window + ViewModels + themes
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

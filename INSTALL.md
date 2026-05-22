# Revit MCP Connector — Installation Guide

## Prerequisites

- Revit 2026
- .NET 8 SDK
- **Claude Code** CLI (`claude` in PATH) **or** **Codex** CLI (`codex` in PATH)

---

## Step 1 — Build the Solution

```
dotnet build RevitMCP.slnx -c Release
```

Output locations:
- `RevitMCP.Addin\bin\Release\net8.0-windows\RevitMCP.Addin.dll`
- `RevitMCP.Bridge\bin\Release\net8.0\RevitMCP.Bridge.exe`

---

## Step 2 — Install the Revit Add-in

Run **as Administrator**:

```
RevitMCP.Config\Install\Install-RevitMCP-Addin.bat
```

This writes:
```
C:\ProgramData\Autodesk\Revit\Addins\2026\RevitMCP.addin
```

> During development you can skip this step and use [ricaun AppLoader](https://github.com/ricaun-io/ricaun.Revit.AppLoader) to load the DLL directly — see the Loading section in README.md.

---

## Step 3 — Register the Bridge with Your AI Client

Both clients connect through the same `RevitMCP.Bridge.exe`. Run the script for whichever client you use.

### Option A — Claude Code

```
RevitMCP.Config\Install\Install-Claude-MCP.bat
```

This runs:
```
claude mcp add --scope user revit-mcp "<path>\RevitMCP.Bridge.exe" -- --client "Claude Code"
```

Verify the registration with `claude mcp list`.

### Option B — Codex

```
RevitMCP.Config\Install\Install-Codex-MCP.bat
```

This generates `RevitMCP.Config\Install\codex-mcp-snippet.toml` with the correct absolute path filled in:

```toml
[mcp_servers.revit-mcp]
command = "C:\\...\\RevitMCP.Bridge.exe"
args = ["--client", "Codex"]
```

Steps:
1. Open (or create) `%USERPROFILE%\.codex\config.toml`
2. Paste the contents of `codex-mcp-snippet.toml` into that file
3. Restart Codex

---

## Step 4 — Start the Connector in Revit

1. Open Revit 2026.
2. Open a project model.
3. Click **RK Tools → MCP Connector** on the ribbon.
4. Click **Start Connector** in the window.
5. Status chip turns **green (Running)**.

---

## Step 5 — Test the Connection

### From Claude Code

```
call revit_get_connection_status
```

### From Codex

```
Use the revit_get_connection_status tool.
```

Expected response:
```json
{
  "success": true,
  "message": "Revit is connected.",
  "data": {
    "revitVersion": "2026",
    "connectorStatus": "Running",
    "isDocumentOpen": true,
    "documentTitle": "YourProject.rvt"
  },
  "warnings": [],
  "errors": []
}
```

---

## Logs

Activity logs are written to:
```
%AppData%\RKTools\RevitMCP\Logs\YYYY-MM-DD.jsonl
```

Each line is a JSON object with: `timestamp`, `client`, `tool`, `status`, `durationMs`, `revitVersion`, `model`, `centralPath`, `activeView`, `isWorkshared`, `revitUsername`.

---

## Troubleshooting

| Problem | Check |
|---------|-------|
| Button not on ribbon | Verify `.addin` file exists in `%ProgramData%\Autodesk\Revit\Addins\2026\` |
| Bridge can't connect | Confirm connector is Running (green chip) in Revit |
| Claude Code doesn't see the tool | Re-run `Install-Claude-MCP.bat`, then run `claude mcp list` to verify, then restart Claude Code |
| Codex doesn't see the tool | Verify `[mcp_servers.revit-mcp]` block is in `%USERPROFILE%\.codex\config.toml` and Codex was restarted |
| `Transport closed` on MCP tool calls | The bridge process for that session has died. Start a new Codex session — Codex spawns a fresh bridge process per session. The config and exe are fine; only the live connection needs to be reset. |
| Log shows wrong client name | Ensure `--client "Claude Code"` or `--client Codex` is in the registered args |
| Revit crash on load | Check Revit journal at `%LocalAppData%\Autodesk\Revit\Autodesk Revit 2026\Journals\` |

# Revit MCP Connector — Installation Guide

## Prerequisites

- Revit 2026 (located at `E:\Revit 2026\`)
- .NET 8 SDK
- Claude Code CLI (`claude` in PATH)

---

## Step 1 — Build the Solution

```
cd C:\Users\mibil\Documents\AntigravityGit\EULE_MCP
dotnet build RevitMCP.slnx -c Release
```

Output DLLs will be in:
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

---

## Step 3 — Register the Bridge with Claude Code

```
RevitMCP.Config\Install\Install-Claude-MCP.bat
```

This runs:
```
claude mcp add --transport stdio revit-mcp -- "...\RevitMCP.Bridge.exe"
```

---

## Step 4 — Start the Connector in Revit

1. Open Revit 2026.
2. Open a project model.
3. Click **RK Tools → MCP Connector** on the ribbon.
4. Click **Start Connector** in the window.
5. Status chip turns **green (Running)**.

---

## Step 5 — Test from Claude Code

```
> call revit_get_connection_status
```

Expected response:
```json
{
  "revitVersion": "2026",
  "connectorStatus": "Running",
  "isDocumentOpen": true,
  "documentTitle": "YourProject.rvt",
  ...
}
```

---

## Logs

Activity logs are written to:
```
%AppData%\RKTools\RevitMCP\Logs\YYYY-MM-DD.jsonl
```

---

## Troubleshooting

| Problem | Check |
|---------|-------|
| Button not on ribbon | Verify `.addin` file in `%ProgramData%\Autodesk\Revit\Addins\2026\` |
| Bridge can't connect | Confirm connector is Running (green chip) in Revit |
| Claude doesn't see the tool | Re-run `Install-Claude-MCP.bat` and restart Claude Code |
| Revit crash on load | Check Revit journal at `%LocalAppData%\Autodesk\Revit\Autodesk Revit 2026\Journals\` |

# MCP benchmark - batch-g-crosscutting

- Model: ``
- Started: 2026-10-25 21:15:45 +02:00
- Bridge: `C:\Users\mibil\Documents\AntigravityGit\EULE_MCP\.claude\worktrees\eule-mcp-revit-testing-93f21d\RevitMCP.Bridge\bin\Release\net8.0\RevitMCP.Bridge.exe`
- Token estimates: characters / 4 (approximation; bytes are exact)

## Tool-schema cost per profile

Paid once per session (or per request when the client does not cache tool definitions).

| Profile | Tools | `tools/list` bytes | ~tokens | Instructions ~tokens |
|---|---:|---:|---:|---:|
| `full` | 303 | 417,819 | 104,455 | 696 |
| `core` | 21 | 25,482 | 6,371 | 696 |
| `query` | 38 | 45,414 | 11,354 | 696 |
| `read-only` | 193 | 257,738 | 64,435 | 696 |

## Scenarios

Result cost is paid per call. *Total* = schema tokens of the profile + result tokens of the workflow.


# MCP benchmark - document-rest-after

- Model: ``
- Started: 2026-10-25 17:51:27 +02:00
- Bridge: `C:\Users\mibil\Documents\AntigravityGit\EULE_MCP\.claude\worktrees\open-github-issues-40c955\RevitMCP.Bridge\bin\Release\net8.0\RevitMCP.Bridge.exe`
- Token estimates: characters / 4 (approximation; bytes are exact)

## Tool-schema cost per profile

Paid once per session (or per request when the client does not cache tool definitions).

| Profile | Tools | `tools/list` bytes | ~tokens | Instructions ~tokens |
|---|---:|---:|---:|---:|
| `full` | 300 | 411,383 | 102,846 | 696 |
| `core` | 21 | 24,926 | 6,232 | 696 |
| `query` | 38 | 44,300 | 11,075 | 696 |
| `read-only` | 201 | 261,116 | 65,279 | 696 |

## Scenarios

Result cost is paid per call. *Total* = schema tokens of the profile + result tokens of the workflow.


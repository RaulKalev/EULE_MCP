# MCP benchmark - shared-memory-phase1

- Model: none (`--schema-only`, run on Linux without Revit; tool-schema cost only)
- Started: 2026-10-23 21:43:26 +00:00
- Bridge: `RevitMCP.Bridge/bin/Release/net8.0/RevitMCP.Bridge`
- Token estimates: characters / 4 (approximation; bytes are exact)

## Tool-schema cost per profile

Paid once per session (or per request when the client does not cache tool definitions).

| Profile | Tools | `tools/list` bytes | ~tokens | Instructions ~tokens |
|---|---:|---:|---:|---:|
| `full` | 271 | 358,849 | 89,713 | 535 |
| `core` | 21 | 24,063 | 6,016 | 535 |
| `query` | 38 | 40,794 | 10,199 | 535 |
| `read-only` | 185 | 229,543 | 57,386 | 535 |

## Scenarios

Result cost is paid per call. *Total* = schema tokens of the profile + result tokens of the workflow.


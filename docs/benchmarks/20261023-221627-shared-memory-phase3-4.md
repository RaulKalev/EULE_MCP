# MCP benchmark - shared-memory-phase3-4

- Model: none (`--schema-only`, run on Linux without Revit; tool-schema cost only)
- Started: 2026-10-23 22:16:04 +00:00
- Bridge: `RevitMCP.Bridge/bin/Release/net8.0/RevitMCP.Bridge`
- Token estimates: characters / 4 (approximation; bytes are exact)

## Tool-schema cost per profile

Paid once per session (or per request when the client does not cache tool definitions).

| Profile | Tools | `tools/list` bytes | ~tokens | Instructions ~tokens |
|---|---:|---:|---:|---:|
| `full` | 274 | 362,447 | 90,612 | 563 |
| `core` | 21 | 24,515 | 6,129 | 563 |
| `query` | 38 | 41,016 | 10,254 | 563 |
| `read-only` | 186 | 230,695 | 57,674 | 563 |

## Scenarios

Result cost is paid per call. *Total* = schema tokens of the profile + result tokens of the workflow.


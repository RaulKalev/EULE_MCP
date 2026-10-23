# MCP benchmark - shared-memory-phase1-before

- Model: none (`--schema-only`, run on Linux without Revit; tool-schema cost only)
- Started: 2026-10-23 21:32:51 +00:00
- Bridge: `RevitMCP.Bridge/bin/Release/net8.0/RevitMCP.Bridge`
- Token estimates: characters / 4 (approximation; bytes are exact)

## Tool-schema cost per profile

Paid once per session (or per request when the client does not cache tool definitions).

| Profile | Tools | `tools/list` bytes | ~tokens | Instructions ~tokens |
|---|---:|---:|---:|---:|
| `full` | 268 | 354,939 | 88,735 | 456 |
| `core` | 19 | 21,284 | 5,321 | 456 |
| `query` | 38 | 40,794 | 10,199 | 456 |
| `read-only` | 183 | 227,201 | 56,801 | 456 |

## Scenarios

Result cost is paid per call. *Total* = schema tokens of the profile + result tokens of the workflow.


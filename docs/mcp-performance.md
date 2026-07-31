# MCP performance and credit usage

MCP cost and latency come from three different places:

1. The tool catalog supplied to the model. A large catalog consumes input context even
   before a tool is called.
2. Tool results supplied back to the model. Verbose element and parameter DTOs consume
   output/context tokens.
3. Work performed inside Revit. Parameter reads must run on Revit's API thread and can
   dominate elapsed time on large categories.

The connector now addresses all three without changing the default tool surface or
response contracts.

## Reduced tool profiles

The default profile remains `full` and advertises all 188 tools. Existing installations
therefore continue to work unchanged.

For query-oriented sessions, start the bridge with:

```toml
[mcp_servers.revit-mcp]
command = "C:\\path\\to\\RevitMCP.Bridge.exe"
args = ["--client", "Codex", "--tool-profile", "query"]
```

Available profiles:

| Profile | Purpose |
|---|---|
| `full` | All tools; backward-compatible default |
| `query` | 32 common connection, model-query, selection, view/sheet, family-type, electrical, and coordination discovery tools |
| `read-only` | Every tool marked read-only or preview-only |

An exact allow-list gives the smallest possible catalog:

```toml
args = [
  "--client", "Codex",
  "--tool-names", "revit_get_connection_status,revit_count_elements,revit_get_elements_info"
]
```

The same values can be set as `RevitMCP:ToolProfile` and `RevitMCP:ToolNames` in
`appsettings.json`. Restart the MCP client after changing a profile because tool
discovery happens when the MCP process starts.

In a local MCP handshake against this revision:

| Catalog | Tools | `tools/list` JSON |
|---|---:|---:|
| `full` | 188 | 206,495 bytes |
| `query` | 32 | 26,247 bytes |
| two-tool exact allow-list | 2 | 1,144 bytes |

The query profile reduces the advertised schema payload by about 87%. Actual credit
savings depend on whether the MCP client caches tool definitions and how its model
provider bills cached input.

Reduced profiles only change what the bridge advertises. They do not remove or disable
add-in functionality; switching back to `full` restores the complete catalog.

## Compact element results

`revit_find_elements_by_parameter` and `revit_get_elements_info` accept
`compact=true`.

Full mode (the default) returns parameter metadata such as storage type, scope,
read-only state, shared-parameter GUID, parameter ID, and raw value. Compact mode keeps
element identity fields and returns parameters as simple name/value pairs. Use compact
mode for discovery, counting, and agent reasoning; use full mode when parameter metadata
is needed for a write or audit.

Also prefer:

- explicit `parameterNames` / `returnParameters`;
- `includeTypeParameters=false` unless type data is required;
- smaller `pageSize` values;
- `summaryOnly=true` before a broad detailed query.

## Automatic Revit-side optimization

The shared element query engine now separates filter parameters from response
parameters:

- elements outside the requested page are no longer materialized with all response
  parameters merely to calculate `totalMatched`;
- filtered scans materialize values only for parameters named by filters;
- full response parameters are read only for elements included in the returned page;
- type parameters remain materialized and cached once per type within each query.

Exact totals, paging metadata, filter semantics, safety limits, and the default full DTO
shape are unchanged.
